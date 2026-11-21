using System;

using IKVM.Runtime;

namespace IKVM.Java.Externs.java.util
{

    /// <summary>
    /// Implements the native methods for IKVM's 'java.util.Arrays' (src/IKVM.Java/jdk25/local/java.base/java/util).
    /// </summary>
    static class Arrays
    {

        /// <summary>
        /// A new array of <paramref name="original"/>'s own runtime type and length <c>to - from</c>, holding
        /// <c>original[from .. min(original.Length, to))</c>. Arrays.copyOf / copyOfRange call it instead of asking the
        /// original for its class (a runtime call under IKVM) and then Array.newInstance (reflective). Answers null for
        /// an array whose innermost element type is object and that carries a ghost tag (its Java type is the tag's, not
        /// its CLR type's): the caller then takes the stock path. The caller has checked the arguments.
        /// </summary>
        public static object[] copyOfSameType(object[] original, int from, int to)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (original == null)
                throw new global::java.lang.NullPointerException();

            var t = original.GetType();
            var e = t.GetElementType();
            while (e.IsArray)
                e = e.GetElementType();
            if (e == typeof(object) && GhostTag.GetTag(original) != null)
                return null;

#if NET9_0_OR_GREATER
            var copy = (object[])Array.CreateInstanceFromArrayType(t, to - from);
#else
            var copy = (object[])Array.CreateInstance(t.GetElementType(), to - from);
#endif
            var n = Math.Min(original.Length - from, to - from);
            if (n > 0)
                Array.Copy(original, from, copy, 0, n);
            return copy;
#endif
        }

    }

}
