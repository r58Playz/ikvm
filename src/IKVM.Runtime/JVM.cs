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

        // Make every eligible method a candidate, not just those with dynamic references. Diagnostic: it turns
        // the trampoline cost on for bodies that have nothing to gain, which is how the indirection's own cost
        // was measured.
        internal static readonly bool LazyAll = SafeGetEnvironmentVariable("IKVM_LAZY_ALL") == "1";

        // Allow candidates containing invokedynamic / MethodHandle call sites. Default OFF, and not merely for
        // caution: recompiling such a body silently corrupts data, because FinishContext would Define* fresh
        // call sites, caches and CallerID nested types instead of REFERENCING the ones already baked into the
        // type. Turning this on needs a "recompile mode" in FinishContext first.
        internal static readonly bool LazyMethodHandles = SafeGetEnvironmentVariable("IKVM_LAZY_MH") == "1";

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

            if (ann != null && sun.reflect.annotation.AnnotationType.getInstance(ann.annotationType()).retention() == java.lang.annotation.RetentionPolicy.RUNTIME)
                return ann;

            return null;
#endif
        }

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
