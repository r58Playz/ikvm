/*
  Copyright (C) 2026 Jeroen Frijters, Jerome Haltom

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
using System;
using System.Collections.Generic;
using System.Reflection;

using IKVM.Runtime.Accessors.Sun.Nio.Fs;

namespace IKVM.Runtime
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Rewrites the errno carried by a 'sun.nio.fs.UnixException' that native code threw, from the host libc's
    /// numbering into the numbering the JDK image was compiled against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two only disagree because one image serves two hosts. src/IKVM.Java.runtime.linux/UnixConstants.java
    /// carries emscripten's WASI errno values (ENOENT=44, EACCES=2, EEXIST=20), which is what emscripten's libc
    /// reports under wasm; a natively built libnio reports glibc's (2, 13, 17). Nothing reconciles them, so on a
    /// native Linux build every errno the JDK compares against a UnixConstants field lands on the wrong branch:
    /// ENOENT reads as EACCES, so a missing file surfaces as AccessDeniedException instead of NoSuchFileException,
    /// and EEXIST matches nothing at all and degrades to a bare FileSystemException. Files.createDirectories()
    /// depends on both of those being right and fails whether or not the directory already exists, which is where
    /// this shows up first (Fabric's mod cache, MinecraftGameProvider).
    /// </para>
    /// <para>
    /// The real fix is per-target constants, which needs the JDK image rebuilt and split; this keeps the single
    /// image working on both by translating at the one point where a natively thrown exception enters managed
    /// code. It engages only when the image's values actually differ from the host's, so a corrected image
    /// disables it automatically, and it is off under wasm, where the image is already correct.
    /// </para>
    /// </remarks>
    static class UnixErrnoTranslation
    {

        /// <summary>
        /// Linux errno values, i.e. what a natively built libnio reports. macOS is deliberately absent: the osx
        /// image carries the matching values already, so there is nothing to translate there.
        /// </summary>
        static readonly (string Name, int Value)[] HostErrno = new[]
        {
            ("ENOENT", 2),
            ("ENXIO", 6),
            ("EAGAIN", 11),
            ("EACCES", 13),
            ("EEXIST", 17),
            ("EXDEV", 18),
            ("ENOTDIR", 20),
            ("EISDIR", 21),
            ("EINVAL", 22),
            ("EMFILE", 24),
            ("ENOSPC", 28),
            ("EROFS", 30),
            ("ERANGE", 34),
            ("ENOSYS", 38),
            ("ENOTEMPTY", 39),
            ("ELOOP", 40),
            ("ENODATA", 61),
        };

        static UnixExceptionAccessor unixExceptionAccessor;
        static UnixConstantsAccessor unixConstantsAccessor;

        static UnixExceptionAccessor UnixExceptionAccessor => JVM.Internal.BaseAccessors.Get(ref unixExceptionAccessor);
        static UnixConstantsAccessor UnixConstantsAccessor => JVM.Internal.BaseAccessors.Get(ref unixConstantsAccessor);

        static Dictionary<int, int> map;
        static Dictionary<int, int> inverse;
        static bool resolved;

        /// <summary>
        /// Gets the host libc's errno of an errno in the numbering of the JDK image.
        /// </summary>
        /// <param name="errno"></param>
        /// <returns></returns>
        internal static int ToHost(int errno)
        {
            try
            {
                if (Resolve() != null && inverse.TryGetValue(errno, out var host))
                    return host;
            }
            catch
            {
                // the image's numbering, then
            }

            return errno;
        }

        /// <summary>
        /// Translates the errno of <paramref name="e"/> in place, if it is a 'sun.nio.fs.UnixException' thrown by
        /// native code on a host whose errno numbering the JDK image does not share.
        /// </summary>
        /// <param name="e"></param>
        internal static void Apply(Exception e)
        {
            // the type test comes first so nothing here touches the JDK image on the ordinary throw path, which
            // matters during startup, when these accessors may not be resolvable yet
            if (e == null || e.GetType().FullName != "sun.nio.fs.UnixException")
                return;

            try
            {
                var m = Resolve();
                if (m == null)
                    return;

                if (m.TryGetValue(UnixExceptionAccessor.InvokeErrno(e), out var translated))
                    UnixExceptionAccessor.InvokeSetError(e, translated);
            }
            catch
            {
                // a failure to translate must not replace the exception the caller is trying to report
            }
        }

        /// <summary>
        /// Builds the host-to-image errno map, or returns null when the two numberings agree and no translation
        /// is wanted.
        /// </summary>
        /// <returns></returns>
        static Dictionary<int, int> Resolve()
        {
            if (resolved)
                return map;

            // only the linux image is built against foreign errno values, and only when running natively: under
            // wasm the image and emscripten's libc agree, and IsLinux is false there anyway
            if (RuntimeUtil.IsLinux == false)
            {
                resolved = true;
                return null;
            }

            var type = UnixConstantsAccessor.Type;
            if (type == null)
            {
                resolved = true;
                return null;
            }

            var built = new Dictionary<int, int>();
            var builtInverse = new Dictionary<int, int>();
            foreach (var (name, host) in HostErrno)
            {
                var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    continue;

                var image = Convert.ToInt32(field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null));
                if (image != host && built.ContainsKey(host) == false)
                    built.Add(host, image);
                if (image != host && builtInverse.ContainsKey(image) == false)
                    builtInverse.Add(image, host);
            }

            inverse = builtInverse;
            map = built.Count > 0 ? built : null;
            resolved = true;
            return map;
        }

    }

#endif

}
