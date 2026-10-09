namespace IKVM.Java.Externs.jdk.@internal.misc
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.misc.Signal'. The runtime does not handle signals: a handler can
    /// be installed but is never invoked.
    /// </summary>
    static class Signal
    {

        public static int findSignal0(string sigName)
        {
            return sigName switch
            {
                "HUP" => 1,
                "INT" => 2,
                "QUIT" => 3,
                "KILL" => 9,
                "USR1" => 10,
                "USR2" => 12,
                "PIPE" => 13,
                "ALRM" => 14,
                "TERM" => 15,
                _ => -1,
            };
        }

        public static long handle0(int sig, long nativeH)
        {
            // the previous handler: the default one
            return 0;
        }

        public static void raise0(int sig)
        {

        }

    }

}
