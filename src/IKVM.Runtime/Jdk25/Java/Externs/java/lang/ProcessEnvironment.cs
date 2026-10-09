using System;
using System.Collections;
using System.Text;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.ProcessEnvironment' (Unix).
    /// </summary>
    static class ProcessEnvironment
    {

        /// <summary>
        /// Returns the environment variables as names and values in turn, encoded as sun.jnu.encoding (UTF-8).
        /// </summary>
        /// <returns></returns>
        public static byte[][] environ()
        {
            var env = Environment.GetEnvironmentVariables();
            var result = new byte[env.Count * 2][];
            var i = 0;
            foreach (DictionaryEntry e in env)
            {
                result[i++] = Encoding.UTF8.GetBytes((string)e.Key);
                result[i++] = Encoding.UTF8.GetBytes((string)e.Value ?? "");
            }

            return result;
        }

    }

}
