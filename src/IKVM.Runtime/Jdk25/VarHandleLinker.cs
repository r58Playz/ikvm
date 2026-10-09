using System;
using System.Reflection;
using System.Reflection.Emit;

namespace IKVM.Runtime
{

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

    /// <summary>
    /// Links a call site of one of VarHandle's access mode methods, as HotSpot does: MethodHandleNatives.linkMethod
    /// gives the method that performs the access for the call site's type (a VarHandleGuards guard, or else a
    /// LambdaForm invoker), and the AccessDescriptor to pass it last. The call site gets a delegate of its own type,
    /// which passes its arguments as basic types to that method's delegate (the resolved MemberName's vmtarget), and
    /// returns the result as the call site's type.
    /// </summary>
    static class VarHandleLinker
    {

        const int REF_invokeVirtual = global::java.lang.invoke.MethodHandleNatives.Constants.REF_invokeVirtual;

        /// <summary>
        /// What a call site's delegate closes over.
        /// </summary>
        sealed class Target
        {

            internal readonly Delegate Method;
            internal readonly object Appendix;

            internal Target(Delegate method, object appendix)
            {
                Method = method;
                Appendix = appendix;
            }

        }

        static readonly FieldInfo targetMethodField = typeof(Target).GetField(nameof(Target.Method), BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo targetAppendixField = typeof(Target).GetField(nameof(Target.Appendix), BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Creates the delegate for a call site.
        /// </summary>
        /// <param name="delegateType">the call site's type, the VarHandle first</param>
        /// <param name="name">the access mode method</param>
        /// <param name="type">the call site's type without the VarHandle</param>
        /// <param name="caller">the class of the call site</param>
        /// <returns></returns>
        internal static Delegate Link(Type delegateType, string name, global::java.lang.invoke.MethodType type, global::java.lang.Class caller)
        {
            var context = JVM.Context;
            var varHandle = context.ClassLoaderFactory.GetJavaTypeFromType(typeof(global::java.lang.invoke.VarHandle));

            var appendix = new object[1];
            var linker = global::java.lang.invoke.MethodHandleNatives.linkMethod(caller, REF_invokeVirtual, varHandle.ClassObject, name, type, appendix);
            if (linker.vmtarget is not Delegate method)
                throw new global::java.lang.InternalError("VarHandle linker method not resolved: " + linker);

            // the call site's parameters: the VarHandle, then the call's
            var parameters = new RuntimeJavaType[type.parameterCount() + 1];
            parameters[0] = varHandle;
            for (int i = 1; i < parameters.Length; i++)
                parameters[i] = RuntimeJavaType.FromClass(type.parameterType(i - 1));
            var returnType = RuntimeJavaType.FromClass(type.returnType());

            var siteInvoke = delegateType.GetMethod("Invoke");
            var siteParameters = siteInvoke.GetParameters();
            var methodInvoke = method.GetType().GetMethod("Invoke");
            var methodParameters = methodInvoke.GetParameters();
            var hasAppendix = appendix[0] != null;
            if (siteParameters.Length != parameters.Length || methodParameters.Length != parameters.Length + (hasAppendix ? 1 : 0))
                throw new global::java.lang.UnsupportedOperationException("VarHandle." + name + type + " has too many parameters");

            var parameterTypes = new Type[siteParameters.Length + 1];
            parameterTypes[0] = typeof(Target);
            for (int i = 0; i < siteParameters.Length; i++)
                parameterTypes[i + 1] = siteParameters[i].ParameterType;

            var dm = DynamicMethodUtil.Create("__<VarHandle>__" + name, typeof(VarHandleLinker), true, siteInvoke.ReturnType, parameterTypes);
            var il = context.CodeEmitterFactory.Create(dm);

            il.EmitLdarg(0);
            il.Emit(OpCodes.Ldfld, targetMethodField);
            il.Emit(OpCodes.Castclass, method.GetType());
            for (int i = 0; i < parameters.Length; i++)
            {
                il.EmitLdarg(i + 1);
                ToBasic(parameters[i], il);
            }

            if (hasAppendix)
            {
                il.EmitLdarg(0);
                il.Emit(OpCodes.Ldfld, targetAppendixField);
            }

            il.Emit(OpCodes.Callvirt, methodInvoke);
            if (siteInvoke.ReturnType == typeof(void))
            {
                if (methodInvoke.ReturnType != typeof(void))
                    il.Emit(OpCodes.Pop);
            }
            else
            {
                FromBasic(returnType, il);
            }

            il.Emit(OpCodes.Ret);
            il.DoEmit();
            return dm.CreateDelegate(delegateType, new Target(method, appendix[0]));
        }

        /// <summary>
        /// Converts a value of the type to its basic type: an object, or an int for the types smaller than one.
        /// </summary>
        static void ToBasic(RuntimeJavaType tw, CodeEmitter il)
        {
            if (tw.IsNonPrimitiveValueType)
                tw.EmitBox(il);
            else if (tw.IsGhost)
                tw.EmitConvSignatureTypeToStackType(il);
        }

        /// <summary>
        /// Converts a value of the basic type of the type to the type.
        /// </summary>
        static void FromBasic(RuntimeJavaType tw, CodeEmitter il)
        {
            if (tw.IsNonPrimitiveValueType)
                tw.EmitUnbox(il);
            else if (tw.IsGhost)
                tw.EmitConvStackTypeToSignatureType(il, null);
            else if (!tw.IsPrimitive && tw != il.Context.JavaBase.TypeOfJavaLangObject)
                tw.EmitCheckcast(il);
        }

    }

#endif

}
