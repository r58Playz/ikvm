/*
  Copyright (C) 2026 Puter Technologies Inc.

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.
*/
using System.Text;

namespace IKVM.Runtime.JNI
{

    /// <summary>
    /// wasm fork: native libraries libjvm's native bridge runs (jvm_native_bridge.h) -- an Android app's arm64-v8a
    /// libraries, run by a binary translator in the same memory -- as ART's libnativebridge does. Their handles and
    /// their methods carry <see cref="Tag"/>, which no static-registry handle and no function-table index has; a JNI
    /// stub calls a bridged method through <see cref="JNIFrame.InvokeBridged"/>, its arguments in 8-byte slots,
    /// instead of by calli.
    /// </summary>
    static class JNIBridge
    {

        public const uint Tag = 0x80000000;

        /// <summary>
        /// Whether there is a native bridge (the browser's libjvm, with an embedder that set one up). Stubs carry the
        /// bridged path only then: on 64-bit hosts a real handle can have bit 31 set.
        /// </summary>
        public static bool Enabled =>
#if FIRST_PASS || IMPORTER || EXPORTER
            false;
#else
            LibJvm.Instance.HasNativeBridge;
#endif

        public static bool IsBridged(nint p) => ((uint)p & Tag) != 0;

        /// <summary>
        /// A method descriptor's shorty, as Dalvik writes them: the return type, then each parameter; L for any
        /// reference, arrays included. "(I[FLjava/lang/String;)Z" is "ZILL".
        /// </summary>
        public static string Shorty(string sig)
        {
            var parameters = new StringBuilder();
            var ret = 'V';
            var output = parameters;
            for (int i = 1; i < sig.Length; i++)
            {
                var c = sig[i];
                if (c == ')')
                {
                    output = null;
                    continue;
                }
                if (c == '[')
                {
                    while (sig[i] == '[')
                        i++;
                    if (sig[i] == 'L')
                        i = sig.IndexOf(';', i);
                    c = 'L';
                }
                else if (c == 'L')
                {
                    i = sig.IndexOf(';', i);
                }
                if (output != null)
                    output.Append(c);
                else
                    ret = c;
            }
            return ret + parameters.ToString();
        }

    }

}
