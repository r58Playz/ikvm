using System;
using System.Diagnostics;

namespace IKVM.Java.Externs.jdk.@internal.perf
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.perf.Perf'. Performance counters live in ordinary buffers, not
    /// shared memory another process can attach to.
    /// </summary>
    static class Perf
    {

        public static void registerNatives()
        {

        }

        public static object attach0(object self, int lvmid)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            throw new global::java.io.IOException("Attaching to the performance data of another VM is not supported");
#endif
        }

        public static void detach(object self, object bb)
        {

        }

        public static object createLong(object self, string name, int variability, int units, long value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var bb = global::java.nio.ByteBuffer.allocate(8).order(global::java.nio.ByteOrder.nativeOrder());
            bb.putLong(0, value);
            return bb;
#endif
        }

        public static object createByteArray(object self, string name, int variability, int units, byte[] value, int maxLength)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var bb = global::java.nio.ByteBuffer.allocate(maxLength);
            bb.put(value, 0, Math.Min(value.Length, maxLength));
            bb.rewind();
            return bb;
#endif
        }

        public static long highResCounter(object self)
        {
            return Stopwatch.GetTimestamp();
        }

        public static long highResFrequency(object self)
        {
            return Stopwatch.Frequency;
        }

    }

}
