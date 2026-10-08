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
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.CompilerServices;

using IKVM.Attributes;
using IKVM.ByteCode;

#if IMPORTER || EXPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;
using IKVM.Tools.Importer;

using Type = IKVM.Reflection.Type;
#else
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
#endif

#if EXPORTER == false

using IKVM.Java.Externs.java.lang.invoke;
using IKVM.Runtime.Accessors.Java.Lang;

#endif

namespace IKVM.Runtime
{

    public static class ByteCodeHelper
    {

#if FIRST_PASS == false && EXPORTER == FALSE

        static ObjectAccessor objectAccessor;

        static ObjectAccessor ObjectAccessor => JVM.Internal.BaseAccessors.Get(ref objectAccessor);

#endif

#if EXPORTER == false

        [DebuggerStepThrough]
        public static object multianewarray(RuntimeTypeHandle typeHandle, int[] lengths)
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] < 0)
                {
#if !FIRST_PASS
                    throw new global::java.lang.NegativeArraySizeException();
#endif
                }
            }

            return MultianewarrayHelper(Type.GetTypeFromHandle(typeHandle).GetElementType(), lengths, 0);
        }


        static object MultianewarrayHelper(Type elemType, int[] lengths, int index)
        {
            var o = Array.CreateInstance(elemType, lengths[index++]);
            if (index < lengths.Length)
            {
                elemType = elemType.GetElementType();
                var a = (object[])o;
                for (int i = 0; i < a.Length; i++)
                    a[i] = MultianewarrayHelper(elemType, lengths, index);
            }

            return o;
        }

        [DebuggerStepThrough]
        public static object multianewarray_ghost(RuntimeTypeHandle typeHandle, int[] lengths)
        {
            var type = Type.GetTypeFromHandle(typeHandle);
            int rank = 0;
            while (ReflectUtil.IsVector(type))
            {
                rank++;
                type = type.GetElementType();
            }

            var obj = multianewarray(RuntimeArrayJavaType.MakeArrayType(typeof(object), rank).TypeHandle, lengths);
            GhostTag.SetTag(obj, typeHandle);
            return obj;
        }

        [DebuggerStepThrough]
        public static T[] anewarray_ghost<T>(int length, RuntimeTypeHandle typeHandle)
        {
            var obj = new T[length];
            GhostTag.SetTag(obj, typeHandle);
            return obj;
        }

        [DebuggerStepThrough]
        public static object DynamicMultianewarray(int[] lengths, global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicMultianewarray");

            var wrapper = RuntimeJavaType.FromClass(clazz);
            var obj = multianewarray(wrapper.TypeAsArrayType.TypeHandle, lengths);
            if (wrapper.IsGhostArray)
                GhostTag.SetTag(obj, wrapper);

            return obj;
#endif
        }

        [DebuggerStepThrough]
        public static object DynamicNewarray(int length, global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicNewarray");
            if (length < 0)
                throw new global::java.lang.NegativeArraySizeException();

            var wrapper = RuntimeJavaType.FromClass(clazz);
            var obj = Array.CreateInstance(wrapper.TypeAsArrayType, length);
            if (wrapper.IsGhost || wrapper.IsGhostArray)
                GhostTag.SetTag(obj, wrapper.MakeArrayType(1));

            return obj;
#endif
        }

#endif

        [DebuggerStepThrough]
        public static void DynamicAastore(object arrayref, int index, object val)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicAastore");
            // any Java reference type array is assignable to object[], so this takes the same
            // stelem.ref path (including the array store check) the compiler emits for loadable
            // array types; only remapped non-primitive value type arrays need the reflective path
            if (arrayref is object[] arr)
                arr[index] = val;
            else
                ((Array)arrayref).SetValue(val, index);
#endif
        }

        [DebuggerStepThrough]
        public static object DynamicAaload(object arrayref, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicAaload");
            // same ldelem.ref path the compiler emits for loadable array types; only remapped
            // non-primitive value type arrays need the reflective (boxing) path
            if (arrayref is object[] arr)
                return arr[index];
            return ((Array)arrayref).GetValue(index);
#endif
        }

#if EXPORTER == false

        // the sole purpose of this method is to check whether the clazz can be instantiated (but not to actually do it)
        [DebuggerStepThrough]
        public static void DynamicNewCheckOnly(global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicNewCheckOnly");
            var wrapper = RuntimeJavaType.FromClass(clazz);
            if (wrapper.IsAbstract)
                throw new global::java.lang.InstantiationError(wrapper.Name);

            wrapper.RunClassInit();
#endif
        }

        /// <summary>
        /// Performs the checks a 'new' of a type that was unloadable when the enclosing method was compiled has to make,
        /// and records on the call site that they have been made.
        /// </summary>
        /// <remarks>
        /// Both answers are stable once reached: a type does not stop being abstract, and a class initializer runs once.
        /// Under lazy class loading a great many allocation sites resolve dynamically, and without this each of them
        /// would re-enter <see cref="RuntimeHelpers.RunClassConstructor"/> on every allocation. The flag is only set
        /// after the checks pass, so a class initializer that failed is retried and rethrows, as the spec requires.
        /// </remarks>
        /// <param name="clazz"></param>
        /// <param name="cache"></param>
        [DebuggerStepThrough]
        public static void DynamicNewCheckOnlyResolve(global::java.lang.Class clazz, ref bool cache)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            DynamicNewCheckOnly(clazz);
            Volatile.Write(ref cache, true);
#endif
        }

        static RuntimeJavaType LoadTypeWrapper(string clazz, ikvm.@internal.CallerID callerId)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var context = RuntimeJavaType.FromClass(callerId.getCallerClass());
                var wrapper = RuntimeClassLoader.FromCallerID(callerId).LoadClassByName(clazz);
                var loader = callerId.getCallerClassLoader();
                if (loader != null)
                {
                    ClassLoaderAccessor cla = null;
                    JVM.Internal.BaseAccessors.Get(ref cla);
                    cla.InvokeCheckPackageAccess(loader, wrapper.ClassObject, callerId.getCallerClass().pd);
                }

                if (!wrapper.IsAccessibleFrom(context))
                    throw new global::java.lang.IllegalAccessError("Try to access class " + wrapper.Name + " from class " + context.Name);

                wrapper.Finish();
                return wrapper;
            }
            catch (RetargetableJavaException e)
            {
                throw e.ToJava();
            }
#endif
        }

        [DebuggerStepThrough]
        public static global::java.lang.Class DynamicClassLiteral(string clazz, ikvm.@internal.CallerID callerId)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Profiler.Count("DynamicClassLiteral");
            return LoadTypeWrapper(clazz, callerId).ClassObject;
#endif
        }

        [DebuggerStepThrough]
        public static object DynamicCast(object obj, global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Debug.Assert(obj != null);
            Profiler.Count("DynamicCast");
            if (!DynamicInstanceOf(obj, clazz))
                return DynamicCastFailed(obj, clazz);

            return obj;
#endif
        }

        [DebuggerStepThrough]
        public static bool DynamicInstanceOf(object obj, global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Debug.Assert(obj != null);
            Profiler.Count("DynamicInstanceOf");

            return RuntimeJavaType.FromClass(clazz).DynamicIsInstance(obj);
#endif
        }

        /// <summary>
        /// Slow path behind a dynamic instanceof call site. Resolves the check and, when it reduces to a single CLR type
        /// test, publishes that type into the call site's cache so later executions test against it directly and never
        /// reach this method again.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="clazz"></param>
        /// <param name="cache"></param>
        /// <returns></returns>
        [DebuggerStepThrough]
        public static bool DynamicInstanceOfResolve(object obj, global::java.lang.Class clazz, ref object cache)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Debug.Assert(obj != null);
            Profiler.Count("DynamicInstanceOfResolve");

            var tw = RuntimeJavaType.FromClass(clazz);
            var check = tw.DynamicInstanceCheck;
            if (check == null)
                return tw.DynamicIsInstance(obj);

            Volatile.Write(ref cache, check);
            return check.IsInstanceOf(obj);
#endif
        }

        /// <summary>
        /// The fast path a resolved dynamic instanceof/cast call site takes: dispatch the cached
        /// <see cref="RuntimeJavaType.InstanceCheck"/>, whose body is `obj is T` in shared reference-generic code.
        /// </summary>
        /// <remarks>
        /// Exists so the emitted call site does not have to name a type internal to the runtime: the cache field is
        /// typed `object` and the cast happens here. It replaces `callvirt Type.IsInstanceOfType` on the same two
        /// stack operands, in the same order, so the call sites needed no reordering.
        /// </remarks>
        [DebuggerStepThrough]
        // No EH clause and a two-instruction body, so the JIT can fold it into the call site: one call level less on
        // every resolved dynamic instanceof/checkcast, and the virtual IsInstanceOf becomes visible to the caller's
        // devirtualization. Generated code cannot name the internal InstanceCheck type itself, so the helper stays.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InstanceCheckIsInstanceOf(object check, object obj)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // Unsafe.As, not a cast: the cast compiles to a castclass, which on this runtime is an
            // out-of-line type-check helper call, and it measured as the bulk of this method's 199
            // instructions -- 1.56% of in-game time for what is semantically one virtual call.
            //
            // The invariant that makes it sound: the only writers of a __<>dynamicInstanceCheckCache field are
            // DynamicInstanceOfResolve/DynamicCastResolve, both of which store a RuntimeJavaType.InstanceCheck
            // and nothing else, and the emitted call site reaches here only after testing that cache non-null.
            // So the runtime type is known by construction and re-checking it buys nothing.
            return Unsafe.As<RuntimeJavaType.InstanceCheck>(check).IsInstanceOf(obj);
#endif
        }

        /// <summary>
        /// Slow path behind a dynamic cast call site, the counterpart to <see cref="DynamicInstanceOfResolve"/>.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="clazz"></param>
        /// <param name="cache"></param>
        /// <returns></returns>
        [DebuggerStepThrough]
        public static object DynamicCastResolve(object obj, global::java.lang.Class clazz, ref object cache)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Debug.Assert(obj != null);
            Profiler.Count("DynamicCastResolve");

            var tw = RuntimeJavaType.FromClass(clazz);
            var check = tw.DynamicInstanceCheck;
            if (check == null)
                return tw.DynamicIsInstance(obj) ? obj : DynamicCastFailed(obj, clazz);

            Volatile.Write(ref cache, check);
            return check.IsInstanceOf(obj) ? obj : DynamicCastFailed(obj, clazz);
#endif
        }

        /// <summary>
        /// Throws the <see cref="global::java.lang.ClassCastException"/> for a dynamic cast that failed. Kept out of line
        /// so a cast call site stays a type test and a branch.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="clazz"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.ClassCastException"></exception>
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object DynamicCastFailed(object obj, global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            throw new global::java.lang.ClassCastException(IKVM.Java.Externs.ikvm.runtime.Util.GetTypeWrapperFromObject(JVM.Context, obj).Name + " cannot be cast to " + clazz.getName());
#endif
        }

        [DebuggerStepThrough]
        public static global::java.lang.invoke.MethodType DynamicLoadMethodType(ref global::java.lang.invoke.MethodType cache, string sig, ikvm.@internal.CallerID callerID)
        {
            if (cache == null)
                DynamicLoadMethodTypeImpl(ref cache, sig, callerID);

            return cache;
        }

        private static void DynamicLoadMethodTypeImpl(ref global::java.lang.invoke.MethodType cache, string sig, global::ikvm.@internal.CallerID callerID)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var loader = RuntimeClassLoader.FromCallerID(callerID);
                var args = loader.ArgJavaTypeListFromSig(sig, LoadMode.LoadOrThrow);
                var ptypes = new global::java.lang.Class[args.Length];
                for (int i = 0; i < ptypes.Length; i++)
                    ptypes[i] = args[i].ClassObject;

                Interlocked.CompareExchange(ref cache, global::java.lang.invoke.MethodType.methodType(loader.RetTypeWrapperFromSig(sig, LoadMode.LoadOrThrow).ClassObject, ptypes), null);
            }
            catch (RetargetableJavaException e)
            {
                throw e.ToJava();
            }
#endif
        }

        [DebuggerStepThrough]
        public static global::java.lang.invoke.MethodHandle DynamicLoadMethodHandle(ref global::java.lang.invoke.MethodHandle cache, int kind, string clazz, string name, string sig, ikvm.@internal.CallerID callerID)
        {
            if (cache == null)
                Interlocked.CompareExchange(ref cache, DynamicLoadMethodHandleImpl(kind, clazz, name, sig, callerID), null);

            return cache;
        }

        static global::java.lang.invoke.MethodHandle DynamicLoadMethodHandleImpl(int kind, string clazz, string name, string sig, ikvm.@internal.CallerID callerID)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var refc = LoadTypeWrapper(clazz, callerID).ClassObject;

            try
            {
                switch ((MethodHandleKind)kind)
                {
                    case MethodHandleKind.GetStatic:
                    case MethodHandleKind.PutStatic:
                    case MethodHandleKind.GetField:
                    case MethodHandleKind.PutField:
                        global::java.lang.Class type = RuntimeClassLoader.FromCallerID(callerID).FieldTypeWrapperFromSig(sig, LoadMode.LoadOrThrow).ClassObject;
                        return global::java.lang.invoke.MethodHandleNatives.linkMethodHandleConstant(callerID.getCallerClass(), kind, refc, name, type);
                    default:
                        global::java.lang.invoke.MethodType mt = null;
                        DynamicLoadMethodType(ref mt, sig, callerID);
                        // HACK linkMethodHandleConstant is broken for MethodHandle.invoke[Exact]
                        if (kind == (int)MethodHandleKind.InvokeVirtual && refc == JVM.Context.JavaBase.TypeOfJavaLangInvokeMethodHandle.ClassObject)
                        {
                            switch (name)
                            {
                                case "invokeExact":
                                    return global::java.lang.invoke.MethodHandles.exactInvoker(mt);
                                case "invoke":
                                    return global::java.lang.invoke.MethodHandles.invoker(mt);
                            }
                        }

                        return global::java.lang.invoke.MethodHandleNatives.linkMethodHandleConstant(callerID.getCallerClass(), kind, refc, name, mt);
                }
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

        static long lazyRecompiled;
        static long lazyFellBack;
        /* Times the direct-binder fast path threw a NullReferenceException and fell through to the
         * MethodHandle path. See the catch in DynamicBinderMemberLookup for why this is a fall-through
         * rather than a crash, and why it must stay visible rather than silent. */
        static long directBinderNre;

        /// <summary>
        /// Prints the generational-body totals: how many method bodies were successfully recompiled versus how
        /// many kept their first generation.
        /// </summary>
        public static void ReportLazyBodies()
        {
            Console.WriteLine($"[IKVM lazybody] TOTALS recompiled={Interlocked.Read(ref lazyRecompiled)} fellBack={Interlocked.Read(ref lazyFellBack)}");
        }

#endif

        /// <summary>
        /// Called from the hook at the top of an in-place first-generation body. Returns false while any
        /// dynamic-member dependency is still unloaded. The return value is the number of subsequent Gen1
        /// invocations the hook should skip, or -1 once it may disable itself permanently.
        /// </summary>
        /// <remarks>
        /// The difference from BuildLazyBody is the whole feature. That one returns a delegate the method
        /// tails into forever; this one replaces the method's body in place, so a relinked method costs a
        /// normal call. R226 measured the delegate at 3x what the recompile saves.
        ///
        /// Never throws, and every refusal is counted. The body that is already executing is a correct
        /// first generation, so the only cost of declining is that it stays -- which is precisely the
        /// behaviour with the feature off. Declared unconditionally with only the body compiled out, for the
        /// same reason BuildLazyBody is: ikvmc binds it by name against the FIRST_PASS reference assembly.
        /// </remarks>
        public static int RelinkBody(int id)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (JVM.LazyRecompile == false)
                return -1;

            var entry = LazyBodyRegistry.Get(id);
            if (entry == null)
                return -1;

            // Ask the runtime once, not once per method: on CoreCLR there is no body swap at all.
            if (LibIkvm.CanReplaceMethodBody == false)
            {
                Interlocked.Increment(ref LazyBodyStats.InPlaceUnsupported);
                Volatile.Write(ref entry.RelinkState, 2);
                entry.Release();
                return -1;
            }

            // ENQUEUE, do not relink here. Generation 1 is a correct body, so deferral is free -- and it is
            // what makes the scratch type affordable.
            //
            // The scratch type is not optional. Two independent failures proved it: generation 2 emitted as a
            // DynamicMethod carries tokens private to that DynamicMethod's own reference table, so once its
            // header is transplanted the interpreter resolves them against the IMAGE and dies with
            // "Could not find required dynamic token 0x00000001" out of interp_transform_call; and pointing
            // the FinishContext at the real, already-baked TypeBuilder instead makes every cache field
            // generation 2 wants throw InvalidOperation_TypeHasBeenCreated. A MethodBuilder on a fresh type in
            // the right module fixes both -- real module tokens, and somewhere to Define.
            //
            // But it cost ~160 ms per method against the delegate path's ~10 ms (1,267 relinks never finished
            // a boot that the delegate path completed with 1,550), essentially all of it DefineType +
            // CreateType, whose metadata finalization also degrades as the module grows. So pay CreateType
            // once per BATCH instead of once per method.
            // Do not even queue the method until every declaring type that caused a dynamic member access is
            // already loaded. This is a non-loading probe: Gen2 must be the only re-emission, and it must be
            // able to replace every __dynamic_* member instruction with a direct access.
            var newlyQueued = false;

            // This path must be lock-free with respect to class loading: it runs at arbitrary Java method
            // entry and can therefore inherit any loader, type-initializer, or application monitor. The
            // readiness test consults RuntimeClassLoader's concurrently published name set, never `types`.
            if (Volatile.Read(ref entry.RelinkState) == 2)
                return -1;

            if (Volatile.Read(ref entry.RelinkState) == 0 && entry.DependenciesLoaded() == false)
            {
                Interlocked.Increment(ref LazyBodyStats.DependencyWaits);
                // wasm fork (R357): name the methods that keep waiting. 64 probes at the capped retry delay is
                // thousands of generation-1 calls, i.e. a hot method whose dependency never reads as published
                // -- which would pin it to its dynamic stubs for the whole run. Bounded to the first 48.
                var waits = Interlocked.Increment(ref entry.Waits);
                if ((waits & (waits - 1)) == 0 && LazyBodyRegistry.IsWatched(entry.Wrapper?.Name, entry.Method?.Name))
                    Console.WriteLine($"[IKVM lazybody] watch {entry.Wrapper?.Name}.{entry.Method?.Name}{entry.Method?.Signature}: wait #{waits} on {entry.FirstUnpublished() ?? "(none now)"} ({entry.Dependencies.Length} deps)");
                if (waits == 64 && Interlocked.Increment(ref LazyBodyStats.StuckNamed) <= 48)
                    Console.WriteLine($"[IKVM lazybody] stuck #{LazyBodyStats.StuckNamed}: {entry.Wrapper?.Name}.{entry.Method?.Name}{entry.Method?.Signature} waits on {entry.FirstUnpublished() ?? "(none now)"} ({entry.Dependencies.Length} deps, loader {entry.Wrapper?.ClassLoader?.GetType().Name})");

                // wasm fork (JVM.LazyLoadStuckProbes): hand the entry to the drain to load what it waits on. Still
                // lock-free and non-loading here; the next probe after the load finds the gate open.
                if (JVM.LazyLoadStuckProbes > 0 && waits >= JVM.LazyLoadStuckProbes && Interlocked.CompareExchange(ref entry.LoadRequested, 1, 0) == 0)
                {
                    lock (relinkPendingLock)
                        loadStuckPending.Add(id);
                    Interlocked.Increment(ref LazyBodyStats.LoadStuckRequested);
                    ScheduleRelinkDrain();
                }

                // wasm fork (JVM.LazyPartialProbes): stop waiting and queue the relink without the unpublished
                // dependencies. FlushRelinkBatch admits the result only if what stays dynamic is exactly those.
                if (JVM.LazyPartialProbes <= 0 || waits < JVM.LazyPartialProbes || HasUnpublishedParameterType(entry))
                    return IncreaseRelinkRetryDelay(entry);

                Volatile.Write(ref entry.Partial, 1);
            }

            if (Interlocked.CompareExchange(ref entry.RelinkState, 1, 0) == 0)
            {
                newlyQueued = true;
                if (Volatile.Read(ref entry.Partial) == 1)
                    Interlocked.Increment(ref LazyBodyStats.PartialQueued);
            }

            if (Volatile.Read(ref entry.RelinkState) == 2)
                return -1;

            // The hook only enqueues. Finish/ResolveMethod, Reflection.Emit, CreateType and body replacement
            // run on the single background drain, never on the caller's class-loading stack.
            lock (relinkPendingLock)
            {
                if (newlyQueued)
                {
                    relinkPending.Add(id);
                    Interlocked.Increment(ref LazyBodyStats.InPlaceQueued);
                    if (LazyBodyRegistry.IsWatched(entry.Wrapper?.Name, entry.Method?.Name))
                        Console.WriteLine($"[IKVM lazybody] watch {entry.Wrapper?.Name}.{entry.Method?.Name}: queued (partial={Volatile.Read(ref entry.Partial)})");
                }
            }

            ScheduleRelinkDrain();

            return Volatile.Read(ref entry.RelinkState) == 2 ? -1 : RelinkQueuedRetryDelay;
#endif
        }

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

        /// <summary>Methods per scratch type. Amortises the one cost that made per-method relinking unaffordable.</summary>
        const int RelinkBatchSize = 48;

        const int RelinkQueuedRetryDelay = 15;

        const int RelinkQuietPeriodMilliseconds = 25;

        const int RelinkMaximumDeferralMilliseconds = 250;

        static readonly object relinkPendingLock = new object();
        static List<int> relinkPending = new List<int>();
        static List<int> loadStuckPending = new List<int>();   // wasm fork (JVM.LazyLoadStuckProbes), under relinkPendingLock

        /// <summary>Dependencies loaded per drain ownership period, so one drain cannot spend a whole boot loading.</summary>
        const int LoadStuckPerDrain = 16;
        static int relinkDrainScheduled;
        static readonly WaitCallback relinkDrainCallback = _ => DrainRelinkQueue();
        static readonly Timer relinkDrainTimer = new Timer(_ => DrainRelinkQueue(), null, Timeout.Infinite, Timeout.Infinite);
        static int relinkDeferralStarted;
        static long relinkObservedDefinitionVersion = -1;

        /// <summary>
        /// wasm fork (JVM.LazyPartialProbes): true if a parameter of the entry's method is a class that is still not
        /// published, which keeps a partial relink from being queued.
        /// </summary>
        /// <remarks>
        /// Generation 2's prologue resolves an unloadable parameter's entry check through Compiler.TryResolveForEntryCheck,
        /// i.e. TryLoadClassByName, which LOADS. For a full relink the class is published by then and nothing loads; for a
        /// partial one it would load, through the user loader, from the relink drain, a class generation 1 never loaded.
        /// </remarks>
        static bool HasUnpublishedParameterType(LazyBodyRegistry.Entry entry)
        {
            foreach (var tw in entry.Method.GetParameters())
            {
                if (!tw.IsUnloadable)
                    continue;

                var name = tw.Name;
                var start = 0;
                while (start < name.Length && name[start] == '[')
                    start++;
                if (start > 0)
                    name = start < name.Length && name[start] == 'L' && name[name.Length - 1] == ';' ? name.Substring(start + 1, name.Length - start - 2) : null;
                if (!string.IsNullOrEmpty(name) && !entry.Wrapper.ClassLoader.IsClassPublished(name))
                    return true;
            }

            return false;
        }

        static int IncreaseRelinkRetryDelay(LazyBodyRegistry.Entry entry)
        {
            while (true)
            {
                var old = Volatile.Read(ref entry.RetryDelay);
                var value = old switch
                {
                    < 1 => 1,
                    < 4 => 4,
                    < 16 => 16,
                    _ => 64,
                };
                if (Interlocked.CompareExchange(ref entry.RetryDelay, value, old) == old)
                    return value;
            }
        }

        static void ScheduleRelinkDrain()
        {
            if (Interlocked.CompareExchange(ref relinkDrainScheduled, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref relinkDeferralStarted, Environment.TickCount);
            Interlocked.Exchange(ref relinkObservedDefinitionVersion, -1);

            if (!ThreadPool.QueueUserWorkItem(relinkDrainCallback) &&
                !relinkDrainTimer.Change(1, Timeout.Infinite))
                ReleaseRelinkDrain();
        }

        static void DrainRelinkQueue()
        {
            var deferred = false;
            try
            {
                // Establish a quiet interval without parking this ThreadPool thread. A one-shot timer retains
                // drain ownership while it waits, so hooks can continue to enqueue without creating workers.
                // The deadline makes the quiet interval an optimisation rather than a liveness condition.
                var definitionVersion = RuntimeClassLoader.ClassDefinitionVersion;
                var observedVersion = Interlocked.Read(ref relinkObservedDefinitionVersion);
                var definitionsActive = RuntimeClassLoader.HasActiveClassDefinitions;
                var elapsed = unchecked((uint)(Environment.TickCount - Volatile.Read(ref relinkDeferralStarted)));
                var unsettled = definitionsActive || observedVersion != definitionVersion;
                if (unsettled && elapsed < (uint)RelinkMaximumDeferralMilliseconds)
                {
                    Interlocked.Exchange(ref relinkObservedDefinitionVersion, definitionVersion);
                    Interlocked.Increment(ref LazyBodyStats.InPlaceDeferrals);
                    if (relinkDrainTimer.Change(RelinkQuietPeriodMilliseconds, Timeout.Infinite))
                    {
                        deferred = true;
                        return;
                    }
                }
                else if (unsettled)
                {
                    var forced = Interlocked.Increment(ref LazyBodyStats.InPlaceForced);
                    if (forced <= 8 || JVM.TraceLazyBodies)
                        Console.WriteLine($"[IKVM lazybody] forcing one relink batch after {elapsed} ms of continuous class-definition activity");
                }

                LoadStuckDependencies();

                List<int> batch;
                lock (relinkPendingLock)
                {
                    if (relinkPending.Count == 0)
                        return;

                    var count = Math.Min(RelinkBatchSize, relinkPending.Count);
                    batch = relinkPending.GetRange(0, count);
                    relinkPending.RemoveRange(0, count);
                }

                // Process at most one batch per ownership period. A remaining tail gets a fresh quiet window
                // and deadline, preventing a large queue from monopolising startup after one forced drain.
                try
                {
                    PrepareRelinkBatch(batch);
                    FlushRelinkBatch(batch);
                }
                catch (Exception e)
                {
                    var n = Interlocked.Add(ref LazyBodyStats.InPlaceFailed, batch.Count);
                    if (n < 16 || JVM.TraceLazyBodies)
                        Console.WriteLine($"[IKVM lazybody] batch FAILED ({batch.Count} members): {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    FinishRelinkBatch(batch);
                }
            }
            finally
            {
                if (!deferred)
                    ReleaseRelinkDrain();
            }
        }

        static void ReleaseRelinkDrain()
        {
            var reschedule = false;
            lock (relinkPendingLock)
            {
                Volatile.Write(ref relinkDrainScheduled, 0);
                reschedule = relinkPending.Count != 0 || loadStuckPending.Count != 0;
            }

            if (reschedule)
                ScheduleRelinkDrain();
        }

        /// <summary>
        /// wasm fork (JVM.LazyLoadStuckProbes): load the unpublished dependencies of the entries RelinkBody named stuck,
        /// through each entry's own loader and without initializing them. Runs on the drain, outside every lock, which is
        /// what the hook cannot do: it runs at arbitrary Java method entry. Loading publishes the name in that loader
        /// (RegisterInitiatingLoader), so the entry's next probe queues an ordinary full relink.
        /// </summary>
        static void LoadStuckDependencies()
        {
            List<int> ids;
            lock (relinkPendingLock)
            {
                if (loadStuckPending.Count == 0)
                    return;

                ids = loadStuckPending;
                loadStuckPending = new List<int>();
            }

            var loads = 0;
            for (int k = 0; k < ids.Count; k++)
            {
                if (loads >= LoadStuckPerDrain)
                {
                    // the rest waits for the next ownership period; ReleaseRelinkDrain reschedules for it
                    lock (relinkPendingLock)
                        loadStuckPending.AddRange(ids.GetRange(k, ids.Count - k));
                    return;
                }

                var entry = LazyBodyRegistry.Get(ids[k]);
                if (entry == null || entry.Wrapper?.ClassLoader == null)
                    continue;

                var loader = entry.Wrapper.ClassLoader;
                foreach (var dep in entry.Dependencies)
                {
                    if (loader.IsClassPublished(dep))
                    {
                        Interlocked.Increment(ref LazyBodyStats.LoadStuckAlready);
                        continue;
                    }

                    loads++;
                    string outcome;
                    try
                    {
                        var tw = loader.TryLoadClassByName(dep);
                        outcome = tw != null && !tw.IsUnloadable ? "loaded" : "not found";
                    }
                    catch (Exception e)
                    {
                        outcome = e.GetType().Name;
                    }

                    var n = outcome == "loaded" ? Interlocked.Increment(ref LazyBodyStats.LoadStuckLoaded) : Interlocked.Increment(ref LazyBodyStats.LoadStuckFailed);
                    if (n <= 16 || LazyBodyRegistry.IsWatched(entry.Wrapper.Name, entry.Method?.Name))
                        Console.WriteLine($"[IKVM lazybody] loadStuck {dep} for {entry.Wrapper.Name}.{entry.Method?.Name}: {outcome}");
                }
            }
        }

        static void PrepareRelinkBatch(List<int> ids)
        {
            foreach (var id in ids)
            {
                var entry = LazyBodyRegistry.Get(id);
                if (entry == null)
                    continue;

                try
                {
                    entry.Method.DeclaringType.Finish();
                    entry.Method.ResolveMethod();
                    var method = RelinkTarget(entry.Method);
                    if (method != null)
                        LibIkvm.MarkRelinkPending(method.MethodHandle.Value, true);
                }
                catch { }
            }
        }

        /// <summary>
        /// Recompiles a batch of registered methods and installs each body in place.
        /// </summary>
        /// <remarks>
        /// Grouped by MODULE, because the scratch type's metadata tokens are only valid for targets in the
        /// same dynamic module, and different class loaders get different modules. Within a module the
        /// reparsed ClassFile is shared per class, which matters because Reparse plus an eager
        /// LoadMode.Link over the whole constant pool is the other per-method cost.
        ///
        /// One bad method must not poison the batch: everything per-method is caught, and a method that
        /// fails to emit is simply not swapped. The type is still created, because its other members are
        /// fine, and the failed method keeps generation 1.
        /// </remarks>
        static void FlushRelinkBatch(List<int> ids)
        {
            var context = JVM.Context;

            // module -> entries
            var byModule = new Dictionary<ModuleBuilder, List<LazyBodyRegistry.Entry>>();
            foreach (var id in ids)
            {
                try
                {
                    var e = LazyBodyRegistry.Get(id);
                    if (e == null)
                    {
                        Interlocked.Increment(ref LazyBodyStats.InPlaceDeclined);
                        continue;
                    }

                    var mod = e.Wrapper.ClassLoader.GetTypeWrapperFactory().ModuleBuilder;
                    if (byModule.TryGetValue(mod, out var list) == false)
                        byModule[mod] = list = new List<LazyBodyRegistry.Entry>();
                    list.Add(e);
                }
                catch
                {
                    Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                }
            }

            foreach (var kv in byModule)
            {
                TypeBuilder scratch;
                try
                {
                    scratch = kv.Key.DefineType("__<>inplace_" + Interlocked.Increment(ref relinkScratchSeq), TypeAttributes.Public);
                }
                catch
                {
                    Interlocked.Add(ref LazyBodyStats.InPlaceFailed, kv.Value.Count);
                    ClearPending(kv.Value);
                    continue;
                }

                var reparsed = new Dictionary<object, ClassFile>();
                var built = new List<RelinkPending>();
                var nameSeq = 0;

                foreach (var entry in kv.Value)
                {
                    // Held outside the try so the catch can repair a MethodBuilder that was defined and then
                    // orphaned by a throw in Compile -- see the repair note in the catch.
                    MethodBuilder mbForRepair = null;
                    try
                    {
                        if (reparsed.TryGetValue(entry.ReparseKey ?? entry, out var classFile) == false)
                        {
                            // Reparse is mandatory: MethodAnalyzer rewrote generation 1's opcode stream in
                            // place. Null means constant-pool patches (anonymous/hidden classes).
                            classFile = entry.Reparse();
                            if (classFile != null)
                            {
                                // LinkNoLoad, NOT LoadMode.Link. This runs at a method's first execution,
                                // which can be inside a Mixin transform, and an eager link there re-enters
                                // Knot's class loader: Mixin throws ReEntrantTransformerError and defines
                                // the class WITHOUT its mixins. That is not a slowdown, it is a DIFFERENT
                                // PROGRAM -- measured at 189 re-entrance errors per run against 0-1 in the
                                // control, which voided the timing comparison entirely.
                                //
                                // Resolving only what is already loaded is enough for the premise here (by
                                // first execution most references are loaded) and cannot trigger a load, so
                                // it cannot re-enter. What is still unresolved stays __dynamic_* and is
                                // rejected by the post-compile Gen2 invariant below.
                                classFile.Link(entry.Wrapper, LoadMode.LinkNoLoad);
                            }
                            reparsed[entry.ReparseKey ?? entry] = classFile;
                        }

                        if (classFile == null)
                        {
                            Interlocked.Increment(ref LazyBodyStats.InPlaceDeclined);
                            continue;
                        }

                        var m = classFile.Methods[entry.MethodIndex];
                        var mw = entry.Method;
                        var parameterTypesForDefine = mw.GetParametersForDefineMethod();
                        var parameterTypes = m.IsStatic
                            ? parameterTypesForDefine
                            : PrependType(entry.Wrapper.TypeAsSignatureType, parameterTypesForDefine);

                        // Name from a counter that advances for EVERY DefineMethod, not from built.Count.
                        // An entry that defines its method and then throws in Compile is absent from
                        // `built` but its method is already ON the type, so reusing built.Count hands the
                        // next entry the same name -- and GetMethod then reports
                        // AmbiguousMatchException on __<>body3 rather than anything about duplicates.
                        var name = "__<>body" + (nameSeq++);
                        mbForRepair = null;
                        var mb = scratch.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, mw.ReturnTypeForDefineMethod, parameterTypes);
                        mbForRepair = mb;
                        var ilgen = context.CodeEmitterFactory.Create(mb);
                        var finish = new RuntimeByteCodeJavaType.FinishContext(context, entry.Host, classFile, entry.Wrapper, scratch);
                        finish.IsLazyRecompile = true;
                        // Distinct name range per context: they all share this scratch type, and each one
                        // otherwise starts its helper numbering at 0. The stride is deliberately huge -- a
                        // single method defining 4096 helpers is not a thing -- and the counter is global so
                        // ranges cannot collide across batches either.
                        finish.SeedHelperNames(Interlocked.Add(ref relinkHelperSeq, 4096));
                        var nonleaf = false;
                        Compiler.Compile(finish, entry.Host, entry.Wrapper, mw, classFile, m, ilgen, ref nonleaf);
                        ilgen.CheckLabels();
                        ilgen.DoEmit();

                        var isPartial = Volatile.Read(ref entry.Partial) == 1;
                        if (Compiler.HasDynamicReferences(m))
                        {
                            // wasm fork (JVM.LazyPartialProbes): a partial relink may keep the dynamic form for references
                            // to its OWN dependencies that are still unpublished -- exactly what generation 1 does for
                            // them. A reference to anything else, including a published dependency whose member did not
                            // resolve, is the defect this invariant exists to catch and declines as before.
                            var loader = entry.Wrapper.ClassLoader;
                            var deps = entry.Dependencies;
                            if (isPartial && Compiler.DynamicReferencesOnlyTo(classFile, m, d => Array.IndexOf(deps, d) >= 0 && !loader.IsClassPublished(d)))
                            {
                                var n = Interlocked.Increment(ref LazyBodyStats.PartialAccepted);
                                if (n <= 48 || JVM.TraceLazyBodies)
                                    Console.WriteLine($"[IKVM lazybody] partial #{n}: {entry.Wrapper.Name}.{m.Name}{m.Signature} keeps {Compiler.DescribeFirstDynamicReference(classFile, m)}");
                            }
                            else
                            {
                                if (isPartial)
                                    Interlocked.Increment(ref LazyBodyStats.PartialRefused);
                                Interlocked.Increment(ref LazyBodyStats.Gen2UnexpectedDynamic);
                                // Tally by the SURVIVING REFERENCE, not by the method: 422 per run is a
                                // population, and only the reference says which gate assumption is wrong.
                                LazyBodyStats.RecordUnexpectedDynamic(Compiler.DescribeFirstDynamicReference(classFile, m));
                                if (JVM.TraceLazyBodies || LazyBodyRegistry.IsWatched(entry.Wrapper.Name, m.Name))
                                    Console.WriteLine($"[IKVM lazybody] unexpected dynamic Gen2 {entry.Wrapper.Name}:{m.Name}{m.Signature} -> {Compiler.DescribeFirstDynamicReference(classFile, m)}");
                                Interlocked.Increment(ref LazyBodyStats.InPlaceDeclined);
                                continue;
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref LazyBodyStats.Gen2Clean);
                        }

                        if (finish.DefinedCallerID)
                        {
                            // A CallerID minted on the scratch type is the wrong identity for this method.
                            Interlocked.Increment(ref LazyBodyStats.Gen2CallerID);
                            Interlocked.Increment(ref LazyBodyStats.InPlaceDeclined);
                            continue;
                        }

                        built.Add(new RelinkPending { Entry = entry, Name = name, Finish = finish });
                    }
                    catch (Exception e)
                    {
                        var n = Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                        LazyBodyStats.RecordInPlaceFailure("emit", e);
                        if (n < 8 || JVM.TraceLazyBodies)
                            Console.WriteLine($"[IKVM lazybody] inplace emit FAILED: {e.GetType().Name}: {e.Message}");

                        // GIVE THE ORPHANED MethodBuilder A BODY, or it takes the whole batch with it.
                        //
                        // DefineMethod happens before Compile, and CodeEmitter BUFFERS -- nothing reaches the
                        // builder until DoEmit(). So a throw inside Compile leaves a defined method with no
                        // IL at all, and `scratch.CreateType()` then fails the ENTIRE type with
                        // `InvalidOperation_BadEmptyMethodBody`, reverting every other member of the batch to
                        // generation 1. Measured (R276): 336 individual emit failures amplified into 1,978
                        // total, i.e. ~1,642 methods lost to batches killed by someone else's failure.
                        //
                        // The stub is never called -- this entry is not added to `built`, so nothing installs
                        // or references it. `ldnull; throw` is valid for ANY signature, which matters because
                        // the return type here is arbitrary.
                        try
                        {
                            if (mbForRepair != null)
                            {
                                var rg = context.CodeEmitterFactory.Create(mbForRepair);
                                rg.Emit(OpCodes.Ldnull);
                                rg.Emit(OpCodes.Throw);
                                rg.DoEmit();
                                Interlocked.Increment(ref LazyBodyStats.InPlaceStubbed);
                            }
                        }
                        catch
                        {
                            // If even the stub will not emit, the batch is lost as before -- counted, not hidden.
                            Interlocked.Increment(ref LazyBodyStats.InPlaceStubFailed);
                        }
                    }
                }

                Type created;
                try
                {
                    created = scratch.CreateType();
                    Interlocked.Increment(ref LazyBodyStats.InPlaceBatches);
                    foreach (var b in built)
                        b.Finish.CreateNestedTypes();
                }
                catch (Exception e)
                {
                    // The whole type failed to bake, so nothing in it can be installed. Every member keeps
                    // generation 1, which is the pre-feature behaviour.
                    var n = Interlocked.Add(ref LazyBodyStats.InPlaceFailed, built.Count);
                    if (n < 16 || JVM.TraceLazyBodies)
                        Console.WriteLine($"[IKVM lazybody] inplace CreateType FAILED ({built.Count} members): {e.GetType().Name}: {e.Message}");
                    ClearPending(kv.Value);
                    continue;
                }

                // Clear the pending mark for EVERY entry in this batch, including the ones that were
                // declined or failed to emit and so are not in `built`. A method left marked is a method
                // the wasm JIT will never compile at all, which is a strictly worse loss than keeping
                // generation 1.
                foreach (var entry in kv.Value)
                {
                    try
                    {
                        var t = RelinkTarget(entry.Method);
                        if (t != null)
                            LibIkvm.MarkRelinkPending(t.MethodHandle.Value, false);
                    }
                    catch { }
                }

                foreach (var b in built)
                {
                    try
                    {
                        var source = created.GetMethod(b.Name, BindingFlags.Public | BindingFlags.Static);
                        if (source == null)
                        {
                            Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                            continue;
                        }

                        var mw = b.Entry.Method;
                        mw.DeclaringType.Finish();
                        mw.ResolveMethod();
                        var target = RelinkTarget(mw);
                        if (target == null)
                        {
                            Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                            continue;
                        }

                        var r = LibIkvm.ReplaceMethodBody(target.MethodHandle.Value, source.MethodHandle.Value);
                        if (LazyBodyRegistry.IsWatched(b.Entry.Wrapper?.Name, mw.Name))
                            Console.WriteLine($"[IKVM lazybody] watch {b.Entry.Wrapper?.Name}.{mw.Name}: body swap r={r} (1 installed, 0 too late, -2 unsupported)");
                        if (r == 1)
                        {
                            var n = Interlocked.Increment(ref LazyBodyStats.InPlaceOk);
                            if (n == 1 || n % 250 == 0)
                                Console.WriteLine($"[IKVM lazybody] inplace ok={n} late={LazyBodyStats.InPlaceLate} declined={LazyBodyStats.InPlaceDeclined} failed={LazyBodyStats.InPlaceFailed} batches={LazyBodyStats.InPlaceBatches}");
                        }
                        else if (r == 0)
                        {
                            // Already tiered up or already wasm-JITted; the runtime will not invalidate
                            // either from here, so the method keeps generation 1.
                            Interlocked.Increment(ref LazyBodyStats.InPlaceLate);
                        }
                        else if (r == -2)
                            Interlocked.Increment(ref LazyBodyStats.InPlaceUnsupported);
                        else
                        {
                            var n = Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                            if (n < 8 || JVM.TraceLazyBodies)
                                Console.WriteLine($"[IKVM lazybody] inplace REJECTED (shape mismatch, r={r})");
                        }
                    }
                    catch (Exception e)
                    {
                        var n = Interlocked.Increment(ref LazyBodyStats.InPlaceFailed);
                        LazyBodyStats.RecordInPlaceFailure("install", e);
                        if (n < 8 || JVM.TraceLazyBodies)
                            Console.WriteLine($"[IKVM lazybody] inplace install FAILED: {e.GetType().Name}: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// wasm fork (R412, JVM.LazyDefaultImpl): the method whose body a relink replaces. For an interface DEFAULT method
        /// that is not GetMethod() -- the abstract interface method the CLR dispatches through -- but the static
        /// `<default>name` helper carrying the body (RuntimeDefaultInterfaceJavaMethod.impl, a MethodBuilder that nothing
        /// ever resolves). Installing on the abstract method was accepted by the runtime (an SRE method, never transformed)
        /// and counted InPlaceOk while the helper kept generation 1 for the life of the process. The baked helper is found
        /// by name and exact parameter types (interface first, as JavaTypeImpl defines it); an ambiguous or missing match
        /// returns null (counted), which every caller treats as "no target".
        /// </summary>
        static MethodBase RelinkTarget(RuntimeJavaMethod mw)
        {
            if (!JVM.LazyDefaultImpl || mw is not RuntimeDefaultInterfaceJavaMethod || mw.DeclaringType.IsGhost)
                return mw.GetMethod();
            var impl = RuntimeDefaultInterfaceJavaMethod.GetImpl(mw);
            if (impl is not MethodBuilder mb)
                return impl;
            mw.DeclaringType.Finish();
            var parameters = mw.GetParameters();
            var types = new Type[parameters.Length + 1 + (mw.HasCallerID ? 1 : 0)];
            types[0] = mw.DeclaringType.TypeAsTBD;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].IsUnloadable)
                    parameters[i].Finish();
                types[i + 1] = parameters[i].TypeAsSignatureType;
            }
            if (mw.HasCallerID)
                types[types.Length - 1] = mw.DeclaringType.Context.JavaBase.TypeOfIkvmInternalCallerID.TypeAsSignatureType;
            MethodInfo found = null;
            int n = 0;
            foreach (var m in mw.DeclaringType.TypeAsTBD.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != mb.Name)
                    continue;
                var ps = m.GetParameters();
                if (ps.Length != types.Length)
                    continue;
                var same = true;
                for (int i = 0; i < ps.Length && same; i++)
                    same = ps[i].ParameterType == types[i];
                if (same) { found = m; n++; }
            }
            if (n != 1)
            {
                Interlocked.Increment(ref LazyBodyStats.DefaultImplUnresolved);
                return null;
            }
            Interlocked.Increment(ref LazyBodyStats.DefaultImplResolved);
            return found;
        }

        /// <summary>Clears the relink-pending mark for a whole group, on any abandonment path.</summary>
        static void ClearPending(List<LazyBodyRegistry.Entry> entries)
        {
            foreach (var e in entries)
            {
                try
                {
                    var t = RelinkTarget(e.Method);
                    if (t != null)
                        LibIkvm.MarkRelinkPending(t.MethodHandle.Value, false);
                }
                catch { }
            }
        }

        /// <summary>Marks every detached entry terminal and clears any pending-JIT suppression even when an
        /// unexpected batch-level exception bypassed the normal cleanup paths.</summary>
        static void FinishRelinkBatch(List<int> ids)
        {
            foreach (var id in ids)
            {
                var entry = LazyBodyRegistry.Get(id);
                if (entry == null)
                    continue;

                try
                {
                    var method = RelinkTarget(entry.Method);
                    if (method != null)
                        LibIkvm.MarkRelinkPending(method.MethodHandle.Value, false);
                }
                catch { }

                Volatile.Write(ref entry.RelinkState, 2);
                entry.Release();
            }
        }

        static int relinkScratchSeq;
        static int relinkHelperSeq;

        sealed class RelinkPending
        {
            internal LazyBodyRegistry.Entry Entry;
            internal string Name;
            internal RuntimeByteCodeJavaType.FinishContext Finish;
        }

#endif

        /// <summary>
        /// Called once per generational method, from its trampoline, to produce the delegate the method will use
        /// from then on: a freshly recompiled second-generation body if that is possible, else the
        /// first-generation helper.
        /// </summary>
        /// <remarks>
        /// Never throws. A recompile failure has to degrade to the first-generation body, because the
        /// alternative is failing a method that was working perfectly well before this optimization existed.
        /// The first few failures are printed with the exception type so a systematic cause is visible without
        /// turning on tracing, then the log goes quiet.
        ///
        /// Declared unconditionally, with only the body compiled out, because ikvmc binds it by name against the
        /// FIRST_PASS reference assembly (ByteCodeHelperMethods.BuildLazyBody). Guarding the whole method makes
        /// the emitter fail with "Runtime method 'BuildLazyBody' not found" rather than silently skipping it.
        /// </remarks>
        public static Delegate BuildLazyBody(int id, Type owner, string helper, Type delegateType)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (JVM.LazyRecompile)
            {
                var limit = JVM.LazyRecompileLimit;
                var atLimit = limit > 0 && Interlocked.Read(ref lazyRecompiled) >= limit;

                try
                {
                    // Logged BEFORE the attempt and flushed, so a recompile that dies mid-way still names the
                    // method that killed it. Diagnosing this from a post-hoc log is otherwise guesswork.
                    if (JVM.TraceLazyBodies && atLimit == false)
                    {
                        var e = LazyBodyRegistry.Get(id);
                        Console.WriteLine($"[IKVM lazybody] attempt id={id} {e?.Wrapper?.Name}.{e?.Method?.Name}{e?.Method?.Signature}");
                        Console.Out.Flush();
                    }

                    var d = atLimit ? null : TryRecompileLazyBody(id, owner, delegateType);
                    if (d != null)
                    {
                        var n = Interlocked.Increment(ref lazyRecompiled);
                        if (JVM.TraceLazyBodies)
                        {
                            var e = LazyBodyRegistry.Get(id);
                            Console.WriteLine($"[IKVM lazybody] #{n} id={id} {e?.Wrapper?.Name}.{e?.Method?.Name}{e?.Method?.Signature}");
                            Console.Out.Flush();
                        }

                        if (n == 1 || n % 50 == 0)
                            Console.WriteLine($"[IKVM lazybody] recompiled={n} fellBack={Interlocked.Read(ref lazyFellBack)}");

                        return d;
                    }

                    if (atLimit == false && Interlocked.Read(ref lazyFellBack) < 8)
                        Console.WriteLine($"[IKVM lazybody] declined id {id} (not recompilable)");
                }
                catch (Exception e)
                {
                    if (Interlocked.Read(ref lazyFellBack) < 8 || JVM.TraceLazyBodies)
                    {
                        // Name the FAILING FRAME, not just the exception. Every Define* on an already-baked
                        // TypeBuilder throws the same InvalidOperation_TypeHasBeenCreated with no operand, and
                        // there are six candidates a recompile can reach (__<GetCallerID>, the dynamic
                        // instance-check and new-check caches, __<>classN class literals, __<>MHC dispatch
                        // stubs, __<>invokeCacheN). Guessing which one costs a build; the top frames say it.
                        var frames = (e.StackTrace ?? "").Split('\n');
                        var where = new System.Text.StringBuilder();
                        for (int fi = 0; fi < frames.Length && fi < 3; fi++)
                        {
                            if (fi > 0)
                                where.Append(" | ");
                            where.Append(frames[fi].Trim());
                        }
                        Console.WriteLine($"[IKVM lazybody] recompile FAILED id {id}: {e.GetType().Name}: {e.Message} AT {where}");
                    }
                }
            }

            var fell = Interlocked.Increment(ref lazyFellBack);
            if (fell == 1 || fell % 50 == 0)
                Console.WriteLine($"[IKVM lazybody] fellBack={fell} recompiled={Interlocked.Read(ref lazyRecompiled)}");

            return BindLazyFallback(owner, helper, delegateType);
#endif
        }

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

        /// <summary>
        /// Recompiles the registered method into a DynamicMethod, now that the types its body referenced are
        /// loaded, and returns it as <paramref name="delegateType"/>. Null means "not recompilable".
        /// </summary>
        static Delegate TryRecompileLazyBody(int id, Type owner, Type delegateType)
        {
            var entry = LazyBodyRegistry.Get(id);
            if (entry == null)
                return null;

            // MANDATORY reparse. MethodAnalyzer rewrote the original ClassFile's opcodes in place during the
            // first compile, so recompiling from it would emit the already-resolved form and defeat the point
            // (and, where it patched a __dynamic_* into a direct reference, emit something inconsistent).
            var classFile = entry.Reparse();
            if (classFile == null)
                return null;

            // DELIBERATELY still LoadMode.Link, unlike the in-place path's LinkNoLoad.
            //
            // The eager link carries a real hazard -- it can re-enter Knot from inside a Mixin transform and
            // leave a class defined without its mixins -- but at this path's volume it does not fire in
            // practice: control runs show 0-1 `ReEntrantTransformerError` against 189 for the batched
            // in-place path, which concentrates up to 48 classes' worth of linking into one arbitrary
            // execution point. This is the SHIPPED default, so changing it would move the baseline every
            // in-place measurement is compared against. Leave it until it is measured on its own.
            classFile.Link(entry.Wrapper, LoadMode.Link);
            var m = classFile.Methods[entry.MethodIndex];

            // Re-checked here and not only at candidacy: the analyzer has now run, so this is the authoritative
            // test, and the candidacy gate could only guess from the constant pool.
            if (Compiler.HasIndyOrMethodHandleSite(m))
                return null;

            var mw = entry.Method;
            var parameterTypesForDefine = mw.GetParametersForDefineMethod();
            var parameterTypes = m.IsStatic ? parameterTypesForDefine : PrependType(entry.Wrapper.TypeAsSignatureType, parameterTypesForDefine);
            var returnType = mw.ReturnTypeForDefineMethod;

            // A DynamicMethod signature cannot mention unbaked builders or open generics.
            if (IsUsableInDynamicMethod(returnType) == false)
                return null;
            for (int i = 0; i < parameterTypes.Length; i++)
                if (IsUsableInDynamicMethod(parameterTypes[i]) == false)
                    return null;

            // Owner-vs-module: DynamicMethod validates the owner and rejects interfaces (a default method's
            // declaring type is one), array/generic types, and TypeBuilders. Falling back to the module keeps
            // those cases recompilable instead of failing them; it cost 46 of 64 observed failures to learn
            // that the earlier "fix" of adjusting signature types was aimed at the wrong argument.
            var dm = IsUsableAsDynamicMethodOwner(owner)
                ? new DynamicMethod("__<>lazyrecompiled_" + id, returnType, parameterTypes, owner, true)
                : new DynamicMethod("__<>lazyrecompiled_" + id, returnType, parameterTypes, owner.Module, true);

            var context = JVM.Context;
            var ilgen = context.CodeEmitterFactory.Create(dm);

            // A recompile may want to define new nested types/fields, which the real TypeBuilder can no longer
            // accept because it has been baked. Give it a scratch type to define them on instead. Only needed
            // for the MethodHandle path, which is off by default.
            TypeBuilder scratch = null;

            var finish = new RuntimeByteCodeJavaType.FinishContext(context, entry.Host, classFile, entry.Wrapper, scratch ?? entry.TypeBuilder);
            // Tells the emitter that typeBuilder is baked and that once-unloadable types are now
            // resolvable, so entry checks take the cheap form instead of defining a fresh cache field.
            finish.IsLazyRecompile = true;
            var nonleaf = false;
            Compiler.Compile(finish, entry.Host, entry.Wrapper, entry.Method, classFile, m, ilgen, ref nonleaf);
            ilgen.CheckLabels();
            ilgen.DoEmit();

            // Did the second generation actually resolve anything? Nothing verified this before, and the
            // whole premise of the feature is that relinking with LoadMode.Link leaves no __dynamic_* behind.
            // It must be asked AFTER Compile: the __dynamic_* opcodes are assigned by MethodAnalyzer, which
            // runs inside the Compiler constructor, so asking earlier reads the pre-analysis opcode stream
            // and answers "clean" for every method -- the exact bug that once made the candidacy gate reject
            // everything while looking healthy.
            if (Compiler.HasDynamicReferences(m))
                Interlocked.Increment(ref LazyBodyStats.Gen2UnexpectedDynamic);
            else
                Interlocked.Increment(ref LazyBodyStats.Gen2Clean);

            if (scratch != null)
            {
                try
                {
                    // A body that needed a CallerID refers to storage on the scratch type, not the real one, so
                    // it must be discarded rather than installed.
                    if (finish.BakeScratchType(scratch) == false)
                    {
                        if (JVM.TraceLazyBodies)
                            Console.WriteLine($"[IKVM lazybody] declined id {id}: recompile needs a CallerID");

                        return null;
                    }
                }
                catch (Exception e)
                {
                    if (JVM.TraceLazyBodies)
                        Console.WriteLine($"[IKVM lazybody] scratch CreateType failed for id {id}: {e.GetType().Name}");

                    return null;
                }
            }

            return dm.CreateDelegate(delegateType);
        }

        /// <summary>
        /// Whether <paramref name="t"/> is acceptable as a DynamicMethod owner.
        /// </summary>
        static bool IsUsableAsDynamicMethodOwner(Type t)
        {
            return t != null
                && t.HasElementType == false
                && t.IsInterface == false
                && t.IsGenericParameter == false
                && t.ContainsGenericParameters == false
                && t is not TypeBuilder;
        }

        static Type[] PrependType(Type first, Type[] rest)
        {
            var a = new Type[rest.Length + 1];
            a[0] = first;
            Array.Copy(rest, 0, a, 1, rest.Length);
            return a;
        }

        /// <summary>
        /// Whether <paramref name="t"/> can appear in a DynamicMethod signature. Unwraps arrays/byrefs first,
        /// since it is the element type that carries the problem.
        /// </summary>
        static bool IsUsableInDynamicMethod(Type t)
        {
            if (t == null)
                return false;

            while (t.HasElementType)
                t = t.GetElementType();

            if (t is TypeBuilder || t is EnumBuilder || t is GenericTypeParameterBuilder)
                return false;

            return t.IsGenericParameter == false && t.ContainsGenericParameters == false;
        }

        /// <summary>
        /// Binds the first-generation helper as the method's implementation.
        /// </summary>
        static Delegate BindLazyFallback(Type owner, string helper, Type delegateType)
        {
            var mi = owner.GetMethod(helper, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null)
                throw new java.lang.InternalError("lazy body helper missing: " + owner.FullName + "." + helper);

            return Delegate.CreateDelegate(delegateType, mi);
        }

#endif

        [DebuggerStepThrough]
        public static T DynamicBinderMemberLookup<T>(int kind, string clazz, string name, string sig, ikvm.@internal.CallerID callerID)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
#if !IMPORTER
            // fast path: resolve the member and compile a small direct-call thunk instead of going
            // through the MethodHandle/LambdaForm machinery; shapes the thunk builder cannot handle
            // (missing members, CallerID, ghost/remapped receivers, packed-arg delegates, volatile
            // fields, access errors) fall through to the MethodHandle path below, which also
            // produces the correct linkage errors
            try
            {
                var direct = TryCreateDirectBinderDelegate<T>((MethodHandleKind)kind, clazz, name, sig, callerID);
                if (direct != null)
                    return direct;
            }
            catch (RetargetableJavaException e)
            {
                throw e.ToJava();
            }
            catch (NullReferenceException e)
            {
                // FALL THROUGH, DO NOT PROPAGATE. The fast path's contract, stated above, is that any
                // shape it cannot handle returns null and defers to the MethodHandle path below, "which
                // also produces the correct linkage errors". It honours that with ~15 explicit `return
                // null` arms -- and then lets a NullReferenceException escape, which breaks the contract
                // in the one case nobody anticipated: a shape whose wrappers are not fully populated
                // (a null FieldTypeWrapper/ReturnType/DeclaringType reaching one of the .Finish() or
                // .IsUnloadable derefs below the switch).
                //
                // An NRE here is always a gap in this method's shape handling, never a Java-level signal
                // -- the Java-level signals are RetargetableJavaException above and the linkage errors the
                // slow path raises -- so deferring is strictly the behaviour the comment already promises.
                //
                // OBSERVED, TWICE, A MONTH APART: 2026-08-11 and 2026-09-12, both
                //   DynamicBinderMemberLookup <- ChunkBuildBuffers.init(ChunkBuildBuffers.java:64)
                //       <- ChunkRenderRebuildTask.performBuild <- ChunkBuilder$WorkerRunnable.run
                // i.e. it killed a Sodium chunk-build worker, surfacing as a CompletionException NPE out
                // of FutureDequeDrain.next on the render thread -- a fatal, three frames from anything
                // that names this method. The slow path handles this site fine; it simply never ran.
                //
                // LOGGED, NOT SWALLOWED, and bounded the same way lazyFellBack is: a masked bug nobody can
                // see is worse than the crash it replaces. The member being bound is printed because that
                // is what this failure has never had -- both occurrences took a full log archaeology pass
                // to attribute, and neither said which member was being resolved.
                if (Interlocked.Increment(ref directBinderNre) <= 8 || JVM.TraceLazyBodies)
                {
                    var frames = (e.StackTrace ?? "").Split('\n');
                    var where = frames.Length > 0 ? frames[0].Trim() : "?";
                    Console.WriteLine($"[IKVM dynbinder] direct-path NRE, deferring to MethodHandle: kind={kind} {clazz}.{name}{sig} at {where}");
                }
            }
#endif

            try
            {
                var mh = DynamicLoadMethodHandleImpl(kind, clazz, name, sig, callerID);
                return GetDelegateForInvokeExact<T>(global::java.lang.invoke.MethodHandles.explicitCastArguments(mh, JVM.Context.MethodHandleUtil.GetDelegateMethodType(typeof(T))));
            }
            catch (global::java.lang.IncompatibleClassChangeError x)
            {
                if (x.getCause() is global::java.lang.NoSuchMethodException)
                {
                    throw new global::java.lang.NoSuchMethodError(x.getCause().Message);
                }
                if (x.getCause() is global::java.lang.NoSuchFieldException)
                {
                    throw new global::java.lang.NoSuchFieldError(x.getCause().Message);
                }
                if (x.getCause() is global::java.lang.IllegalAccessException)
                {
                    throw new global::java.lang.IllegalAccessError(x.getCause().Message);
                }
                throw;
            }
#endif
        }

#if !FIRST_PASS && !IMPORTER

        /// <summary>
        /// Attempts to resolve the member referenced by a __dynamic_* call site and compile a small
        /// DynamicMethod thunk with the call site delegate's exact signature that casts and invokes
        /// the member directly. Returns null for any shape it cannot handle; the caller then falls
        /// back to the MethodHandle path, which also produces the correct linkage errors.
        /// </summary>
        static T TryCreateDirectBinderDelegate<T>(MethodHandleKind kind, string clazz, string name, string sig, ikvm.@internal.CallerID callerID)
            where T : class, Delegate
        {
            var context = JVM.Context;
            var tw = LoadTypeWrapper(clazz, callerID);
            if (tw.IsGhost || tw.IsRemapped || tw.IsNonPrimitiveValueType || tw.IsFakeTypeContainer)
                return null;

            var caller = RuntimeJavaType.FromClass(callerID.getCallerClass());
            var invoke = context.MethodHandleUtil.GetDelegateInvokeMethod(typeof(T));
            var dp = invoke.GetParameters();

            RuntimeJavaMethod mw = null;
            RuntimeJavaField fw = null;
            RuntimeJavaType[] args;
            RuntimeJavaType ret;
            bool hasReceiver;

            switch (kind)
            {
                case MethodHandleKind.InvokeStatic:
                case MethodHandleKind.InvokeVirtual:
                case MethodHandleKind.InvokeInterface:
                case MethodHandleKind.NewInvokeSpecial:
                    mw = tw.GetMethod(name, sig, kind != MethodHandleKind.NewInvokeSpecial);
                    if (mw == null || mw.HasCallerID || mw.IsFinalizeOrClone)
                        return null;
                    mw.Link();
                    if (!mw.IsAccessibleFrom(tw, caller, tw))
                        return null;
                    if (kind == MethodHandleKind.InvokeStatic ? !mw.IsStatic : mw.IsStatic)
                        return null;
                    if (kind == MethodHandleKind.NewInvokeSpecial && (!mw.IsConstructor || tw.IsAbstract))
                        return null;
                    if ((kind == MethodHandleKind.InvokeVirtual || kind == MethodHandleKind.InvokeInterface) && mw.IsConstructor)
                        return null;
                    hasReceiver = kind == MethodHandleKind.InvokeVirtual || kind == MethodHandleKind.InvokeInterface;
                    args = mw.GetParameters();
                    ret = kind == MethodHandleKind.NewInvokeSpecial ? tw : mw.ReturnType;
                    break;
                case MethodHandleKind.GetField:
                case MethodHandleKind.GetStatic:
                case MethodHandleKind.PutField:
                case MethodHandleKind.PutStatic:
                    fw = tw.GetFieldWrapper(name, sig);
                    if (fw == null)
                        return null;
                    fw.Link();
                    if (!fw.IsAccessibleFrom(tw, caller, tw))
                        return null;
                    var isStaticKind = kind == MethodHandleKind.GetStatic || kind == MethodHandleKind.PutStatic;
                    var isPut = kind == MethodHandleKind.PutField || kind == MethodHandleKind.PutStatic;
                    if (fw.IsStatic != isStaticKind)
                        return null;
                    // preserve the MethodHandle path's final-field error and volatile access semantics
                    if (isPut && fw.IsFinal)
                        return null;
                    if (fw.IsVolatile)
                        return null;
                    hasReceiver = !isStaticKind;
                    args = isPut ? new[] { fw.FieldTypeWrapper } : Array.Empty<RuntimeJavaType>();
                    ret = isPut ? context.PrimitiveJavaTypeFactory.VOID : fw.FieldTypeWrapper;
                    break;
                default:
                    return null;
            }

            var firstArg = hasReceiver ? 1 : 0;

            // a length mismatch means the delegate uses packed args (arity > MaxArity)
            if (dp.Length != firstArg + args.Length)
                return null;

            if (hasReceiver && (dp[0].ParameterType.IsValueType || tw.TypeAsSignatureType.IsValueType))
                return null;
            for (int i = 0; i < args.Length; i++)
                if (!CanEmitDirectBinderConversion(dp[firstArg + i].ParameterType, args[i]))
                    return null;
            if (!CanEmitDirectBinderReturn(ret, invoke.ReturnType, context))
                return null;

            // the DynamicMethod signature and body must not reference unbaked types (unloadable
            // signature types are already erased to object and cannot be finished), and the member
            // must be re-resolved from its builder to the baked runtime member
            tw.Finish();
            foreach (var arg in args)
                if (!arg.IsUnloadable)
                    arg.Finish();
            if (!ret.IsUnloadable)
                ret.Finish();

            if (mw != null)
            {
                mw.DeclaringType.Finish();
                mw.ResolveMethod();
            }
            else
            {
                fw.DeclaringType.Finish();
                fw.ResolveField();
            }

            Profiler.Count("DynamicBinderDirect");

            // owning the DynamicMethod with the target type can run the target's class initializer
            // at delegate creation; that matches JVMS timing for the kinds that must initialize on
            // first execution (we are inside the referencing instruction's first execution), but
            // getfield/invokevirtual/invokeinterface must never trigger initialization (JVMS §5.5)
            var initializes = kind == MethodHandleKind.InvokeStatic || kind == MethodHandleKind.NewInvokeSpecial
                || kind == MethodHandleKind.GetStatic || kind == MethodHandleKind.PutStatic;
            var owner = initializes ? tw.TypeAsBaseType : typeof(ByteCodeHelper);
            if (!ReflectUtil.CanOwnDynamicMethod(owner))
                owner = typeof(ByteCodeHelper);

            var dpTypes = new Type[dp.Length];
            for (int i = 0; i < dp.Length; i++)
                dpTypes[i] = dp[i].ParameterType;

            var dm = new DynamicMethod($"__<>DynamicBinder__{tw.Name}__{name}", invoke.ReturnType, dpTypes, owner, true);
            var ilgen = context.CodeEmitterFactory.Create(dm);

            if (hasReceiver)
            {
                ilgen.EmitLdarg(0);
                if (dpTypes[0] != tw.TypeAsSignatureType)
                    tw.EmitCheckcast(ilgen);
            }

            for (int i = 0; i < args.Length; i++)
            {
                ilgen.EmitLdarg(firstArg + i);
                EmitDirectBinderConversion(ilgen, dpTypes[firstArg + i], args[i]);
            }

            if (mw != null)
            {
                if (kind == MethodHandleKind.NewInvokeSpecial)
                    mw.EmitNewobj(ilgen);
                else if (hasReceiver)
                    mw.EmitCallvirt(ilgen);
                else
                    mw.EmitCall(ilgen);
            }
            else if (kind == MethodHandleKind.GetField || kind == MethodHandleKind.GetStatic)
            {
                fw.EmitGet(ilgen);
            }
            else
            {
                fw.EmitSet(ilgen);
            }

            EmitDirectBinderReturn(ilgen, ret, invoke.ReturnType, context);

            ilgen.Emit(OpCodes.Ret);
            ilgen.DoEmit();

            return (T)dm.CreateDelegate(typeof(T));
        }

        static bool CanEmitDirectBinderConversion(Type have, RuntimeJavaType want)
        {
            if (want.IsUnloadable)
                return !have.IsValueType;

            var wantType = want.TypeAsSignatureType;
            if (have == wantType)
                return true;

            if (want.IsGhost)
                return !have.IsValueType;

            // primitive and value-type shapes must match exactly; everything else is a reference cast
            if (want.IsPrimitive || wantType.IsValueType || have.IsValueType)
                return false;

            return true;
        }

        static void EmitDirectBinderConversion(CodeEmitter ilgen, Type have, RuntimeJavaType want)
        {
            if (want.IsUnloadable || have == want.TypeAsSignatureType)
                return;

            want.EmitCheckcast(ilgen);
            if (want.IsGhost)
                want.EmitConvStackTypeToSignatureType(ilgen, null);
        }

        static bool CanEmitDirectBinderReturn(RuntimeJavaType ret, Type want, RuntimeContext context)
        {
            if (ret == context.PrimitiveJavaTypeFactory.VOID)
                return want == typeof(void);

            var retType = ret.TypeAsSignatureType;
            if (retType == want)
                return true;

            if (ret.IsGhost)
                return !want.IsValueType;

            if (ret.IsPrimitive || retType.IsValueType || want.IsValueType)
                return false;

            return true;
        }

        static void EmitDirectBinderReturn(CodeEmitter ilgen, RuntimeJavaType ret, Type want, RuntimeContext context)
        {
            if (ret == context.PrimitiveJavaTypeFactory.VOID || ret.TypeAsSignatureType == want)
                return;

            if (ret.IsGhost)
                ret.EmitConvSignatureTypeToStackType(ilgen);

            if (want != typeof(object) && !want.IsValueType)
                ilgen.EmitCastclass(want);
        }

#endif

        [DebuggerStepThrough]
        public static Delegate DynamicCreateDelegate(object obj, Type delegateType, string name, string sig)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            RuntimeJavaType tw = RuntimeJavaType.FromClass(global::ikvm.runtime.Util.getClassFromObject(obj));
            RuntimeJavaMethod mw = tw.GetMethod(name, sig, true);
            if (mw == null || mw.IsStatic || !mw.IsPublic)
            {
#if NO_REF_EMIT
				global::java.lang.invoke.MethodType methodType = MethodHandleUtil.GetDelegateMethodType(delegateType);
				if (methodType.parameterCount() > MethodHandleUtil.MaxArity)
				{
					throw new NotImplementedException();
				}
				global::java.lang.invoke.MethodHandle exception = global::java.lang.invoke.MethodHandles.publicLookup()
					.findConstructor(mw == null || mw.IsStatic ? typeof(global::java.lang.AbstractMethodError) : typeof(global::java.lang.IllegalAccessError),
						global::java.lang.invoke.MethodType.methodType(typeof(void), typeof(string)))
					.bindTo(tw.Name + ".Invoke" + sig);
				return Delegate.CreateDelegate(delegateType,
						global::java.lang.invoke.MethodHandles.dropArguments(
							global::java.lang.invoke.MethodHandles.foldArguments(global::java.lang.invoke.MethodHandles.throwException(methodType.returnType(), exception.type().returnType()), exception),
							0, methodType.parameterArray()).vmtarget, "Invoke");
#else
                MethodInfo invoke = delegateType.GetMethod("Invoke");
                ParameterInfo[] parameters = invoke.GetParameters();
                Type[] parameterTypes = new Type[parameters.Length + 1];
                parameterTypes[0] = typeof(object);
                for (int i = 0; i < parameters.Length; i++)
                {
                    parameterTypes[i + 1] = parameters[i].ParameterType;
                }
                System.Reflection.Emit.DynamicMethod dm = new System.Reflection.Emit.DynamicMethod("Invoke", invoke.ReturnType, parameterTypes);
                CodeEmitter ilgen = JVM.Context.CodeEmitterFactory.Create(dm);
                ilgen.Emit(System.Reflection.Emit.OpCodes.Ldstr, tw.Name + ".Invoke" + sig);
                JVM.Context.ClassLoaderFactory.GetBootstrapClassLoader()
                    .LoadClassByName(mw == null || mw.IsStatic ? "global::java.lang.AbstractMethodError" : "global::java.lang.IllegalAccessError")
                    .GetMethod("<init>", "(Lglobal::java.lang.String;)V", false)
                    .EmitNewobj(ilgen);
                ilgen.Emit(System.Reflection.Emit.OpCodes.Throw);
                ilgen.DoEmit();
                return dm.CreateDelegate(delegateType, obj);
#endif
            }
            else
            {
                mw.ResolveMethod();
                return Delegate.CreateDelegate(delegateType, obj, (MethodInfo)mw.GetMethod());
            }
#endif
        }

        [DebuggerStepThrough]
        public static ikvm.@internal.CallerID DynamicCallerID(object capability)
        {
            return ((DynamicCallerIDProvider)capability).GetCallerID();
        }

        [DebuggerStepThrough]
        public static global::java.lang.invoke.MethodHandle DynamicEraseInvokeExact(global::java.lang.invoke.MethodHandle mh, global::java.lang.invoke.MethodType expected, global::java.lang.invoke.MethodType target)
        {
#if FIRST_PASS
            return null;
#else
            if (mh.type() != expected)
            {
                throw new global::java.lang.invoke.WrongMethodTypeException();
            }
            return global::java.lang.invoke.MethodHandles.explicitCastArguments(mh, target);
#endif
        }

#endif

        [DebuggerStepThrough]
        public static int f2i(float f)
        {
            if (f > int.MinValue && f < int.MaxValue)
            {
                return (int)f;
            }
            if (f <= int.MinValue)
            {
                return int.MinValue;
            }
            if (f >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return 0;
        }

        [DebuggerStepThrough]
        public static long f2l(float f)
        {
            if (f > long.MinValue && f < long.MaxValue)
            {
                return (long)f;
            }
            if (f <= long.MinValue)
            {
                return long.MinValue;
            }
            if (f >= long.MaxValue)
            {
                return long.MaxValue;
            }
            return 0;
        }

        [DebuggerStepThrough]
        public static int d2i(double d)
        {
            if (d > int.MinValue && d < int.MaxValue)
            {
                return (int)d;
            }
            if (d <= int.MinValue)
            {
                return int.MinValue;
            }
            if (d >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return 0;
        }

        [DebuggerStepThrough]
        public static long d2l(double d)
        {
            if (d > long.MinValue && d < long.MaxValue)
            {
                return (long)d;
            }
            if (d <= long.MinValue)
            {
                return long.MinValue;
            }
            if (d >= long.MaxValue)
            {
                return long.MaxValue;
            }
            return 0;
        }

#if EXPORTER == false

        // This is used by static JNI and synchronized methods that need a class object
        [DebuggerStepThrough]
        public static object GetClassFromTypeHandle(RuntimeTypeHandle typeHandle)
        {
            return IKVM.Java.Externs.ikvm.runtime.Util.getClassFromTypeHandle(typeHandle);
        }

        [DebuggerStepThrough]
        public static void arraycopy(object src, int srcStart, object dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
#if NET
            if (JVM.ArraycopyObj && src is object[] s && dest is object[] d)
            {
                // Reference arrays, any runtime element types (IKVM_ARRAYCOPY_OBJ, R429/R430/R435), in this frame: the
                // copies TimSort makes are short and many, and each nested call (a helper, Span.CopyTo, the bulk-move
                // icall) was a per-copy cost. Java's rules: bounds before anything is copied; an element the destination
                // cannot hold throws ArrayStoreException with the elements before it already copied.
                if (len < 0 || srcStart < 0 || destStart < 0 || srcStart > s.Length - len || destStart > d.Length - len)
                    throw new global::java.lang.ArrayIndexOutOfBoundsException();
                ref object sr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(s), srcStart);
                ref object dr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(d), destStart);
                // Every element is storable when they are one array, the same array type, or the destination is exactly
                // object[]. A short range is a ref-store loop (each store card-marks inline; backwards when one array's
                // ranges overlap upwards), a longer one Span.CopyTo (the runtime's bulk array-ref copy).
                if (s == d || (object)d.GetType() == (object)typeof(object[]) || (object)s.GetType() == (object)d.GetType())
                {
                    if (len > JVM.ArraycopyLoop)
                        MemoryMarshal.CreateReadOnlySpan(ref sr, len).CopyTo(MemoryMarshal.CreateSpan(ref dr, len));
                    else if (s == d && destStart > srcStart)
                        for (int i = len - 1; i >= 0; i--)
                            Unsafe.Add(ref dr, i) = Unsafe.Add(ref sr, i);
                    else
                        for (int i = 0; i < len; i++)
                            Unsafe.Add(ref dr, i) = Unsafe.Add(ref sr, i);
                    return;
                }
                // Otherwise every element needs a store check (TimSort's object[] tmp back into the Pair[] it sorts). A
                // checked store that succeeds proves the element's exact class storable, so later elements of that class
                // are stored directly. Each element is loaded once and the value checked is the value stored, so a thread
                // writing src concurrently cannot slip an unstorable object past the check. Distinct arrays never overlap.
                object ok = null;
                for (int i = 0; i < len; i++)
                {
                    object o = Unsafe.Add(ref sr, i);
                    if (o != null && (object)o.GetType() != ok)
                    {
                        d[destStart + i] = o; // ArrayTypeMismatchException surfaces as ArrayStoreException
                        ok = o.GetType();
                    }
                    else
                    {
                        Unsafe.Add(ref dr, i) = o;
                    }
                }
                return;
            }
#endif
            // If the two arrays are the same, we can use the fast path, but we're also required to do so,
            // to get the required memmove semantics.
            if (src == dest)
            {
                try
                {
                    arraycopy_fast((Array)src, srcStart, (Array)dest, destStart, len);
                    return;
                }
                catch (InvalidCastException)
                {
                    throw new global::java.lang.ArrayStoreException();
                }
            }
            else if (src == null || dest == null)
            {
                throw new global::java.lang.NullPointerException();
            }
            else if (len < 0)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
            else
            {
                if (src is object[] src1 && dest is object[] dst1)
                {
                    // for small copies, don't bother comparing the types as this is relatively expensive
                    if (len > 100 && src.GetType() == dest.GetType())
                    {
                        arraycopy_fast(src1, srcStart, dst1, destStart, len);
                        return;
                    }
                    else
                    {
                        for (; len > 0; len--)
                        {
                            // NOTE we don't need to catch ArrayTypeMismatchException & IndexOutOfRangeException, because
                            // they automatically get converted to the Java equivalents anyway.
                            dst1[destStart++] = src1[srcStart++];
                        }

                        return;
                    }
                }
                else if (src.GetType() != dest.GetType() && (IsPrimitiveArrayType(src.GetType()) || IsPrimitiveArrayType(dest.GetType())))
                {
                    // we don't want to allow copying a primitive into an object array!
                    throw new global::java.lang.ArrayStoreException();
                }
                else
                {
                    try
                    {
                        arraycopy_fast((Array)src, srcStart, (Array)dest, destStart, len);
                        return;
                    }
                    catch (InvalidCastException)
                    {
                        throw new global::java.lang.ArrayStoreException();
                    }
                }
            }
#endif
        }

        private static bool IsPrimitiveArrayType(Type type)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return type.IsArray && JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type.GetElementType()).IsPrimitive;
#endif
        }

        [DebuggerStepThrough]
        public static void arraycopy_fast(Array src, int srcStart, Array dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Array.Copy(src, srcStart, dest, destStart, len);
            }
            catch (ArgumentNullException)
            {
                throw new global::java.lang.NullPointerException();
            }
            catch (ArgumentException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
#endif
        }

        [DebuggerStepThrough]
        public static void arraycopy_primitive_8(Array src, int srcStart, Array dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                checked
                {
                    Buffer.BlockCopy(src, srcStart * 8, dest, destStart * 8, len * 8);
                    return;
                }
            }
            catch (ArgumentNullException)
            {
                throw new global::java.lang.NullPointerException();
            }
            catch (OverflowException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
            catch (ArgumentException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
#endif
        }

        [DebuggerStepThrough]
        public static void arraycopy_primitive_4(Array src, int srcStart, Array dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                checked
                {
                    Buffer.BlockCopy(src, srcStart * 4, dest, destStart * 4, len * 4);
                    return;
                }
            }
            catch (ArgumentNullException)
            {
                throw new global::java.lang.NullPointerException();
            }
            catch (OverflowException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
            catch (ArgumentException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
#endif
        }

        [DebuggerStepThrough]
        public static void arraycopy_primitive_2(Array src, int srcStart, Array dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                checked
                {
                    Buffer.BlockCopy(src, srcStart * 2, dest, destStart * 2, len * 2);
                    return;
                }
            }
            catch (ArgumentNullException)
            {
                throw new global::java.lang.NullPointerException();
            }
            catch (OverflowException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
            catch (ArgumentException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
#endif
        }

        [DebuggerStepThrough]
        public static void arraycopy_primitive_1(Array src, int srcStart, Array dest, int destStart, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Buffer.BlockCopy(src, srcStart, dest, destStart, len);
                return;
            }
            catch (ArgumentNullException)
            {
                throw new global::java.lang.NullPointerException();
            }
            catch (OverflowException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
            catch (ArgumentException)
            {
                throw new global::java.lang.ArrayIndexOutOfBoundsException();
            }
#endif
        }

        [HideFromJava]
        public static void VerboseCastFailure(RuntimeTypeHandle typeHandle, object obj)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            Type t1 = obj.GetType();
            Type t2 = Type.GetTypeFromHandle(typeHandle);
            string msg;
            if (t1.Assembly.FullName == t2.Assembly.FullName && t1.Assembly.Location != t2.Assembly.Location)
            {
                string l1 = t1.Assembly.Location;
                string l2 = t2.Assembly.Location;
                if (l1 == "")
                {
                    l1 = "unknown location";
                }
                if (l2 == "")
                {
                    l2 = "unknown location";
                }
                msg = String.Format("Object of type \"{0}\" loaded from {1} cannot be cast to \"{2}\" loaded from {3}", t1.AssemblyQualifiedName, l1, t2.AssemblyQualifiedName, l2);
            }
            else
            {
                msg = String.Format("Object of type \"{0}\" cannot be cast to \"{1}\"", t1.AssemblyQualifiedName, t2.AssemblyQualifiedName);
            }
            throw new global::java.lang.ClassCastException(msg);
#endif
        }

        /// <summary>
        /// Returns <c>true</c> if the Java finalizer should be skipped.
        /// </summary>
        /// <returns></returns>
        public static bool SkipFinalizer()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return Environment.HasShutdownStarted && !global::java.lang.Shutdown.runFinalizersOnExit;
#endif
        }

        /// <summary>
        /// Returns <c>true</c> if the Java finalizer for the given object should be skipped.
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public static bool SkipFinalizer(object o)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (SkipFinalizer())
                return true;

            if (o is global::java.lang.Object jlo)
                if (ObjectAccessor.GetInit(jlo) == false)
                    return true;

            return false;
#endif
        }

#endif

        // wasm fork: the 20 volatile/CAS helpers below drop the try/catch (SEHException) upstream IKVM wraps them in.
        // An SEHException cannot occur on wasm, and the clause made every one a CLAUSE METHOD, which mono's
        // llvm-only AOT compiles in deopt mode (an LMF push, an il_state holding the address of every argument, a landing
        // pad): ~150-175 instructions per call for one atomic operation. NoInlining keeps today's shape for JIT'd callers
        // -- a clause method was never inlinable -- because the runtime's wasm JIT does not yet lower the atomic
        // intrinsics these bodies expand to (OP_ATOMIC_LOAD_*, OP_ATOMIC_CAS_I4, ...), so inlining one would bail the
        // caller. Drop the attribute once it does.

        /// <summary>
        /// Implements a volatile read against a reference to a bool. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool VolatileRead(ref bool location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a bool. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref bool location, bool value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a byte. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static byte VolatileRead(ref byte location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a byte. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref byte location, byte value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a char. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static char VolatileRead(ref char location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return (char)Volatile.Read(ref Unsafe.As<char, short>(ref location));
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a char. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref char location, char value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref Unsafe.As<char, short>(ref location), (short)value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a short. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static short VolatileRead(ref short location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a short. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref short location, short value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a int. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int VolatileRead(ref int location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a int. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref int location, int value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a long. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long VolatileRead(ref long location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            // Volatile.Read is atomic for 64-bit values on every platform .NET runs on, and a Java volatile read
            // needs acquire only (the fence lives on the write, which stays Interlocked.Exchange). The CAS this
            // replaced turned every read into a locked read-modify-write.
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a long. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref long location, long value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Interlocked.Exchange(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a float. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float VolatileRead(ref float location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a float. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref float location, float value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Volatile.Write(ref location, value);
#endif
        }

        /// <summary>
        /// Implements a volatile read against a reference to a double. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double VolatileRead(ref double location)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            // See VolatileRead(ref long).
            return Volatile.Read(ref location);
#endif
        }

        /// <summary>
        /// Implements a volatile write against a reference to a double. In Java, a volatile operation is atomic.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void VolatileWrite(ref double location, double value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            Interlocked.Exchange(ref location, value);
#endif
        }

        /// <summary>
        /// Implements the compare and swap operation against a reference to an object.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="expected"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CompareAndSwap(ref object location, object expected, object value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Interlocked.CompareExchange(ref location, value, expected) == expected;
#endif
        }

        /// <summary>
        /// Implements the compare and swap operation against a reference to an int.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="expected"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CompareAndSwap(ref int location, int expected, int value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Interlocked.CompareExchange(ref location, value, expected) == expected;
#endif
        }

        /// <summary>
        /// Implements a compare and swap operation against a reference to a long.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="expected"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CompareAndSwap(ref long location, long expected, long value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Interlocked.CompareExchange(ref location, value, expected) == expected;
#endif
        }

        /// <summary>
        /// Implements a compare and swap operation against a reference to a double.
        /// </summary>
        /// <param name="location"></param>
        /// <param name="expected"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool CompareAndSwap(ref double location, double expected, double value)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return Interlocked.CompareExchange(ref location, value, expected) == expected;
#endif
        }

#if EXPORTER == false

        /// <summary>
        /// Invoked by exported assemblies.
        /// </summary>
        /// <param name="module"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InitializeModule(Module module)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var asm = Assembly.GetCallingAssembly();
            if (module.Assembly != asm)
                throw new ArgumentOutOfRangeException();

            // check for InitializeModule method present on classloader
            var classLoader = JVM.Context.AssemblyClassLoaderFactory.FromAssembly(asm).GetJavaClassLoader();
            if (classLoader != null)
            {
                var init = (Action<Module>)Delegate.CreateDelegate(typeof(Action<Module>), classLoader, "InitializeModule", false, false);
                if (init != null)
                    init(module);
            }
#endif
        }

        public static T GetDotNetEnumField<T>(string name)
#if !FIRST_PASS
            where T : global::java.lang.Enum
#endif
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return (T)global::java.lang.Enum.valueOf((global::java.lang.Class)ClassLiteral<T>.Value, name);
            }
            catch (global::java.lang.IllegalArgumentException)
            {
                throw new global::java.lang.NoSuchFieldError(((global::java.lang.Class)ClassLiteral<T>.Value).getName() + "." + name);
            }
#endif
        }

        [Flags]
        public enum MapFlags
        {
            None = 0,
            NoRemapping = 1,
            Unused = 2,
        }

        [HideFromJava]
        public static T MapException<T>(Exception x, MapFlags mode) where T : Exception
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return JVM.Context.ExceptionHelper.MapException<T>(x, (mode & MapFlags.NoRemapping) == 0, (mode & MapFlags.Unused) != 0);
#endif
        }

        [HideFromJava]
        public static Exception DynamicMapException(Exception x, MapFlags mode, global::java.lang.Class exceptionClass)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var exceptionTypeWrapper = RuntimeJavaType.FromClass(exceptionClass);
            mode &= ~MapFlags.NoRemapping;
            if (exceptionTypeWrapper.IsSubTypeOf(JVM.Context.JavaBase.TypeOfCliSystemException))
                mode |= MapFlags.NoRemapping;

            var exceptionType = exceptionTypeWrapper == JVM.Context.JavaBase.TypeOfjavaLangThrowable ? typeof(System.Exception) : exceptionTypeWrapper.TypeAsBaseType;
            return (Exception)JVM.Context.ByteCodeHelperMethods.MapException.MakeGenericMethod(exceptionType).Invoke(null, new object[] { x, mode });
#endif
        }

        public static T GetDelegateForInvokeExact<T>(object methodHandle)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var methodHandle_ = (global::java.lang.invoke.MethodHandle)methodHandle;
            var del = methodHandle_._invokeExactDelegate as T;
            del ??= JVM.Context.MethodHandleUtil.GetDelegateForInvokeExact<T>(methodHandle_);
            return del;
#endif
        }

        /// <summary>
        /// Returns the delegate an invokeExact call site dispatches to, without checking that it matches the call
        /// site's signature.
        /// </summary>
        /// <remarks>
        /// Untyped for the same reason as <see cref="GetTargetForInvokeBasic"/>: the call site knows its own delegate
        /// type concretely and can test it with an inline isinst, where a generic helper would need a shared-generic
        /// one. The call site is responsible for making a mismatch reach <see cref="WrongMethodType{T}"/>, which is
        /// what gives invokeExact its exact-match semantics.
        /// </remarks>
        /// <param name="methodHandle"></param>
        /// <returns></returns>
        public static object GetTargetForInvokeExact(object methodHandle)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var methodHandle_ = (global::java.lang.invoke.MethodHandle)methodHandle;
            return methodHandle_._invokeExactDelegate ?? JVM.Context.MethodHandleUtil.GetTargetForInvokeExact(methodHandle_);
#endif
        }

        /// <summary>
        /// Throws the <see cref="global::java.lang.invoke.WrongMethodTypeException"/> for an invokeExact whose target
        /// does not have the call site's exact signature. Only reached when the call fails, so this is the one place
        /// the call site's type has to be materialized generically. Declared to return <typeparamref name="T"/> so the
        /// call site's two paths agree on what they leave on the stack; it never returns.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="methodHandle"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T ThrowWrongMethodType<T>(object methodHandle)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var methodHandle_ = (global::java.lang.invoke.MethodHandle)methodHandle;
            throw global::java.lang.invoke.Invokers.newWrongMethodTypeException(LoadMethodType<T>(), methodHandle_.type());
#endif
        }

        public static T GetDelegateForInvoke<T>(object methodHandle, object realType, ref InvokeCache<T> cache)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var methodHandle_ = (global::java.lang.invoke.MethodHandle)methodHandle;
            var realType_ = (global::java.lang.invoke.MethodType)realType;

            T del;
            if (cache.Type == methodHandle_.type() && (del = (methodHandle_.isVarargsCollector() ? cache.varArg : cache.fixedArg)) != null)
                return del;

            del = methodHandle_.form.vmentry.vmtarget as T;
            if (del == null)
            {
                var adapter = global::java.lang.invoke.MethodHandles.exactInvoker(methodHandle_.type());
                if (methodHandle_.isVarargsCollector())
                    adapter = adapter.asVarargsCollector(methodHandle_.type().parameterType(methodHandle_.type().parameterCount() - 1));

                // if realType is set, the delegate contains erased unloadable types
                if (realType_ != null)
                    adapter = adapter.asType(realType_.insertParameterTypes(0, (global::java.lang.Class)ClassLiteral<global::java.lang.invoke.MethodHandle>.Value)).asFixedArity();

                adapter = adapter.asType(JVM.Context.MethodHandleUtil.GetDelegateMethodType(typeof(T)));
                del = GetDelegateForInvokeExact<T>(adapter);
                if (cache.TrySetType(methodHandle_.type()))
                {
                    if (methodHandle_.isVarargsCollector())
                        cache.varArg = del;
                    else
                        cache.fixedArg = del;
                }
            }

            return del;
#endif
        }

        /// <remarks>
        /// Emitted call sites do not use this: they call <see cref="GetTargetForInvokeBasic"/> or
        /// <see cref="GetVoidTargetForInvokeBasic"/> and cast the result themselves. See those for why.
        /// </remarks>
        public static T GetDelegateForInvokeBasic<T>(global::java.lang.invoke.MethodHandle h)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var del = h.form.vmentry.vmtarget as T;
            del ??= MethodHandleUtil.GetVoidAdapter(h.form.vmentry) as T;
            return del;
#endif
        }

        /// <summary>
        /// Returns the delegate a MethodHandle's LambdaForm dispatches to, for an invokeBasic call site that keeps the
        /// return value.
        /// </summary>
        /// <remarks>
        /// Every invokeBasic in every compiled LambdaForm runs this, so it is deliberately untyped. The call site's
        /// delegate type is a concrete type there, so it can cast with a plain castclass; a generic helper returning T
        /// would instead compile to a shared-generic isinst, which costs a runtime generic context lookup and an
        /// out-of-line type check on every single dispatch.
        /// </remarks>
        /// <param name="h"></param>
        /// <returns></returns>
        public static object GetTargetForInvokeBasic(global::java.lang.invoke.MethodHandle h)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return h.form.vmentry.vmtarget;
#endif
        }

        /// <summary>
        /// The counterpart of <see cref="GetTargetForInvokeBasic"/> for a call site that discards the return value. The
        /// LambdaForm may still produce one, in which case dispatch goes through an adapter that pops it.
        /// </summary>
        /// <remarks>
        /// Which of the two a call site needs is decided by its own return type, so the emitter picks between them and
        /// no dispatch has to discover it by way of a failed cast.
        /// </remarks>
        /// <param name="h"></param>
        /// <returns></returns>
        public static object GetVoidTargetForInvokeBasic(global::java.lang.invoke.MethodHandle h)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return MethodHandleUtil.GetVoidAdapter(h.form.vmentry);
#endif
        }

        public static global::java.lang.invoke.MethodType LoadMethodType<T>()
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return JVM.Context.MethodHandleUtil.GetDelegateMethodType(typeof(T));
#endif
        }

        [HideFromJava]
        public static void LinkIndyCallSite<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            LinkIndyCallSiteCore(ref site, cs, x, false);
#endif
        }

        /// <summary>
        /// <see cref="LinkIndyCallSite{T}"/> for a site whose bootstrap method is LambdaMetafactory.metafactory or
        /// altMetafactory: links it to a direct target when the CallSite's shape allows one. Emitted only under
        /// JVM.IndyDirect.
        /// </summary>
        [HideFromJava]
        public static void LinkLambdaIndyCallSite<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            LinkIndyCallSiteCore(ref site, cs, x, true);
#endif
        }

        [HideFromJava]
        public static void DynamicLinkIndyCallSite<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x, string signature, ikvm.@internal.CallerID callerID)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            DynamicLinkIndyCallSiteCore(ref site, cs, x, signature, callerID, false);
#endif
        }

        /// <summary>
        /// <see cref="DynamicLinkIndyCallSite{T}"/> for a LambdaMetafactory site; see <see cref="LinkLambdaIndyCallSite{T}"/>.
        /// </summary>
        [HideFromJava]
        public static void DynamicLinkLambdaIndyCallSite<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x, string signature, ikvm.@internal.CallerID callerID)
            where T : class, Delegate
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            DynamicLinkIndyCallSiteCore(ref site, cs, x, signature, callerID, true);
#endif
        }

#if !FIRST_PASS

        [HideFromJava]
        static void LinkIndyCallSiteCore<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x, bool lambda)
            where T : class, Delegate
        {
            // when a CallSite is first constructed, it doesn't call MethodHandleNatives.setCallSiteTargetNormal(),
            // so we have to check if we need to initialize it here (i.e. attach an IndyCallSite<T> to it)
            if (cs != null)
            {
                if (cs.ics == null)
                    MethodHandleNatives.InitializeCallSite(cs);

                lock (cs.ics)
                    cs.ics.SetTarget(cs.target);
            }

            IndyCallSite<T> ics;
            IndyCallSite<T> direct = null;
            if (x != null || cs == null || (ics = cs.ics as IndyCallSite<T>) == null)
            {
                x = MapException<Exception>(x ?? (cs == null
                    ? (Exception)new global::java.lang.ClassCastException("bootstrap method failed to produce a CallSite")
                    : new global::java.lang.invoke.WrongMethodTypeException()), MapFlags.None);
                global::java.lang.invoke.MethodType type = LoadMethodType<T>();
                global::java.lang.invoke.MethodHandle exc = x is global::java.lang.BootstrapMethodError
                    ? global::java.lang.invoke.MethodHandles.constant(typeof(global::java.lang.BootstrapMethodError), x)
                    : global::java.lang.invoke.MethodHandles.publicLookup().findConstructor(typeof(global::java.lang.BootstrapMethodError), global::java.lang.invoke.MethodType.methodType(typeof(void), typeof(string), typeof(Exception)))
                        .bindTo("call site initialization exception").bindTo(x);
                ics = new IndyCallSite<T>();
                ((IIndyCallSite)ics).SetTarget(
                    global::java.lang.invoke.MethodHandles.dropArguments(
                        global::java.lang.invoke.MethodHandles.foldArguments(
                            global::java.lang.invoke.MethodHandles.throwException(type.returnType(), typeof(global::java.lang.BootstrapMethodError)),
                                exc),
                        0, type.parameterArray()));
            }
            else if (lambda && (direct = TryCreateDirectLambdaCallSite<T>(cs)) != null)
            {
                // wasm fork (JVM.IndyDirect): replaces cs.ics for THIS site only. cs is a ConstantCallSite, so cs.ics
                // can never be retargeted, and nothing else reads this site's field.
                ics = direct;
            }

            var curr = site;
            if (curr.IsBootstrap && Interlocked.CompareExchange(ref site, ics, curr) == curr && direct != null)
                LazyBodyStats.NoteIndyInstalled();
        }

        [HideFromJava]
        static void DynamicLinkIndyCallSiteCore<T>(ref IndyCallSite<T> site, global::java.lang.invoke.CallSite cs, Exception x, string signature, ikvm.@internal.CallerID callerID, bool lambda)
            where T : class, Delegate
        {
            // when a CallSite is first constructed, it doesn't call MethodHandleNatives.setCallSiteTargetNormal(),
            // so we have to check if we need to initialize it here (i.e. attach an IndyCallSite<T> to it)
            if (cs != null)
            {
                if (cs.ics == null)
                    MethodHandleNatives.InitializeCallSite(cs);

                lock (cs.ics)
                    cs.ics.SetTarget(cs.target);
            }

            global::java.lang.invoke.MethodType typeCache = null;
            IndyCallSite<T> ics;
            IndyCallSite<T> direct = null;
            if (x != null || cs == null || cs.type() != DynamicLoadMethodType(ref typeCache, signature, callerID))
            {
                x = MapException<Exception>(x ?? (cs == null
                    ? (Exception)new global::java.lang.ClassCastException("bootstrap method failed to produce a CallSite")
                    : new global::java.lang.invoke.WrongMethodTypeException()), MapFlags.None);
                global::java.lang.invoke.MethodType type = LoadMethodType<T>();
                global::java.lang.invoke.MethodHandle exc = x is global::java.lang.BootstrapMethodError
                    ? global::java.lang.invoke.MethodHandles.constant(typeof(global::java.lang.BootstrapMethodError), x)
                    : global::java.lang.invoke.MethodHandles.publicLookup().findConstructor(typeof(global::java.lang.BootstrapMethodError), global::java.lang.invoke.MethodType.methodType(typeof(void), typeof(string), typeof(Exception)))
                        .bindTo("call site initialization exception").bindTo(x);
                ics = new IndyCallSite<T>();
                ((IIndyCallSite)ics).SetTarget(
                    global::java.lang.invoke.MethodHandles.dropArguments(
                        global::java.lang.invoke.MethodHandles.foldArguments(
                            global::java.lang.invoke.MethodHandles.throwException(type.returnType(), typeof(global::java.lang.BootstrapMethodError)),
                                exc),
                        0, type.parameterArray()));
            }
            else if (lambda && (direct = TryCreateDirectLambdaCallSite<T>(cs)) != null)
            {
                // wasm fork (JVM.IndyDirect): instead of dynamicInvoker().asType(), whose PairwiseConvert adapter casts
                // the erased arguments on every call; the direct target casts them itself (EmitDirectBinderConversion).
                ics = direct;
            }
            else
            {
                ics = new IndyCallSite<T>();
                ((IIndyCallSite)ics).SetTarget(cs.dynamicInvoker().asType(LoadMethodType<T>()));
            }

            IndyCallSite<T> curr = site;
            if (curr.IsBootstrap && Interlocked.CompareExchange(ref site, ics, curr) == curr && direct != null)
                LazyBodyStats.NoteIndyInstalled();
        }

        /// <summary>
        /// wasm fork (JVM.IndyDirect): an <see cref="IndyCallSite{T}"/> whose target bypasses the MethodHandle of a
        /// LambdaMetafactory CallSite, or null to keep the MethodHandle chain.
        /// </summary>
        /// <remarks>
        /// Only for sites whose bootstrap is LambdaMetafactory.metafactory/altMetafactory -- the compiler picks the *Lambda*
        /// link helpers only for those -- because the second shape below is a constant only by InnerClassLambdaMetafactory's
        /// construction. buildCallSite returns a ConstantCallSite in both cases, so the target read here is final:
        ///
        /// * captured arguments: findStatic(innerClass, "get$Lambda", invokedType), an exact DirectMethodHandle. Calling the
        ///   factory directly is what invoking that handle does, minus InvokeExact, the DMH.invokeStatic LambdaForm,
        ///   linkToStatic and the MemberName thunk -- plus PairwiseConvert and its castReference binders on an erased site.
        /// * no captured arguments: MethodHandles.constant(samBase, inst), inst constructed by buildCallSite. Invoking it once
        ///   has no side effect and returns what every invocation returns.
        ///
        /// Never throws: any failure keeps the chain, which is the behaviour with the knob off. Racing bootstraps each get a
        /// candidate; only the one whose link CAS wins is installed and counted.
        /// </remarks>
        static IndyCallSite<T> TryCreateDirectLambdaCallSite<T>(global::java.lang.invoke.CallSite cs)
            where T : class, Delegate
        {
            try
            {
                if (cs is not global::java.lang.invoke.ConstantCallSite constant)
                {
                    Interlocked.Increment(ref LazyBodyStats.IndyRejNotConstant);
                    return null;
                }

                var target = constant.getTarget();
                var direct = target != null && target.GetType() == typeof(global::java.lang.invoke.DirectMethodHandle)
                    ? CreateDirectLambdaFactory<T>((global::java.lang.invoke.DirectMethodHandle)target)
                    : CreateDirectLambdaConstant<T>(target);
                return direct == null ? null : new IndyCallSite<T>(direct, false);
            }
            catch (Exception e)
            {
                if (Interlocked.Increment(ref LazyBodyStats.IndyFailed) <= 8 || JVM.TraceLazyBodies)
                    Console.WriteLine($"[IKVM indy] direct link FAILED, keeping the MethodHandle chain: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The capturing shape: a delegate calling the static factory <paramref name="dmh"/> names, bound straight to it when
        /// the call site's delegate signature is the factory's own, else through a thunk that casts erased arguments.
        /// </summary>
        static T CreateDirectLambdaFactory<T>(global::java.lang.invoke.DirectMethodHandle dmh)
            where T : class, Delegate
        {
            var context = JVM.Context;

            // exactly DirectMethodHandle, checked by the caller: its subclasses (Special, Interface, Constructor, the field
            // accessors) carry receiver or allocation semantics this does not reproduce
            var member = dmh.member;
            if (member == null || !member.isMethod() || member.getReferenceKind() != global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeStatic)
            {
                Interlocked.Increment(ref LazyBodyStats.IndyRejShape);
                return null;
            }

            // resolved the way MethodHandleNatives.ResolveMethod resolved it
            var tw = RuntimeJavaType.FromClass(member.getDeclaringClass());
            var mw = tw.GetMethod(member.getName(), member.getSignature().Replace('/', '.'), true);
            if (mw == null || !mw.IsStatic || mw.IsDynamicOnly || mw.HasCallerID || mw.IsFinalizeOrClone
                || mw.DeclaringType.IsGhost || mw.DeclaringType.IsRemapped || mw.DeclaringType.IsNonPrimitiveValueType || mw.DeclaringType.IsFakeTypeContainer
                || RuntimeByteCodeJavaType.RequiresDynamicReflectionCallerClass(mw.DeclaringType.Name, mw.Name, mw.Signature))
            {
                Interlocked.Increment(ref LazyBodyStats.IndyRejShape);
                return null;
            }

            mw.Link();
            var invoke = context.MethodHandleUtil.GetDelegateInvokeMethod(typeof(T));
            var dp = invoke.GetParameters();
            var args = mw.GetParameters();
            var ret = mw.ReturnType;

            // a length mismatch means the delegate packs its arguments (arity > MethodHandleUtil.MaxArity)
            if (dp.Length != args.Length || !CanEmitDirectBinderReturn(ret, invoke.ReturnType, context))
            {
                Interlocked.Increment(ref LazyBodyStats.IndyRejSig);
                return null;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (!CanEmitDirectBinderConversion(dp[i].ParameterType, args[i]))
                {
                    Interlocked.Increment(ref LazyBodyStats.IndyRejSig);
                    return null;
                }
            }

            // as TryCreateDirectBinderDelegate: nothing unbaked in a signature, and the member re-resolved from its builder
            mw.DeclaringType.Finish();
            foreach (var arg in args)
                if (!arg.IsUnloadable)
                    arg.Finish();
            if (!ret.IsUnloadable)
                ret.Finish();
            mw.ResolveMethod();

            // the usual case: the site was compiled with every type loadable, so its delegate has the factory's signature
            if (mw.GetMethod() is MethodInfo mi && HasExactSignature(mi, invoke))
            {
                var bound = Delegate.CreateDelegate(typeof(T), mi, false) as T;
                if (bound != null)
                {
                    Interlocked.Increment(ref LazyBodyStats.IndyBind);
                    return bound;
                }
            }

            var dpTypes = new Type[dp.Length];
            for (int i = 0; i < dp.Length; i++)
                dpTypes[i] = dp[i].ParameterType;

            // owned like the MethodHandle adapters it replaces, which also keeps it out of Java stack traces (ExceptionHelper)
            var dm = new DynamicMethod($"__<>IndyDirect__{tw.Name}__{mw.Name}", invoke.ReturnType, dpTypes, typeof(MethodHandleUtil.DynamicMethodBuilder), true);
            var ilgen = context.CodeEmitterFactory.Create(dm);
            for (int i = 0; i < args.Length; i++)
            {
                ilgen.EmitLdarg(i);
                EmitDirectBinderConversion(ilgen, dpTypes[i], args[i]);
            }

            mw.EmitCall(ilgen);
            EmitDirectBinderReturn(ilgen, ret, invoke.ReturnType, context);
            ilgen.Emit(OpCodes.Ret);
            ilgen.DoEmit();

            Interlocked.Increment(ref LazyBodyStats.IndyThunk);
            return (T)dm.CreateDelegate(typeof(T));
        }

        /// <summary>
        /// The non-capturing shape: a delegate closed over the lambda instance that returns it. See
        /// <see cref="TryCreateDirectLambdaCallSite{T}"/> for why invoking <paramref name="target"/> here is sound.
        /// </summary>
        static T CreateDirectLambdaConstant<T>(global::java.lang.invoke.MethodHandle target)
            where T : class, Delegate
        {
            var invoke = JVM.Context.MethodHandleUtil.GetDelegateInvokeMethod(typeof(T));
            var rt = invoke.ReturnType;
            if (target == null || target.type().parameterCount() != 0 || invoke.GetParameters().Length != 0 || rt == typeof(void) || rt.IsValueType)
            {
                Interlocked.Increment(ref LazyBodyStats.IndyRejShape);
                return null;
            }

            var inst = target.invokeWithArguments(new object[0]);
            if (inst == null || !rt.IsInstanceOfType(inst))
            {
                Interlocked.Increment(ref LazyBodyStats.IndyRejSig);
                return null;
            }

            // the parameter is the instance's own class, as DynamicMethodBuilder types a bound target, so the delegate
            // binds as a closed static (BOUND_STATIC to the wasm JIT's delegate PIC) and `ldarg.0; ret` is verifiable
            var dm = new DynamicMethod("__<>IndyConstant__" + inst.GetType().Name, rt, new[] { inst.GetType() }, typeof(MethodHandleUtil.DynamicMethodBuilder), true);
            var ilgen = JVM.Context.CodeEmitterFactory.Create(dm);
            ilgen.Emit(OpCodes.Ldarg_0);
            ilgen.Emit(OpCodes.Ret);
            ilgen.DoEmit();

            Interlocked.Increment(ref LazyBodyStats.IndyConst);
            return (T)dm.CreateDelegate(typeof(T), inst);
        }

        static bool HasExactSignature(MethodInfo method, MethodInfo invoke)
        {
            if (method.ReturnType != invoke.ReturnType)
                return false;

            var mp = method.GetParameters();
            var ip = invoke.GetParameters();
            if (mp.Length != ip.Length)
                return false;

            for (int i = 0; i < mp.Length; i++)
                if (mp[i].ParameterType != ip[i].ParameterType)
                    return false;

            return true;
        }

#endif

#endif

    }

#if EXPORTER == false

    interface IIndyCallSite
    {

        void SetTarget(global::java.lang.invoke.MethodHandle mh);

    }

    public sealed class IndyCallSite<T> : IIndyCallSite
        where T : class, Delegate
    {

        internal readonly bool IsBootstrap;
        volatile T target;

        internal IndyCallSite()

        {
        }

        internal IndyCallSite(T target, bool bootstrap)
        {
            this.IsBootstrap = bootstrap;
            this.target = target;
        }

        void IIndyCallSite.SetTarget(global::java.lang.invoke.MethodHandle mh)
        {
#if !FIRST_PASS
            target = ByteCodeHelper.GetDelegateForInvokeExact<T>(mh);
#endif
        }

        public static IndyCallSite<T> CreateBootstrap(T bootstrap)
        {
            return new IndyCallSite<T>(bootstrap, true);
        }

        public T GetTarget()
        {
            return target;
        }

    }

    public struct InvokeCache<T>
        where T : class
    {

        internal T fixedArg;
        internal T varArg;

        global::java.lang.invoke.MethodType type;

        internal global::java.lang.invoke.MethodType Type => type;

        internal bool TrySetType(global::java.lang.invoke.MethodType newType)
        {
            Interlocked.CompareExchange(ref type, newType, null);
            return type == newType;
        }

    }

#endif

    public static class DoubleConverter
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ToLong(double value) =>
#if NET8_0_OR_GREATER
            Unsafe.BitCast<double, long>(value);
#else
            Unsafe.As<double, long>(ref value);
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ToDouble(long value) =>
#if NET8_0_OR_GREATER
            Unsafe.BitCast<long, double>(value);
#else
            Unsafe.As<long, double>(ref value);
#endif

    }

    public static class FloatConverter
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToInt(float value) =>
#if NET8_0_OR_GREATER
            Unsafe.BitCast<float, int>(value);
#else
            Unsafe.As<float, int>(ref value);
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ToFloat(int value) =>
#if NET8_0_OR_GREATER
            Unsafe.BitCast<int, float>(value);
#else
            Unsafe.As<int, float>(ref value);
#endif

    }

    public struct MHA<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public T1 t1;
        public T2 t2;
        public T3 t3;
        public T4 t4;
        public T5 t5;
        public T6 t6;
        public T7 t7;
        public T8 t8;

        public MHA(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7, T8 t8)
        {
            this.t1 = t1;
            this.t2 = t2;
            this.t3 = t3;
            this.t4 = t4;
            this.t5 = t5;
            this.t6 = t6;
            this.t7 = t7;
            this.t8 = t8;
        }
    }

    public delegate void MHV();
    public delegate void MHV<T1>(T1 t1);
    public delegate void MHV<T1, T2>(T1 t1, T2 t2);
    public delegate void MHV<T1, T2, T3>(T1 t1, T2 t2, T3 t3);
    public delegate void MHV<T1, T2, T3, T4>(T1 t1, T2 t2, T3 t3, T4 t4);
    public delegate void MHV<T1, T2, T3, T4, T5>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5);
    public delegate void MHV<T1, T2, T3, T4, T5, T6>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6);
    public delegate void MHV<T1, T2, T3, T4, T5, T6, T7>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7);
    public delegate void MHV<T1, T2, T3, T4, T5, T6, T7, T8>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7, T8 t8);

    public delegate TResult MH<TResult>();
    public delegate TResult MH<T1, TResult>(T1 t1);
    public delegate TResult MH<T1, T2, TResult>(T1 t1, T2 t2);
    public delegate TResult MH<T1, T2, T3, TResult>(T1 t1, T2 t2, T3 t3);
    public delegate TResult MH<T1, T2, T3, T4, TResult>(T1 t1, T2 t2, T3 t3, T4 t4);
    public delegate TResult MH<T1, T2, T3, T4, T5, TResult>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5);
    public delegate TResult MH<T1, T2, T3, T4, T5, T6, TResult>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6);
    public delegate TResult MH<T1, T2, T3, T4, T5, T6, T7, TResult>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7);
    public delegate TResult MH<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7, T8 t8);

    public static class LiveObjectHolder<T>
    {

        public static object[] values;

    }

}
