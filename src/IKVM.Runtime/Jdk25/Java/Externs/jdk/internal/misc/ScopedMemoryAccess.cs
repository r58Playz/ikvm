namespace IKVM.Java.Externs.jdk.@internal.misc
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.misc.ScopedMemoryAccess'.
    /// </summary>
    static class ScopedMemoryAccess
    {

        public static void registerNatives()
        {

        }

        public static void closeScope0(object self, object session, object error)
        {
            // HotSpot uses a handshake to find threads accessing the closed session; the runtime does not check
        }

    }

}
