namespace IKVM.Java.Externs.java.lang.@ref
{

    /// <summary>
    /// Implements the native methods of 'java.lang.ref.Finalizer'. The runtime runs finalize() from the .NET
    /// finalizer, so the class library's finalization (and its thread) is off.
    /// </summary>
    static class Finalizer
    {

        public static bool isFinalizationEnabled()
        {
            return false;
        }

        public static void reportComplete(object finalizee)
        {

        }

    }

}
