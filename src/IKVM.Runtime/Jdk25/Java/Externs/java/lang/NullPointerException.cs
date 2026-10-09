namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of 'java.lang.NullPointerException'.
    /// </summary>
    static class NullPointerException
    {

        public static string getExtendedNPEMessage(object self)
        {
            // HotSpot describes the null from the bytecode that failed (JEP 358); the runtime does not
            return null;
        }

    }

}
