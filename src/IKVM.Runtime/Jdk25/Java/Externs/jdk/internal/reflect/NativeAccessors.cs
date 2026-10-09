using System;

using IKVM.Runtime;

namespace IKVM.Java.Externs.jdk.@internal.reflect
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'jdk.internal.reflect.DirectMethodHandleAccessor$NativeAccessor', which
    /// invokes a method reflectively until method handles can (early in the boot), as HotSpot's
    /// NativeAccessors::invoke_method does.
    /// </summary>
    static class DirectMethodHandleAccessor
    {

        public static class NativeAccessor
        {

            public static object invoke0(global::java.lang.reflect.Method m, object obj, object[] args)
            {
#if FIRST_PASS
                throw new NotImplementedException();
#else
                var mw = RuntimeJavaMethod.FromExecutable(m);
                mw.Link();
                mw.ResolveMethod();

                if (!mw.IsStatic && !mw.DeclaringType.IsInstance(obj))
                {
                    if (obj == null)
                        throw new global::java.lang.NullPointerException();

                    throw new global::java.lang.IllegalArgumentException("object is not an instance of declaring class");
                }

                args = ReflectionArguments.Convert(mw.DeclaringType.ClassLoader, mw.GetParameters(), args);

                // .NET reflection does not initialize an interface
                if (mw.DeclaringType.IsInterface)
                    mw.DeclaringType.RunClassInit();

                // a caller-sensitive method sees the class that declares it as its caller
                if (mw.HasCallerID)
                    args = ArrayUtil.Concat(args, global::ikvm.@internal.CallerID.create(mw.DeclaringType.TypeAsBaseType.TypeHandle));

                object result;
                try
                {
                    result = mw.Invoke(obj, args);
                }
                catch (Exception e)
                {
                    throw new global::java.lang.reflect.InvocationTargetException(global::ikvm.runtime.Util.mapException(e));
                }

                if (mw.ReturnType.IsPrimitive && mw.ReturnType != mw.DeclaringType.Context.PrimitiveJavaTypeFactory.VOID)
                    return JVM.Box(result);

                return mw.ReturnType.GhostUnwrap(result);
#endif
            }

        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'jdk.internal.reflect.DirectConstructorHandleAccessor$NativeAccessor',
    /// which creates an object reflectively until method handles can.
    /// </summary>
    static class DirectConstructorHandleAccessor
    {

        public static class NativeAccessor
        {

            public static object newInstance0(global::java.lang.reflect.Constructor c, object[] args)
            {
#if FIRST_PASS
                throw new NotImplementedException();
#else
                var mw = RuntimeJavaMethod.FromExecutable(c);
                mw.Link();
                mw.ResolveMethod();

                args = ReflectionArguments.Convert(mw.DeclaringType.ClassLoader, mw.GetParameters(), args);
                try
                {
                    return mw.CreateInstance(args);
                }
                catch (Exception e)
                {
                    throw new global::java.lang.reflect.InvocationTargetException(global::ikvm.runtime.Util.mapException(e));
                }
#endif
            }

        }

    }

}
