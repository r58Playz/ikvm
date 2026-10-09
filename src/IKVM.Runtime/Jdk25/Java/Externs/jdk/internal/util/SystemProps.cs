using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

using IKVM.Runtime;

namespace IKVM.Java.Externs.jdk.@internal.util
{

    static class SystemProps
    {

        /// <summary>
        /// Implements the native methods of 'jdk.internal.util.SystemProps$Raw', from which System.initPhase1 makes the
        /// system properties: those of the VM (and the command line), and those of the platform, which HotSpot's
        /// libjava finds with the C library.
        /// </summary>
        public static class Raw
        {

            // the indices of the platform properties (SystemProps.Raw)
            const int _display_country_NDX = 0;
            const int _display_language_NDX = 1 + _display_country_NDX;
            const int _display_script_NDX = 1 + _display_language_NDX;
            const int _display_variant_NDX = 1 + _display_script_NDX;
            const int _file_separator_NDX = 1 + _display_variant_NDX;
            const int _format_country_NDX = 1 + _file_separator_NDX;
            const int _format_language_NDX = 1 + _format_country_NDX;
            const int _format_script_NDX = 1 + _format_language_NDX;
            const int _format_variant_NDX = 1 + _format_script_NDX;
            const int _ftp_nonProxyHosts_NDX = 1 + _format_variant_NDX;
            const int _ftp_proxyHost_NDX = 1 + _ftp_nonProxyHosts_NDX;
            const int _ftp_proxyPort_NDX = 1 + _ftp_proxyHost_NDX;
            const int _http_nonProxyHosts_NDX = 1 + _ftp_proxyPort_NDX;
            const int _http_proxyHost_NDX = 1 + _http_nonProxyHosts_NDX;
            const int _http_proxyPort_NDX = 1 + _http_proxyHost_NDX;
            const int _https_proxyHost_NDX = 1 + _http_proxyPort_NDX;
            const int _https_proxyPort_NDX = 1 + _https_proxyHost_NDX;
            const int _java_io_tmpdir_NDX = 1 + _https_proxyPort_NDX;
            const int _line_separator_NDX = 1 + _java_io_tmpdir_NDX;
            const int _native_encoding_NDX = 1 + _line_separator_NDX;
            const int _os_arch_NDX = 1 + _native_encoding_NDX;
            const int _os_name_NDX = 1 + _os_arch_NDX;
            const int _os_version_NDX = 1 + _os_name_NDX;
            const int _path_separator_NDX = 1 + _os_version_NDX;
            const int _socksNonProxyHosts_NDX = 1 + _path_separator_NDX;
            const int _socksProxyHost_NDX = 1 + _socksNonProxyHosts_NDX;
            const int _socksProxyPort_NDX = 1 + _socksProxyHost_NDX;
            const int _stderr_encoding_NDX = 1 + _socksProxyPort_NDX;
            const int _stdin_encoding_NDX = 1 + _stderr_encoding_NDX;
            const int _stdout_encoding_NDX = 1 + _stdin_encoding_NDX;
            const int _sun_arch_abi_NDX = 1 + _stdout_encoding_NDX;
            const int _sun_arch_data_model_NDX = 1 + _sun_arch_abi_NDX;
            const int _sun_cpu_endian_NDX = 1 + _sun_arch_data_model_NDX;
            const int _sun_cpu_isalist_NDX = 1 + _sun_cpu_endian_NDX;
            const int _sun_io_unicode_encoding_NDX = 1 + _sun_cpu_isalist_NDX;
            const int _sun_jnu_encoding_NDX = 1 + _sun_io_unicode_encoding_NDX;
            const int _sun_os_patch_level_NDX = 1 + _sun_jnu_encoding_NDX;
            const int _user_dir_NDX = 1 + _sun_os_patch_level_NDX;
            const int _user_home_NDX = 1 + _user_dir_NDX;
            const int _user_name_NDX = 1 + _user_home_NDX;
            const int FIXED_LENGTH = 1 + _user_name_NDX;

            /// <summary>
            /// Returns the VM and command line properties, as alternating keys and values.
            /// </summary>
            /// <returns></returns>
            public static string[] vmProperties()
            {
#if FIRST_PASS
                throw new NotImplementedException();
#else
                var list = new List<string>();
                foreach (var kvp in JVM.Properties.Init)
                {
                    list.Add(kvp.Key);
                    list.Add(kvp.Value);
                }

                return list.ToArray();
#endif
            }

            /// <summary>
            /// Returns the platform properties, at the indices of SystemProps.Raw. A property may be null.
            /// </summary>
            /// <returns></returns>
            public static string[] platformProperties()
            {
                var p = new string[FIXED_LENGTH];

                // the locale's language and country, for display and formatting alike
                var culture = CultureInfo.CurrentCulture;
                if (culture.Equals(CultureInfo.InvariantCulture) == false)
                {
                    var parts = culture.Name.Split('-');
                    p[_display_language_NDX] = p[_format_language_NDX] = parts[0];
                    if (parts.Length > 1 && parts[parts.Length - 1].Length == 2)
                        p[_display_country_NDX] = p[_format_country_NDX] = parts[parts.Length - 1].ToUpperInvariant();
                }
                else
                {
                    p[_display_language_NDX] = p[_format_language_NDX] = "en";
                }

                p[_file_separator_NDX] = Path.DirectorySeparatorChar.ToString();
                p[_path_separator_NDX] = Path.PathSeparator.ToString();
                p[_line_separator_NDX] = Environment.NewLine;
                p[_java_io_tmpdir_NDX] = GetTempDir();

                // .NET strings are UTF-16, and the runtime treats native strings as UTF-8
                p[_native_encoding_NDX] = "UTF-8";
                p[_sun_jnu_encoding_NDX] = "UTF-8";
                p[_sun_io_unicode_encoding_NDX] = BitConverter.IsLittleEndian ? "UnicodeLittle" : "UnicodeBig";

                p[_os_name_NDX] = GetOSName();
                p[_os_version_NDX] = Environment.OSVersion.Version.ToString();
                p[_os_arch_NDX] = GetOSArch();
                p[_sun_arch_data_model_NDX] = IntPtr.Size == 8 ? "64" : "32";
                p[_sun_cpu_endian_NDX] = BitConverter.IsLittleEndian ? "little" : "big";
                p[_sun_os_patch_level_NDX] = "unknown";

                p[_user_dir_NDX] = Environment.CurrentDirectory;
                p[_user_home_NDX] = GetUserHome();
                p[_user_name_NDX] = GetUserName();

                return p;
            }

            static string GetTempDir()
            {
                var tmp = Path.GetTempPath();
                if (tmp.Length > 1 && (tmp[tmp.Length - 1] == Path.DirectorySeparatorChar || tmp[tmp.Length - 1] == Path.AltDirectorySeparatorChar))
                    tmp = tmp.Substring(0, tmp.Length - 1);

                return tmp;
            }

            static string GetOSName()
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return Environment.OSVersion.Version.Major >= 10 ? "Windows 10" : "Windows";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "Mac OS X";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
                    return "FreeBSD";

                // Linux, and the browser, where the class library is the Linux one
                return "Linux";
            }

            static string GetOSArch()
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "amd64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "aarch64",
                    Architecture.Arm => "arm",
                    Architecture.Wasm => "wasm32",
                    _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                };
            }

            static string GetUserHome()
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                    home = Environment.GetEnvironmentVariable("HOME");

                return string.IsNullOrEmpty(home) ? "?" : home;
            }

            static string GetUserName()
            {
                try
                {
                    var name = Environment.UserName;
                    return string.IsNullOrEmpty(name) ? "?" : name;
                }
                catch (Exception)
                {
                    return "?";
                }
            }

        }

    }

}
