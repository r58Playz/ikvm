using System;
using System.Threading;

using IKVM.Runtime;

using Impl = IKVM.Java.Externs.sun.misc.Unsafe;

namespace IKVM.Java.Externs.jdk.@internal.misc
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'jdk.internal.misc.Unsafe' with the implementation of JDK 8's
    /// sun.misc.Unsafe ones. JDK 25 has fewer natives: the address-only and compound operations (getAndAdd, the
    /// release/acquire/opaque forms, weak CAS) are Java code on top of these.
    /// </summary>
    static class Unsafe
    {

        public static void registerNatives()
        {

        }

        /// <summary>
        /// Gets the offset Unsafe uses for the instance field.
        /// </summary>
        /// <param name="field"></param>
        /// <returns></returns>
        internal static long GetObjectFieldOffset(RuntimeJavaField field)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return Impl.GetObjectFieldOffset(field);
#endif
        }

        /// <summary>
        /// Unparks the thread.
        /// </summary>
        /// <param name="thread"></param>
        internal static void Unpark(object thread)
        {
            Impl.unpark(null, thread);
        }

        public static object getReference(object self, object o, long offset) => Impl.getObject(self, o, offset);

        public static void putReference(object self, object o, long offset, object x) => Impl.putObject(self, o, offset, x);

        public static bool getBoolean(object self, object o, long offset) => Impl.getBoolean(self, o, offset);

        public static void putBoolean(object self, object o, long offset, bool x) => Impl.putBoolean(self, o, offset, x);

        public static byte getByte(object self, object o, long offset) => Impl.getByte(self, o, offset);

        public static void putByte(object self, object o, long offset, byte x) => Impl.putByte(self, o, offset, x);

        public static short getShort(object self, object o, long offset) => Impl.getShort(self, o, offset);

        public static void putShort(object self, object o, long offset, short x) => Impl.putShort(self, o, offset, x);

        public static char getChar(object self, object o, long offset) => Impl.getChar(self, o, offset);

        public static void putChar(object self, object o, long offset, char x) => Impl.putChar(self, o, offset, x);

        public static int getInt(object self, object o, long offset) => Impl.getInt(self, o, offset);

        public static void putInt(object self, object o, long offset, int x) => Impl.putInt(self, o, offset, x);

        public static long getLong(object self, object o, long offset) => Impl.getLong(self, o, offset);

        public static void putLong(object self, object o, long offset, long x) => Impl.putLong(self, o, offset, x);

        public static float getFloat(object self, object o, long offset) => Impl.getFloat(self, o, offset);

        public static void putFloat(object self, object o, long offset, float x) => Impl.putFloat(self, o, offset, x);

        public static double getDouble(object self, object o, long offset) => Impl.getDouble(self, o, offset);

        public static void putDouble(object self, object o, long offset, double x) => Impl.putDouble(self, o, offset, x);

        public static object getReferenceVolatile(object self, object o, long offset) => Impl.getObjectVolatile(self, o, offset);

        public static void putReferenceVolatile(object self, object o, long offset, object x) => Impl.putObjectVolatile(self, o, offset, x);

        public static bool getBooleanVolatile(object self, object o, long offset) => Impl.getBooleanVolatile(self, o, offset);

        public static void putBooleanVolatile(object self, object o, long offset, bool x) => Impl.putBooleanVolatile(self, o, offset, x);

        public static byte getByteVolatile(object self, object o, long offset) => Impl.getByteVolatile(self, o, offset);

        public static void putByteVolatile(object self, object o, long offset, byte x) => Impl.putByteVolatile(self, o, offset, x);

        public static short getShortVolatile(object self, object o, long offset) => Impl.getShortVolatile(self, o, offset);

        public static void putShortVolatile(object self, object o, long offset, short x) => Impl.putShortVolatile(self, o, offset, x);

        public static char getCharVolatile(object self, object o, long offset) => Impl.getCharVolatile(self, o, offset);

        public static void putCharVolatile(object self, object o, long offset, char x) => Impl.putCharVolatile(self, o, offset, x);

        public static int getIntVolatile(object self, object o, long offset) => Impl.getIntVolatile(self, o, offset);

        public static void putIntVolatile(object self, object o, long offset, int x) => Impl.putIntVolatile(self, o, offset, x);

        public static long getLongVolatile(object self, object o, long offset) => Impl.getLongVolatile(self, o, offset);

        public static void putLongVolatile(object self, object o, long offset, long x) => Impl.putLongVolatile(self, o, offset, x);

        public static float getFloatVolatile(object self, object o, long offset) => Impl.getFloatVolatile(self, o, offset);

        public static void putFloatVolatile(object self, object o, long offset, float x) => Impl.putFloatVolatile(self, o, offset, x);

        public static double getDoubleVolatile(object self, object o, long offset) => Impl.getDoubleVolatile(self, o, offset);

        public static void putDoubleVolatile(object self, object o, long offset, double x) => Impl.putDoubleVolatile(self, o, offset, x);

        public static object getUncompressedObject(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // there are no object pointers to read
            throw new global::java.lang.UnsupportedOperationException();
#endif
        }

        public static bool compareAndSetReference(object self, object o, long offset, object expected, object x) => Impl.compareAndSwapObject(self, o, offset, expected, x);

        public static bool compareAndSetInt(object self, object o, long offset, int expected, int x) => Impl.compareAndSwapInt(self, o, offset, expected, x);

        public static bool compareAndSetLong(object self, object o, long offset, long expected, long x) => Impl.compareAndSwapLong(self, o, offset, expected, x);

        // A compare-and-exchange returns the value it found. As a CAS that fails can only report failure, the value
        // is the one read before a CAS that succeeds or a read that no longer matches; either was the variable's value
        // at some point during the call, which makes the operation linearizable.

        public static object compareAndExchangeReference(object self, object o, long offset, object expected, object x)
        {
            while (true)
            {
                var witness = Impl.getObjectVolatile(self, o, offset);
                if (witness != expected)
                    return witness;
                if (Impl.compareAndSwapObject(self, o, offset, expected, x))
                    return expected;
            }
        }

        public static int compareAndExchangeInt(object self, object o, long offset, int expected, int x)
        {
            while (true)
            {
                var witness = Impl.getIntVolatile(self, o, offset);
                if (witness != expected)
                    return witness;
                if (Impl.compareAndSwapInt(self, o, offset, expected, x))
                    return expected;
            }
        }

        public static long compareAndExchangeLong(object self, object o, long offset, long expected, long x)
        {
            while (true)
            {
                var witness = Impl.getLongVolatile(self, o, offset);
                if (witness != expected)
                    return witness;
                if (Impl.compareAndSwapLong(self, o, offset, expected, x))
                    return expected;
            }
        }

        // JDK 25 implements the byte and short ones in Java with a CAS of the int around the value, which needs the
        // offset of a field to be its place in memory; map.xml makes them call these instead. A boolean field is
        // accessed as a byte, a char one as a short.

        public static byte compareAndExchangeByte(object self, object o, long offset, byte expected, byte x)
        {
            while (true)
            {
                var witness = Impl.getByteVolatile(self, o, offset);
                if (witness != expected)
                    return witness;
                if (CompareAndSetByte(o, offset, expected, x))
                    return expected;
            }
        }

        public static short compareAndExchangeShort(object self, object o, long offset, short expected, short x)
        {
            while (true)
            {
                var witness = Impl.getShortVolatile(self, o, offset);
                if (witness != expected)
                    return witness;
                if (CompareAndSetShort(o, offset, expected, x))
                    return expected;
            }
        }

        static unsafe bool CompareAndSetByte(object o, long offset, byte expected, byte x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => ByteCodeHelper.CompareAndSwap(ref *(byte*)offset, expected, x),
                    Array array => ByteCodeHelper.CompareAndSwap(ref ArrayRef<byte>(array, offset), expected, x),
                    _ => Impl.CompareAndSwapField(o, offset, expected, x),
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        static unsafe bool CompareAndSetShort(object o, long offset, short expected, short x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => ByteCodeHelper.CompareAndSwap(ref *(short*)offset, expected, x),
                    Array array => ByteCodeHelper.CompareAndSwap(ref ArrayRef<short>(array, offset), expected, x),
                    _ => Impl.CompareAndSwapField(o, offset, expected, x),
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Gets a reference to the value at the offset into an array's elements (where Unsafe's array offsets start).
        /// </summary>
        static ref T ArrayRef<T>(Array array, long offset)
        {
            return ref System.Runtime.CompilerServices.Unsafe.As<byte, T>(ref System.Runtime.CompilerServices.Unsafe.AddByteOffset(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(array), (nint)offset));
        }

        public static long allocateMemory0(object self, long bytes) => Impl.allocateMemory(self, bytes);

        public static long reallocateMemory0(object self, long address, long bytes) => Impl.reallocateMemory(self, address, bytes);

        public static void freeMemory0(object self, long address) => Impl.freeMemory(self, address);

        public static void setMemory0(object self, object o, long offset, long bytes, byte value) => Impl.setMemory(self, o, offset, bytes, value);

        public static void copyMemory0(object self, object srcBase, long srcOffset, object destBase, long destOffset, long bytes) => Impl.copyMemory(self, srcBase, srcOffset, destBase, destOffset, bytes);

        public static void copySwapMemory0(object self, object srcBase, long srcOffset, object destBase, long destOffset, long bytes, long elemSize)
        {
            // copies the elements one at a time through the byte accessors, reversing the bytes of each
            for (long i = 0; i < bytes; i += elemSize)
                for (long j = 0; j < elemSize; j++)
                    Impl.putByte(self, destBase, destOffset + i + j, Impl.getByte(self, srcBase, srcOffset + i + elemSize - 1 - j));
        }

        public static void writeback0(object self, long address)
        {
            // there is no persistent memory to write back to
        }

        public static void writebackPreSync0(object self)
        {

        }

        public static void writebackPostSync0(object self)
        {

        }

        public static long objectFieldOffset0(object self, global::java.lang.reflect.Field f) => Impl.objectFieldOffset(self, f);

        public static long objectFieldOffset1(object self, global::java.lang.Class c, string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            foreach (var fw in RuntimeJavaType.FromClass(c).GetFields())
                if (fw.Name == name && fw.IsStatic == false)
                    return Impl.GetObjectFieldOffset(fw);

            throw new global::java.lang.InternalError(name);
#endif
        }

        public static long staticFieldOffset0(object self, global::java.lang.reflect.Field f) => Impl.staticFieldOffset(self, f);

        public static object staticFieldBase0(object self, global::java.lang.reflect.Field f) => Impl.staticFieldBase(self, f);

        public static bool shouldBeInitialized0(object self, global::java.lang.Class c) => Impl.shouldBeInitialized(self, c);

        public static void ensureClassInitialized0(object self, global::java.lang.Class c) => Impl.ensureClassInitialized(self, c);

        public static int arrayBaseOffset0(object self, global::java.lang.Class arrayClass) => Impl.arrayBaseOffset(self, arrayClass);

        public static int arrayIndexScale0(object self, global::java.lang.Class arrayClass) => Impl.arrayIndexScale(self, arrayClass);

        public static global::java.lang.Class defineClass0(object self, string name, byte[] b, int off, int len, global::java.lang.ClassLoader loader, global::java.security.ProtectionDomain protectionDomain) => Impl.defineClass(self, name, b, off, len, loader, protectionDomain);

        public static object allocateInstance(object self, global::java.lang.Class cls) => Impl.allocateInstance(self, cls);

        public static void throwException(object self, Exception ee) => Impl.throwException(self, ee);

        public static void unpark(object self, object thread) => Impl.unpark(self, thread);

        public static void park(object self, bool isAbsolute, long time) => Impl.park(self, isAbsolute, time);

        public static int getLoadAverage0(object self, double[] loadavg, int nelems) => Impl.getLoadAverage(self, loadavg, nelems);

        public static void fullFence(object self) => Impl.fullFence(self);

    }

}
