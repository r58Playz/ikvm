using System;
using System.Reflection;
using System.Runtime.CompilerServices;

using IKVM.Attributes;

namespace IKVM.Runtime
{

    /// <summary>
    /// Which .NET frames Java does not see in its stack walks and stack traces.
    /// </summary>
    static class StackWalkUtil
    {

        static readonly ConditionalWeakTable<MethodBase, Lazy<HideFromJavaFlags>> hideFromJavaFlagCache = new ConditionalWeakTable<MethodBase, Lazy<HideFromJavaFlags>>();

        /// <summary>
        /// Gets the <see cref="HideFromJavaFlags"/> of the method.
        /// </summary>
        /// <param name="mb"></param>
        /// <returns></returns>
        internal static HideFromJavaFlags GetHideFromJavaFlags(MethodBase mb)
        {
            return hideFromJavaFlagCache.GetValue(mb, _ => new Lazy<HideFromJavaFlags>(() => GetHideFromJavaFlagsImpl(_), true)).Value;
        }

        static HideFromJavaFlags GetHideFromJavaFlagsImpl(MethodBase mb)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return mb.Name.StartsWith("__<", StringComparison.Ordinal) ? HideFromJavaFlags.All : JVM.Context.AttributeHelper.GetHideFromJavaFlags(mb);
#endif
        }

        /// <summary>
        /// Returns <c>true</c> if a stack walk passes over the method: it is not Java code or is hidden from stack walks.
        /// </summary>
        /// <param name="mb"></param>
        /// <returns></returns>
        internal static bool IsHideFromStackWalk(MethodBase mb)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var type = mb.DeclaringType;
            if (type == null ||
                type.Assembly == typeof(object).Assembly ||
                type.Assembly == typeof(StackWalkUtil).Assembly ||
                type == typeof(global::java.lang.reflect.Method) ||
                type == typeof(global::java.lang.reflect.Constructor) ||
                (GetHideFromJavaFlags(mb) & HideFromJavaFlags.StackWalk) != 0)
                return true;

            return false;
#endif
        }

    }

}
