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
        /// The Java SE feature release this build implements: the version whose class library it runs, and whose
        /// multi-release JAR entries ikvmc picks.
        /// </summary>
        internal const int JavaFeatureVersion = 25;

        /// <summary>
        /// The class file major version of that release: 69, for Java 25.
        /// </summary>
        internal const int ClassFileMajorVersion = JavaFeatureVersion + 44;

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
        // Any value but "0" enables it ("0" is off, so a harness can take it away with ?wj.IKVM_LAZY_CLASS_LOADING=0).
        internal static readonly bool LazyClassLoading = SafeGetEnvironmentVariable("IKVM_LAZY_CLASS_LOADING") is { } lazyClassLoading && lazyClassLoading != "0";
        // logs every invocation of a Java class loader's loadClass with the loader's type, for
        // diffing load order/set against a real JVM run with -verbose:class
        internal static readonly bool TraceLoadClass = SafeGetEnvironmentVariable("IKVM_TRACE_LOADCLASS") != null;
        // logs every class a Java class loader defines, with the loader and the type whose verifier loads were being
        // run at the time, for diffing definition order against a real JVM run with -Xlog:class+load
        internal static readonly bool TraceDefine = SafeGetEnvironmentVariable("IKVM_TRACE_DEFINE") != null;

        // wasm fork: HotSpot's verifier loads classes while it links a class (assignability checks of call arguments,
        // receivers, field values, returns, athrow and catch types); lazy linking defers every one of them to the first
        // execution of the instruction, which Fabric mods can observe (a mixin plugin that references a nested class of
        // its own mixin package is only allowed to load it while its config is still pending). "1" records them while a
        // lazily linked class finishes and loads them at the class's HotSpot link points (VerifierLoads), "shadow"
        // records and logs them without loading, "0" turns it off.
        internal static readonly string VerifyLoadsMode = SafeGetEnvironmentVariable("IKVM_VERIFY_LOADS") ?? "1";
        internal static readonly bool VerifyLoads = VerifyLoadsMode != "0";
        internal static readonly bool VerifyLoadsShadow = VerifyLoadsMode == "shadow";
        // whether a lazily linked class's static constructor runs its pending verifier loads first, which covers the
        // initializations that reach it through directly bound code rather than through RunClassInit
        internal static readonly bool VerifyLoadsCctor = SafeGetEnvironmentVariable("IKVM_VERIFY_LOADS_CCTOR") != "0";
        internal static readonly bool TraceVerifyLoads = SafeGetEnvironmentVariable("IKVM_TRACE_VERIFYLOADS") != null;

        // wasm fork: a failed resolution of a class, MethodType or MethodHandle reference is remembered per referencing
        // class and rethrown on every later attempt, as JVMS §5.4.3 requires, instead of calling the loader again.
        internal static readonly bool ResolutionErrors = SafeGetEnvironmentVariable("IKVM_RESOLUTION_ERRORS") != "0";
        internal static readonly bool TraceResolutionErrors = SafeGetEnvironmentVariable("IKVM_TRACE_RESOLUTION_ERRORS") != null;

        // wasm fork: initializing a class first initializes its superinterfaces that declare default methods (JLS 12.4.2
        // step 7). "0" restores initializing them only at their first default method call.
        internal static readonly bool InitSuperInterfaces = SafeGetEnvironmentVariable("IKVM_INIT_SUPERINTERFACES") != "0";

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
        /// IKVM_REPARSE_BYTES (wasm fork, R513): a ReparseSource keeps the class's raw bytes -- the private copy the defining
        /// class loader made -- and decodes them again when a recompile reparses, instead of keeping the decoded reader. A
        /// lazy entry whose method never runs is never released, so its class's decoded constant pool (ConstantData[],
        /// Utf8ConstantData[] with one string per UTF-8 constant, Attribute[]) stayed alive for good: ~80 MB on the 26.2
        /// world-load heap (R512). "0" keeps the decoded reader, as before.
        /// </summary>
        internal static readonly bool ReparseBytes = SafeGetEnvironmentVariable("IKVM_REPARSE_BYTES") != "0";

        /// <summary>
        /// IKVM_INTERN_LDSTR (wasm fork, R513): every ldstr operand CodeEmitter emits is interned. In a dynamic assembly mono's
        /// ldstr returns exactly the object handed to the ILGenerator, which for a Java string literal was that class's own
        /// copy of the UTF-8 constant -- so equal literals in two classes were two objects (Java requires one), and every
        /// interpreter/JIT literal table and the dynamic image's token table kept its own copy (~30 MB of duplicate strings
        /// referenced from native memory on the 26.2 world-load heap). "0" emits the string as given, as before.
        /// </summary>
        internal static readonly bool InternLdstr = SafeGetEnvironmentVariable("IKVM_INTERN_LDSTR") != "0";

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
        // generation 1 (with this off it would have taken the delegate path instead), unless
        // IKVM_RELINK_BACKEND supplies an installer (below).
        internal static readonly bool LazyInPlace = SafeGetEnvironmentVariable("IKVM_LAZY_INPLACE") == "1";

        // wasm fork: the path of an assembly that installs in-place relinks where the runtime cannot swap an IL body
        // (RelinkBackend; src/IKVM.Runtime.Relink.MonoMod detours generation 1's native code to generation 2's entry
        // point on CoreCLR). Unset by default; ignored where mono's swap works.
        internal static readonly string LazyRelinkBackend = SafeGetEnvironmentVariable("IKVM_RELINK_BACKEND");

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

        // wasm fork (plan v3 L6(b)): the RETURN half of LazyTypeDeps's retyping. A call whose callee's baked signature
        // erased its return type (the callee's class linked before that type loaded) leaves the value unloadable in the
        // caller for good, so every consumer casts it again (tickChunk's sections loop: Array, Object[] and
        // LevelChunkSection at 14-15 k casts a tick each, 2026-11-21 census). With this on, the analyzer types the call's
        // result with the constant pool's now-loadable type and the compiler casts once, right after the call
        // (MethodAnalyzer.GetRetypedReturnType; the same restrictions as fields). Candidacy is untouched: a callee return
        // type still makes no method a relink candidate (compiler.cs, typed-spindle s1's bridge recursion). "1" = on.
        internal static readonly bool LazyRetypeReturns = SafeGetEnvironmentVariable("IKVM_LAZY_RETYPE_RET") == "1";

        // wasm fork: the PARAMETER half of the retyping. A generation-2 body checks each erased (unloadable) parameter once
        // at entry (Compiler, TryResolveForEntryCheck: `ldarg; castclass T; pop`), but the parameter keeps its erased
        // type, so every later use casts it again at its consumer (getfield, call arguments, ...). With this on, a
        // generation-2 compile keeps that entry cast and stores its result in a local typed T, and the analyzer types the
        // parameter's local slot T from entry (MethodAnalyzer's retypedArgs, handed over by the compiler, so the two
        // cannot disagree). Only where T is a class or an array of classes accessible from the method's class (every
        // refusal of TryResolveForEntryCheck stands: ghost, ghost array, value, remapped), never `this`, never a
        // parameter whose local slot the method ever writes, never in a method with jsr. Counted as argRetyped in the
        // [IKVM lazybody] eval line (and argRetyped #N lines). jbox2d, R440 protocol, 2026-11-24 (M4, n=3 a side, on first
        // twice, off first once): 32-63 parameters retyped, and SLOWER, 0.4295-0.4378 ms/step against 0.4152-0.4195 off; unexplained.
        // Default off; "1" = on.
        internal static readonly bool LazyRetypeArgs = SafeGetEnvironmentVariable("IKVM_LAZY_RETYPE_ARGS") == "1";

        // wasm fork, AN INSTRUMENT THAT MUST NEVER SHIP (plan v2 lever 1a, findings M26): the read-side casts of erased
        // CLASS types -- retyped field reads, retyped call results, the generation-2 parameter entry check -- become
        // Unsafe.As<T> (mono: a move typed T, no check). IKVM verifies no store into an erased slot (InstructionState
        // PopTypeImpl accepts it when either side is unloadable), so these casts are today the only check of what such a
        // slot holds: with this on, invalid bytecode corrupts memory instead of throwing. It exists to measure the cast
        // part of lazy linking's residue on bytecode known to be valid (the benchmarks). Interfaces, ghosts and remapped
        // types keep their casts. A bit mask: 1 field reads, 2 call results, 4 parameter entry checks; "1" alone was all
        // three before 2026-11-24 evening, so 7 is that. 0 / unset = off.
        internal static readonly int InstrErasedUnchecked = int.TryParse(SafeGetEnvironmentVariable("IKVM_INSTR_ERASED_UNCHECKED"), out var ieu) ? ieu : 0;

        // wasm fork (plan v2 lever 1b, findings M26/M27): HotSpot's invariant for erased slots, so their reads need no cast.
        // HotSpot checks a typed slot only at the store, in the verifier of the STORING class (loading the slot's class
        // through that class's loader); IKVM's verifier accepts a store into an erased slot unchecked, which is why every
        // read of one casts. Bit 1, FIELDS: every putfield / putstatic into a field whose baked type is erased either is
        // proven at compile time (writer and field share a loader, both types loaded, value assignable, or the target is an
        // interface / Object, which HotSpot does not check either) or checks the value at the store -- a castclass, or
        // ByteCodeHelper.CheckErasedFieldStore resolving the type through the DECLARING class's loader (cross-loader writers,
        // types still unloaded at compile time); generation 1's direct binder for a dynamic putfield casts to the type when
        // the declaring loader has it loaded (LinkNoLoad: no new load), else declines to the method-handle path, whose
        // DirectMethodHandle checks. Reflection and VarHandle already check; Unsafe and JNI do not, as in HotSpot. Then the
        // retyped read of an erased class-typed field (same loader as its declaring class) is Unsafe.As<T>. No class is
        // loaded earlier than today. Counted as vstore= in the [IKVM lazybody] eval line. 0 / unset = off.
        internal static readonly int LazyVerifyStores = int.TryParse(SafeGetEnvironmentVariable("IKVM_LAZY_VERIFY_STORES"), out var lvs) ? lvs : 0;

        // wasm fork (plan v3 L6(c)): RuntimeJavaType.InstanceCheckOf<T> remembers the exact type of the last instance that
        // passed, so a dynamic cast or instanceof against a cached check answers a repeated type with two loads instead
        // of the shared-generic `obj is T`. "1" = on.
        internal static readonly bool InstanceCheckMemo = SafeGetEnvironmentVariable("IKVM_INSTANCECHECK_MEMO") == "1";

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
        // measured as nothing (R478, j2d/w4, u s s u s u). "1" enables. That was the JDK 8 class library, whose setBlocker
        // called putObject; JDK 25's goes through putReferenceOpaque -> putObjectVolatile, which this does not cover (see
        // ParkFast).
        internal static readonly bool UnsafeRefFast = SafeGetEnvironmentVariable("IKVM_UNSAFE_REF") == "1";

        // wasm fork (IKVM_UNSAFE_FAST, 2026-11-16): two cuts in the object form of Unsafe's scalar get/put.
        // Bit 1: an access that starts at a primitive field but is not that field's own kind or size -- JOML's
        // MemUtilUnsafe reads a Matrix4f eight bytes (two float fields) at a time with getLong(m, m00 + 8*i) -- is done in
        // place when the fields it covers lie in the CLR object exactly as in the encoded offset space (checked once per
        // start field, Unsafe.cs ObjectFieldOffsetInfo.GetRawSpanOffset), instead of byte by byte through each field's
        // reflective accessor (~2,700 instructions a getLong; 23% of the 26.2 render thread's instructions, 2026-11-16).
        // Bit 2: a call site of sun.misc / jdk.internal.misc Unsafe.get/put<T>(Object, long) whose base is not provably
        // null calls the extern directly (intrinsics.cs, ByteCodeHelper.UnsafeGet*/UnsafePut*), skipping sun.misc.Unsafe's
        // Java wrapper (and its beforeMemoryAccess warning check), the native stub and the jdk.internal forwarder; it is
        // decided when the site is compiled. Default 3; "0" is the A/B.
        internal static readonly int UnsafeFast = ParseInt(SafeGetEnvironmentVariable("IKVM_UNSAFE_FAST"), 3);

        // wasm fork (IKVM_RECORD_UNLOADABLE, 2026-11-17): a record's ObjectMethods.bootstrap site (javac's toString /
        // hashCode / equals) is compiled inline (compiler.ObjectMethods.cs) even when a component's type is still unloadable
        // -- under lazy class loading, any component class not yet loaded when the record links. Declining it ran the JDK's
        // MethodHandle chain on every call: LambdaForm invokers, delegate dispatch, an Unsafe getReference per component and
        // DirectMethodHandle's class-init check; on 26.2's world-generation workers that chain (DensityFunction records
        // hashed as map keys) is most of the 12-14% delegate dispatch (findings 2026-11-15, levers2). The inline code is the
        // JDK's formula (hashCombiner x*31+h, Objects.hashCode / equals, String.valueOf), so a record's results do not change
        // with the path. `[IKVM records]` counts sites and declines. Default 1; "0" is the A/B (decided when a site compiles).
        internal static readonly bool RecordUnloadable = SafeGetEnvironmentVariable("IKVM_RECORD_UNLOADABLE") != "0";

        // wasm fork (IKVM_MH_INIT_DONE, 2026-11-17): Unsafe.shouldBeInitialized answers false once the class's static
        // initializer has FINISHED (mono's vtable->initialized, LibIkvm.TypeInitialized), where it answered
        // HasStaticInitializer -- a fixed property, so DirectMethodHandle.checkInitialized never succeeded, updateForm never
        // dropped the invokeStaticInit barrier, and every static call through a method handle on a class with a <clinit>
        // ran ensureClassInitialized -> Finish -> RunClassInit -> RunClassConstructor (3.4% of a 26.2 world-generation
        // worker, findings 2026-11-15 levers2). ensureClassInitialized takes the same answer as an early exit. A class
        // whose initializer failed or is still running keeps the barrier. Default 1; "0" is the A/B.
        internal static readonly bool MhInitDone = SafeGetEnvironmentVariable("IKVM_MH_INIT_DONE") != "0";

        // wasm fork (IKVM_GETCLASS_FAST, 2026-11-17): Object.getClass() asks the ghost-tag table only for an array whose
        // innermost element type is object -- the only arrays a ghost tag can be on -- and only after the type cache
        // (Util.GetTypeWrapperFromObject); int[], String[], Foo[] skip it. And an array class caches its component type
        // (RuntimeJavaType.ElementTypeWrapper), which getComponentType -- Arrays.copyOf(T[]), toArray(T[]) -- recomputed
        // with a substring and a locked class-loader lookup on every call. Default 1; "0" is the A/B.
        internal static readonly bool GetClassFast = SafeGetEnvironmentVariable("IKVM_GETCLASS_FAST") != "0";

        /// <summary>
        /// IKVM_GETCLASS_INLINE=1: Object.getClass() answers from the direct-mapped type cache's own Class in an
        /// AggressiveInlining fast path (Util.getClassFromObject), so a JIT that inlines it makes no call; the slow path
        /// fills the entry's Class. On by default since 2026-11-22 (M4 findings M3: a bundle with CAST_MEMO, ATOMIC_INLINE
        /// and the wasm JIT's cast / class-record / inline knobs, mctick -4.8% cycles a tick); "0": every getClass is a
        /// call into GetTypeWrapperFromObject, as before. Plan abstract-hatching-stearns C1c.
        /// </summary>
        internal static readonly bool GetClassInline = SafeGetEnvironmentVariable("IKVM_GETCLASS_INLINE") != "0";

        /// <summary>
        /// IKVM_CAST_MEMO=1: in a method that reads an erased (retyped) field more than once, each read's cast compares
        /// the value with the last value this activation cast for that field and skips the castclass when they are the
        /// same reference (Compiler.EmitRetypedFieldCast). Exact: a hit is a reference already checked against the same
        /// type; a racing writer's new value misses and is checked. mctick: ~141 k of ~950 k checks a tick re-check a
        /// re-read field (cast census v3, 2026-11-21). Plan abstract-hatching-stearns L1. On by default since 2026-11-22
        /// (the M3 bundle, IKVM_GETCLASS_INLINE); "0" turns it off.
        /// </summary>
        internal static readonly bool CastMemo = SafeGetEnvironmentVariable("IKVM_CAST_MEMO") != "0";

        /// <summary>
        /// IKVM_ATOMIC_INLINE=1: ByteCodeHelper's int / long / reference VolatileRead, VolatileWrite and CompareAndSwap do
        /// the atomic in their own (AggressiveInlining) body, so a JIT that inlines them emits one atomic; "0": they call
        /// their NoInlining twins, as before. On by default since 2026-11-22 (the M3 bundle, IKVM_GETCLASS_INLINE), with
        /// MONO_WASM_JIT_ATOMIC_I8 now on by default too: off there, an inlined 64-bit atomic is an icall through the
        /// interpreter. Plan abstract-hatching-stearns C2.
        /// </summary>
        internal static readonly bool AtomicInline = SafeGetEnvironmentVariable("IKVM_ATOMIC_INLINE") != "0";

        // wasm fork (IKVM_DEFAULT_INLINE, 2026-11-17): a default method whose bytecode is at most this many bytes, with no
        // exception handlers, gets AggressiveInlining on its static <default> helper (RuntimeByteCodeJavaType.FinishContext),
        // so the per-implementor forwarder -- `call <default>m` -- carries the body. Without it the helper is usually over
        // mono's 20-byte inline limit (the lazy-relink hook alone is ~15 IL bytes), and 26.2's LevelHeightAccessor.getMaxY /
        // getMinSectionY family still cost a call each after RECV_CLASS (`<default>` bodies called 9.7 k a server tick,
        // findings 2026-11-15 levers2). 35 is HotSpot's MaxInlineSize; 0 = off. Decided when a class is compiled.
        // DEFAULT 0: as first built (every small helper, relink hook included) it made the forwarders themselves fail to JIT
        // -- 262n server door_shape 133 -> 8.55 M calls in a 60 s window, server tick +26% cycles / +62% instructions, gone
        // with "0" (findings 2026-11-17, build 31 bisect). Now restricted to helpers with no relink hook or lazy trampoline,
        // which is unmeasured.
        internal static readonly int DefaultInline = ParseInt(SafeGetEnvironmentVariable("IKVM_DEFAULT_INLINE"), 0);
        internal static readonly bool UnsafeSpanFast = (UnsafeFast & 1) != 0;
        internal static readonly bool UnsafeCallFast = (UnsafeFast & 2) != 0;

        // wasm fork (IKVM_PARK_FAST, 2026-11-05): the server's idle wait between ticks parks ~211 times a tick
        // (LockSupport.parkNanos(100 us)), and each park paid (a) two setBlocker stores through the reflective
        // Unsafe.putReferenceVolatile path -- a catch-when filter, a Dictionary lookup by offset, a delegate into a
        // DynamicMethod; ~1.86 M instructions a tick -- and (b) a virtual Thread.getId() and two non-blocking P/Invokes with
        // full GC transitions in FutexPark/unpark. "1": LockSupport.setBlocker stores Thread.parkBlocker directly (an
        // IKVM.Java map.xml prologue reading ByteCodeHelper.ParkFast), and FutexPark/unpark key on Thread.tid and use the
        // SuppressGCTransition imports of IKVM_park_seq / IKVM_park_post (LibIkvm). Idle-loop CPU only, not tick work.
        internal static readonly bool ParkFast = SafeGetEnvironmentVariable("IKVM_PARK_FAST") == "1";

        // wasm fork (IKVM_LAZY_SIGLOAD): HotSpot's C2 request. After this many unanswered dependency probes RelinkBody loads
        // the method's SIGNATURE classes -- parameters, return type, array element types -- through the declaring class's
        // loader, without initializing them, failures swallowed: what CompileBroker::compile_method does before every C2
        // compile (Method::load_signature_classes, jdk-25 compileBroker.cpp ~1426, method.cpp ~1764), on the requesting
        // Java thread. Here that is the thread running generation 1, never the relink drain (R514), and never while that
        // thread is inside a class load or definition (a Mixin transform): those defer to the next probe. jbox2d's
        // Contact.update(ContactListener) is the case (R523): the bench passes only null, so generation 1 never loads the
        // parameter's class and the relink waited forever. The count is in probes: 1, 4, 16, then 64 generation-1 calls
        // apart, so 64 is ~3,900 calls (HotSpot's Tier4InvocationThreshold is 5,000). 0 is off.
        internal static readonly int LazySigLoadProbes = ParseInt(SafeGetEnvironmentVariable("IKVM_LAZY_SIGLOAD"), 64);

        // wasm fork (IKVM_LAZY_PARTIAL): after this many unanswered dependency probes -- and after the signature loads
        // above, so a parameter never stays dynamic only because this came first -- relink an in-place candidate anyway
        // and let generation 2 keep the dynamic form for references to the dependencies that are STILL unpublished
        // (ByteCodeHelper.RelinkBody, FlushRelinkBatch): C2's uncommon trap for a class the method's execution has not
        // needed. R357's stuck entries wait on classes referenced only on error paths -- JsonSyntaxException,
        // CrashException (net.minecraft.class_148) -- and jbox2d's on ChainShape (R523), which never load, so the
        // all-dependencies gate never opens. A generation 2 is admitted only if each __dynamic_* reference it still holds
        // names one of the entry's own unpublished dependencies (Compiler.DynamicReferencesOnlyTo); anything else declines.
        // An unpublished PARAMETER's entry check falls back to the dynamic cast (Compiler.TryResolveForEntryCheck returns
        // null under InRelinkDrain), which is not a __dynamic_* opcode. The count is in probes, as above; a small value
        // relinks before a hot dependency has had time to load, and that dependency then stays dynamic for good. 0 is off.
        // R472's two faults with it on predate the heap-corruption fixes (R489, R500).
        internal static readonly int LazyPartialProbes = ParseInt(SafeGetEnvironmentVariable("IKVM_LAZY_PARTIAL"), 64);

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

                        InitClassLibrary();
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
