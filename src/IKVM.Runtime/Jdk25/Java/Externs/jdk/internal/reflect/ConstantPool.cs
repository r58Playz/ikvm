using System;
using System.Reflection;

namespace IKVM.Java.Externs.jdk.@internal.reflect
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.reflect.ConstantPool'. Its 'constantPoolOop' is the constant
    /// pool the runtime keeps of a class, an object[] with the values annotation parsing reads.
    /// </summary>
    static class ConstantPool
    {

#if FIRST_PASS == false

        static FieldInfo constantPoolOopField;

        /// <summary>
        /// Creates a ConstantPool for the constant pool of a class.
        /// </summary>
        /// <param name="constantPool"></param>
        /// <returns></returns>
        internal static object Create(object[] constantPool)
        {
            var pool = new global::jdk.@internal.reflect.ConstantPool();
            constantPoolOopField ??= typeof(global::jdk.@internal.reflect.ConstantPool).GetField("constantPoolOop", BindingFlags.NonPublic | BindingFlags.Instance);
            constantPoolOopField.SetValue(pool, constantPool);
            return pool;
        }

        static object Get(object constantPoolOop, int index)
        {
            var pool = (object[])constantPoolOop;
            if (index < 0 || index >= pool.Length || pool[index] == null)
                throw new global::java.lang.IllegalArgumentException("Wrong type at constant pool index");

            return pool[index];
        }

#endif

        public static int getSize0(object self, object constantPoolOop)
        {
            return ((object[])constantPoolOop).Length;
        }

        public static string getUTF8At0(object self, object constantPoolOop, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (string)Get(constantPoolOop, index);
#endif
        }

        public static int getIntAt0(object self, object constantPoolOop, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (int)Get(constantPoolOop, index);
#endif
        }

        public static long getLongAt0(object self, object constantPoolOop, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (long)Get(constantPoolOop, index);
#endif
        }

        public static float getFloatAt0(object self, object constantPoolOop, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (float)Get(constantPoolOop, index);
#endif
        }

        public static double getDoubleAt0(object self, object constantPoolOop, int index)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (double)Get(constantPoolOop, index);
#endif
        }

    }

}
