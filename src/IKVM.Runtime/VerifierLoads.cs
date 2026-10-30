/*
  Copyright (C) 2002-2015 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net

*/
using System;
using System.Collections.Generic;
using System.Threading;

namespace IKVM.Runtime
{

    /// <summary>
    /// wasm fork: the class loads HotSpot's verifier performs while it links a class, for classes that link lazily.
    /// </summary>
    /// <remarks>
    /// HotSpot's type-checking verifier resolves classes to decide assignability (VerificationType::
    /// is_reference_assignable_from): the expected type unless it is java.lang.Object or the same name, and, when that
    /// turns out to be a class rather than an interface, the actual type too. It does this for call arguments and
    /// receivers, field values and receivers, returned values, athrow and exception handler catch types, all while the
    /// class links, i.e. before it initializes. Lazy linking (<see cref="LoadMode.LinkLazy"/>) leaves those types
    /// unloadable and IKVM's analyzer accepts them as they are, so the first load moves to the first execution of the
    /// instruction. Fabric mods observe the difference: capsaicin's mixin plugin passes a nested class of its own mixin
    /// package to an ASM method, which HotSpot loads while Mixin instantiates the plugin (config still pending, so
    /// allowed), and IKVM loaded when the plugin first ran (config active, IllegalClassLoadError).
    ///
    /// The analyzer records the pairs while the first generation of each method compiles (never on a relink
    /// recompile), and the class publishes them when it finishes. They are loaded at the points where HotSpot links a
    /// class -- initialization, the static constructor, reflection on its members -- and nowhere else: IKVM finishes
    /// classes at many points HotSpot does not link them (a CLR type resolve, a call site's argument types), and loading
    /// there would be as wrong as loading late. A pair is a subset of what HotSpot loads for valid bytecode, because the
    /// analyzer's types only differ from HotSpot's at merge points, where HotSpot loads the merged types as well.
    /// </remarks>
    static class VerifierLoads
    {

        /// <summary>
        /// A deferred assignability check. A null <see cref="Target"/> is an expected type that was already loaded and
        /// is a class, so only <see cref="Source"/> needs loading; a null <see cref="Source"/> means the actual type
        /// needs nothing (loaded already, or an array).
        /// </summary>
        internal readonly struct Pair : IEquatable<Pair>
        {

            internal readonly string Target;
            internal readonly string Source;

            internal Pair(string target, string source)
            {
                Target = target;
                Source = source;
            }

            public bool Equals(Pair other) => Target == other.Target && Source == other.Source;

            public override bool Equals(object obj) => obj is Pair p && Equals(p);

            public override int GetHashCode() => ((Target?.GetHashCode() ?? 0) * 31) ^ (Source?.GetHashCode() ?? 0);

            public override string ToString() => $"{Target ?? "(loaded)"} <- {Source ?? "(none)"}";

        }

        /// <summary>
        /// The pairs of one class, in the order they were first seen, without duplicates.
        /// </summary>
        internal sealed class Builder
        {

            readonly List<Pair> pairs = new List<Pair>();
            readonly HashSet<Pair> seen = new HashSet<Pair>();

            internal int Count => pairs.Count;

            internal void Add(Pair pair)
            {
                if (seen.Add(pair))
                    pairs.Add(pair);
            }

            internal void AddRange(Builder other)
            {
                foreach (var pair in other.pairs)
                    Add(pair);
            }

            internal Pair[] ToArray() => pairs.ToArray();

        }

#pragma warning disable CS0649 // only assigned by Begin/End, which the runtime has and the static compiler does not
        [ThreadStatic]
        static Builder recorder;
#pragma warning restore CS0649

        /// <summary>
        /// Whether this thread is recording, for callers that would do extra work to compute a pair.
        /// </summary>
        internal static bool Recording => recorder != null;

        /// <summary>
        /// Records that the analyzer accepted <paramref name="source"/> where <paramref name="target"/> is expected.
        /// Does nothing unless a first-generation compile of a lazily linked class is recording on this thread.
        /// </summary>
        internal static void NoteAssignable(RuntimeJavaType target, RuntimeJavaType source)
        {
            var rec = recorder;
            if (rec == null || target == null || source == null)
                return;

            // null, uninitialized, extended float/double, ...: nothing HotSpot loads
            if (target.IsPrimitive || source.IsPrimitive || target is RuntimeVerifierJavaType || source is RuntimeVerifierJavaType)
                return;

            // both loaded: the loads HotSpot does already happened
            var targetLoaded = target.IsUnloadable == false;
            var sourceLoaded = source.IsUnloadable == false;
            if (targetLoaded && sourceLoaded)
                return;

            var targetName = target.Name;
            var sourceName = source.Name;
            if (targetName == null || sourceName == null || targetName.Length == 0 || sourceName.Length == 0)
                return;

            // both arrays: HotSpot compares the component types
            var dims = 0;
            while (dims < targetName.Length && dims < sourceName.Length && targetName[dims] == '[' && sourceName[dims] == '[')
                dims++;

            if (dims > 0)
            {
                targetName = ComponentName(targetName, dims);
                sourceName = ComponentName(sourceName, dims);
                if (targetName == null || sourceName == null)
                    return;
            }

            // an array expected where something else is: not assignable, nothing to load for valid code
            if (targetName[0] == '[')
                return;

            // `<verifier>` (a merge the analyzer gave up on) and other placeholders have no class behind them
            if (targetName[0] == '<' || sourceName[0] == '<')
                return;

            if (targetName == "java.lang.Object" || targetName == sourceName)
                return;

            if (targetLoaded)
            {
                var t = target;
                for (var i = 0; i < dims && t != null; i++)
                    t = t.ElementTypeWrapper;

                // HotSpot stops at an interface: any reference is assignable to it
                if (t == null || t.IsInterface)
                    return;

                targetName = null;
            }

            // an array is only ever checked against the expected class, never loaded for a subclass test
            if (sourceLoaded || sourceName[0] == '[')
                sourceName = null;

            if (targetName == null && sourceName == null)
                return;

            rec.Add(new Pair(targetName, sourceName));
        }

        /// <summary>
        /// Strips <paramref name="dims"/> array dimensions from a class name, or returns null when what remains is a
        /// primitive (nothing to load).
        /// </summary>
        static string ComponentName(string name, int dims)
        {
            if (name.Length <= dims)
                return null;

            if (name[dims] == '[')
                return name.Substring(dims);

            if (name[dims] == 'L' && name[name.Length - 1] == ';')
                return name.Substring(dims + 1, name.Length - dims - 2);

            return null;
        }

#if !IMPORTER && !FIRST_PASS && !EXPORTER

        /// <summary>
        /// Whether the method being compiled records its verifier loads: the first generation of a method of a lazily
        /// linked class. A relink recompile runs at first execution, long after HotSpot would have verified.
        /// </summary>
        internal static bool ShouldRecord(RuntimeByteCodeJavaType.FinishContext finish, RuntimeByteCodeJavaType clazz)
        {
            return JVM.VerifyLoads && finish.IsLazyRecompile == false && ByteCodeHelper.InRelinkDrain == false && clazz.ClassLoader.LinksLazily;
        }

        /// <summary>
        /// Starts recording the verifier loads of one method on this thread. Returns the recorder it displaces, which
        /// <see cref="End"/> restores (a finish can nest inside another's compile).
        /// </summary>
        internal static object Begin()
        {
            var prev = recorder;
            recorder = new Builder();
            return prev;
        }

        /// <summary>
        /// Stops recording. The method's pairs go to <paramref name="commitTo"/>, or are dropped when it is null (the
        /// method failed verification: HotSpot would not have got as far as loading them all).
        /// </summary>
        internal static void End(object prev, RuntimeByteCodeJavaType commitTo)
        {
            var rec = recorder;
            recorder = (Builder)prev;

            if (commitTo != null && rec != null && rec.Count > 0)
                commitTo.AddVerifierLoads(rec);
        }

        [ThreadStatic]
        static RuntimeJavaType currentDrain;

        /// <summary>
        /// The class whose verifier loads this thread is running, for attributing the classes they define.
        /// </summary>
        internal static RuntimeJavaType CurrentDrain => currentDrain;

        internal const int ReasonInit = 0;
        internal const int ReasonCctor = 1;
        internal const int ReasonReflect = 2;

        static string ReasonName(int reason) => reason switch
        {
            ReasonInit => "init",
            ReasonCctor => "cctor",
            ReasonReflect => "reflect",
            _ => "?",
        };

        /// <summary>
        /// Loads the pairs of <paramref name="type"/> through its own class loader, as HotSpot's verifier resolves
        /// through the class being verified's loader. Every failure is swallowed: HotSpot would fail verification of
        /// the class, but the class is already usable here and its first execution reports the error as before. The
        /// loaded classes are not finished; HotSpot's verifier only loads.
        /// </summary>
        internal static void Drain(RuntimeByteCodeJavaType type, Pair[] pairs, int reason)
        {
            switch (reason)
            {
                case ReasonInit:
                    Interlocked.Increment(ref LazyBodyStats.VerifyDrainInit);
                    break;
                case ReasonCctor:
                    Interlocked.Increment(ref LazyBodyStats.VerifyDrainCctor);
                    break;
                case ReasonReflect:
                    Interlocked.Increment(ref LazyBodyStats.VerifyDrainReflect);
                    break;
            }

            if (JVM.VerifyLoadsShadow)
            {
                foreach (var pair in pairs)
                    Console.Error.WriteLine($"[IKVM verifyloads] shadow {ReasonName(reason)} {type.Name}: {pair}");

                return;
            }

            var prev = currentDrain;
            currentDrain = type;
            try
            {
                foreach (var pair in pairs)
                {
                    if (pair.Target != null)
                    {
                        var target = TryLoad(type, pair.Target);
                        if (target == null || target.IsUnloadable || target.IsInterface)
                            continue;
                    }

                    if (pair.Source != null)
                        TryLoad(type, pair.Source);
                }
            }
            finally
            {
                currentDrain = prev;
            }

            if (JVM.TraceVerifyLoads)
                Console.Error.WriteLine($"[IKVM verifyloads] {ReasonName(reason)} {type.Name}: {pairs.Length} pair(s) done");
        }

        static RuntimeJavaType TryLoad(RuntimeByteCodeJavaType type, string name)
        {
            try
            {
                var tw = type.ClassLoader.LoadClass(name, LoadMode.LoadOrThrow);
                if (JVM.TraceVerifyLoads)
                    Console.Error.WriteLine($"[IKVM verifyloads] {type.Name}: loaded {name}");

                return tw;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref LazyBodyStats.VerifyLoadFail);
                if (JVM.TraceVerifyLoads)
                    Console.Error.WriteLine($"[IKVM verifyloads] {type.Name}: loading {name} failed: {e.GetType().FullName}: {e.Message}");

                return null;
            }
        }

        static readonly List<RuntimeByteCodeJavaType> registered = new List<RuntimeByteCodeJavaType>();

        /// <summary>
        /// Returns the id a static constructor passes to <see cref="ByteCodeHelper.LinkVerifierLoads"/> for
        /// <paramref name="type"/>: emitted code can only carry immediates.
        /// </summary>
        internal static int Register(RuntimeByteCodeJavaType type)
        {
            lock (registered)
            {
                registered.Add(type);
                return registered.Count - 1;
            }
        }

        internal static RuntimeByteCodeJavaType GetRegistered(int id)
        {
            lock (registered)
                return id >= 0 && id < registered.Count ? registered[id] : null;
        }

#endif

    }

}
