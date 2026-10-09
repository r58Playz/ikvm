using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.java.io
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.UnixFileSystem' with .NET's file system, and IKVM's virtual
    /// file system for the paths it mounts (which are read-only).
    /// </summary>
    static class UnixFileSystem
    {

        const int BA_EXISTS = 0x01;
        const int BA_REGULAR = 0x02;
        const int BA_DIRECTORY = 0x04;

        const int ACCESS_READ = 0x04;
        const int ACCESS_WRITE = 0x02;
        const int ACCESS_EXECUTE = 0x01;

        const int SPACE_TOTAL = 0;
        const int SPACE_FREE = 1;
        const int SPACE_USABLE = 2;

        // symbolic links followed before canonicalization gives up, as Linux's MAXSYMLINKS
        const int MaxSymbolicLinks = 40;

        [DllImport("libc", EntryPoint = "access", SetLastError = true)]
        static extern int libc_access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

#if FIRST_PASS == false

        static string GetPath(object f) => ((global::java.io.File)f).getPath();

        static DateTime UnixEpoch => DateTime.UnixEpoch;

        /// <summary>
        /// Canonicalizes a path of the virtual file system, which has no links.
        /// </summary>
        static string CanonicalizeVfs(string path)
        {
            return Path.GetFullPath(path);
        }

        /// <summary>
        /// Canonicalizes an absolute path as realpath(3) would, also for the part of it that does not exist: names are
        /// resolved from the root, following symbolic links, so that '..' leaves the directory a link points to.
        /// </summary>
        static string Canonicalize(string path, int links)
        {
            var result = "/";
            foreach (var name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == ".")
                    continue;

                if (name == "..")
                {
                    result = Path.GetDirectoryName(result) ?? "/";
                    continue;
                }

                var next = result == "/" ? "/" + name : result + "/" + name;

                try
                {
                    if (links < MaxSymbolicLinks && new FileInfo(next).LinkTarget is string target)
                        next = Canonicalize(Path.IsPathRooted(target) ? target : result + "/" + target, links + 1);
                }
                catch (IOException)
                {

                }
                catch (UnauthorizedAccessException)
                {

                }

                result = next;
            }

            return result;
        }

        /// <summary>
        /// Returns whether the current user may access the file as access(2) says, or else as its permissions say.
        /// </summary>
        static bool CheckAccess(string path, int access)
        {
            try
            {
                // ACCESS_READ, ACCESS_WRITE and ACCESS_EXECUTE are R_OK, W_OK and X_OK
                return libc_access(path, access) == 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // no libc, as in the browser
            }

            if (File.Exists(path) == false && Directory.Exists(path) == false)
                return false;

            var mode = File.GetUnixFileMode(path);
            return access switch
            {
                ACCESS_READ => (mode & (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0,
                ACCESS_WRITE => (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0,
                ACCESS_EXECUTE => (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0,
                _ => true,
            };
        }

        /// <summary>
        /// Gets the permission bits of a kind of access, for the owner alone or for everyone.
        /// </summary>
        static UnixFileMode GetPermissionBits(int access, bool owneronly)
        {
            return access switch
            {
                ACCESS_READ => owneronly ? UnixFileMode.UserRead : UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ACCESS_WRITE => owneronly ? UnixFileMode.UserWrite : UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite,
                ACCESS_EXECUTE => owneronly ? UnixFileMode.UserExecute : UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
                _ => UnixFileMode.None,
            };
        }

#endif

        public static void initIDs()
        {

        }

        public static string canonicalize0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.Vfs.IsPath(path))
                return CanonicalizeVfs(path);

            return Canonicalize(path, 0);
#endif
        }

        public static int getBooleanAttributes0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return JVM.Vfs.GetBooleanAttributes(path);

            try
            {
                if (Directory.Exists(path))
                    return BA_EXISTS | BA_DIRECTORY;
                if (File.Exists(path))
                    return BA_EXISTS | BA_REGULAR;
            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }

            return 0;
#endif
        }

        public static bool checkAccess0(object self, object f, int access)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
            {
                return JVM.Vfs.GetEntry(path) switch
                {
                    VfsDirectory => access != ACCESS_WRITE,
                    VfsFile => access == ACCESS_READ,
                    _ => false,
                };
            }

            try
            {
                return CheckAccess(path, access);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
#endif
        }

        public static long getLastModifiedTime0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return 0;

            try
            {
                if (Directory.Exists(path))
                    return (long)(Directory.GetLastWriteTimeUtc(path) - UnixEpoch).TotalMilliseconds;
                if (File.Exists(path))
                    return (long)(File.GetLastWriteTimeUtc(path) - UnixEpoch).TotalMilliseconds;
            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }

            return 0;
#endif
        }

        public static long getLength0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return JVM.Vfs.GetLength(path);

            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
#endif
        }

        public static bool setPermission0(object self, object f, int access, bool enable, bool owneronly)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                var mode = File.GetUnixFileMode(path);
                var bits = GetPermissionBits(access, owneronly);
                File.SetUnixFileMode(path, enable ? mode | bits : mode & ~bits);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
#endif
        }

        public static bool createFileExclusively0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    return true;
            }
            catch (DirectoryNotFoundException)
            {
                throw new global::java.io.IOException("No such file or directory");
            }
            catch (UnauthorizedAccessException)
            {
                throw new global::java.io.IOException("Permission denied");
            }
            catch (IOException e)
            {
                // it exists already
                if (File.Exists(path) || Directory.Exists(path))
                    return false;

                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static bool delete0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                // a link is deleted, not what it points to; a directory only if it is empty
                if (new FileInfo(path).LinkTarget == null && Directory.Exists(path))
                {
                    Directory.Delete(path, false);
                    return true;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }

            return false;
#endif
        }

        public static string[] list0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return JVM.Vfs.List(path);

            try
            {
                return Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).ToArray();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
#endif
        }

        public static bool createDirectory0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                // as mkdir(2): neither when it exists nor when its parent does not (Directory.CreateDirectory makes both)
                if (File.Exists(path) || Directory.Exists(path))
                    return false;

                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (parent != null && Directory.Exists(parent) == false)
                    return false;

                Directory.CreateDirectory(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
#endif
        }

        public static bool rename0(object self, object f1, object f2)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var from = GetPath(f1);
            var to = GetPath(f2);
            if (JVM.Vfs.IsPath(from) || JVM.Vfs.IsPath(to))
                return false;

            try
            {
                // as rename(2), a file replaces the file it is renamed to
                if (new FileInfo(from).LinkTarget == null && Directory.Exists(from))
                    Directory.Move(from, to);
                else
                    File.Move(from, to, true);

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
#endif
        }

        public static bool setLastModifiedTime0(object self, object f, long time)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                var value = UnixEpoch.AddMilliseconds(time);
                if (Directory.Exists(path))
                    Directory.SetLastWriteTimeUtc(path, value);
                else if (File.Exists(path))
                    File.SetLastWriteTimeUtc(path, value);
                else
                    return false;

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
#endif
        }

        public static bool setReadOnly0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return false;

            try
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
#endif
        }

        public static long getSpace0(object self, object f, int t)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = GetPath(f);
            if (JVM.Vfs.IsPath(path))
                return 0;

            try
            {
                // on Unix DriveInfo takes any path, and reports the file system it is on
                var drive = new DriveInfo(path);
                return t switch
                {
                    SPACE_TOTAL => drive.TotalSize,
                    SPACE_FREE => drive.TotalFreeSpace,
                    SPACE_USABLE => drive.AvailableFreeSpace,
                    _ => 0,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                return 0;
            }
#endif
        }

        public static long getNameMax0(object self, string path)
        {
            // NAME_MAX of the file systems .NET runs on
            return 255;
        }

    }

}
