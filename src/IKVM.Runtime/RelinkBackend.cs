using System;
using System.IO;
using System.Reflection;

namespace IKVM.Runtime
{

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

    /// <summary>
    /// wasm fork: the in-place relink (JVM.LazyInPlace) on a runtime that cannot swap a method's IL body, through an
    /// external assembly named by IKVM_RELINK_BACKEND (JVM.LazyRelinkBackend).
    /// </summary>
    /// <remarks>
    /// Mono's swap (LibIkvm.ReplaceMethodBody) always wins: the backend is loaded only where that reports unsupported,
    /// i.e. CoreCLR, where without one every relink candidate keeps generation 1 for the life of the process. A backend
    /// is an assembly holding a public static class <c>IKVM.Runtime.Relink.Backend</c> with
    /// <list type="bullet">
    /// <item><c>string Initialize()</c>: null if it can install on this runtime, else why not;</item>
    /// <item><c>int Replace(MethodBase target, MethodBase source)</c>: makes every later call of <c>target</c> run
    /// <c>source</c> (a static method on a baked scratch type of the same module, taking an instance target's receiver
    /// first). LibIkvm.ReplaceMethodBody's codes: 1 installed, 0 too late, -2 unsupported, other values refused. It may
    /// throw; FlushRelinkBatch counts that as an install failure and the method keeps generation 1.</item>
    /// </list>
    /// Generation 2 runs as itself, not as the target, which mono's swap never had to care about:
    /// <list type="bullet">
    /// <item>an instance <c>synchronized</c> target locks in its prolog (MethodImplAttributes.Synchronized), so generation 2
    /// locks its receiver in IL when a backend is active (FinishContext.LocksReceiver);</item>
    /// <item>CoreCLR checks CLR access, and the scratch type is not the Java class: a protected member of a superclass in
    /// another assembly fails with FieldAccessException/MethodAccessException unless the host grants the dynamic
    /// assemblies [IgnoresAccessChecksTo] (the CoreCLR arm's startup hook does); mono checks none;</item>
    /// <item>stack frames name the scratch method (CallerID methods are declined before this point).</item>
    /// </list>
    /// It is a separate assembly so IKVM.Runtime takes no dependency on one (the MonoMod backend,
    /// src/IKVM.Runtime.Relink.MonoMod, brings MonoMod.Core, its Iced and Mono.Cecil). Loaded with Assembly.LoadFrom, so
    /// its dependencies resolve from its own directory.
    /// </remarks>
    static class RelinkBackend
    {

        static readonly Func<MethodBase, MethodBase, int> replace = Load(out Name);

        /// <summary>The loaded backend's file name, or why there is none; printed with the lazybody statistics.</summary>
        internal static readonly string Name;

        /// <summary>
        /// True when this runtime has no IL body swap and a backend loaded and initialised. Decided once.
        /// </summary>
        internal static bool Active => replace != null;

        internal static int Replace(MethodBase target, MethodBase source) => replace(target, source);

        static Func<MethodBase, MethodBase, int> Load(out string name)
        {
            var path = JVM.LazyRelinkBackend;
            if (string.IsNullOrEmpty(path))
            {
                name = "none";
                return null;
            }

            if (LibIkvm.CanReplaceMethodBody)
            {
                name = "mono (IKVM_RELINK_BACKEND ignored)";
                return null;
            }

            try
            {
                var type = Assembly.LoadFrom(path).GetType("IKVM.Runtime.Relink.Backend", true);
                var why = (string)type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null).Invoke(null, null);
                if (why != null)
                {
                    name = Path.GetFileName(path) + " refused: " + why;
                    Console.Error.WriteLine("[IKVM lazybody] relink backend " + name);
                    return null;
                }

                var m = type.GetMethod("Replace", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(MethodBase), typeof(MethodBase) }, null);
                var f = (Func<MethodBase, MethodBase, int>)m.CreateDelegate(typeof(Func<MethodBase, MethodBase, int>));
                name = Path.GetFileName(path);
                Console.Error.WriteLine("[IKVM lazybody] relink backend " + name + " loaded");
                return f;
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException t && t.InnerException != null)
                    e = t.InnerException;
                name = Path.GetFileName(path) + " failed: " + e.GetType().Name + ": " + e.Message;
                Console.Error.WriteLine("[IKVM lazybody] relink backend " + name);
                return null;
            }
        }

    }

#endif

}
