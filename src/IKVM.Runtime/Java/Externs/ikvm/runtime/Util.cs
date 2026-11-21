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
using System.Collections.Concurrent;

using IKVM.Attributes;
using IKVM.Runtime;

namespace IKVM.Java.Externs.ikvm.runtime
{

    static class Util
    {

        // concrete .NET Type -> resolved RuntimeJavaType (the result of the ghost-less resolution below).
        // The mapping is invariant per concrete runtime type, so caching it removes the per-call
        // IsRemappedType (Dictionary<Type,string>) lookup + the abstract-helper BaseType walk on this
        // very hot path (getClass / instanceof / reflection bridging). Keyed by Type only, which is
        // correct for a single RuntimeContext (the runtime case); hang it off the factory if multiple
        // contexts are ever used concurrently.
        static readonly ConcurrentDictionary<Type, RuntimeJavaType> wrapperByConcreteType = new ConcurrentDictionary<Type, RuntimeJavaType>();

        /// <summary>
        /// Object.getClass(). The fast path is small enough to inline into every Java caller, on mono's wasm JIT and on
        /// RyuJIT alike: the direct-mapped type cache, and the Class the entry holds once a slow call has seen it. An
        /// entry whose type can carry a ghost tag never holds one (the tag, per object, overrides the type's class).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static global::java.lang.Class getClassFromObject(object o)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.GetClassInline)
            {
                var t = o.GetType();
                var e = System.Threading.Volatile.Read(ref wrapperByConcreteTypeFast[DirectCacheIndex(t.TypeHandle.Value)]);
                if (e != null && ReferenceEquals(e.Key, t) && e.Class != null)
                    return e.Class;
            }

            return getClassFromObjectSlow(o);
#endif
        }

#if !FIRST_PASS
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static global::java.lang.Class getClassFromObjectSlow(object o)
        {
            var c = GetTypeWrapperFromObject(JVM.Context, o).ClassObject;
            if (JVM.GetClassInline)
            {
                // the entry GetTypeWrapperFromObject left for this type: give it the Class, unless a ghost tag can apply
                var t = o.GetType();
                ref var slot = ref wrapperByConcreteTypeFast[DirectCacheIndex(t.TypeHandle.Value)];
                var e = System.Threading.Volatile.Read(ref slot);
                if (e != null && ReferenceEquals(e.Key, t) && e.Class == null && e.MayBeGhost == false && ReferenceEquals(e.Value.ClassObject, c))
                    System.Threading.Volatile.Write(ref slot, new TypeCacheEntry(t, e.Value, false, c));
            }

            return c;
        }
#endif

        const int DirectCacheMask = 1023;

        static int DirectCacheIndex(IntPtr handle) => (int)(((long)handle >> 3) & DirectCacheMask);

        /// <summary>
        /// A <see cref="wrapperByConcreteTypeFast"/> entry: the type's wrapper, and whether an instance of the type can
        /// carry a ghost tag, which then overrides the wrapper. Immutable and published into its slot with one reference
        /// write, so a reader sees either a whole old entry or a whole new one.
        /// </summary>
        sealed class TypeCacheEntry
        {
            internal readonly Type Key;
            internal readonly RuntimeJavaType Value;
            internal readonly bool MayBeGhost;
#if !FIRST_PASS
            /// <summary>Value's Class for getClassFromObject's fast path: null until a slow call sets it, and always null when MayBeGhost.</summary>
            internal readonly global::java.lang.Class Class;
            internal TypeCacheEntry(Type key, RuntimeJavaType value, bool mayBeGhost, global::java.lang.Class cls) { Key = key; Value = value; MayBeGhost = mayBeGhost; Class = cls; }
#endif
            internal TypeCacheEntry(Type key, RuntimeJavaType value, bool mayBeGhost) { Key = key; Value = value; MayBeGhost = mayBeGhost; }
        }

        // concrete CLR type -> RuntimeJavaType, in front of wrapperByConcreteType (R314/I1: the ConcurrentDictionary
        // lookup was most of Object.getClass()'s cost on the server tick)
        static readonly TypeCacheEntry[] wrapperByConcreteTypeFast = new TypeCacheEntry[DirectCacheMask + 1];

        /// <summary>
        /// Whether an instance of <paramref name="t"/> can carry a ghost tag. Ghost tags are per object (a side table)
        /// and only ARRAYS are ever tagged (every GhostTag.SetTag site tags a new array: multianewarray_ghost,
        /// anewarray_ghost, DynamicMultianewarray, DynamicNewarray, reflect.Array, CloneArrayWithGhostTag), and a ghost
        /// array is an object[] of some rank (RuntimeJavaType.TypeAsArrayType), so with IKVM_GETCLASS_FAST only an array
        /// whose innermost element type is object is asked; without it, every array, as before.
        /// </summary>
        static bool MayBeGhostArray(Type t)
        {
            if (t.IsArray == false)
                return false;
            if (JVM.GetClassFast == false)
                return true;
            while (t.IsArray)
                t = t.GetElementType();
            return t == typeof(object);
        }

        internal static RuntimeJavaType GetTypeWrapperFromObject(RuntimeContext context, object o)
        {
            // The type cache first, then the ghost tag where the type says one can exist. Before IKVM_GETCLASS_FAST every
            // array asked the ConditionalWeakTable first (a ghost tag overrides the wrapper either way): 31 k
            // GetTypeWrapperFromObject calls and 2.5 M instructions a 26.2 server tick, mostly Arrays.copyOf / toArray
            // asking an array for getClass() (findings 2026-11-09 / 2026-11-16).
            var t = o.GetType();
            ref var slot = ref wrapperByConcreteTypeFast[DirectCacheIndex(t.TypeHandle.Value)];
            var e = System.Threading.Volatile.Read(ref slot);
            if (e == null || ReferenceEquals(e.Key, t) == false)
            {
                if (!wrapperByConcreteType.TryGetValue(t, out var resolved))
                {
                    resolved = ResolveTypeWrapperFromType(context, t);
                    wrapperByConcreteType[t] = resolved;
                }

                if (resolved == null)
                    return o is Array ? GhostTag.GetTag(o) : null;

                e = new TypeCacheEntry(t, resolved, MayBeGhostArray(t));
                System.Threading.Volatile.Write(ref slot, e);
            }

            if (e.MayBeGhost)
            {
                var ghostType = GhostTag.GetTag(o);
                if (ghostType != null)
                    return ghostType;
            }

            return e.Value;
        }

        static RuntimeJavaType ResolveTypeWrapperFromType(RuntimeContext context, Type t)
        {
            if (t.IsPrimitive || context.ClassLoaderFactory.IsRemappedType(t) && !t.IsSealed)
                return context.ManagedJavaTypeFactory.GetJavaTypeFromManagedType(t);

            for (; ; )
            {
                // if GetWrapperFromType returns null (or if tw.IsAbstract), that
                // must mean that the Type of the object is an implementation helper class
                // (e.g. an AtomicReferenceFieldUpdater or ThreadLocal instrinsic subclass)
                var tw = context.ClassLoaderFactory.GetJavaTypeFromType(t);
                if (tw != null && (!tw.IsAbstract || tw.IsArray))
                    return tw;

                t = t.BaseType;
            }
        }

#if !FIRST_PASS
        // type handle -> Class for class literals (R314/I1). Every `Foo.class` in a dynamically compiled class comes
        // here (RuntimeByteCodeJavaType is not IsFastClassLiteralSafe), and the uncached path -- GetTypeFromHandle,
        // IsRemappedType, IsVisibleAsClass, a ConcurrentDictionary lookup -- was 2.4% of the server tick. Only
        // non-null results are cached; all of the inputs are stable for a type (IKVM's dynamic assemblies are not
        // collectible, so a handle is never reused for another type), and the handle is kept in the entry so a
        // collision is a miss, never a wrong answer.
        sealed class HandleClassEntry
        {
            internal readonly IntPtr Handle;
            internal readonly global::java.lang.Class Class;
            internal HandleClassEntry(IntPtr handle, global::java.lang.Class cls) { Handle = handle; Class = cls; }
        }

        static readonly HandleClassEntry[] classByTypeHandle = new HandleClassEntry[DirectCacheMask + 1];
#endif

        public static global::java.lang.Class getClassFromTypeHandle(RuntimeTypeHandle handle)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var key = handle.Value;
            ref var slot = ref classByTypeHandle[DirectCacheIndex(key)];
            var e = System.Threading.Volatile.Read(ref slot);
            if (e != null && e.Handle == key)
                return e.Class;

            var result = getClassFromTypeHandleSlow(handle);
            if (result != null)
                System.Threading.Volatile.Write(ref slot, new HandleClassEntry(key, result));
            return result;
#endif
        }

        static global::java.lang.Class getClassFromTypeHandleSlow(RuntimeTypeHandle handle)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var t = Type.GetTypeFromHandle(handle);
            if (t.IsPrimitive || JVM.Context.ClassLoaderFactory.IsRemappedType(t) || t == typeof(void))
                return JVM.Context.ManagedJavaTypeFactory.GetJavaTypeFromManagedType(t).ClassObject;

            if (!IsVisibleAsClass(t))
                return null;

            var tw = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(t);
            if (tw != null)
                return tw.ClassObject;

            return null;
#endif
        }

        public static global::java.lang.Class getClassFromTypeHandle(RuntimeTypeHandle handle, int rank)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var t = Type.GetTypeFromHandle(handle);
            if (t.IsPrimitive || JVM.Context.ClassLoaderFactory.IsRemappedType(t) || t == typeof(void))
                return JVM.Context.ManagedJavaTypeFactory.GetJavaTypeFromManagedType(t).MakeArrayType(rank).ClassObject;

            if (!IsVisibleAsClass(t))
                return null;

            var tw = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(t);
            if (tw != null)
                return tw.MakeArrayType(rank).ClassObject;

            return null;
#endif
        }

        public static global::java.lang.Class getFriendlyClassFromType(Type type)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            int rank = 0;
            while (ReflectUtil.IsVector(type))
            {
                type = type.GetElementType();
                rank++;
            }

            if (type.DeclaringType != null && JVM.Context.AttributeHelper.IsGhostInterface(type.DeclaringType))
                type = type.DeclaringType;

            if (!IsVisibleAsClass(type))
                return null;

            var wrapper = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type);
            if (wrapper == null)
                return null;

            if (rank > 0)
                wrapper = wrapper.MakeArrayType(rank);

            return wrapper.ClassObject;
#endif
        }

        private static bool IsVisibleAsClass(Type type)
        {
            while (type.HasElementType)
            {
                if (type.IsPointer || type.IsByRef)
                {
                    return false;
                }
                type = type.GetElementType();
            }
            if (type.ContainsGenericParameters && !type.IsGenericTypeDefinition)
            {
                return false;
            }
            System.Reflection.Emit.TypeBuilder tb = type as System.Reflection.Emit.TypeBuilder;
            if (tb != null && !tb.IsCreated())
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets the underlying CLR <see cref="Type"/> instance.
        /// </summary>
        /// <param name="classObject"></param>
        /// <returns></returns>
        public static Type getInstanceTypeFromClass(global::java.lang.Class classObject)
        {
            var wrapper = RuntimeJavaType.FromClass(classObject);
            if (wrapper.IsRemapped && wrapper.IsFinal)
                return wrapper.TypeAsTBD;
            else
                return wrapper.TypeAsBaseType;
        }

        /// <summary>
        /// Gets the underlying CLR <see cref="Type"/> instance resovled fully at runtime.
        /// </summary>
        /// <param name="classObject"></param>
        /// <returns></returns>
        public static Type getRuntimeTypeFromClass(global::java.lang.Class classObject)
        {
            var wrapper = RuntimeJavaType.FromClass(classObject);
            wrapper.Finish();

            if (wrapper.IsRemapped && wrapper.IsFinal)
                return wrapper.TypeAsTBD;
            else
                return wrapper.TypeAsBaseType;
        }

        [HideFromJava]
        public static Exception mapException(Exception e)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return JVM.Context.ExceptionHelper.MapException<Exception>(e, true, false);
#endif
        }

        public static Exception unmapException(Exception e)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return ExceptionHelper.UnmapException(e);
#endif
        }

    }

}
