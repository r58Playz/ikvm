using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using IKVM.Runtime;

namespace IKVM.Java.Externs.jdk.@internal.misc
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.misc.VM'.
    /// </summary>
    static class VM
    {

        public static void initialize()
        {

        }

        public static global::java.lang.ClassLoader latestUserDefinedLoader0()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the class loader of the first method up the stack that has one, not counting the core class library
            var trace = new StackTrace(2, false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var m = trace.GetFrame(i).GetMethod();
                if (m == null || StackWalkUtil.IsHideFromStackWalk(m))
                    continue;

                var type = m.DeclaringType;
                if (type == null)
                    continue;

                var tw = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type);
                if (tw?.ClassLoader.GetJavaClassLoader() is global::java.lang.ClassLoader loader)
                    return loader;
            }

            return null;
#endif
        }

        [DllImport("libc", EntryPoint = "getuid")]
        static extern uint libc_getuid();

        [DllImport("libc", EntryPoint = "geteuid")]
        static extern uint libc_geteuid();

        [DllImport("libc", EntryPoint = "getgid")]
        static extern uint libc_getgid();

        [DllImport("libc", EntryPoint = "getegid")]
        static extern uint libc_getegid();

        static long Id(Func<uint> f)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return -1;

            try
            {
                return f();
            }
            catch (Exception)
            {
                // no libc, as in the browser
                return 0;
            }
        }

        public static long getuid() => Id(libc_getuid);

        public static long geteuid() => Id(libc_geteuid);

        public static long getgid() => Id(libc_getgid);

        public static long getegid() => Id(libc_getegid);

        /// <summary>
        /// Implements the native method 'getNanoTimeAdjustment': the nanoseconds of the current UTC time since the
        /// given second of the epoch, or -1 if that is too far off, as HotSpot's JVM_GetNanoTimeAdjustment.
        /// </summary>
        public static long getNanoTimeAdjustment(long offsetInSeconds)
        {
            const long MAX_DIFF_SECS = 0x0100000000L;
            const long MIN_DIFF_SECS = -MAX_DIFF_SECS;

            // DateTime ticks are 100 ns
            var ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
            var seconds = ticks / TimeSpan.TicksPerSecond;
            var nanos = (ticks % TimeSpan.TicksPerSecond) * 100;

            var diff = seconds - offsetInSeconds;
            if (diff >= MAX_DIFF_SECS || diff <= MIN_DIFF_SECS)
                return -1;

            return diff * 1000000000L + nanos;
        }

        public static string[] getRuntimeArguments()
        {
            return Array.Empty<string>();
        }

    }

}
