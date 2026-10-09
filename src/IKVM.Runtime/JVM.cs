using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Threading;

using IKVM.CoreLib.Diagnostics.Tracing;
using IKVM.Runtime.Vfs;

namespace IKVM.Runtime
{

    /// <summary>
    /// Main state of the running JVM.
    /// </summary>
    public static partial class JVM
    {

        /// <summary>
        /// The Java SE feature release this build implements (the IkvmJdk flavor it was compiled for): the version
        /// whose class library it runs, and whose multi-release JAR entries ikvmc picks.
        /// </summary>
#if JDK25
        internal const int JavaFeatureVersion = 25;
#else
        internal const int JavaFeatureVersion = 8;
#endif

        static readonly object syncRoot = new object();
        static bool initialized;

#if EXPORTER == false
        static int emitSymbols;
        internal static bool RelaxedVerification = true;
        internal static bool AllowNonVirtualCalls;
        internal static readonly bool DisableEagerClassLoading = SafeGetEnvironmentVariable("IKVM_DISABLE_EAGER_CLASS_LOADING") != null;
        // OpenJDK-aligned lazy linking for classes defined by user Java class loaders: constant pool
        // and member signatures link without invoking user loadClass; unresolved references fall back
        // to the __dynamic_* stubs which resolve at first execution (JVMS §5.4.3 timing).
        internal static readonly bool LazyClassLoading = SafeGetEnvironmentVariable("IKVM_LAZY_CLASS_LOADING") != null;
        // logs every invocation of a Java class loader's loadClass with the loader's type, for
        // diffing load order/set against a real JVM run with -verbose:class
        internal static readonly bool TraceLoadClass = SafeGetEnvironmentVariable("IKVM_TRACE_LOADCLASS") != null;

        // GENERATIONAL METHOD BODIES ("lazy bodies").
        //
        // A method whose body still contains __dynamic_* references is compiled into a static helper
        // (__<>lazybody_N) reached through a cached delegate, instead of directly into the method. On first
        // execution the trampoline calls ByteCodeHelper.BuildLazyBody, which RECOMPILES the body now that the
        // once-unloadable types have been resolved, so the second generation binds those references directly
        // and the __dynamic_* stubs disappear from the hot path.
        //
        // Default ON (`!= "0"`), which is the configuration that measured -15% in-game over 8 interleaved runs.
        internal static readonly bool LazyBodies = SafeGetEnvironmentVariable("IKVM_LAZY_BODIES") != "0";

        // Whether the trampoline actually attempts the recompile. With this off the trampoline still exists but
        // always binds the first-generation helper, which isolates "did the indirection cost anything" from
        // "did the recompile buy anything".
        internal static readonly bool LazyRecompile = SafeGetEnvironmentVariable("IKVM_LAZY_RECOMPILE") != "0";
        /// <summary>
        /// IKVM_LAZY_SLIM (wasm fork, R484): a lazy-body registry entry keeps only what a recompile needs to reparse its
        /// class -- the decoded class-file reader and the parse options -- instead of the first-generation ClassFile with
        /// its instruction streams, constant-pool objects and UTF-8 table, and a TERMINAL entry releases even that. The
        /// registry is never pruned, so before this every lazy candidate's class stayed fully parsed for the life of the
        /// process (~105 MiB of parse state on the 1.16.1 plateau, R482). "0" keeps the whole ClassFile, as before.
        /// </summary>
        internal static readonly bool LazySlim = SafeGetEnvironmentVariable("IKVM_LAZY_SLIM") != "0";

        /// <summary>
        /// IKVM_ARRAYCOPY_OBJ (R429/R430/R435): System.arraycopy between reference arrays is copied inside
        /// ByteCodeHelper.arraycopy -- a ref-store loop (or Span.CopyTo past IKVM_ARRAYCOPY_LOOP elements) when no
        /// element can need a store check, otherwise a loop that store-checks only on a change of element class. "0"
        /// restores Array.Copy and the per-element loop.
        /// </summary>
        internal static readonly bool ArraycopyObj = SafeGetEnvironmentVariable("IKVM_ARRAYCOPY_OBJ") != "0";

        /// <summary>
        /// IKVM_ARRAYCOPY_LOOP (R437): the longest unchecked reference copy done by ByteCodeHelper.arraycopy's ref-store
        /// loop; longer ones go to Span.CopyTo (the runtime's bulk array-ref copy).
        /// </summary>
        internal static readonly int ArraycopyLoop = ParseInt(SafeGetEnvironmentVariable("IKVM_ARRAYCOPY_LOOP"), 64);

        // Cap on successful recompiles (0 = unlimited). Used to bisect a recompile-induced fault down to the
        // first N methods.
        internal static readonly int LazyRecompileLimit = ParseInt(SafeGetEnvironmentVariable("IKVM_LAZY_RECOMPILE_LIMIT"), 0);

        // Exclude methods with exception handlers from candidacy. Default OFF: it was on while the EH-bearing
        // recompile path was unproven, and leaving it on now would decline a large share of real candidates.
        internal static readonly bool LazySkipExceptionHandlers = SafeGetEnvironmentVariable("IKVM_LAZY_SKIP_EH") == "1";

        // Put the second-generation body IN the method instead of behind a cached delegate. Default OFF.
        //
        // This is the point of the whole feature, and the delegate was always the compromise. Reaching a
        // recompiled body through a cache field costs a static load, a branch, a Delegate::Invoke and a
        // non-inlinable frame on EVERY call for the life of the process -- and R226 measured that at 3x
        // what removing the dynamic stubs saved: the dispatch pool fell 11.359% -> 4.640% of the client
        // render thread and total work still rose +14.2% per frame, because ~2,949 newly-admitted methods
        // each acquired a permanent hop. Swapping the body pays none of it, and it also removes the two
        // limits the delegate imposed: MethodHandleUtil.MaxArity (8) and any signature the delegate cannot
        // express.
        //
        // Requires a runtime that can swap a method's IL (mono_interp_replace_method_body, reached via
        // LibIkvm.ReplaceMethodBody). On CoreCLR the swap reports unsupported and every candidate keeps
        // generation 1, i.e. exactly the behaviour it has with this off.
        internal static readonly bool LazyInPlace = SafeGetEnvironmentVariable("IKVM_LAZY_INPLACE") == "1";

        // Allow <init> to be relinked IN PLACE. DEFAULT OFF until measured.
        //
        // The shared candidacy gate rejects <init>/<clinit> because "a constructor's TRAMPOLINE would run
        // before the object is initialised" -- which is correct for TryMoveBodyToHelper, where the body is
        // moved into a static helper reached through a delegate. The in-place path has no trampoline: its
        // hook is `volatile ldsfld flag; dup; brfalse; ldc.i4.0; blt` plus a static call, touching no `this`,
        // and the body is later swapped wholesale by mono_interp_replace_method_body. The restriction is
        // inherited from the other path, not derived from this one.
        //
        // Worth measuring because constructors run per allocation and the largest surviving lazy-link
        // binder in the plateau is one: VoxelShapeSimpleCube.<init> at 3.06 M instr/tick (R279).
        // <clinit> stays excluded unconditionally -- it runs once, so relinking buys no steady state, and
        // the hook would fire inside type initialisation.
        //
        // WORKS, AND REQUIRES A RUNTIME FIX THAT SHIPPED WITH IT (R280). Enabling this alone against a
        // runtime without mono's `clause_is_dead` guard in compute_bb_regions aborts the process at
        // `method-to-ir.c:805, condition 'bb' not met` during world generation -- not a fault in this
        // hook. A generation-2 constructor commonly loads a field of an unloadable type, mono answers
        // that by replacing the whole body with a throw and marking every EH clause dead, and
        // compute_bb_regions then walked the clause table anyway. If a constructor arm ever asserts
        // there again, check the RUNTIME before this file.
        //
        // Measured within one binary, this knob as the only variable: `gen2 clean` 4293 -> 4501
        // (+208 bodies), faults [] on 2/2, `failed`/`declined`/`unexpectedDynamic` all 0, and the
        // runtime's `[wasm-jit badeh] bad_offsets` 0 on BOTH arms.
        //
        // The hook is emitted at the CALL SITE of CompileConstructorBody, not inside it: that helper
        // also compiles <clinit>, which must stay exempt. It is static-only IL -- a volatile static
        // read, a branch and a static call -- so it never touches `this` and is legal ahead of the
        // base-constructor call.
        internal static readonly bool LazyInPlaceCtors = SafeGetEnvironmentVariable("IKVM_LAZY_CTORS") == "1";

        // Admit a candidate whose SIGNATURE mentions an unloadable type. Default OFF.
        //
        // The signature screen used to refuse these together with ghost/remapped/value types, and that
        // conflated two different problems. Ghost and value types need real conversion at the delegate
        // boundary, which the trampoline does not do. An unloadable does not: it erases to plain
        // System.Object (RuntimeJavaType.TypeAsSignatureType), so the delegate type is expressible, and
        // TryRecompileLazyBody builds generation 2's signature from the generation-1 RuntimeJavaMethod
        // anyway -- so the erased signature is already stable across generations.
        //
        // It matters because an unloadable in the signature is the MARKER of the very condition a second
        // generation exists to remove, so the screen was refusing the population with the most to gain:
        // measured 2026-09-07 on Minecraft, sig=5,583 of 51,000 evaluations, and the bodies whose own
        // signature carries __<Unloadable> are the callers of 77.7% of the residual __<>MHC /
        // __<>DynamicBinder__ dispatch pool (itself 11.673% of the client render thread).
        internal static readonly bool LazyUnloadableSig = SafeGetEnvironmentVariable("IKVM_LAZY_SIG") == "1";

        // wasm fork (R354): count an unloadable class OPERAND (checkcast/instanceof/anewarray/multianewarray) as a
        // dynamic dependency, so the method gets a generation 2 that binds it. Default ON; "0" restores the old
        // candidacy, for the A/B.
        internal static readonly bool LazyOperandDeps = SafeGetEnvironmentVariable("IKVM_LAZY_OPERANDS") != "0";

        // wasm fork (plan typed-spindle P1-0): count an unloadable FIELD TYPE (get/putfield, get/putstatic) as a dynamic
        // dependency (not for bridge/synthetic methods; callee return types were tried and faulted -- compiler.cs). aaload/aastore on such a value compile to the
        // dynamic form (DynamicAaload/DynamicAastore) from the verifier's stack type -- a checkcast is dynamic only by its own operand -- which
        // no constant-pool operand names, so the method was never a relink candidate and kept generation 1 forever --
        // DataTracker.getEntry's entries-array read is the hot case.
        // Also gates the half that lets generation 2 pay (MethodAnalyzer.GetRetypedFieldType): the analyzer typed a field
        // load with the FIELD's link-time type, which stays unloadable for the class's life, so generation 2 re-emitted the
        // same dynamic forms. That half applies to every compile, not only relinks. Measured alone, stores -9.1% on the
        // Minecraft server tick (R461); with the runtime's compact virtual sites and strict tier 2, part of a bundle that
        // read -3.7% cycles / -4.2% instructions, non-overlapping (R466). "0" is the A/B.
        internal static readonly bool LazyTypeDeps = SafeGetEnvironmentVariable("IKVM_LAZY_TYPEDEPS") != "0";

        // wasm fork (plan2x F7a): count an unloadable PARAMETER type as a relink dependency. A method whose only
        // unloadable is its own signature was refused as `nodyn`, and so kept generation 1's entry check -- the full
        // dynamic cast chain (DynamicCastResolve + InstanceCheckOf.IsInstanceOf) on every unloadable parameter on
        // EVERY call -- for the life of the process, while generation 2 emits one checkcast (Compiler,
        // TryResolveForEntryCheck). The population is 2,744 of 34,497 nodyn methods (b2 g1); all admit, and clean
        // generation-2 bodies rise 4,148 -> 4,892. Shipped with the runtime's wave-1 set (-5.3% server-tick cycles
        // as one set, R382); "0" is the A/B. LazyBodyStats.NoDynSig counts the population either way.
        internal static readonly bool LazySigDeps = SafeGetEnvironmentVariable("IKVM_LAZY_SIGDEPS") != "0";

        // wasm fork (plan2x F7e): drop the 8-parameter candidacy cap for the IN-PLACE path. The cap exists for the
        // delegate path's fixed-arity delegate types; the in-place scratch method takes the real method's signature.
        // 265 in-place rejections per run -> 0 (b2 g1). Shipped with the runtime's wave-1 set (R382); "0" is the A/B.
        internal static readonly bool LazyInPlaceArity = SafeGetEnvironmentVariable("IKVM_LAZY_ARITY") != "0";

        // wasm fork (W3, R408): a PRIVILEGED invoke -- an anonymous class (the stock InnerClassLambdaMetafactory's
        // `X$$Lambda$N`, built when IKVM's own LambdaMetafactory.Emit refuses) calling its HOST's private member, rewritten
        // by MethodAnalyzer.PatchInvoke -- calls the resolved method directly instead of through the dynamic binder, whose
        // InvokeSpecial kind has no direct-delegate case (ByteCodeHelper.TryCreateDirectBinderDelegate) and so runs four
        // delegate hops plus LambdaForm frames per lambda call. Sound ONLY because this mono fork skips method access
        // checks (metadata/class.c mono_method_can_access_method returns TRUE); on a stock runtime the call would throw
        // MethodAccessException. Constructors and CallerID methods keep the binder. Minecraft server tick (plan2x/py b t t b):
        // instructions -2.6%, calls -5.0% (both disjoint), cycles -1.4% (overlapping). "0" is the A/B.
        internal static readonly bool PrivilegedDirect = SafeGetEnvironmentVariable("IKVM_PRIVILEGED_DIRECT") != "0";

        // wasm fork (R412): install a relinked interface DEFAULT method's body on its static `<default>` helper, not on the
        // abstract interface method (ByteCodeHelper.RelinkTarget). Without it every default-method relink is counted
        // InPlaceOk and changes nothing. Minecraft server tick (plan2x/qb b d d b): cycles -3.3%, instructions -2.3%, calls
        // -2.3%, all disjoint. "0" is the A/B.
        internal static readonly bool LazyDefaultImpl = SafeGetEnvironmentVariable("IKVM_LAZY_DEFAULT_IMPL") != "0";

        // wasm fork (IKVM_INDY_DIRECT): link an invokedynamic whose bootstrap is LambdaMetafactory.metafactory/altMetafactory
        // -- a site IKVM's own LambdaMetafactory.Emit refused, which therefore runs the stock InnerClassLambdaMetafactory --
        // to a DIRECT target instead of to the CallSite's MethodHandle (ByteCodeHelper.TryCreateDirectLambdaCallSite). That
        // CallSite is a ConstantCallSite, so its target can never change: a capturing lambda's target is a DirectMethodHandle
        // to the spun class's static get$Lambda, which is called directly; a non-capturing lambda's is
        // MethodHandles.constant(samBase, inst), which becomes a delegate returning inst. The lambda class and instance are
        // the ones the bootstrap produced, so identity is unchanged. Without it every lambda creation runs the chain
        // InvokeExact -> [PairwiseConvert ->] DMH.invokeStatic or BMH.reinvoke -> linkToStatic -> MemberName thunk.
        // Decided when the bootstrap stub is EMITTED, so it reaches classes compiled after it is set. Minecraft server
        // tick (R472, j2d/s16b + s21, k i p | p i k | k p i): cycles -3.4%, instructions -4.3%, calls -6.6%, disjoint;
        // peak VmData +72 MiB (each direct site costs a DynamicMethod). "0" is the A/B. The [IKVM indy] line counts the
        // sites compiled for it and the direct targets installed.
        internal static readonly bool IndyDirect = SafeGetEnvironmentVariable("IKVM_INDY_DIRECT") != "0";

        // wasm fork (plan swirling-moore S1): sun.misc.Unsafe getObject/putObject read and write a reference field in place
        // (Unsafe.cs, FieldKindRefObject / FieldKindRefTyped) instead of through the reflective field accessor; putObject
        // only into a field whose CLR type is object, and both only after a receiver-type check. Aimed at LockSupport.park's
        // setBlocker (1.3 M instructions per server tick in R461, before park went native); on the Minecraft server tick it
        // measured as nothing (R478, j2d/w4, u s s u s u). "1" enables.
        internal static readonly bool UnsafeRefFast = SafeGetEnvironmentVariable("IKVM_UNSAFE_REF") == "1";

        // wasm fork (IKVM_LAZY_PARTIAL): after this many unanswered dependency probes, relink an in-place candidate anyway
        // and let generation 2 keep the dynamic form for references to the dependencies that are STILL unpublished
        // (ByteCodeHelper.RelinkBody, FlushRelinkBatch). R357's stuck entries wait on classes referenced only on error
        // paths -- JsonSyntaxException, CrashException (net.minecraft.class_148), CancellationException -- which never load,
        // so the all-dependencies gate never opens and every other dynamic reference in the method stays dynamic for the
        // life of the process. A generation 2 is admitted only if each __dynamic_* reference it still holds names one of
        // the entry's own unpublished dependencies (Compiler.DynamicReferencesOnlyTo); anything else declines as before. Not
        // for an entry with an unpublished PARAMETER type, whose generation 2 would load it (HasUnpublishedParameterType).
        // 0 is off. The count is in RelinkBody's probes, not calls: 64 is the `stuck` line's threshold and, on the capped
        // retry schedule (1, 4, 16, then 64 calls per probe), ~3,900 generation-1 calls. A small value relinks before a
        // hot dependency has had time to load, and that dependency then stays dynamic for good.
        internal static readonly int LazyPartialProbes = ParseInt(SafeGetEnvironmentVariable("IKVM_LAZY_PARTIAL"), 0);

        // wasm fork (IKVM_LAZY_LOAD_STUCK, plan swirling-moore S4): after this many unanswered dependency probes, ask the
        // relink drain to LOAD the entry's unpublished dependencies -- through the entry's own loader, without initializing
        // them -- so the all-dependencies gate opens and the relink is a full one. The alternative to IKVM_LAZY_PARTIAL for
        // the same stuck entries: Entity.move (class_1297.method_5784) waits on CrashException (class_148), which only its
        // catch handler names, and stays generation 1 with its __<>MHC stubs (R478). Java allows a class to be loaded
        // before its first active use; a failed load is swallowed and the entry keeps waiting as before. One request per
        // entry. 0 is off; the count is in probes, as for IKVM_LAZY_PARTIAL. Measured (R479, j2d/w6): at 64, +1,190
        // generation-2 bodies (+23%) and both Entity.move and MinecraftClient.render relink, but the server tick and the
        // client frame do not move -- the stubs it removes are ~1% of tick calls.
        internal static readonly int LazyLoadStuckProbes = ParseInt(SafeGetEnvironmentVariable("IKVM_LAZY_LOAD_STUCK"), 0);

        // Make every eligible method a candidate, not just those with dynamic references. Diagnostic: it turns
        // the trampoline cost on for bodies that have nothing to gain, which is how the indirection's own cost
        // was measured.
        internal static readonly bool LazyAll = SafeGetEnvironmentVariable("IKVM_LAZY_ALL") == "1";

        // Report each failing cast with its source and target type, for locating an intermittent
        // Arg_InvalidCastException that lives in the MethodHandle PairwiseConvert/castReference path.
        internal static readonly bool TraceCastFailures = SafeGetEnvironmentVariable("IKVM_TRACE_CASTFAIL") != null;

        // Per-method tracing of the lazy-body path: candidacy, recompile attempt, and outcome.
        internal static readonly bool TraceLazyBodies = SafeGetEnvironmentVariable("IKVM_TRACE_LAZY_BODIES") != null;

        // Traces the dynamic binder and the residual __dynamic_* reference population respectively.
        internal static readonly bool TraceBinder = SafeGetEnvironmentVariable("IKVM_TRACE_BINDER") != null;
        internal static readonly bool TraceDynRefs = SafeGetEnvironmentVariable("IKVM_TRACE_DYNREFS") != null;
#endif

        /// <summary>
        /// Ensures the JVM is initialized.
        /// </summary>
        internal static void Init()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (initialized == false)
            {
                lock (syncRoot)
                {
                    if (initialized == false)
                    {
                        initialized = true;

                        // always required
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.String).TypeHandle);

                        // initialize java_lang.System (needed before creating the thread)
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.System).TypeHandle);

                        // initialize thread groups
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.ThreadGroup).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.Thread).TypeHandle);
                        GC.KeepAlive(SystemThreadGroup);
                        GC.KeepAlive(MainThreadGroup);

                        // the VM creates & returns objects of this class
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.Class).TypeHandle);

                        // the VM preresolves methods to these classes
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.reflect.Method).TypeHandle);

                        // ensure the System class is initialized
                        Internal.SystemAccessor.InvokeInitializeSystemClass();

                        // should be done before System, but that fails for now
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.@ref.Finalizer).TypeHandle);

                        // initialize certain classes after System properties are available
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.OutOfMemoryError).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.NullPointerException).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.ClassCastException).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.ArrayStoreException).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.ArithmeticException).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.StackOverflowError).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.IllegalMonitorStateException).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.IllegalArgumentException).TypeHandle);

                        // the static initializer of java.lang.Compiler tries to read property "java.compiler" and read & write property "java.vm.info"
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.Compiler).TypeHandle);

                        // initialize some JSR292 core classes to avoid deadlock during class loading
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MemberName).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MethodHandle).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MethodHandleNatives).TypeHandle);
                    }
                }
            }
#endif
        }

#if !IMPORTER && !EXPORTER

        internal static bool EmitSymbols
        {
            get
            {
                if (emitSymbols == 0)
                {
                    var state = 2;

#if NETFRAMEWORK
                    // check app.config on Framework
                    if (string.Equals(System.Configuration.ConfigurationManager.AppSettings["ikvm-emit-symbols"] ?? "", "true", StringComparison.OrdinalIgnoreCase))
                        state = 1;
#endif

                    // respect the IKVM_EMIT_SYMBOLs environmental variable
                    if (string.Equals(Environment.GetEnvironmentVariable("IKVM_EMIT_SYMBOLS") ?? "", "true", StringComparison.OrdinalIgnoreCase))
                        state = 1;

                    // by default enable symbols if a debugger is attached
                    if (state == 2 && Debugger.IsAttached)
                        state = 1;

                    // make sure we only set the value once, because it isn't allowed to changed as that could cause
                    // the compiler to try emitting symbols into a ModuleBuilder that doesn't accept them (and would
                    // throw an InvalidOperationException)
                    Interlocked.CompareExchange(ref emitSymbols, state, 0);
                }

                return emitSymbols == 1;
            }
        }

#endif

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

        /// <summary>
        /// Gets the current <see cref="RuntimeContext"/> of the JVM.
        /// </summary>
        internal static RuntimeContext Context => Internal.context;

        /// <summary>
        /// Gets the current <see cref="VfsTable"/> of the JVM.
        /// </summary>
        internal static VfsTable Vfs => Internal.vfs;

        /// <summary>
        /// Gets the 'system' thread group.
        /// </summary>
        internal static object SystemThreadGroup => Internal.systemThreadGroup.Value;

        /// <summary>
        /// Gets the 'main' thread group.
        /// </summary>
        internal static object MainThreadGroup => Internal.mainThreadGroup.Value;

        /// <summary>
        /// Thread-local storage of queued exception to be thrown.
        /// </summary>
        [ThreadStatic]
        static Exception pendingException;

        /// <summary>
        /// Gets the currently pending exception for the current thread.
        /// </summary>
        internal static Exception GetPendingException()
        {
            return pendingException;
        }

        /// <summary>
        /// Sets the pending exception for the current thread.
        /// </summary>
        /// <param name="e"></param>
        internal static void SetPendingException(Exception e)
        {
			//Console.WriteLine($"[IKVM internal] SetPendingException {e}");
            pendingException = e != null ? ikvm.runtime.Util.mapException(e) : null;
        }

        /// <summary>
        /// Throws a pending exception for the current thread.
        /// </summary>
        internal static void ThrowPendingException()
        {
            var e = GetPendingException();
            if (e is not null)
            {
                pendingException = null;
                ExceptionDispatchInfo.Capture(e).Throw();
                throw null;
            }
        }

#endif

        /// <summary>
        /// Gets an environmental variable.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        internal static string SafeGetEnvironmentVariable(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch (SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses an integer knob value, falling back to <paramref name="dflt"/> for null/unparseable input.
        /// </summary>
        static int ParseInt(string s, int dflt)
        {
            return int.TryParse(s, out var v) ? v : dflt;
        }

        internal static string MangleResourceName(string name)
        {
            // FXBUG there really shouldn't be any need to mangle the resource names,
            // but in order for ILDASM/ILASM round tripping to work reliably, we have
            // to make sure that we don't produce resource names that'll cause ILDASM
            // to generate invalid filenames.
            var sb = new ValueStringBuilder(name.Length + 6);
            sb.Append("ikvm__");

            foreach (var c in name)
            {
                if ("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-+.()$#@~=&{}[]0123456789`".IndexOf(c) != -1)
                {
                    sb.Append(c);
                }
                else if (c == '/')
                {
                    sb.Append('!');
                }
                else
                {
                    sb.Append('%');
                    sb.Append(string.Format("{0:X4}", (int)c));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Returns a hash code for the specified string value which is guarenteed to always be the same.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        internal static int PersistableHash(string str)
        {
            var key = 1u;

            foreach (char c in str)
            {
                key += c;
                key += (key << 12);
                key ^= (key >> 22);
                key += (key << 4);
                key ^= (key >> 9);
                key += (key << 10);
                key ^= (key >> 2);
                key += (key << 7);
                key ^= (key >> 12);
            }

            return (int)key;
        }

        internal static object Box(object val)
        {
#if IMPORTER || FIRST_PASS || EXPORTER
            throw new NotImplementedException();
#else
            return val switch
            {
                byte => java.lang.Byte.valueOf((byte)val),
                bool => java.lang.Boolean.valueOf((bool)val),
                short => java.lang.Short.valueOf((short)val),
                char => java.lang.Character.valueOf((char)val),
                int => java.lang.Integer.valueOf((int)val),
                float => java.lang.Float.valueOf((float)val),
                long => java.lang.Long.valueOf((long)val),
                double => java.lang.Double.valueOf((double)val),
                _ => throw new java.lang.IllegalArgumentException()
            };
#endif
        }

        internal static object Unbox(object val)
        {
#if IMPORTER || FIRST_PASS || EXPORTER
            throw new NotImplementedException();
#else
            return val switch
            {
                java.lang.Byte b => b.byteValue(),
                java.lang.Boolean b1 => b1.booleanValue(),
                java.lang.Short s => s.shortValue(),
                java.lang.Character c => c.charValue(),
                java.lang.Integer i => i.intValue(),
                java.lang.Float f => f.floatValue(),
                java.lang.Long l => l.longValue(),
                java.lang.Double d => d.doubleValue(),
                _ => throw new java.lang.IllegalArgumentException()
            };
#endif
        }

#if !IMPORTER && !EXPORTER

        internal static object NewAnnotation(java.lang.ClassLoader classLoader, object definition)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            java.lang.annotation.Annotation ann = null;
            try
            {
                ann = (java.lang.annotation.Annotation)ikvm.@internal.AnnotationAttributeBase.newAnnotation(classLoader, definition);
            }
            catch (java.lang.TypeNotPresentException)
            {

            }

            if (ann != null && IsRuntimeRetained(ann.annotationType()))
                return ann;

            return null;
#endif
        }

#if !FIRST_PASS

        /// <summary>
        /// wasm fork: annotation types whose retention this thread is resolving through AnnotationType.getInstance in
        /// IsRuntimeRetained, innermost last.
        /// </summary>
        [ThreadStatic]
        static System.Collections.Generic.List<java.lang.Class> retentionPending;

        /// <summary>
        /// wasm fork: whether annotations of this type are retained at runtime, without recursing forever on a type that
        /// reaches itself through its own meta-annotations.
        /// </summary>
        /// <remarks>
        /// AnnotationType's constructor asks the type for its @Retention through getDeclaredAnnotation, which decodes ALL
        /// of the type's annotations and brings each one back through NewAnnotation, and getInstance caches only once that
        /// constructor returns. So a type annotated with itself recursed until the stack ran out: kotlin.Metadata, which
        /// every Kotlin class carries (kotlin-stdlib also has Target, MustBeDocumented and Retention in cycles with it),
        /// and groovy's Incubating. OpenJDK's constructor parses only Retention and Inherited out of the raw bytes, so it
        /// never re-enters. When this thread is already resolving the type, read its @Retention out of its raw
        /// definitions instead; every other call is unchanged.
        /// </remarks>
        static bool IsRuntimeRetained(java.lang.Class annotationType)
        {
            var pending = retentionPending ??= new System.Collections.Generic.List<java.lang.Class>();
            foreach (var c in pending)
                if (ReferenceEquals(c, annotationType))
                    return RawRetentionIsRuntime(annotationType);

            pending.Add(annotationType);
            try
            {
                return sun.reflect.annotation.AnnotationType.getInstance(annotationType).retention() == java.lang.annotation.RetentionPolicy.RUNTIME;
            }
            finally
            {
                pending.RemoveAt(pending.Count - 1);
            }
        }

        /// <summary>
        /// The type's own @java.lang.annotation.Retention read from its undecoded definitions (RetentionPolicy.CLASS when
        /// it has none, as AnnotationType has it). A type whose definitions are not available here keeps the annotation:
        /// the runtime reads only RuntimeVisibleAnnotations (ClassFile), which a compiler writes for RUNTIME retention.
        /// </summary>
        static bool RawRetentionIsRuntime(java.lang.Class annotationType)
        {
            if (RuntimeJavaType.FromClass(annotationType) is not RuntimeByteCodeJavaType tw)
                return true;

            var definitions = tw.GetRawDeclaredAnnotations();
            if (definitions == null)
                return true;

            foreach (var d in definitions)
            {
                // [1] is the type's descriptor until AnnotationAttributeBase.newAnnotation resolves it in place to the Class
                if (d is not object[] def || def.Length < 2)
                    continue;
                if (def[1] is java.lang.Class c ? c.getName() != "java.lang.annotation.Retention" : !"Ljava/lang/annotation/Retention;".Equals(def[1]))
                    continue;

                for (int i = 2; i + 1 < def.Length; i += 2)
                    if ("value".Equals(def[i]) && def[i + 1] is object[] e && e.Length == 3)
                        return "RUNTIME".Equals(e[2]);

                return false;
            }

            return false;
        }

#endif

#endif

#if !IMPORTER && !EXPORTER

        internal static object NewAnnotationElementValue(java.lang.ClassLoader classLoader, java.lang.Class expectedClass, object definition)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return ikvm.@internal.AnnotationAttributeBase.decodeElementValue(definition, expectedClass, classLoader);
            }
            catch (java.lang.IllegalAccessException)
            {
                // TODO this shouldn't be here
                return null;
            }
#endif
        }

#endif

    }

}
