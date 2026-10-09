/*
  Copyright (C) 2002-2014 Jeroen Frijters

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

using IKVM.Attributes;

using System.Threading;

#if IMPORTER || EXPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;

using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif

namespace IKVM.Runtime
{

    abstract class RuntimeJavaField : RuntimeJavaMember
    {

#if !IMPORTER && !FIRST_PASS && !EXPORTER
        volatile java.lang.reflect.Field reflectionField;
#endif
        FieldInfo field;
        RuntimeJavaType fieldType;
        bool linked;    // set once linking has completed; read lock-free by Link()
        bool linking;   // LinkField is running on the thread holding the lock

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="declaringType"></param>
        /// <param name="fieldType"></param>
        /// <param name="name"></param>
        /// <param name="sig"></param>
        /// <param name="modifiers"></param>
        /// <param name="field"></param>
        /// <param name="flags"></param>
        internal RuntimeJavaField(RuntimeJavaType declaringType, RuntimeJavaType fieldType, string name, string sig, Modifiers modifiers, FieldInfo field, MemberFlags flags) :
            base(declaringType, name, sig, modifiers, flags)
        {
            if (name == null)
                throw new ArgumentNullException();
            if (sig == null)
                throw new ArgumentNullException();

            this.fieldType = fieldType;
            this.field = field;

            UpdateNonPublicTypeInSignatureFlag();

#if IMPORTER
            if (IsFinal && 
                DeclaringType.IsPublic && 
                !DeclaringType.IsInterface && 
                (IsPublic || (IsProtected && !DeclaringType.IsFinal)) &&
                !DeclaringType.ClassLoader.StrictFinalFieldSemantics && 
                DeclaringType.IsDynamic && 
                this is not RuntimeConstantJavaField && 
                this is not RuntimeByteCodePropertyJavaField)
            {
                SetType2FinalField();
            }
#endif
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="declaringType"></param>
        /// <param name="fieldType"></param>
        /// <param name="name"></param>
        /// <param name="sig"></param>
        /// <param name="modifiers"></param>
        /// <param name="field"></param>
        internal RuntimeJavaField(RuntimeJavaType declaringType, RuntimeJavaType fieldType, string name, string sig, ExModifiers modifiers, FieldInfo field) :
            this(declaringType, fieldType, name, sig, modifiers.Modifiers, field, (modifiers.IsInternal ? MemberFlags.InternalAccess : MemberFlags.None))
        {

        }

        void UpdateNonPublicTypeInSignatureFlag()
        {
            if ((IsPublic || IsProtected) && fieldType != null && !IsAccessStub)
            {
                if (!fieldType.IsPublic && !fieldType.IsUnloadable)
                {
                    SetNonPublicTypeInSignatureFlag();
                }
            }
        }

        /// <summary>
        /// Gets the CLR <see cref="FieldInfo"/> that forms the implementation of the field.
        /// </summary>
        /// <returns></returns>
        internal FieldInfo GetField()
        {
            AssertLinked();
            return field;
        }

        /// <summary>
        /// Fails if the field has not yet been linked.
        /// </summary>
        [Conditional("DEBUG")]
        internal void AssertLinked()
        {
            if (fieldType == null)
                throw new InternalException($"Field is not linked: {DeclaringType.Name}::{Name} ({Signature})");

            Debug.Assert(fieldType != null, this.DeclaringType.Name + "::" + this.Name + " (" + this.Signature + ")");
        }

#if !IMPORTER && !EXPORTER

        /// <summary>
        /// Gets the <see cref="RuntimeJavaField"/> for the given <see cref="java.lang.reflect.Field"/>.
        /// </summary>
        /// <param name="field"></param>
        /// <returns></returns>
        internal static RuntimeJavaField FromField(java.lang.reflect.Field field)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            int slot = field._slot();
            if (slot == -1)
            {
                // it's a Field created by Unsafe.objectFieldOffset(Class,String) so we must resolve based on the name
                foreach (var fw in RuntimeJavaType.FromClass(field.getDeclaringClass()).GetFields())
                    if (fw.Name == field.getName())
                        return fw;
            }

            return RuntimeJavaType.FromClass(field.getDeclaringClass()).GetFields()[slot];
#endif
        }

        internal object ToField(bool copy)
        {
            return ToField(copy, null);
        }

        internal object ToField(bool copy, int? fieldIndex)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            java.lang.reflect.Field field = reflectionField;
            if (field == null)
            {
                const Modifiers ReflectionFieldModifiersMask = Modifiers.Public | Modifiers.Private | Modifiers.Protected | Modifiers.Static | Modifiers.Final | Modifiers.Volatile | Modifiers.Transient | Modifiers.Synthetic | Modifiers.Enum;
                Link();
                field = new java.lang.reflect.Field(
                    DeclaringType.ClassObject,
                    Name,
                    FieldTypeWrapper.EnsureLoadable(DeclaringType.ClassLoader).ClassObject,
                    (int)(Modifiers & ReflectionFieldModifiersMask) | (IsInternal ? 0x40000000 : 0),
#if JDK25
                    // trusted final: a final field that even setAccessible does not make writable, as with HotSpot
                    IsFinal && (IsStatic || DeclaringType.IsRecord || DeclaringType.IsHidden),
#endif
                    fieldIndex ?? Array.IndexOf(DeclaringType.GetFields(), this),
                    DeclaringType.GetGenericFieldSignature(this),
                    null
                );
            }

            lock (this)
            {
                if (reflectionField == null)
                    reflectionField = field;
                else
                    field = reflectionField;
            }

            if (copy)
                field = field.copy();

            return field;
#endif
        }

#endif

        /// <summary>
        /// Resolves the <see cref="RuntimeJavaField"/> instance referenced by the specified cookie.
        /// </summary>
        /// <param name="cookie"></param>
        /// <returns></returns>
        [System.Security.SecurityCritical]
        internal static RuntimeJavaField FromCookie(IntPtr cookie)
        {
            return (RuntimeJavaField)FromCookieImpl(cookie);
        }

        internal RuntimeJavaType FieldTypeWrapper
        {
            get
            {
                AssertLinked();
                return fieldType;
            }
        }

#if EMITTERS

        /// <summary>
        /// Emits the IL code to set the value of the field.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitGet(CodeEmitter il)
        {
            AssertLinked();
            EmitGetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to set the value of the field.
        /// </summary>
        /// <param name="il"></param>
        protected abstract void EmitGetImpl(CodeEmitter il);

        /// <summary>
        /// Emits the IL code to implement the unsafe get operation.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitUnsafeGet(CodeEmitter il)
        {
            AssertLinked();
            EmitUnsafeGetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe get operation.
        /// </summary>
        /// <param name="il"></param>
        protected virtual void EmitUnsafeGetImpl(CodeEmitter il)
        {
            EmitGetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe volatile get operation.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitUnsafeVolatileGet(CodeEmitter il)
        {
            AssertLinked();
            EmitUnsafeVolatileGetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe volatile get operation.
        /// </summary>
        /// <param name="il"></param>
        protected virtual void EmitUnsafeVolatileGetImpl(CodeEmitter il)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Emits the IL code to implement the set operation.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitSet(CodeEmitter il)
        {
            AssertLinked();
            EmitSetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the set operation.
        /// </summary>
        /// <param name="il"></param>
        protected abstract void EmitSetImpl(CodeEmitter il);

        /// <summary>
        /// Emits the IL code to implement the unsafe set operation.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitUnsafeSet(CodeEmitter il)
        {
            AssertLinked();
            EmitSetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe set operation.
        /// </summary>
        /// <param name="il"></param>
        protected virtual void EmitUnsafeSetImpl(CodeEmitter il)
        {
            EmitSetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe volatile set operation.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitUnsafeVolatileSet(CodeEmitter il)
        {
            AssertLinked();
            EmitUnsafeVolatileSetImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement the unsafe volatile set operation.
        /// </summary>
        /// <param name="il"></param>
        protected virtual void EmitUnsafeVolatileSetImpl(CodeEmitter il)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Emits the IL code to implement an unsafe compare/exchange operation. The stack is expected to contain the object,
        /// the comparand and the value. The applied value is placed on the stack after execution.
        /// </summary>
        /// <param name="il"></param>
        internal void EmitUnsafeCompareAndSwap(CodeEmitter il)
        {
            AssertLinked();
            EmitUnsafeCompareAndSwapImpl(il);
        }

        /// <summary>
        /// Emits the IL code to implement an unsafe compare/exchange operation. The stack is expected to contain the object,
        /// the comparand and the value. The applied value is placed on the stack after execution.
        /// </summary>
        /// <param name="il"></param>
        protected virtual void EmitUnsafeCompareAndSwapImpl(CodeEmitter il)
        {
            throw new NotSupportedException();
        }

#endif

#if IMPORTER

        internal bool IsLinked
        {
            get { return fieldType != null; }
        }

#endif

        internal void Link()
        {
            Link(LoadMode.Link);
        }

        internal void Link(LoadMode mode)
        {
            // wasm fork: lock-free once linked. Link() is called on every Unsafe field access that takes the general
            // path and on every reflective one, and the monitor it took each time was most of the client thread's lock
            // traffic (R352). Under the lock, fieldType != null means linked unless LinkField is still running (it can
            // re-enter Link on the same thread) -- a failed LinkField resets fieldType -- hence the linking flag.
            if (Volatile.Read(ref linked))
                return;

            lock (this)
                if (fieldType != null)
                {
                    if (linking == false)
                        Volatile.Write(ref linked, true);

                    return;
                }

            var fld = DeclaringType.ClassLoader.FieldTypeWrapperFromSig(Signature, mode);

            lock (this)
            {
                    // critical code in the finally block to avoid Thread.Abort interrupting the thread
					// removed for mono aot
                    if (fieldType == null)
                    {
                        fieldType = fld;
                        UpdateNonPublicTypeInSignatureFlag();

                        linking = true;
                        try
                        {
                            field = this.DeclaringType.LinkField(this);
                        }
                        catch
                        {
                            // HACK if linking fails, we unlink to make sure
                            // that the next link attempt will fail again
                            fieldType = null;
                            throw;
                        }
                        finally
                        {
                            linking = false;
                        }
                    }

                    Volatile.Write(ref linked, true);
            }
        }

        internal bool IsVolatile
        {
            get
            {
                return (Modifiers & Modifiers.Volatile) != 0;
            }
        }

        internal bool IsSerialVersionUID
        {
            get
            {
                // a serialVersionUID field must be static and final to be recognized (see ObjectStreamClass.getDeclaredSUID())
                return (Modifiers & (Modifiers.Static | Modifiers.Final)) == (Modifiers.Static | Modifiers.Final)
                    && Name == "serialVersionUID"
                    && (FieldTypeWrapper == DeclaringType.Context.PrimitiveJavaTypeFactory.LONG
                        || FieldTypeWrapper == DeclaringType.Context.PrimitiveJavaTypeFactory.INT
                        || FieldTypeWrapper == DeclaringType.Context.PrimitiveJavaTypeFactory.CHAR
                        || FieldTypeWrapper == DeclaringType.Context.PrimitiveJavaTypeFactory.SHORT
                        || FieldTypeWrapper == DeclaringType.Context.PrimitiveJavaTypeFactory.BYTE);
            }
        }

        internal static RuntimeJavaField Create(RuntimeJavaType declaringType, RuntimeJavaType fieldType, FieldInfo fi, string name, string sig, ExModifiers modifiers)
        {
            // volatile long & double field accesses must be made atomic
            if ((modifiers.Modifiers & Modifiers.Volatile) != 0 && (sig == "J" || sig == "D"))
                return new RuntimeVolatileLongDoubleJavaField(declaringType, fieldType, fi, name, sig, modifiers);

            return new RuntimeSimpleJavaField(declaringType, fieldType, fi, name, sig, modifiers);
        }

#if !IMPORTER && !EXPORTER
        internal virtual void ResolveField()
        {
            var fb = field as FieldBuilder;
            if (fb != null)
            {
#if NETFRAMEWORK
                field = fb.DeclaringType.Module.ResolveField(fb.GetToken().Token);
#else
                BindingFlags flags = BindingFlags.DeclaredOnly;
                flags |= fb.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;
                flags |= fb.IsStatic ? BindingFlags.Static : BindingFlags.Instance;
                field = DeclaringType.TypeAsTBD.GetField(fb.Name, flags);
#endif
            }
        }

#if !FIRST_PASS

        /// <summary>
        /// Gets the value of the field.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        internal abstract object GetValue(object obj);

        /// <summary>
        /// Sets the value of the field.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="value"></param>
        internal abstract void SetValue(object obj, object value);

        Delegate unsafeGetValueDelegate;
        Delegate unsafeSetValueDelegate;
        Delegate unsafeVolatileGetDelegate;
        Delegate unsafeVolatileSetDelegate;
        Delegate unsafeCompareExchangeDelegate;

        /// <summary>
        /// Performs an unsafe get operation on the field.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        internal virtual TField UnsafeGetValue<TField>(object obj)
        {
            var d = GetUnsafeGetDelegate();
            return d is Func<object, TField> f ? f(obj) : PunnedGet<TField>(d, obj);
        }

        /// <summary>
        /// Gets a delegate capable of performing an unsafe get operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate GetUnsafeGetDelegate()
        {
            if (unsafeGetValueDelegate == null)
                Interlocked.CompareExchange(ref unsafeGetValueDelegate, CreateUnsafeGetDelegate(), null);

            return unsafeGetValueDelegate;
        }

        /// <summary>
        /// Creates a delegate capable of performing an unsafe get operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate CreateUnsafeGetDelegate()
        {
            DeclaringType.Finish();
            if (!FieldTypeWrapper.IsUnloadable)
                FieldTypeWrapper.Finish();
            ResolveField();

            var ft = FieldTypeWrapper.IsPrimitive ? FieldTypeWrapper.TypeAsSignatureType : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafeGet>__{DeclaringType.Name.Replace(".", "_")}__{Name}", DeclaringType.TypeAsTBD, true, ft, new[] { typeof(object) });
            var il = JVM.Context.CodeEmitterFactory.Create(dm);

            if (IsStatic == false)
                il.Emit(OpCodes.Ldarg_0);

            EmitUnsafeGet(il);

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(object), ft));
        }

        /// <summary>
        /// Performs an unsafe set operation on the field.
        /// </summary>
        /// <typeparam name="TField"></typeparam>
        /// <param name="obj"></param>
        /// <param name="value"></param>
        internal virtual void UnsafeSetValue<TField>(object obj, TField value)
        {
            var d = GetUnsafeSetDelegate();
            if (d is Action<object, TField> f)
                f(obj, value);
            else
                PunnedSet(d, obj, value);
        }

        /// <summary>
        /// Gets a delegate capable of performing an unsafe set operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate GetUnsafeSetDelegate()
        {
            if (unsafeSetValueDelegate == null)
                Interlocked.CompareExchange(ref unsafeSetValueDelegate, CreateUnsafeSetDelegate(), null);

            return unsafeSetValueDelegate;
        }

        /// <summary>
        /// Creates a delegate capable of performing an unsafe set operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate CreateUnsafeSetDelegate()
        {
            DeclaringType.Finish();
            if (!FieldTypeWrapper.IsUnloadable)
                FieldTypeWrapper.Finish();
            ResolveField();

            var ft = FieldTypeWrapper.IsPrimitive ? FieldTypeWrapper.TypeAsSignatureType : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafeSet>__{DeclaringType.Name.Replace(".", "_")}__{Name}", DeclaringType.TypeAsTBD, true, typeof(void), new[] { typeof(object), ft });
            var il = JVM.Context.CodeEmitterFactory.Create(dm);

            if (IsStatic == false)
                il.Emit(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_1);
            EmitUnsafeSet(il);

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(object), ft));
        }

        /// <summary>
        /// Performs an unsafe volatile get operation on the field.
        /// </summary>
        /// <typeparam name="TField"></typeparam>
        /// <param name="obj"></param>
        /// <returns></returns>
        internal virtual TField UnsafeVolatileGet<TField>(object obj)
        {
            var d = GetUnsafeVolatileGetDelegate();
            return d is Func<object, TField> f ? f(obj) : PunnedGet<TField>(d, obj);
        }

        /// <summary>
        /// Gets a delegate capable of performing an unsafe volatile get operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate GetUnsafeVolatileGetDelegate()
        {
            if (unsafeVolatileGetDelegate == null)
                Interlocked.CompareExchange(ref unsafeVolatileGetDelegate, CreateUnsafeVolatileGetDelegate(), null);

            return unsafeVolatileGetDelegate;
        }

        /// <summary>
        /// Creates a delegate capable of performing an unsafe volatile get operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate CreateUnsafeVolatileGetDelegate()
        {
            ResolveField();
            var ft = FieldTypeWrapper.IsPrimitive ? FieldTypeWrapper.TypeAsSignatureType : typeof(object);
            var dm = new DynamicMethod($"__<UnsafeVolatileGet>__{DeclaringType.Name.Replace(".", "_")}__{Name}", ft, new[] { typeof(object) }, DeclaringType.TypeAsTBD.Module, true);
            var il = JVM.Context.CodeEmitterFactory.Create(dm);

            if (IsStatic == false)
                il.Emit(OpCodes.Ldarg_0);

            EmitUnsafeVolatileGet(il);

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(object), ft));
        }

        /// <summary>
        /// Performs an unsafe volatile set operation on the field.
        /// </summary>
        /// <typeparam name="TField"></typeparam>
        /// <param name="obj"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        internal virtual void UnsafeVolatileSet<TField>(object obj, TField value)
        {
            var d = GetUnsafeVolatileSetDelegate();
            if (d is Action<object, TField> f)
                f(obj, value);
            else
                PunnedSet(d, obj, value);
        }

        /// <summary>
        /// Gets a delegate capable of performing an unsafe volatile set operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate GetUnsafeVolatileSetDelegate()
        {
            if (unsafeVolatileSetDelegate == null)
                Interlocked.CompareExchange(ref unsafeVolatileSetDelegate, CreateUnsafeVolatileSetDelegate(), null);

            return unsafeVolatileSetDelegate;
        }

        /// <summary>
        /// Creates a delegate capable of performing an unsafe volatile set operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate CreateUnsafeVolatileSetDelegate()
        {
            ResolveField();
            var ft = FieldTypeWrapper.IsPrimitive ? FieldTypeWrapper.TypeAsSignatureType : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafeVolatileSet>__{DeclaringType.Name.Replace(".", "_")}__{Name}", DeclaringType.TypeAsTBD, true, typeof(void), new[] { typeof(object), ft });
            var il = JVM.Context.CodeEmitterFactory.Create(dm);

            if (IsStatic == false)
                il.Emit(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_1);
            EmitUnsafeVolatileSet(il);

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(object), ft));
        }

        /// <summary>
        /// Unsafe compare and swap implementation for the field.
        /// </summary>
        /// <typeparam name="TField"></typeparam>
        /// <param name="obj"></param>
        /// <param name="expected"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        internal virtual bool UnsafeCompareAndSwap<TField>(object obj, TField expected, TField value)
        {
            var d = GetUnsafeCompareAndSwapDelegate();
            return d is Func<object, TField, TField, bool> f ? f(obj, expected, value) : PunnedCompareAndSwap(d, obj, expected, value);
        }

        // JDK 9+'s Unsafe accesses a field as another type of the same size: its float and double operations are int
        // and long ones on the bits, its boolean ones byte ones and its char ones short ones. These convert between the
        // type asked for and the field's.

        static TField PunnedGet<TField>(Delegate d, object obj)
        {
            return d switch
            {
                Func<object, float> f when typeof(TField) == typeof(int) => (TField)(object)FloatToBits(f(obj)),
                Func<object, double> f when typeof(TField) == typeof(long) => (TField)(object)DoubleToBits(f(obj)),
                Func<object, bool> f when typeof(TField) == typeof(byte) => (TField)(object)(byte)(f(obj) ? 1 : 0),
                Func<object, char> f when typeof(TField) == typeof(short) => (TField)(object)(short)f(obj),
                _ => throw new InvalidCastException($"Unable to access a field of {d.GetType().GetGenericArguments()[1]} as {typeof(TField)}."),
            };
        }

        static void PunnedSet<TField>(Delegate d, object obj, TField value)
        {
            switch (d)
            {
                case Action<object, float> f when typeof(TField) == typeof(int):
                    f(obj, BitsToFloat((int)(object)value));
                    break;
                case Action<object, double> f when typeof(TField) == typeof(long):
                    f(obj, BitsToDouble((long)(object)value));
                    break;
                case Action<object, bool> f when typeof(TField) == typeof(byte):
                    f(obj, (byte)(object)value != 0);
                    break;
                case Action<object, char> f when typeof(TField) == typeof(short):
                    f(obj, (char)(short)(object)value);
                    break;
                default:
                    throw new InvalidCastException($"Unable to access a field of {d.GetType().GetGenericArguments()[1]} as {typeof(TField)}.");
            }
        }

        static bool PunnedCompareAndSwap<TField>(Delegate d, object obj, TField expected, TField value)
        {
            return d switch
            {
                Func<object, float, float, bool> f when typeof(TField) == typeof(int) => f(obj, BitsToFloat((int)(object)expected), BitsToFloat((int)(object)value)),
                Func<object, double, double, bool> f when typeof(TField) == typeof(long) => f(obj, BitsToDouble((long)(object)expected), BitsToDouble((long)(object)value)),
                Func<object, bool, bool, bool> f when typeof(TField) == typeof(byte) => f(obj, (byte)(object)expected != 0, (byte)(object)value != 0),
                Func<object, char, char, bool> f when typeof(TField) == typeof(short) => f(obj, (char)(short)(object)expected, (char)(short)(object)value),
                _ => throw new InvalidCastException($"Unable to access a field of {d.GetType().GetGenericArguments()[1]} as {typeof(TField)}."),
            };
        }

        static int FloatToBits(float value) => System.Runtime.CompilerServices.Unsafe.As<float, int>(ref value);

        static float BitsToFloat(int value) => System.Runtime.CompilerServices.Unsafe.As<int, float>(ref value);

        static long DoubleToBits(double value) => System.Runtime.CompilerServices.Unsafe.As<double, long>(ref value);

        static double BitsToDouble(long value) => System.Runtime.CompilerServices.Unsafe.As<long, double>(ref value);


        /// <summary>
        /// Gets a delegate implementing the unsafe compare and swap operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate GetUnsafeCompareAndSwapDelegate()
        {
            if (unsafeCompareExchangeDelegate == null)
                Interlocked.CompareExchange(ref unsafeCompareExchangeDelegate, CreateUnsafeCompareAndSwapDelegate(), null);

            return unsafeCompareExchangeDelegate;
        }

        /// <summary>
        /// Creates a delegate implementing the unsafe compare and swap operation on the field.
        /// </summary>
        /// <returns></returns>
        Delegate CreateUnsafeCompareAndSwapDelegate()
        {
            ResolveField();
            var ft = FieldTypeWrapper.IsPrimitive ? FieldTypeWrapper.TypeAsSignatureType : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafeCompareAndSwap>__{DeclaringType.Name.Replace(".", "_")}__{Name}", DeclaringType.TypeAsTBD, true, typeof(bool), new[] { typeof(object), ft, ft });
            var il = JVM.Context.CodeEmitterFactory.Create(dm);

            if (IsStatic == false)
                il.Emit(OpCodes.Ldarg_0);

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            EmitUnsafeCompareAndSwap(il);

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(typeof(Func<,,,>).MakeGenericType(typeof(object), ft, ft, typeof(bool)));
        }

#endif

#endif

    }

}
