using System;
using System.Diagnostics;

namespace IKVM.Java.Externs.sun.management
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'sun.management.VMManagementImpl', which HotSpot answers through its
    /// management interface: what the runtime knows (the process, its start, the processors), and nothing for HotSpot's
    /// own statistics (safepoints, compilation, class sizes), which the optional supports say are absent.
    /// </summary>
    static class VMManagementImpl
    {

        static readonly long startupTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static readonly Stopwatch uptime = Stopwatch.StartNew();

        public static string getVersion0() => "4.0";

        public static void initOptionalSupportFields()
        {
            // nothing is supported, which the fields' defaults say
        }

        public static bool isThreadContentionMonitoringEnabled(object self) => false;

        public static bool isThreadCpuTimeEnabled(object self) => false;

        public static bool isThreadAllocatedMemoryEnabled(object self) => false;

        public static bool endAOTRecording(object self) => false;

        public static long getTotalClassCount(object self) => 0;

        public static long getUnloadedClassCount(object self) => 0;

        public static bool getVerboseClass(object self) => false;

        public static bool getVerboseGC(object self) => false;

        public static int getProcessId(object self) => Environment.ProcessId;

        public static string[] getVmArguments0(object self) => Array.Empty<string>();

        public static long getStartupTime(object self) => startupTime;

        public static long getUptime0(object self) => uptime.ElapsedMilliseconds;

        public static int getAvailableProcessors(object self) => Environment.ProcessorCount;

        public static long getTotalCompileTime(object self) => 0;

#if FIRST_PASS
        public static long getTotalThreadCount(object self) => throw new NotImplementedException();

        public static int getLiveThreadCount(object self) => throw new NotImplementedException();

        public static int getPeakThreadCount(object self) => throw new NotImplementedException();

        public static int getDaemonThreadCount(object self) => throw new NotImplementedException();
#else
        public static long getTotalThreadCount(object self) => IKVM.Java.Externs.java.lang.Thread.StartedThreadCount;

        public static int getLiveThreadCount(object self) => IKVM.Java.Externs.java.lang.Thread.LiveThreadCount;

        public static int getPeakThreadCount(object self) => IKVM.Java.Externs.java.lang.Thread.PeakThreadCount;

        public static int getDaemonThreadCount(object self) => IKVM.Java.Externs.java.lang.Thread.DaemonThreadCount;
#endif

        public static long getSafepointCount(object self) => 0;

        public static long getTotalSafepointTime(object self) => 0;

        public static long getSafepointSyncTime(object self) => 0;

        public static long getTotalApplicationNonStoppedTime(object self) => 0;

        public static long getLoadedClassSize(object self) => 0;

        public static long getUnloadedClassSize(object self) => 0;

        public static long getClassLoadingTime(object self) => 0;

        public static long getMethodDataSize(object self) => 0;

        public static long getInitializedClassCount(object self) => 0;

        public static long getClassInitializationTime(object self) => 0;

        public static long getClassVerificationTime(object self) => 0;

    }

}
