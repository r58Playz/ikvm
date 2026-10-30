using System;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods for 'java.lang.Shutdown'.
    /// </summary>
    static class Shutdown
    {

        /// <summary>
        /// Implements the native method 'beforeHalt'.
        /// </summary>
        public static void beforeHalt()
        {

        }

        /// <summary>
        /// Implements the native method 'halt0'.
        /// </summary>
        public static void halt0(int status)
        {
            if (OperatingSystem.IsBrowser())
            {
                // wasm fork: the page keeps the runtime alive, so an exit ends no process, and Environment.Exit on a
                // worker thread (every managed thread is one) unwinds that thread out of the runtime without detaching
                // it: the next collection waits for it to reach a safepoint, forever, and every other thread with it.
                // Tell the host (AppContext "IKVM.Halt", an Action<int>, if it set one), then stop this thread here,
                // asleep, as nothing more of a halted VM runs on it.
                (AppContext.GetData("IKVM.Halt") as Action<int>)?.Invoke(status);
                for (;;)
                {
                    try
                    {
                        global::System.Threading.Thread.Sleep(global::System.Threading.Timeout.Infinite);
                    }
                    catch (global::System.Threading.ThreadInterruptedException)
                    {
                    }
                }
            }

            Environment.Exit(status);
        }

        /// <summary>
        /// Implements the native method 'runAllFinalizers'.
        /// </summary>
        public static void runAllFinalizers()
        {

        }

    }

}
