/*
  Copyright (C) 2011-2015 Jeroen Frijters

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
using System.Reflection;
using System.Threading;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang.Invoke;

namespace IKVM.Java.Externs.java.lang.invoke
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.invoke.MethodHandleNatives'. A resolved MemberName has the
    /// delegate that invokes its member in 'vmtarget' (added by map.xml), which the compiler's linkTo* calls use.
    /// </summary>
    static class MethodHandleNatives
    {

#if FIRST_PASS == false

        static CallSiteAccessor callSiteAccessor;

        static CallSiteAccessor CallSiteAccessor => JVM.Internal.BaseAccessors.Get(ref callSiteAccessor);

        const int MN_IS_METHOD = global::java.lang.invoke.MethodHandleNatives.Constants.MN_IS_METHOD;
        const int MN_IS_CONSTRUCTOR = global::java.lang.invoke.MethodHandleNatives.Constants.MN_IS_CONSTRUCTOR;
        const int MN_IS_FIELD = global::java.lang.invoke.MethodHandleNatives.Constants.MN_IS_FIELD;
        const int MN_CALLER_SENSITIVE = global::java.lang.invoke.MethodHandleNatives.Constants.MN_CALLER_SENSITIVE;
        const int MN_TRUSTED_FINAL = global::java.lang.invoke.MethodHandleNatives.Constants.MN_TRUSTED_FINAL;
        const int MN_REFERENCE_KIND_SHIFT = global::java.lang.invoke.MethodHandleNatives.Constants.MN_REFERENCE_KIND_SHIFT;
        const int REF_getField = global::java.lang.invoke.MethodHandleNatives.Constants.REF_getField;
        const int REF_getStatic = global::java.lang.invoke.MethodHandleNatives.Constants.REF_getStatic;
        const int REF_putField = global::java.lang.invoke.MethodHandleNatives.Constants.REF_putField;
        const int REF_putStatic = global::java.lang.invoke.MethodHandleNatives.Constants.REF_putStatic;
        const int REF_invokeVirtual = global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeVirtual;
        const int REF_invokeStatic = global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeStatic;
        const int REF_invokeSpecial = global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeSpecial;
        const int REF_newInvokeSpecial = global::java.lang.invoke.MethodHandleNatives.Constants.REF_newInvokeSpecial;
        const int REF_invokeInterface = global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeInterface;

#endif

        // called from map.xml as a replacement for Class.isInstance() in MethodHandleImpl.castReference()
        public static bool Class_isInstance(global::java.lang.Class clazz, object obj)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var tw = RuntimeJavaType.FromClass(clazz);
            // handle the type system hole that is caused by arrays being both derived from cli.System.Array and directly from java.lang.Object
            return tw.IsInstance(obj) || (tw == tw.Context.JavaBase.TypeOfCliSystemObject && obj is Array);
#endif
        }

        public static void init(global::java.lang.invoke.MemberName self, object refObj)
        {
            init(self, refObj, false);
        }

        // this overload is called via a map.xml patch to the MemberName(Method, boolean) constructor, because we need wantSpecial
        public static void init(global::java.lang.invoke.MemberName self, object refObj, bool wantSpecial)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            switch (refObj)
            {
                case global::java.lang.reflect.Method method:
                    InitMethodImpl(self, RuntimeJavaMethod.FromExecutable(method), wantSpecial);
                    break;
                case global::java.lang.reflect.Constructor constructor:
                    InitMethodImpl(self, RuntimeJavaMethod.FromExecutable(constructor), wantSpecial);
                    break;
                case global::java.lang.reflect.Field field:
                    var fw = RuntimeJavaField.FromField(field);
                    self._clazz(fw.DeclaringType.ClassObject);
                    int flags = (int)fw.Modifiers | MN_IS_FIELD;
                    flags |= (fw.IsStatic ? REF_getStatic : REF_getField) << MN_REFERENCE_KIND_SHIFT;
                    if (IsTrustedFinal(fw))
                        flags |= MN_TRUSTED_FINAL;
                    self._flags(flags);
                    break;
                default:
                    throw new InvalidOperationException();
            }
#endif
        }

#if FIRST_PASS == false

        static void InitMethodImpl(global::java.lang.invoke.MemberName self, RuntimeJavaMethod mw, bool wantSpecial)
        {
            int flags = (int)mw.Modifiers;
            flags |= mw.IsConstructor ? MN_IS_CONSTRUCTOR : MN_IS_METHOD;
            if (mw.IsStatic)
                flags |= REF_invokeStatic << MN_REFERENCE_KIND_SHIFT;
            else if (mw.IsConstructor && !wantSpecial)
                flags |= REF_newInvokeSpecial << MN_REFERENCE_KIND_SHIFT;
            else if (mw.IsPrivate || mw.IsFinal || mw.IsConstructor || wantSpecial)
                flags |= REF_invokeSpecial << MN_REFERENCE_KIND_SHIFT;
            else if (mw.DeclaringType.IsInterface)
                flags |= REF_invokeInterface << MN_REFERENCE_KIND_SHIFT;
            else
                flags |= REF_invokeVirtual << MN_REFERENCE_KIND_SHIFT;

            if (mw.HasCallerID || RuntimeByteCodeJavaType.RequiresDynamicReflectionCallerClass(mw.DeclaringType.Name, mw.Name, mw.Signature))
                flags |= MN_CALLER_SENSITIVE;

            if (mw.IsConstructor && mw.DeclaringType == JVM.Context.JavaBase.TypeOfJavaLangString)
            {
                var parameters1 = new global::java.lang.Class[mw.GetParameters().Length];
                for (int i = 0; i < mw.GetParameters().Length; i++)
                    parameters1[i] = mw.GetParameters()[i].ClassObject;

                var mt = global::java.lang.invoke.MethodType.methodType(JVM.Context.PrimitiveJavaTypeFactory.VOID.ClassObject, parameters1);
                self._type(mt);
                self._flags(flags);
                self._clazz(mw.DeclaringType.ClassObject);
                self.vmtarget = CreateMemberNameDelegate(mw, null, false, self.getMethodType().changeReturnType(JVM.Context.JavaBase.TypeOfJavaLangString.ClassObject));
                return;
            }

            self._flags(flags);
            self._clazz(mw.DeclaringType.ClassObject);
            int firstParam = mw.IsStatic ? 0 : 1;
            var parameters = new global::java.lang.Class[mw.GetParameters().Length + firstParam];
            for (int i = 0; i < mw.GetParameters().Length; i++)
                parameters[i + firstParam] = mw.GetParameters()[i].EnsureLoadable(mw.DeclaringType.ClassLoader).ClassObject;

            if (!mw.IsStatic)
                parameters[0] = mw.DeclaringType.ClassObject;

            var returnClass = mw.ReturnType.EnsureLoadable(mw.DeclaringType.ClassLoader).ClassObject;
            self.vmtarget = CreateMemberNameDelegate(mw, returnClass, !wantSpecial, global::java.lang.invoke.MethodType.methodType(returnClass, parameters));
        }

        /// <summary>
        /// Returns whether writes to the final field are refused even through a setAccessible reflective field: as
        /// HotSpot, a static final field or a final field of a record or hidden class.
        /// </summary>
        /// <param name="fw"></param>
        /// <returns></returns>
        static bool IsTrustedFinal(RuntimeJavaField fw)
        {
            return fw.IsFinal && (fw.IsStatic || fw.DeclaringType.IsRecord || fw.DeclaringType.IsHidden);
        }

        static void SetModifiers(global::java.lang.invoke.MemberName self, RuntimeJavaMember mw)
        {
            self._flags(self._flags() | (int)mw.Modifiers);
        }

#endif

        public static void expand(global::java.lang.invoke.MemberName self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // a MemberName made by init has all of its parts already
            if (self.getDeclaringClass() == null || self.getName() == null || self.getType() == null)
                throw new global::java.lang.InternalError("cannot expand " + self);
#endif
        }

        public static global::java.lang.invoke.MemberName resolve(global::java.lang.invoke.MemberName self, global::java.lang.Class caller, int lookupMode, bool speculativeResolve)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (self.getReferenceKind())
                {
                    case REF_invokeStatic:
                        if (self.getDeclaringClass() == JVM.Context.JavaBase.TypeOfJavaLangInvokeMethodHandle.ClassObject)
                        {
                            switch (self.getName())
                            {
                                case "linkToVirtual":
                                case "linkToStatic":
                                case "linkToSpecial":
                                case "linkToInterface":
                                    // this delegate is never used normally, only by the PrivateInvokeTest white-box JSR-292 tests
                                    self.vmtarget = MethodHandleUtil.DynamicMethodBuilder.CreateMethodHandleLinkTo(JVM.Context, self);
                                    self._flags(self._flags() | global::java.lang.reflect.Modifier.STATIC | global::java.lang.reflect.Modifier.NATIVE | MN_IS_METHOD);
                                    return self;
                            }
                        }
                        ResolveMethod(self, caller);
                        break;
                    case REF_invokeVirtual:
                        if (self.getDeclaringClass() == JVM.Context.JavaBase.TypeOfJavaLangInvokeMethodHandle.ClassObject)
                        {
                            switch (self.getName())
                            {
                                case "invoke":
                                case "invokeExact":
                                case "invokeBasic":
                                    self.vmtarget = MethodHandleUtil.DynamicMethodBuilder.CreateMethodHandleInvoke(JVM.Context, self);
                                    self._flags(self._flags() | global::java.lang.reflect.Modifier.NATIVE | global::java.lang.reflect.Modifier.FINAL | MN_IS_METHOD);
                                    return self;
                            }
                        }
                        ResolveMethod(self, caller);
                        break;
                    case REF_invokeInterface:
                    case REF_invokeSpecial:
                    case REF_newInvokeSpecial:
                        ResolveMethod(self, caller);
                        break;
                    case REF_getField:
                    case REF_putField:
                    case REF_getStatic:
                    case REF_putStatic:
                        ResolveField(self);
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            catch (global::java.lang.LinkageError) when (speculativeResolve)
            {
                // a speculative resolution reports a member it cannot find with null
                return null;
            }

            return self;
#endif
        }

#if FIRST_PASS == false

        static void ResolveMethod(global::java.lang.invoke.MemberName self, global::java.lang.Class caller)
        {
            bool invokeSpecial = self.getReferenceKind() == REF_invokeSpecial;
            bool newInvokeSpecial = self.getReferenceKind() == REF_newInvokeSpecial;
            bool searchBaseClasses = !newInvokeSpecial;
            var mw = RuntimeJavaType.FromClass(self.getDeclaringClass()).GetMethod(self.getName(), self.getSignature().Replace('/', '.'), searchBaseClasses);
            if (mw == null)
            {
                if (self.getReferenceKind() == REF_invokeInterface)
                {
                    mw = RuntimeJavaType.FromClass(self.getDeclaringClass()).GetInterfaceMethod(self.getName(), self.getSignature().Replace('/', '.'));
                    mw ??= JVM.Context.JavaBase.TypeOfJavaLangObject.GetMethod(self.getName(), self.getSignature().Replace('/', '.'), false);
                    if (mw != null && mw.IsConstructor)
                        throw new global::java.lang.IncompatibleClassChangeError("Found interface " + self.getDeclaringClass().getName() + ", but class was expected");
                }

                if (mw == null)
                {
                    var msg = string.Format(invokeSpecial ? "{0}: method {1}{2} not found" : "{0}.{1}{2}", self.getDeclaringClass().getName(), self.getName(), self.getSignature());
                    throw new global::java.lang.NoSuchMethodError(msg);
                }
            }

            if (mw.IsStatic != IsReferenceKindStatic(self.getReferenceKind()))
            {
                var msg = string.Format(mw.IsStatic ? "Expecting non-static method {0}.{1}{2}" : "Expected static method {0}.{1}{2}", mw.DeclaringType.Name, self.getName(), self.getSignature());
                throw new global::java.lang.IncompatibleClassChangeError(msg);
            }

            if (self.getReferenceKind() == REF_invokeVirtual && mw.DeclaringType.IsInterface)
                throw new global::java.lang.IncompatibleClassChangeError("Found interface " + mw.DeclaringType.Name + ", but class was expected");

            if (mw.IsConstructor && mw.DeclaringType == JVM.Context.JavaBase.TypeOfJavaLangString)
            {
                self.vmtarget = CreateMemberNameDelegate(mw, caller, false, self.getMethodType().changeReturnType(JVM.Context.JavaBase.TypeOfJavaLangString.ClassObject));
            }
            else if (!mw.IsConstructor || invokeSpecial || newInvokeSpecial)
            {
                var methodType = self.getMethodType();
                if (!mw.IsStatic)
                {
                    methodType = methodType.insertParameterTypes(0, mw.DeclaringType.ClassObject);
                    if (newInvokeSpecial)
                        methodType = methodType.changeReturnType(global::java.lang.Void.TYPE);
                }

                self.vmtarget = CreateMemberNameDelegate(mw, caller, self.hasReceiverTypeDispatch(), methodType);
            }

            // the class that declares the method, as HotSpot resolves it: Lookup's access checks go by it (a protected
            // method inherited through a reference class in another package is accessible from the declarer's package)
            self._clazz(mw.DeclaringType.ClassObject);
            SetModifiers(self, mw);
            self._flags(self._flags() | (mw.IsConstructor ? MN_IS_CONSTRUCTOR : MN_IS_METHOD));

            // a virtual call that does not dispatch is a special one (JDK 11+: private interface methods are invoked
            // with invokeinterface, which also does not dispatch)
            if ((self.getReferenceKind() == REF_invokeVirtual || self.getReferenceKind() == REF_invokeInterface) && (mw.IsPrivate || mw.IsFinal || mw.IsConstructor))
            {
                int flags = self._flags();
                flags -= self.getReferenceKind() << MN_REFERENCE_KIND_SHIFT;
                flags += REF_invokeSpecial << MN_REFERENCE_KIND_SHIFT;
                self._flags(flags);
            }

            if (mw.HasCallerID || RuntimeByteCodeJavaType.RequiresDynamicReflectionCallerClass(mw.DeclaringType.Name, mw.Name, mw.Signature))
                self._flags(self._flags() | MN_CALLER_SENSITIVE);
        }

        static void ResolveField(global::java.lang.invoke.MemberName self)
        {
            var fw = RuntimeJavaType.FromClass(self.getDeclaringClass()).GetFieldWrapper(self.getName(), self.getSignature().Replace('/', '.'));
            if (fw == null)
                throw new global::java.lang.NoSuchFieldError(self.getName());

            // the class that declares the field (see ResolveMethod)
            self._clazz(fw.DeclaringType.ClassObject);
            SetModifiers(self, fw);
            self._flags(self._flags() | MN_IS_FIELD);
            if (IsTrustedFinal(fw))
                self._flags(self._flags() | MN_TRUSTED_FINAL);

            if (fw.IsStatic != IsReferenceKindStatic(self.getReferenceKind()))
            {
                int newReferenceKind = self.getReferenceKind() switch
                {
                    REF_getField => REF_getStatic,
                    REF_putField => REF_putStatic,
                    REF_getStatic => REF_getField,
                    REF_putStatic => REF_putField,
                    _ => throw new InvalidOperationException(),
                };

                int flags = self._flags();
                flags -= self.getReferenceKind() << MN_REFERENCE_KIND_SHIFT;
                flags += newReferenceKind << MN_REFERENCE_KIND_SHIFT;
                self._flags(flags);
            }
        }

        static bool IsReferenceKindStatic(int referenceKind)
        {
            return referenceKind switch
            {
                REF_getField or REF_putField or REF_invokeVirtual or REF_invokeSpecial or REF_newInvokeSpecial or REF_invokeInterface => false,
                REF_getStatic or REF_putStatic or REF_invokeStatic => true,
                _ => throw new InvalidOperationException(),
            };
        }

        // TODO consider caching this delegate in MethodWrapper
        static Delegate CreateMemberNameDelegate(RuntimeJavaMethod mw, global::java.lang.Class caller, bool doDispatch, global::java.lang.invoke.MethodType type)
        {
            if (mw.IsDynamicOnly)
                return MethodHandleUtil.DynamicMethodBuilder.CreateDynamicOnly(mw.DeclaringType.Context, mw, type);

            // HACK this code is duplicated in compiler.cs
            if (mw.IsFinalizeOrClone)
            {
                var thisType = RuntimeJavaType.FromClass(caller);

                // HACK we may need to redirect finalize or clone from java.lang.Object/Throwable
                // to a more specific base type.
                if (thisType.IsAssignableTo(thisType.Context.JavaBase.TypeOfCliSystemObject))
                    mw = thisType.Context.JavaBase.TypeOfCliSystemObject.GetMethod(mw.Name, mw.Signature, true);
                else if (thisType.IsAssignableTo(thisType.Context.JavaBase.TypeOfCliSystemException))
                    mw = thisType.Context.JavaBase.TypeOfCliSystemException.GetMethod(mw.Name, mw.Signature, true);
                else if (thisType.IsAssignableTo(thisType.Context.JavaBase.TypeOfjavaLangThrowable))
                    mw = thisType.Context.JavaBase.TypeOfjavaLangThrowable.GetMethod(mw.Name, mw.Signature, true);
            }

            var tw = mw.DeclaringType;
            tw.Finish();
            mw.Link();
            mw.ResolveMethod();
            if (mw.GetMethod() is MethodInfo mi
                && !mw.HasCallerID
                && mw.IsStatic
                && tw.Context.MethodHandleUtil.HasOnlyBasicTypes(mw.GetParameters(), mw.ReturnType)
                && type.parameterCount() <= MethodHandleUtil.MaxArity)
            {
                return Delegate.CreateDelegate(tw.Context.MethodHandleUtil.CreateMemberWrapperDelegateType(mw.GetParameters(), mw.ReturnType), mi);
            }
            else
            {
                // slow path where we emit a DynamicMethod
                return MethodHandleUtil.DynamicMethodBuilder.CreateMemberName(tw.Context, mw, type, doDispatch);
            }
        }

        static RuntimeJavaField GetField(global::java.lang.invoke.MemberName self)
        {
            return RuntimeJavaType.FromClass(self.getDeclaringClass()).GetFieldWrapper(self.getName(), self.getSignature().Replace('/', '.'));
        }

#endif

        public static long objectFieldOffset(global::java.lang.invoke.MemberName self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var fw = GetField(self);
            if (fw.IsStatic)
                throw new global::java.lang.IllegalArgumentException();

            return IKVM.Java.Externs.jdk.@internal.misc.Unsafe.GetObjectFieldOffset(fw);
#endif
        }

        public static long staticFieldOffset(global::java.lang.invoke.MemberName self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var fw = GetField(self);
            if (fw.IsStatic == false)
                throw new global::java.lang.IllegalArgumentException();

            return (long)fw.Cookie;
#endif
        }

        public static object staticFieldBase(global::java.lang.invoke.MemberName self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var fw = GetField(self);
            if (fw.IsStatic == false)
                throw new global::java.lang.IllegalArgumentException();

            return fw.DeclaringType;
#endif
        }

        public static object getMemberVMInfo(global::java.lang.invoke.MemberName self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (self.isField())
                return new object[] { global::java.lang.Long.valueOf(0), self.getDeclaringClass() };

            if (global::java.lang.invoke.MethodHandleNatives.refKindDoesDispatch(self.getReferenceKind()))
                return new object[] { global::java.lang.Long.valueOf(0), self };

            return new object[] { global::java.lang.Long.valueOf(-1), self };
#endif
        }

#if FIRST_PASS == false

        internal static void InitializeCallSite(global::java.lang.invoke.CallSite site)
        {
            var type = typeof(IKVM.Runtime.IndyCallSite<>).MakeGenericType(JVM.Context.MethodHandleUtil.GetDelegateTypeForInvokeExact(site.type()));
            var ics = (IKVM.Runtime.IIndyCallSite)Activator.CreateInstance(type, true);
            Interlocked.CompareExchange(ref site.ics, ics, null);
        }

#endif

        public static void setCallSiteTargetNormal(global::java.lang.invoke.CallSite site, global::java.lang.invoke.MethodHandle target)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (site.ics == null)
                InitializeCallSite(site);

            lock (site.ics)
            {
                CallSiteAccessor.SetTarget(site, target);
                site.ics.SetTarget(target);
            }
#endif
        }

        public static void setCallSiteTargetVolatile(global::java.lang.invoke.CallSite site, global::java.lang.invoke.MethodHandle target)
        {
            setCallSiteTargetNormal(site, target);
        }

        public static void copyOutBootstrapArguments(global::java.lang.Class caller, int[] indexInfo, int start, int end, object[] buf, int pos, bool resolve, object ifNotAvailable)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // only for bootstrap methods the VM calls with their arguments left in the constant pool, which the compiler
            // never does: it resolves a bootstrap method's arguments where it compiles the call site
            throw new global::java.lang.UnsupportedOperationException();
#endif
        }

        public static void registerNatives()
        {

        }

        public static int getNamedCon(int which, object[] name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var fields = typeof(global::java.lang.invoke.MethodHandleNatives.Constants).GetFields(BindingFlags.Static | BindingFlags.NonPublic);
            if (which >= fields.Length)
            {
                name[0] = null;
                return -1;
            }

            name[0] = fields[which].Name;
            return ((IConvertible)fields[which].GetRawConstantValue()).ToInt32(null);
#endif
        }

    }

}
