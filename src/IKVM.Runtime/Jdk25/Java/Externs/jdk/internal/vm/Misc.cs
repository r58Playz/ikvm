using System;
using System.IO;

namespace IKVM.Java.Externs.jdk.@internal.vm
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.vm.ContinuationSupport': the runtime has no continuations, so
    /// a virtual thread is bound to a thread of its own.
    /// </summary>
    static class ContinuationSupport
    {

        public static bool isSupported0()
        {
            return false;
        }

    }

    /// <summary>
    /// Implements the native methods of 'jdk.internal.vm.VMSupport'.
    /// </summary>
    static class VMSupport
    {

        public static object initAgentProperties(object props)
        {
            return props;
        }

        public static string getVMTemporaryDirectory()
        {
            var tmp = Path.GetTempPath();
            return tmp.Length > 1 ? tmp.TrimEnd(Path.DirectorySeparatorChar) : tmp;
        }

    }

}
