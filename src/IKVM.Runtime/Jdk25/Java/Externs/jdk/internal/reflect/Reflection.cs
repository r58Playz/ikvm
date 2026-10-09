using System;
using System.Diagnostics;

using IKVM.Attributes;
using IKVM.Runtime;

namespace IKVM.Java.Externs.jdk.@internal.reflect
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.reflect.Reflection'.
    /// </summary>
    static class Reflection
    {

        public static global::java.lang.Class getCallerClass()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the compiler makes a call to this method an intrinsic; it only gets here through reflection, where
            // HotSpot fails the same way
            throw new global::java.lang.InternalError("CallerSensitive annotation expected at frame 1");
#endif
        }

        /// <summary>
        /// The dynamic version of the getCallerClass() intrinsic, which map.xml adds as getCallerClass(int): the class
        /// of the Java frame the given number of frames up the stack, not counting frames hidden from Java.
        /// </summary>
        /// <param name="realFramesToSkip"></param>
        /// <returns></returns>
        public static global::java.lang.Class getCallerClass(int realFramesToSkip)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (realFramesToSkip <= 0)
                return (global::java.lang.Class)ClassLiteral<global::jdk.@internal.reflect.Reflection>.Value;

            for (int i = 2; ;)
            {
                var method = new StackFrame(i++, false).GetMethod();
                if (method == null)
                    return null;

                if (StackWalkUtil.IsHideFromStackWalk(method))
                    continue;

                if ((StackWalkUtil.GetHideFromJavaFlags(method) & HideFromJavaFlags.StackTrace) != 0)
                    continue;

                if (--realFramesToSkip == 0)
                    return JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(method.DeclaringType).ClassObject;
            }
#endif
        }

        public static int getClassAccessFlags(global::java.lang.Class clazz)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the class's own access flags, not those of the InnerClasses attribute; an interface is abstract
            int mods = (int)RuntimeJavaType.FromClass(clazz).Modifiers & 0x7631;
            mods |= (mods & 0x0200) << 1;
            return mods;
#endif
        }

        public static bool areNestMates(global::java.lang.Class currentClass, global::java.lang.Class memberClass)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return RuntimeJavaType.FromClass(currentClass).IsNestmateOf(RuntimeJavaType.FromClass(memberClass));
#endif
        }

    }

}
