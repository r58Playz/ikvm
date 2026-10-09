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
using System.Diagnostics;

using IKVM.Attributes;
using IKVM.CoreLib.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;


#if IMPORTER || EXPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;

using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif

#if IMPORTER
using IKVM.Tools.Importer;
#endif

namespace IKVM.Runtime
{

    /// <summary>
    /// Encapsulates the information about a type available to Java.
    /// </summary>
    internal abstract class RuntimeJavaType
    {

        internal const Modifiers UnloadableModifiersHack = Modifiers.Final | Modifiers.Interface | Modifiers.Private;
        internal const Modifiers VerifierTypeModifiersHack = Modifiers.Final | Modifiers.Interface;

        static readonly object flagsLock = new object();

        readonly RuntimeContext context;
        readonly string name; // java name (e.g. java.lang.Object)
        readonly Modifiers modifiers;
        TypeFlags flags;
        RuntimeJavaMethod[] methods;
        RuntimeJavaField[] fields;
#if !IMPORTER && !EXPORTER
        java.lang.Class classObject;

        /// <summary>
        /// Marks a type whose <see cref="IsInstance"/> cannot be answered by a direct CLR type check.
        /// </summary>
        static readonly object SlowInstanceCheck = new object();

        /// <summary>
        /// Answers 'is this object an instance of some fixed type' without going through reflection.
        /// </summary>
        /// <remarks>
        /// <see cref="Type.IsInstanceOfType"/> is a reflection entry point, and on Mono it bottoms out in an internal
        /// call: a managed call into corlib, a raw-to-handle icall wrapper that pushes a handle frame, and only then the
        /// actual check. That is a lot of machinery for what the CLR expresses as a single isinst, and it is on the hot
        /// path behind Class.isInstance, which java.lang.invoke reaches on every interface MethodHandle invocation and
        /// every reference conversion an asType adapter performs.
        ///
        /// A closed generic subclass turns the check back into a real isinst against its type argument. Dispatch to it
        /// is an ordinary virtual call through this non-generic base, so the caller needs to know nothing about the type
        /// being tested, and the body is shared reference-generic code the AOT compiler has already produced - no
        /// runtime code generation, and nothing that falls back to the interpreter.
        /// </remarks>
        internal abstract class InstanceCheck
        {

            /// <summary>
            /// Creates the check for the given type, which must be a reference type the runtime can instantiate a
            /// generic over.
            /// </summary>
            /// <param name="type"></param>
            /// <returns></returns>
            internal static InstanceCheck For(Type type)
            {
                return (InstanceCheck)Activator.CreateInstance(typeof(InstanceCheckOf<>).MakeGenericType(type));
            }

            /// <summary>
            /// The type being tested against. Retained because callers cache the check in place of the type and still
            /// need to be able to name it.
            /// </summary>
            internal abstract Type Type { get; }

            /// <summary>
            /// Whether <paramref name="obj"/> is an instance of <see cref="Type"/>.
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            internal abstract bool IsInstanceOf(object obj);

        }

        sealed class InstanceCheckOf<T> : InstanceCheck
            where T : class
        {

            internal override Type Type => typeof(T);

            internal override bool IsInstanceOf(object obj) => obj is T;

        }

        /// <summary>
        /// The runtime's own <see cref="Type"/> implementation; anything else (a <see cref="System.Reflection.Emit.TypeBuilder"/>
        /// and friends) cannot answer <see cref="Type.IsInstanceOfType"/>.
        /// </summary>
        static readonly Type RuntimeTypeType = typeof(object).GetType();

        /// <summary>
        /// Memoized dispatch for <see cref="IsInstance"/>: null until classified, then either the <see cref="Type"/> to
        /// test against directly or <see cref="SlowInstanceCheck"/>. A single reference field keeps publication atomic,
        /// so no partially written state is observable from another thread.
        /// </summary>
        object instanceCheckCache;

        /// <summary>
        /// Memoized dispatch for <see cref="DynamicIsInstance"/>, in the same encoding as <see cref="instanceCheckCache"/>.
        /// Kept separate because a dynamic instanceof additionally lets the .NET-isms of a remapped type show through,
        /// exactly as the bytecode would, so it can answer with a direct type test where <see cref="IsInstance"/> cannot.
        /// </summary>
        object dynamicInstanceCheckCache;
#endif

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="flags"></param>
        /// <param name="modifiers"></param>
        /// <param name="name"></param>
        /// <exception cref="InternalException"></exception>
        internal RuntimeJavaType(RuntimeContext context, TypeFlags flags, Modifiers modifiers, string name)
        {
            Profiler.Count("TypeWrapper");

            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.flags = flags;
            this.modifiers = modifiers;
            this.name = name == null ? null : string.Intern(name);
        }

        /// <summary>
        /// Gets a reference to the <see cref="RuntimeContext"/> this <see cref="RuntimeJavaType"/> is hosted within.
        /// </summary>
        public RuntimeContext Context => context;

        /// <summary>
        /// Gets the <see cref="IDiagnosticHandler"/> where events related to this type should be sent.
        /// </summary>
        public virtual IDiagnosticHandler Diagnostics => Context.Diagnostics;

#if EMITTERS

        internal void EmitClassLiteral(CodeEmitter ilgen)
        {
            Debug.Assert(!this.IsPrimitive);

            var type = GetClassLiteralType();

            // note that this has to be the same check as in LazyInitClass
            if (!this.IsFastClassLiteralSafe || IsForbiddenTypeParameterType(type))
            {
                int rank = 0;
                while (ReflectUtil.IsVector(type))
                {
                    rank++;
                    type = type.GetElementType();
                }
                if (rank == 0)
                {
                    ilgen.Emit(OpCodes.Ldtoken, type);
                    context.CompilerFactory.GetClassFromTypeHandle.EmitCall(ilgen);
                }
                else
                {
                    ilgen.Emit(OpCodes.Ldtoken, type);
                    ilgen.EmitLdc_I4(rank);
                    context.CompilerFactory.GetClassFromTypeHandle2.EmitCall(ilgen);
                }
            }
            else
            {
                var classLiteralType = Context.Resolver.ResolveRuntimeType("IKVM.Runtime.ClassLiteral`1").AsReflection().MakeGenericType(type);
                ilgen.Emit(OpCodes.Call, classLiteralType.GetProperty("Value").GetMethod);
            }
        }

#endif

        private Type GetClassLiteralType()
        {
            Debug.Assert(!this.IsPrimitive);

            RuntimeJavaType tw = this;
            if (tw.IsGhostArray)
            {
                var rank = tw.ArrayRank;
                while (tw.IsArray)
                    tw = tw.ElementTypeWrapper;

                return RuntimeArrayJavaType.MakeArrayType(tw.TypeAsTBD, rank);
            }
            else
            {
                return tw.IsRemapped ? tw.TypeAsBaseType : tw.TypeAsTBD;
            }
        }

        bool IsForbiddenTypeParameterType(Type type)
        {
            // these are the types that may not be used as a type argument when instantiating a generic type
            return type == context.Types.Void
#if NETFRAMEWORK
                || type == context.Resolver.ResolveCoreType(typeof(ArgIterator).FullName).AsReflection()
#endif
                || type == context.Resolver.ResolveCoreType(typeof(RuntimeArgumentHandle).FullName).AsReflection()
                || type == context.Resolver.ResolveCoreType(typeof(TypedReference).FullName).AsReflection()
                || type.ContainsGenericParameters
                || type.IsByRef;
        }

        internal virtual bool IsFastClassLiteralSafe
        {
            get { return false; }
        }

#if !IMPORTER && !EXPORTER

        internal void SetClassObject(java.lang.Class classObject)
        {
            this.classObject = classObject;
        }

        internal java.lang.Class ClassObject
        {
            get
            {
                Debug.Assert(!IsUnloadable && !IsVerifierType);

                if (classObject == null)
                    LazyInitClass();

                return classObject;
            }
        }

#if !FIRST_PASS

        private java.lang.Class GetPrimitiveClass()
        {
            if (this == context.PrimitiveJavaTypeFactory.BYTE)
            {
                return java.lang.Byte.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.CHAR)
            {
                return java.lang.Character.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.DOUBLE)
            {
                return java.lang.Double.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.FLOAT)
            {
                return java.lang.Float.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.INT)
            {
                return java.lang.Integer.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.LONG)
            {
                return java.lang.Long.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.SHORT)
            {
                return java.lang.Short.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.BOOLEAN)
            {
                return java.lang.Boolean.TYPE;
            }
            else if (this == context.PrimitiveJavaTypeFactory.VOID)
            {
                return java.lang.Void.TYPE;
            }
            else
            {
                throw new InvalidOperationException();
            }
        }
#endif

        private void LazyInitClass()
        {
            lock (this)
            {
                if (classObject == null)
                {
                    // DynamicTypeWrapper should haved already had SetClassObject explicitly
                    Debug.Assert(!IsDynamic);
#if !FIRST_PASS
                    java.lang.Class clazz;
                    // note that this has to be the same check as in EmitClassLiteral
                    if (!this.IsFastClassLiteralSafe)
                    {
                        if (this.IsPrimitive)
                        {
                            clazz = GetPrimitiveClass();
                        }
                        else
                        {
                            clazz = new java.lang.Class(null);
                        }
                    }
                    else
                    {
                        Type type = GetClassLiteralType();
                        if (IsForbiddenTypeParameterType(type))
                        {
                            clazz = new java.lang.Class(type);
                        }
                        else
                        {
                            clazz = (java.lang.Class)typeof(ClassLiteral<>).MakeGenericType(type).GetProperty("Value").GetGetMethod().Invoke(null, Array.Empty<object>());
                        }
                    }
                    clazz.typeWrapper = this;

                    // MONOBUG Interlocked.Exchange is broken on Mono, so we use CompareExchange
                    System.Threading.Interlocked.CompareExchange(ref classObject, clazz, null);
#endif
                }
            }
        }

#if __MonoCS__
		// MONOBUG this method is to work around an mcs bug
		internal static void SetTypeWrapperHack(object clazz, TypeWrapper type)
		{
#if !FIRST_PASS
			typeof(java.lang.Class).GetField("typeWrapper", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(clazz, type);
#endif
		}
#endif

#if !FIRST_PASS

        private static void ResolvePrimitiveTypeWrapperClasses(RuntimeContext context)
        {
            // note that we're evaluating all ClassObject properties for the side effect
            // (to initialize and associate the ClassObject with the TypeWrapper)
            if (context.PrimitiveJavaTypeFactory.BYTE.ClassObject == null
                || context.PrimitiveJavaTypeFactory.CHAR.ClassObject == null
                || context.PrimitiveJavaTypeFactory.DOUBLE.ClassObject == null
                || context.PrimitiveJavaTypeFactory.FLOAT.ClassObject == null
                || context.PrimitiveJavaTypeFactory.INT.ClassObject == null
                || context.PrimitiveJavaTypeFactory.LONG.ClassObject == null
                || context.PrimitiveJavaTypeFactory.SHORT.ClassObject == null
                || context.PrimitiveJavaTypeFactory.BOOLEAN.ClassObject == null
                || context.PrimitiveJavaTypeFactory.VOID.ClassObject == null)
            {
                throw new InvalidOperationException();
            }
        }
#endif

        private static readonly bool s_fromClassTraceEnabled =
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("IKVM_FROMCLASS_TRACE"));

        /// <summary>
        /// Resolves the <see cref="RuntimeJavaType"/> a <see cref="java.lang.Class"/> stands for.
        /// </summary>
        /// <remarks>
        /// This is one of the most called methods in the runtime, so the already-resolved case is kept down to a field
        /// load and a null test that the AOT compiler can inline into the caller. Everything else lives out of line:
        /// Mono refuses to inline any method containing an exception clause at all, so the try/catch the tracing needs
        /// would otherwise force every one of the hundreds of call sites through a real call.
        /// </remarks>
        /// <param name="clazz"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static RuntimeJavaType FromClass(java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (s_fromClassTraceEnabled)
                return FromClassTraced(clazz);

            // MONOBUG redundant cast to workaround mcs bug
            var tw = (RuntimeJavaType)(object)clazz.typeWrapper;
            return tw ?? FromClassResolve(clazz);
#endif
        }

#if !FIRST_PASS

        /// <summary>
        /// <see cref="FromClass"/> with the entry tracing IKVM_FROMCLASS_TRACE asks for. Split out so the untraced path
        /// carries neither the exception clause nor the code size.
        /// </summary>
        /// <param name="clazz"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static RuntimeJavaType FromClassTraced(java.lang.Class clazz)
        {
            try
            {
                if (clazz == null)
                {
                    System.IO.File.WriteAllText("/dev/stderr", "[FromClass] clazz=<null>\n");
                }
                else
                {
                    // Touch the .NET-level identity first, before any IKVM-level field access,
                    // so we can tell whether the OOB happens reading clazz.typeWrapper or
                    // somewhere later. Calling RuntimeHelpers.GetHashCode avoids virtual
                    // dispatch on a possibly-corrupted vtable.
                    int idHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(clazz);
                    System.IO.File.WriteAllText("/dev/stderr",
                        $"[FromClass] enter clazz_hash=0x{idHash:x8}\n");
                }
            }
            catch
            {
            }

            // MONOBUG redundant cast to workaround mcs bug
            var tw = (RuntimeJavaType)(object)clazz.typeWrapper;
            return tw ?? FromClassResolve(clazz);
        }

        /// <summary>
        /// Resolves and associates the <see cref="RuntimeJavaType"/> for a <see cref="java.lang.Class"/> that does not
        /// have one yet. Runs at most once per class, so it stays out of line.
        /// </summary>
        /// <param name="clazz"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static RuntimeJavaType FromClassResolve(java.lang.Class clazz)
        {
            var type = clazz.type;
            if (type == null)
            {
                ResolvePrimitiveTypeWrapperClasses(JVM.Context);
                return FromClass(clazz);
            }

            if (s_fromClassTraceEnabled)
                TraceFromClassResolve(type);

            RuntimeJavaType tw;
            if (type == typeof(void) || type.IsPrimitive || JVM.Context.ClassLoaderFactory.IsRemappedType(type))
                tw = JVM.Context.ManagedJavaTypeFactory.GetJavaTypeFromManagedType(type);
            else
                tw = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type);

            clazz.typeWrapper = tw;
            return tw;
        }

        /// <summary>
        /// Reports the type <see cref="FromClassResolve"/> is about to resolve.
        /// </summary>
        /// <param name="type"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TraceFromClassResolve(Type type)
        {
            try
            {
                System.IO.File.WriteAllText("/dev/stderr", $"[FromClass] resolving type={type.FullName ?? "<null>"}\n");
            }
            catch
            {
            }
        }

#endif

#endif // !IMPORTER && !EXPORTER

        public override string ToString()
        {
            return GetType().Name + "[" + name + "]";
        }

        // For UnloadableTypeWrapper it tries to load the type through the specified loader
        // and if that fails it throw a NoClassDefFoundError (not a java.lang.NoClassDefFoundError),
        // for all other types this is a no-op.
        internal virtual RuntimeJavaType EnsureLoadable(RuntimeClassLoader loader)
        {
            return this;
        }

        private void SetTypeFlag(TypeFlags flag)
        {
            // we use a global lock object, since the chance of contention is very small
            lock (flagsLock)
            {
                flags |= flag;
            }
        }

        internal bool HasIncompleteInterfaceImplementation
        {
            get
            {
                RuntimeJavaType baseWrapper = this.BaseTypeWrapper;
                return (flags & TypeFlags.HasIncompleteInterfaceImplementation) != 0 || (baseWrapper != null && baseWrapper.HasIncompleteInterfaceImplementation);
            }
        }

        internal void SetHasIncompleteInterfaceImplementation()
        {
            SetTypeFlag(TypeFlags.HasIncompleteInterfaceImplementation);
        }

        internal bool HasUnsupportedAbstractMethods
        {
            get
            {
                foreach (var iface in this.Interfaces)
                    if (iface.HasUnsupportedAbstractMethods)
                        return true;

                var baseWrapper = this.BaseTypeWrapper;
                return (flags & TypeFlags.HasUnsupportedAbstractMethods) != 0 || (baseWrapper != null && baseWrapper.HasUnsupportedAbstractMethods);
            }
        }

        internal void SetHasUnsupportedAbstractMethods()
        {
            SetTypeFlag(TypeFlags.HasUnsupportedAbstractMethods);
        }

        internal virtual bool HasStaticInitializer
        {
            get
            {
                return (flags & TypeFlags.HasStaticInitializer) != 0;
            }
        }

        internal void SetHasStaticInitializer()
        {
            SetTypeFlag(TypeFlags.HasStaticInitializer);
        }

        internal bool HasVerifyError
        {
            get
            {
                return (flags & TypeFlags.VerifyError) != 0;
            }
        }

        internal void SetHasVerifyError()
        {
            SetTypeFlag(TypeFlags.VerifyError);
        }

        internal bool HasClassFormatError
        {
            get
            {
                return (flags & TypeFlags.ClassFormatError) != 0;
            }
        }

        internal void SetHasClassFormatError()
        {
            SetTypeFlag(TypeFlags.ClassFormatError);
        }

        internal virtual bool IsFakeTypeContainer
        {
            get
            {
                return false;
            }
        }

        internal virtual bool IsFakeNestedType
        {
            get
            {
                return false;
            }
        }

        // is this an anonymous class (in the sense of Unsafe.defineAnonymousClass(), not the JLS)
        internal bool IsUnsafeAnonymous
        {
            get { return (flags & TypeFlags.Anonymous) != 0; }
        }

        // is this a hidden class (JDK 15+): one defined like an anonymous class, or a lambda proxy class IKVM compiles itself
        internal virtual bool IsHidden => IsUnsafeAnonymous;

        // is this a record class (JDK 16+). HotSpot looks for the Record attribute; javac makes a record a final direct
        // subclass of java.lang.Record.
        internal bool IsRecord => IsFinal && BaseTypeWrapper != null && BaseTypeWrapper.Name == "java.lang.Record";

        // a ghost is an interface that appears to be implemented by a .NET type
        // (e.g. System.String (aka java.lang.String) appears to implement java.lang.CharSequence,
        // so java.lang.CharSequence is a ghost)
        internal virtual bool IsGhost
        {
            get
            {
                return false;
            }
        }

        // is this an array type of which the ultimate element type is a ghost?
        internal bool IsGhostArray
        {
            get
            {
                return !IsUnloadable && IsArray && (ElementTypeWrapper.IsGhost || ElementTypeWrapper.IsGhostArray);
            }
        }

        internal virtual FieldInfo GhostRefField
        {
            get
            {
                throw new InvalidOperationException();
            }
        }

        internal virtual bool IsRemapped
        {
            get
            {
                return false;
            }
        }

        internal bool IsArray
        {
            get
            {
                return name != null && name[0] == '[';
            }
        }

        // NOTE for non-array types this returns 0
        internal int ArrayRank
        {
            get
            {
                int i = 0;
                if (name != null)
                {
                    while (name[i] == '[')
                    {
                        i++;
                    }
                }
                return i;
            }
        }

        internal virtual RuntimeJavaType GetUltimateElementTypeWrapper()
        {
            throw new InvalidOperationException();
        }

        internal bool IsNonPrimitiveValueType
        {
            get
            {
                // unloadable types are erased to object, never a value type (and TypeAsTBD throws)
                return this != context.VerifierJavaTypeFactory.Null && !IsUnloadable && !IsPrimitive && !IsGhost && TypeAsTBD.IsValueType;
            }
        }

        internal bool IsPrimitive
        {
            get
            {
                return name == null;
            }
        }

        internal bool IsWidePrimitive
        {
            get
            {
                return this == context.PrimitiveJavaTypeFactory.LONG || this == context.PrimitiveJavaTypeFactory.DOUBLE;
            }
        }

        internal bool IsIntOnStackPrimitive
        {
            get
            {
                return name == null &&
                    (this == context.PrimitiveJavaTypeFactory.BOOLEAN ||
                    this == context.PrimitiveJavaTypeFactory.BYTE ||
                    this == context.PrimitiveJavaTypeFactory.CHAR ||
                    this == context.PrimitiveJavaTypeFactory.SHORT ||
                    this == context.PrimitiveJavaTypeFactory.INT);
            }
        }

        private static bool IsJavaPrimitive(RuntimeContext context, Type type)
        {
            return type == context.PrimitiveJavaTypeFactory.BOOLEAN.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.BYTE.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.CHAR.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.DOUBLE.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.FLOAT.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.INT.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.LONG.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.SHORT.TypeAsTBD
                || type == context.PrimitiveJavaTypeFactory.VOID.TypeAsTBD;
        }

        internal bool IsBoxedPrimitive
        {
            get
            {
                return !IsPrimitive && IsJavaPrimitive(Context, TypeAsSignatureType);
            }
        }

        internal bool IsErasedOrBoxedPrimitiveOrRemapped
        {
            get
            {
                bool erased = IsUnloadable || IsGhostArray;
                return erased || IsBoxedPrimitive || (IsRemapped && this is RuntimeManagedJavaType);
            }
        }

        internal bool IsUnloadable
        {
            get
            {
                // NOTE we abuse modifiers to note unloadable classes
                return modifiers == UnloadableModifiersHack;
            }
        }

        internal bool IsVerifierType
        {
            get
            {
                // NOTE we abuse modifiers to note verifier types
                return modifiers == VerifierTypeModifiersHack;
            }
        }

        internal virtual bool IsMapUnsafeException
        {
            get
            {
                return false;
            }
        }

        internal Modifiers Modifiers
        {
            get
            {
                return modifiers;
            }
        }

        // since for inner classes, the modifiers returned by Class.getModifiers are different from the actual
        // modifiers (as used by the VM access control mechanism), we have this additional property
        internal virtual Modifiers ReflectiveModifiers
        {
            get
            {
                return modifiers;
            }
        }

        internal bool IsInternal
        {
            get
            {
                return (flags & TypeFlags.InternalAccess) != 0;
            }
        }

        internal bool IsPublic
        {
            get
            {
                return (modifiers & Modifiers.Public) != 0;
            }
        }

        internal bool IsAbstract
        {
            get
            {
                // interfaces don't need to marked abstract explicitly (and javac 1.1 didn't do it)
                return (modifiers & (Modifiers.Abstract | Modifiers.Interface)) != 0;
            }
        }

        internal bool IsFinal
        {
            get
            {
                return (modifiers & Modifiers.Final) != 0;
            }
        }

        internal bool IsInterface
        {
            get
            {
                Debug.Assert(!IsUnloadable && !IsVerifierType);
                return (modifiers & Modifiers.Interface) != 0;
            }
        }

        // this exists because interfaces and arrays of interfaces are treated specially
        // by the verifier, interfaces don't have a common base (other than java.lang.Object)
        // so any object reference or object array reference can be used where an interface
        // or interface array reference is expected (the compiler will insert the required casts).
        internal bool IsInterfaceOrInterfaceArray
        {
            get
            {
                RuntimeJavaType tw = this;
                while (tw.IsArray)
                {
                    tw = tw.ElementTypeWrapper;
                }
                return tw.IsInterface;
            }
        }

        /// <summary>
        /// Gets the associated <see cref="RuntimeClassLoader"/>.
        /// </summary>
        internal abstract RuntimeClassLoader ClassLoader { get; }

        /// <summary>
        /// Searches for the <see cref="RuntimeJavaField"/> with the specified name and signature.
        /// </summary>
        /// <param name="fieldName"></param>
        /// <param name="fieldSig"></param>
        /// <returns></returns>
        internal RuntimeJavaField GetFieldWrapper(string fieldName, string fieldSig)
        {
            foreach (var fw in GetFields())
                if (fw.Name == fieldName && fw.Signature == fieldSig)
                    return fw;

            foreach (var iface in Interfaces)
            {
                var fw = iface.GetFieldWrapper(fieldName, fieldSig);
                if (fw != null)
                    return fw;
            }

            var baseWrapper = BaseTypeWrapper;
            if (baseWrapper != null)
                return baseWrapper.GetFieldWrapper(fieldName, fieldSig);

            return null;
        }

        protected virtual void LazyPublishMembers()
        {
            methods ??= Array.Empty<RuntimeJavaMethod>();
            fields ??= Array.Empty<RuntimeJavaField>();
        }

        protected virtual void LazyPublishMethods()
        {
            LazyPublishMembers();
        }

        protected virtual void LazyPublishFields()
        {
            LazyPublishMembers();
        }

        /// <summary>
        /// Gets the set of methods defined by this type.
        /// </summary>
        /// <returns></returns>
        internal RuntimeJavaMethod[] GetMethods()
        {
            if (methods == null)
            {
                lock (this)
                {
                    if (methods == null)
                    {
#if IMPORTER
                        if (IsUnloadable || !CheckMissingBaseTypes(Context, TypeAsBaseType))
                            return methods = Array.Empty<RuntimeJavaMethod>();
#endif
                        LazyPublishMethods();
                    }
                }
            }

            return methods;
        }

        /// <summary>
        /// Gets the set of fields declared by this type.
        /// </summary>
        /// <returns></returns>
        internal RuntimeJavaField[] GetFields()
        {
            if (fields == null)
            {
                lock (this)
                {
                    if (fields == null)
                    {
#if IMPORTER
                        if (IsUnloadable || !CheckMissingBaseTypes(Context, TypeAsBaseType))
                            return fields = Array.Empty<RuntimeJavaField>();
#endif

                        LazyPublishFields();
                    }
                }
            }

            return fields;
        }

#if IMPORTER

        private static bool CheckMissingBaseTypes(RuntimeContext context, Type type)
        {
            while (type != null)
            {
                if (type.__ContainsMissingType)
                {
                    context.StaticCompiler.IssueMissingTypeMessage(type);
                    return false;
                }
                bool ok = true;
                foreach (Type iface in type.__GetDeclaredInterfaces())
                {
                    ok &= CheckMissingBaseTypes(context, iface);
                }
                if (!ok)
                {
                    return false;
                }
                type = type.BaseType;
            }
            return true;
        }

#endif

        /// <summary>
        /// Gets the method with the specified name and descriptor. Optionally searches the inheritence hierarchy.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="desc"></param>
        /// <param name="inherit"></param>
        /// <returns></returns>
        internal RuntimeJavaMethod GetMethod(string name, string desc, bool inherit)
        {
            // ensure params are interned
            name = string.Intern(name);
            desc = string.Intern(desc);

            // scan for method with matching name and descriptor
            foreach (var method in GetMethods())
                if (ReferenceEquals(method.Name, name) && ReferenceEquals(method.Signature, desc))
                    return method;

            var baseWrapper = BaseTypeWrapper;
            if (inherit && baseWrapper != null)
                return baseWrapper.GetMethod(name, desc, inherit);

            return null;
        }

        internal RuntimeJavaMethod GetInterfaceMethod(string name, string sig)
        {
            var method = GetMethod(name, sig, false);
            if (method != null)
            {
                return method;
            }

            var interfaces = Interfaces;
            for (int i = 0; i < interfaces.Length; i++)
            {
                method = interfaces[i].GetInterfaceMethod(name, sig);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        internal void SetMethods(RuntimeJavaMethod[] methods)
        {
            Debug.Assert(methods != null);
            System.Threading.Thread.MemoryBarrier();
            this.methods = methods;
        }

        internal void SetFields(RuntimeJavaField[] fields)
        {
            Debug.Assert(fields != null);
            System.Threading.Thread.MemoryBarrier();
            this.fields = fields;
        }

        internal string Name => name;

        /// <summary>
        /// The name of the type as it appears in a Java signature string (e.g. "Ljava.lang.Object;" or "I").
        /// </summary>
        internal virtual string SigName => "L" + Name + ";";

        // returns true iff wrapper is allowed to access us
        internal bool IsAccessibleFrom(RuntimeJavaType wrapper)
        {
            return IsPublic
                || (IsInternal && InternalsVisibleTo(wrapper))
                || IsPackageAccessibleFrom(wrapper);
        }

        internal bool InternalsVisibleTo(RuntimeJavaType wrapper)
        {
            return ClassLoader.InternalsVisibleToImpl(this, wrapper);
        }

        /// <summary>
        /// The host of the nest this type claims to belong to through its NestHost attribute, or null when it has none
        /// and so hosts its own nest (JVMS 5.4.4).
        /// </summary>
        internal virtual string NestHostName => null;

        /// <summary>
        /// Whether this type's NestMembers attribute lists the named class.
        /// </summary>
        internal virtual bool ListsNestMember(string name) => false;

        /// <summary>
        /// The classes this type's NestMembers attribute lists, or null.
        /// </summary>
        internal virtual string[] NestMemberNames => null;

        /// <summary>
        /// Whether this is a sealed class or interface, which only the classes its PermittedSubclasses attribute names
        /// may directly extend or implement (JVMS 5.3.5).
        /// </summary>
        internal virtual bool IsSealed => false;

        /// <summary>
        /// Whether this sealed type's PermittedSubclasses attribute names the class.
        /// </summary>
        internal virtual bool PermitsSubclass(string name) => true;

        /// <summary>
        /// The classes this sealed type's PermittedSubclasses attribute names, or null if it is not sealed.
        /// </summary>
        internal virtual string[] PermittedSubclassNames => null;

        /// <summary>
        /// The components of this record class, or null if its Record attribute is not known.
        /// </summary>
        internal virtual RecordComponentInfo[] RecordComponents => null;

        /// <summary>
        /// Whether this type and <paramref name="other"/> are nestmates, which may access each other's private members
        /// (JVMS 5.4.4): they name the same nest host, are in the same run-time package, and the host lists each of them
        /// that is not the host itself.
        /// </summary>
        /// <remarks>
        /// When neither is the host, the host is looked up as linking would: under lazy class loading that runs no user
        /// loadClass, and a host that is not loaded yet is then taken on trust rather than loaded.
        /// </remarks>
        internal bool IsNestmateOf(RuntimeJavaType other)
        {
            if (this == other)
                return true;

            var hostName = NestHostName ?? Name;
            if (hostName != (other.NestHostName ?? other.Name) || IsPackageAccessibleFrom(other) == false)
                return false;

            RuntimeJavaType host;
            if (hostName == Name)
                host = this;
            else if (hostName == other.Name)
                host = other;
            else
            {
#if EXPORTER
                var lazy = false;
#else
                var lazy = JVM.LazyClassLoading && ClassLoader.InvokesUserLoadClass;
#endif
                host = ClassLoader.LoadClass(hostName, lazy ? LoadMode.LinkNoLoad : LoadMode.Link);
                if (host == null || host.IsUnloadable)
                    return lazy;
            }

            return (host == this || host.ListsNestMember(Name)) && (host == other || host.ListsNestMember(other.Name));
        }

        internal virtual bool IsPackageAccessibleFrom(RuntimeJavaType wrapper)
        {
            if (MatchingPackageNames(name, wrapper.name))
            {
#if IMPORTER
                ImportClassLoader ccl = ClassLoader as ImportClassLoader;
                if (ccl != null)
                {
                    // this is a hack for multi target -sharedclassloader compilation
                    // (during compilation we have multiple CompilerClassLoader instances to represent the single shared runtime class loader)
                    return ccl.IsEquivalentTo(wrapper.ClassLoader);
                }
#endif
                return ClassLoader == wrapper.ClassLoader;
            }
            else
            {
                return false;
            }
        }

        static bool MatchingPackageNames(string name1, string name2)
        {
            int index1 = name1.LastIndexOf('.');
            int index2 = name2.LastIndexOf('.');
            if (index1 == -1 && index2 == -1)
                return true;

            // for array types we need to skip the brackets
            int skip1 = 0;
            int skip2 = 0;
            while (name1[skip1] == '[')
            {
                skip1++;
            }
            while (name2[skip2] == '[')
            {
                skip2++;
            }
            if (skip1 > 0)
            {
                // skip over the L that follows the brackets
                skip1++;
            }
            if (skip2 > 0)
            {
                // skip over the L that follows the brackets
                skip2++;
            }
            if ((index1 - skip1) != (index2 - skip2))
            {
                return false;
            }

            return string.CompareOrdinal(name1, skip1, name2, skip2, index1 - skip1) == 0;
        }

        internal abstract Type TypeAsTBD
        {
            get;
        }

        internal Type TypeAsSignatureType
        {
            get
            {
                if (IsUnloadable)
                    return ((RuntimeUnloadableJavaType)this).MissingType ?? context.Types.Object;

                if (IsGhostArray)
                    return RuntimeArrayJavaType.MakeArrayType(context.Types.Object, ArrayRank);

                return TypeAsTBD;
            }
        }

        internal Type TypeAsPublicSignatureType => (IsPublic ? this : GetPublicBaseTypeWrapper()).TypeAsSignatureType;

        internal virtual Type TypeAsBaseType => TypeAsTBD;

        internal Type TypeAsLocalOrStackType
        {
            get
            {
                if (IsUnloadable || IsGhost)
                    return context.Types.Object;

                if (IsNonPrimitiveValueType)
                {
                    // return either System.ValueType or System.Enum
                    return TypeAsTBD.BaseType;
                }

                if (IsGhostArray)
                    return RuntimeArrayJavaType.MakeArrayType(context.Types.Object, ArrayRank);

                return TypeAsTBD;
            }
        }

        /** <summary>Use this if the type is used as an array or array element</summary> */
        internal Type TypeAsArrayType
        {
            get
            {
                if (IsUnloadable || IsGhost)
                    return context.Types.Object;

                if (IsGhostArray)
                    return RuntimeArrayJavaType.MakeArrayType(context.Types.Object, ArrayRank);

                return TypeAsTBD;
            }
        }

        internal Type TypeAsExceptionType
        {
            get
            {
                if (IsUnloadable)
                    return context.Types.Exception;

                return TypeAsTBD;
            }
        }

        internal abstract RuntimeJavaType BaseTypeWrapper
        {
            get;
        }

        internal RuntimeJavaType ElementTypeWrapper
        {
            get
            {
                Debug.Assert(IsUnloadable == false);
                Debug.Assert(this == context.VerifierJavaTypeFactory.Null || IsArray);

                if (this == context.VerifierJavaTypeFactory.Null)
                {
                    return context.VerifierJavaTypeFactory.Null;
                }

                // TODO consider caching the element type
                switch (name[1])
                {
                    case '[':
                        // NOTE this call to LoadClassByDottedNameFast can never fail and will not trigger a class load
                        // (because the ultimate element type was already loaded when this type was created)
                        return ClassLoader.TryLoadClassByName(name.Substring(1));
                    case 'L':
                        // NOTE this call to LoadClassByDottedNameFast can never fail and will not trigger a class load
                        // (because the ultimate element type was already loaded when this type was created)
                        return ClassLoader.TryLoadClassByName(name.Substring(2, name.Length - 3));
                    case 'Z':
                        return context.PrimitiveJavaTypeFactory.BOOLEAN;
                    case 'B':
                        return context.PrimitiveJavaTypeFactory.BYTE;
                    case 'S':
                        return context.PrimitiveJavaTypeFactory.SHORT;
                    case 'C':
                        return context.PrimitiveJavaTypeFactory.CHAR;
                    case 'I':
                        return context.PrimitiveJavaTypeFactory.INT;
                    case 'J':
                        return context.PrimitiveJavaTypeFactory.LONG;
                    case 'F':
                        return context.PrimitiveJavaTypeFactory.FLOAT;
                    case 'D':
                        return context.PrimitiveJavaTypeFactory.DOUBLE;
                    default:
                        throw new InvalidOperationException(name);
                }
            }
        }

        internal RuntimeJavaType MakeArrayType(int rank)
        {
            Debug.Assert(rank != 0);
            // NOTE this call to LoadClassByDottedNameFast can never fail and will not trigger a class load
            return ClassLoader.TryLoadClassByName(new String('[', rank) + this.SigName);
        }

        /// <summary>
        /// Returns <c>true</c> if the current <see cref="RuntimeJavaType"/> implements the <paramref name="iface"/> interface.
        /// </summary>
        /// <param name="iface"></param>
        /// <returns></returns>
        internal bool ImplementsInterface(RuntimeJavaType iface)
        {
            var self = this;

            while (self != null)
            {
                var interfaces = self.Interfaces;

                for (int i = 0; i < interfaces.Length; i++)
                {
                    if (interfaces[i] == iface)
                        return true;

                    if (interfaces[i].ImplementsInterface(iface))
                        return true;
                }

                self = self.BaseTypeWrapper;
            }

            return false;
        }

        internal bool IsSubTypeOf(RuntimeJavaType baseType)
        {
            // make sure IsSubTypeOf isn't used on primitives
            Debug.Assert(!this.IsPrimitive);
            Debug.Assert(!baseType.IsPrimitive);
            // can't be used on Unloadable
            Debug.Assert(!this.IsUnloadable);
            Debug.Assert(!baseType.IsUnloadable);

            if (baseType.IsInterface)
            {
                if (baseType == this)
                {
                    return true;
                }
                return ImplementsInterface(baseType);
            }
            // NOTE this isn't just an optimization, it is also required when this is an interface
            if (baseType == Context.JavaBase.TypeOfJavaLangObject)
            {
                return true;
            }
            RuntimeJavaType subType = this;
            while (subType != baseType)
            {
                subType = subType.BaseTypeWrapper;
                if (subType == null)
                {
                    return false;
                }
            }
            return true;
        }

        internal bool IsAssignableTo(RuntimeJavaType wrapper)
        {
            if (this == wrapper)
            {
                return true;
            }
            if (this.IsPrimitive || wrapper.IsPrimitive)
            {
                return false;
            }
            // only a verifier type can be the null type, and testing that is a plain field compare, so this avoids
            // touching the factory at all for the ordinary types that dominate this path
            if (IsVerifierType && this == context.VerifierJavaTypeFactory.Null)
            {
                return true;
            }
            if (wrapper.IsInterface)
            {
                return ImplementsInterface(wrapper);
            }
            int rank1 = this.ArrayRank;
            int rank2 = wrapper.ArrayRank;
            if (rank1 > 0 && rank2 > 0)
            {
                rank1--;
                rank2--;
                RuntimeJavaType elem1 = this.ElementTypeWrapper;
                RuntimeJavaType elem2 = wrapper.ElementTypeWrapper;
                while (rank1 != 0 && rank2 != 0)
                {
                    elem1 = elem1.ElementTypeWrapper;
                    elem2 = elem2.ElementTypeWrapper;
                    rank1--;
                    rank2--;
                }
                if (elem1.IsPrimitive || elem2.IsPrimitive)
                {
                    return false;
                }
                return (!elem1.IsNonPrimitiveValueType && elem1.IsSubTypeOf(elem2));
            }
            return this.IsSubTypeOf(wrapper);
        }

#if !IMPORTER && !EXPORTER
        internal bool IsInstance(object obj)
        {
            if (obj == null)
                return false;

            // For every type EmitInstanceOf compiles down to a bare 'isinst TypeAsTBD', the CLR check is by
            // construction equivalent to the Java one, so we can skip resolving the object's RuntimeJavaType and
            // walking the type graph. This is the hot path behind Class.isInstance, which java.lang.invoke calls on
            // every interface MethodHandle invocation.
            var cache = Volatile.Read(ref instanceCheckCache) ?? ClassifyInstanceCheck();
            if (cache is InstanceCheck check)
                return check.IsInstanceOf(obj);

            return IKVM.Java.Externs.ikvm.runtime.Util.GetTypeWrapperFromObject(Context, obj).IsAssignableTo(this);
        }

        /// <summary>
        /// Determines whether <see cref="IsInstance"/> can be answered by a direct CLR type check, memoizing the answer
        /// once it is known to be stable.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        object ClassifyInstanceCheck()
        {
            // ghost, ghost array, remapped and unloadable types all get custom handling in EmitInstanceOf, so the
            // plain CLR check would not agree with Java semantics for them
            if (!IsUnloadable && !IsVerifierType && !IsPrimitive && !IsGhost && !IsGhostArray && !IsRemapped)
            {
                var type = TypeAsTBD;

                // a dynamic type that has not been finished yet still reports a TypeBuilder here, which cannot answer
                // the check; take the slow path without memoizing so the type upgrades once it is finished
                if (type != null && type.GetType() == RuntimeTypeType)
                {
                    var check = MakeInstanceCheck(type);
                    if (check != null)
                    {
                        Volatile.Write(ref instanceCheckCache, check);
                        return check;
                    }
                }

                return SlowInstanceCheck;
            }

            Volatile.Write(ref instanceCheckCache, SlowInstanceCheck);
            return SlowInstanceCheck;
        }

        /// <summary>
        /// Builds the <see cref="InstanceCheck"/> for a type, or null when the runtime cannot make one and the caller
        /// has to fall back to walking the Java type graph.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static InstanceCheck MakeInstanceCheck(Type type)
        {
            // the generic the check closes over is constrained to a reference type
            if (type.IsValueType || type.IsPointer || type.IsByRef || type.ContainsGenericParameters)
                return null;

            try
            {
                return InstanceCheck.For(type);
            }
            catch
            {
                // the check is only ever an optimization, so any type the runtime declines to instantiate the generic
                // over just keeps the slow path
                return null;
            }
        }

        /// <summary>
        /// Answers an instanceof against a type that was unloadable when the referencing method was compiled. Mirrors
        /// what the compiler would have emitted inline had the type been loadable, including the .NET-isms a remapped
        /// type lets show through.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        internal bool DynamicIsInstance(object obj)
        {
            var cache = Volatile.Read(ref dynamicInstanceCheckCache) ?? ClassifyDynamicInstanceCheck();
            if (cache is InstanceCheck check)
                return check.IsInstanceOf(obj);

            return IsInstance(obj);
        }

        /// <summary>
        /// The <see cref="InstanceCheck"/> a dynamic instanceof or cast against this type can be answered by, or null
        /// when the check needs the full Java type graph walk. Never changes once non-null, so a call site may cache it
        /// and dispatch through it from then on.
        /// </summary>
        /// <remarks>
        /// Returns the check rather than its <see cref="InstanceCheck.Type"/> deliberately. Handing out only the Type
        /// forces the call site to ask <see cref="Type.IsInstanceOfType"/>, which is a reflection ICALL: measured at
        /// 1.13% of in-game time in `ves_icall_RuntimeTypeHandle_IsInstanceOfType_raw`, plus the `mono_handle_new` and
        /// `mono_mem_manager_lock` the icall boundary drags in, and roughly a third of that symbol is frame spill
        /// traffic. The check answers the identical question as `obj is T` in shared reference-generic code with no
        /// boundary at all -- and it was already built here, then thrown away.
        /// </remarks>
        internal InstanceCheck DynamicInstanceCheck => (Volatile.Read(ref dynamicInstanceCheckCache) ?? ClassifyDynamicInstanceCheck()) as InstanceCheck;

        /// <summary>
        /// Classifies the dynamic instanceof check for this type, memoizing the answer once it is known to be stable.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        object ClassifyDynamicInstanceCheck()
        {
            // a dynamic instanceof has to mimick the bytecode behaviour, which allows these .NET-isms of a remapped type
            // to show through; each is exactly what Type.IsInstanceOfType answers for the type in question, so they fold
            // into the same cached-type dispatch as the ordinary case. None of these is a type we can ever emit, so a
            // TypeBuilder never matches and the answer is stable as soon as one does.
            var baseType = TypeAsBaseType;
            if (baseType == typeof(Array) || baseType == typeof(string) || baseType == typeof(IComparable))
            {
                var baseCheck = MakeInstanceCheck(baseType);
                if (baseCheck != null)
                {
                    Volatile.Write(ref dynamicInstanceCheckCache, baseCheck);
                    return baseCheck;
                }

                return SlowInstanceCheck;
            }

            // no .NET-ism applies, so the answer is whatever the ordinary instance check resolves to
            var inner = Volatile.Read(ref instanceCheckCache) ?? ClassifyInstanceCheck();
            if (inner is InstanceCheck check)
            {
                Volatile.Write(ref dynamicInstanceCheckCache, check);
                return check;
            }

            // ClassifyInstanceCheck memoizes a slow answer that is stable and leaves the field null for a type that has
            // not been finished yet; only memoize the former, so an unfinished type still upgrades once it finishes
            if (Volatile.Read(ref instanceCheckCache) != null)
                Volatile.Write(ref dynamicInstanceCheckCache, SlowInstanceCheck);

            return SlowInstanceCheck;
        }
#endif

        internal virtual RuntimeJavaType[] Interfaces => Array.Empty<RuntimeJavaType>();

        // NOTE this property can only be called for finished types!
        internal virtual RuntimeJavaType[] InnerClasses => Array.Empty<RuntimeJavaType>();

        // NOTE this property can only be called for finished types!
        internal virtual RuntimeJavaType DeclaringTypeWrapper => null;

        internal virtual void Finish()
        {

        }

        internal void LinkAll()
        {
            if ((flags & TypeFlags.Linked) == 0)
            {
                RuntimeJavaType tw = BaseTypeWrapper;
                if (tw != null)
                {
                    tw.LinkAll();
                }
                foreach (RuntimeJavaType iface in Interfaces)
                {
                    iface.LinkAll();
                }
                foreach (RuntimeJavaMethod mw in GetMethods())
                {
                    mw.Link();
                }
                foreach (RuntimeJavaField fw in GetFields())
                {
                    fw.Link();
                }
                SetTypeFlag(TypeFlags.Linked);
            }
        }

#if !IMPORTER
        [Conditional("DEBUG")]
        internal static void AssertFinished(Type type)
        {
            if (type != null)
            {
                while (type.HasElementType)
                {
                    type = type.GetElementType();
                }
                Debug.Assert(!(type is TypeBuilder));
            }
        }
#endif

#if !IMPORTER && !EXPORTER

        internal void RunClassInit()
        {
            Type t = IsRemapped ? TypeAsBaseType : TypeAsTBD;
            if (t != null)
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            }
        }

#endif

#if EMITTERS

        internal void EmitUnbox(CodeEmitter ilgen)
        {
            Debug.Assert(this.IsNonPrimitiveValueType);

            ilgen.EmitUnboxSpecial(this.TypeAsTBD);
        }

        internal void EmitBox(CodeEmitter ilgen)
        {
            Debug.Assert(this.IsNonPrimitiveValueType);

            ilgen.Emit(OpCodes.Box, this.TypeAsTBD);
        }

        internal void EmitConvSignatureTypeToStackType(CodeEmitter ilgen)
        {
            if (IsUnloadable)
            {
            }
            else if (this == context.PrimitiveJavaTypeFactory.BYTE)
            {
                ilgen.Emit(OpCodes.Conv_I1);
            }
            else if (IsNonPrimitiveValueType)
            {
                EmitBox(ilgen);
            }
            else if (IsGhost)
            {
                CodeEmitterLocal local = ilgen.DeclareLocal(TypeAsSignatureType);
                ilgen.Emit(OpCodes.Stloc, local);
                ilgen.Emit(OpCodes.Ldloca, local);
                ilgen.Emit(OpCodes.Ldfld, GhostRefField);
            }
        }

        // NOTE sourceType is optional and only used for interfaces,
        // it is *not* used to automatically downcast
        internal void EmitConvStackTypeToSignatureType(CodeEmitter ilgen, RuntimeJavaType sourceType)
        {
            if (!IsUnloadable)
            {
                if (IsGhost)
                {
                    CodeEmitterLocal local1 = ilgen.DeclareLocal(TypeAsLocalOrStackType);
                    ilgen.Emit(OpCodes.Stloc, local1);
                    CodeEmitterLocal local2 = ilgen.DeclareLocal(TypeAsSignatureType);
                    ilgen.Emit(OpCodes.Ldloca, local2);
                    ilgen.Emit(OpCodes.Ldloc, local1);
                    ilgen.Emit(OpCodes.Stfld, GhostRefField);
                    ilgen.Emit(OpCodes.Ldloca, local2);
                    ilgen.Emit(OpCodes.Ldobj, TypeAsSignatureType);
                }
                // because of the way interface merging works, any reference is valid
                // for any interface reference
                else if (IsInterfaceOrInterfaceArray && (sourceType == null || sourceType.IsUnloadable || !sourceType.IsAssignableTo(this)))
                {
                    ilgen.EmitAssertType(TypeAsTBD, "IKVM interface-downcast: checkcast to " + Name + (sourceType != null ? " from " + sourceType.Name : ""));
                    Profiler.Count("InterfaceDownCast");
                }
                else if (IsNonPrimitiveValueType)
                {
                    EmitUnbox(ilgen);
                }
                else if (sourceType != null && sourceType.IsUnloadable)
                {
                    ilgen.Emit(OpCodes.Castclass, TypeAsSignatureType);
                }
            }
        }

        internal virtual void EmitCheckcast(CodeEmitter ilgen)
        {
            if (IsGhost)
            {
                ilgen.Emit(OpCodes.Dup);
                // TODO make sure we get the right "Cast" method and cache it
                // NOTE for dynamic ghosts we don't end up here because AotTypeWrapper overrides this method,
                // so we're safe to call GetMethod on TypeAsTBD (because it has to be a compiled type, if we're here)
                ilgen.Emit(OpCodes.Call, TypeAsTBD.GetMethod("Cast"));
                ilgen.Emit(OpCodes.Pop);
            }
            else if (IsGhostArray)
            {
                ilgen.Emit(OpCodes.Dup);
                // TODO make sure we get the right "CastArray" method and cache it
                // NOTE for dynamic ghosts we don't end up here because AotTypeWrapper overrides this method,
                // so we're safe to call GetMethod on TypeAsTBD (because it has to be a compiled type, if we're here)
                RuntimeJavaType tw = this;
                int rank = 0;
                while (tw.IsArray)
                {
                    rank++;
                    tw = tw.ElementTypeWrapper;
                }
                ilgen.EmitLdc_I4(rank);
                ilgen.Emit(OpCodes.Call, tw.TypeAsTBD.GetMethod("CastArray"));
                ilgen.Emit(OpCodes.Castclass, RuntimeArrayJavaType.MakeArrayType(context.Types.Object, rank));
            }
            else
            {
                ilgen.EmitCastclass(TypeAsTBD);
            }
        }

        internal virtual void EmitInstanceOf(CodeEmitter ilgen)
        {
            if (IsGhost)
            {
                // TODO make sure we get the right "IsInstance" method and cache it
                // NOTE for dynamic ghosts we don't end up here because DynamicTypeWrapper overrides this method,
                // so we're safe to call GetMethod on TypeAsTBD (because it has to be a compiled type, if we're here)
                ilgen.Emit(OpCodes.Call, TypeAsTBD.GetMethod("IsInstance"));
            }
            else if (IsGhostArray)
            {
                // TODO make sure we get the right "IsInstanceArray" method and cache it
                // NOTE for dynamic ghosts we don't end up here because DynamicTypeWrapper overrides this method,
                // so we're safe to call GetMethod on TypeAsTBD (because it has to be a compiled type, if we're here)
                RuntimeJavaType tw = this;
                int rank = 0;
                while (tw.IsArray)
                {
                    rank++;
                    tw = tw.ElementTypeWrapper;
                }
                ilgen.EmitLdc_I4(rank);
                ilgen.Emit(OpCodes.Call, tw.TypeAsTBD.GetMethod("IsInstanceArray"));
            }
            else
            {
                ilgen.Emit_instanceof(TypeAsTBD);
            }
        }

        /// <summary>
        /// Emits the appropriate instruction to do an indirect load of a reference to this type.
        /// </summary>
        /// <param name="il"></param>
        internal virtual void EmitLdind(CodeEmitter il)
        {
            if (this == context.PrimitiveJavaTypeFactory.BOOLEAN)
                il.Emit(OpCodes.Ldind_U1);
            else if (this == context.PrimitiveJavaTypeFactory.BYTE)
                il.Emit(OpCodes.Ldind_U1);
            else if (this == context.PrimitiveJavaTypeFactory.CHAR)
                il.Emit(OpCodes.Ldind_U2);
            else if (this == context.PrimitiveJavaTypeFactory.SHORT)
                il.Emit(OpCodes.Ldind_I2);
            else if (this == context.PrimitiveJavaTypeFactory.INT)
                il.Emit(OpCodes.Ldind_I4);
            else if (this == context.PrimitiveJavaTypeFactory.LONG)
                il.Emit(OpCodes.Ldind_I8);
            else if (this == context.PrimitiveJavaTypeFactory.FLOAT)
                il.Emit(OpCodes.Ldind_R4);
            else if (this == context.PrimitiveJavaTypeFactory.DOUBLE)
                il.Emit(OpCodes.Ldind_R8);
            else
                il.Emit(OpCodes.Ldind_Ref);
        }

        /// <summary>
        /// Emits the appropriate instruction to do an indirect store of a reference to this type.
        /// </summary>
        /// <param name="il"></param>
        internal virtual void EmitStind(CodeEmitter il)
        {
            if (this == context.PrimitiveJavaTypeFactory.BOOLEAN)
                il.Emit(OpCodes.Stind_I1);
            else if (this == context.PrimitiveJavaTypeFactory.BYTE)
                il.Emit(OpCodes.Stind_I1);
            else if (this == context.PrimitiveJavaTypeFactory.CHAR)
                il.Emit(OpCodes.Stind_I2);
            else if (this == context.PrimitiveJavaTypeFactory.SHORT)
                il.Emit(OpCodes.Stind_I2);
            else if (this == context.PrimitiveJavaTypeFactory.INT)
                il.Emit(OpCodes.Stind_I4);
            else if (this == context.PrimitiveJavaTypeFactory.LONG)
                il.Emit(OpCodes.Stind_I8);
            else if (this == context.PrimitiveJavaTypeFactory.FLOAT)
                il.Emit(OpCodes.Stind_R4);
            else if (this == context.PrimitiveJavaTypeFactory.DOUBLE)
                il.Emit(OpCodes.Stind_R8);
            else
                il.Emit(OpCodes.Stind_Ref);
        }

#endif

        // NOTE don't call this method, call MethodWrapper.Link instead
        internal virtual MethodBase LinkMethod(RuntimeJavaMethod mw)
        {
            return mw.GetMethod();
        }

        // NOTE don't call this method, call FieldWrapper.Link instead
        internal virtual FieldInfo LinkField(RuntimeJavaField fw)
        {
            return fw.GetField();
        }

#if EMITTERS
        internal virtual void EmitRunClassConstructor(CodeEmitter ilgen)
        {
        }
#endif // EMITTERS

        internal virtual string GetGenericSignature()
        {
            return null;
        }

        internal virtual string GetGenericMethodSignature(RuntimeJavaMethod mw)
        {
            return null;
        }

        internal virtual string GetGenericFieldSignature(RuntimeJavaField fw)
        {
            return null;
        }

        internal virtual MethodParametersEntry[] GetMethodParameters(RuntimeJavaMethod mw)
        {
            return null;
        }

#if !IMPORTER && !EXPORTER
        internal virtual string[] GetEnclosingMethod()
        {
            return null;
        }

        internal virtual object[] GetDeclaredAnnotations()
        {
            return null;
        }

        internal virtual object[] GetMethodAnnotations(RuntimeJavaMethod mw)
        {
            return null;
        }

        internal virtual object[][] GetParameterAnnotations(RuntimeJavaMethod mw)
        {
            return null;
        }

        internal virtual object[] GetFieldAnnotations(RuntimeJavaField fw)
        {
            return null;
        }

        internal virtual string GetSourceFileName()
        {
            return null;
        }

        internal virtual int GetSourceLineNumber(MethodBase mb, int ilOffset)
        {
            return -1;
        }

        internal virtual object GetAnnotationDefault(RuntimeJavaMethod mw)
        {
            MethodBase mb = mw.GetMethod();
            if (mb != null)
            {
                object[] attr = mb.GetCustomAttributes(typeof(AnnotationDefaultAttribute), false);
                if (attr.Length == 1)
                {
                    return JVM.NewAnnotationElementValue(mw.DeclaringType.ClassLoader.GetJavaClassLoader(), mw.ReturnType.EnsureLoadable(mw.DeclaringType.ClassLoader).ClassObject, ((AnnotationDefaultAttribute)attr[0]).Value);
                }
            }
            return null;
        }
#endif // !IMPORTER && !EXPORTER

        internal virtual Annotation Annotation => null;

        internal virtual Type EnumType => null;

        private static Type[] GetInterfaces(Type type)
        {
#if IMPORTER || EXPORTER
            List<Type> list = new List<Type>();
            for (; type != null && !type.__IsMissing; type = type.BaseType)
            {
                AddInterfaces(list, type);
            }
            return list.ToArray();
#else
            return type.GetInterfaces();
#endif
        }

#if IMPORTER || EXPORTER

        private static void AddInterfaces(List<Type> list, Type type)
        {
            foreach (var iface in type.__GetDeclaredInterfaces())
            {
                if (!list.Contains(iface))
                {
                    list.Add(iface);
                    if (!iface.__IsMissing)
                    {
                        AddInterfaces(list, iface);
                    }
                }
            }
        }

#endif

        protected static RuntimeJavaType[] GetImplementedInterfacesAsTypeWrappers(RuntimeContext context, Type type)
        {
            var interfaceTypes = GetInterfaces(type);
            var interfaces = new RuntimeJavaType[interfaceTypes.Length];

            for (int i = 0; i < interfaceTypes.Length; i++)
            {
                var decl = interfaceTypes[i].DeclaringType;
                if (decl != null && context.AttributeHelper.IsGhostInterface(decl))
                {
                    // we have to return the declaring type for ghost interfaces
                    interfaces[i] = context.ClassLoaderFactory.GetJavaTypeFromType(decl);
                }
                else
                {
                    interfaces[i] = context.ClassLoaderFactory.GetJavaTypeFromType(interfaceTypes[i]);
                }
            }

            for (int i = 0; i < interfaceTypes.Length; i++)
            {
                if (interfaces[i].IsRemapped)
                {
                    // for remapped interfaces, we also return the original interface (Java types will ignore it, if it isn't listed in the ImplementsAttribute)
                    var twRemapped = interfaces[i];
                    var tw = context.ManagedJavaTypeFactory.GetJavaTypeFromManagedType(interfaceTypes[i]);
                    interfaces[i] = tw;
                    if (Array.IndexOf(interfaces, twRemapped) == -1)
                        interfaces = ArrayUtil.Concat(interfaces, twRemapped);
                }
            }

            return interfaces;
        }

        internal RuntimeJavaType GetPublicBaseTypeWrapper()
        {
            Debug.Assert(!IsPublic);

            if (IsUnloadable || IsInterface)
                return Context.JavaBase.TypeOfJavaLangObject;

            for (var tw = this; ; tw = tw.BaseTypeWrapper)
                if (tw.IsPublic)
                    return tw;
        }

#if !EXPORTER

        // return the constructor used for automagic .NET serialization
        internal virtual MethodBase GetSerializationConstructor()
        {
            return TypeAsBaseType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [context.Resolver.ResolveCoreType(typeof(System.Runtime.Serialization.SerializationInfo).FullName).AsReflection(), context.Resolver.ResolveCoreType(typeof(System.Runtime.Serialization.StreamingContext).FullName).AsReflection()], null);
        }

        internal virtual MethodBase GetBaseSerializationConstructor()
        {
            return BaseTypeWrapper.GetSerializationConstructor();
        }

#endif

#if !IMPORTER && !EXPORTER
        internal virtual object GhostWrap(object obj)
        {
            return obj;
        }

        internal virtual object GhostUnwrap(object obj)
        {
            return obj;
        }
#endif

        internal bool IsDynamic
        {
#if EXPORTER
            get { return false; }
#else
            get { return this is RuntimeByteCodeJavaType; }
#endif
        }

        internal virtual object[] GetConstantPool()
        {
            return null;
        }

        internal virtual byte[] GetRawTypeAnnotations()
        {
            return null;
        }

        internal virtual byte[] GetMethodRawTypeAnnotations(RuntimeJavaMethod mw)
        {
            return null;
        }

        internal virtual byte[] GetFieldRawTypeAnnotations(RuntimeJavaField fw)
        {
            return null;
        }

#if !IMPORTER && !EXPORTER
        internal virtual RuntimeJavaType Host
        {
            get { return null; }
        }
#endif
    }

}
