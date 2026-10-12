using System;
using System.Diagnostics;

namespace IKVM.Java.Externs.com.sun.management.@internal
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.OperatingSystemImpl' (Unix) from what .NET
    /// knows of the machine and the process. A load the runtime cannot measure is negative, as the JDK has it.
    /// </summary>
    static class OperatingSystemImpl
    {

        public static void initialize0()
        {

        }

        static GCMemoryInfo MemoryInfo => GC.GetGCMemoryInfo();

        public static long getCommittedVirtualMemorySize0(object self) => MemoryInfo.TotalCommittedBytes;

        public static long getFreeMemorySize0(object self) => Math.Max(0, MemoryInfo.TotalAvailableMemoryBytes - MemoryInfo.MemoryLoadBytes);

        public static long getTotalMemorySize0(object self) => MemoryInfo.TotalAvailableMemoryBytes;

        public static long getFreeSwapSpaceSize0(object self) => 0;

        public static long getTotalSwapSpaceSize0(object self) => 0;

        public static long getMaxFileDescriptorCount0(object self) => -1;

        public static long getOpenFileDescriptorCount0(object self) => -1;

        public static long getProcessCpuTime0(object self)
        {
            try
            {
                return (long)(Process.GetCurrentProcess().TotalProcessorTime.Ticks * 100);
            }
            catch (PlatformNotSupportedException)
            {
                return -1;
            }
        }

        public static double getProcessCpuLoad0(object self) => -1;

        public static double getCpuLoad0(object self) => -1;

        public static double getSingleCpuLoad0(object self, int cpuNum) => -1;

        public static int getHostConfiguredCpuCount0(object self) => Environment.ProcessorCount;

        public static int getHostOnlineCpuCount0(object self) => Environment.ProcessorCount;

        public static long getHostTotalCpuTicks0(object self) => -1;

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.GarbageCollectorExtImpl': the runtime sends
    /// no GC notifications.
    /// </summary>
    static class GarbageCollectorExtImpl
    {

        public static void setNotificationEnabled(object self, object gc, bool enabled)
        {

        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.GcInfoBuilder': the runtime has no
    /// collection details.
    /// </summary>
    static class GcInfoBuilder
    {

        public static int getNumGcExtAttributes(object self, object gc) => 0;

        public static void fillGcAttributeInfo(object self, object gc, int numAttributes, string[] attributeNames, char[] types, string[] descriptions)
        {

        }

        public static object getLastGcInfo0(object self, object gc, int numExtAtts, object[] extAttValues, char[] types, object[] before, object[] after) => null;

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.Flag': the runtime has no VM flags.
    /// </summary>
    static class Flag
    {

        public static string[] getAllFlagNames() => Array.Empty<string>();

        public static int getFlags(string[] names, object[] flags, int count) => 0;

        public static int getInternalFlagCount() => 0;

        public static void setLongValue(string name, long value) => throw new global::java.lang.IllegalArgumentException("VM option \"" + name + "\" does not exist");

        public static void setDoubleValue(string name, double value) => throw new global::java.lang.IllegalArgumentException("VM option \"" + name + "\" does not exist");

        public static void setBooleanValue(string name, bool value) => throw new global::java.lang.IllegalArgumentException("VM option \"" + name + "\" does not exist");

        public static void setStringValue(string name, string value) => throw new global::java.lang.IllegalArgumentException("VM option \"" + name + "\" does not exist");

        public static void initialize()
        {

        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.DiagnosticCommandImpl': the runtime has no
    /// diagnostic commands.
    /// </summary>
    static class DiagnosticCommandImpl
    {

        public static void setNotificationEnabled(object self, bool enabled)
        {

        }

        public static string[] getDiagnosticCommands(object self) => Array.Empty<string>();

        public static object[] getDiagnosticCommandInfo(object self, string[] commands) => new object[0];

        public static string executeDiagnosticCommand(object self, string command) => throw new global::java.lang.IllegalArgumentException("Unknown diagnostic command");

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'com.sun.management.internal.HotSpotDiagnostic': no heap dumps.
    /// </summary>
    static class HotSpotDiagnostic
    {

        public static void dumpHeap0(object self, string outputFile, bool live)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            throw new global::java.io.IOException("heap dumps are not supported");
#endif
        }

    }

}
