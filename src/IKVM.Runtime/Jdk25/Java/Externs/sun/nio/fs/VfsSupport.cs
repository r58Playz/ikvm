using System;
using System.IO;
using System.Text;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.sun.nio.fs
{

    /// <summary>
    /// Implements the native methods of 'sun.nio.fs.VfsSupport' (IKVM's), which the Unix file system provider asks
    /// about a path before opening a channel or a directory stream of it, as libnio cannot open a path of the virtual
    /// file system.
    /// </summary>
    static class VfsSupport
    {

#if FIRST_PASS == false

        // the kinds of path kind0 tells apart
        const int KindHost = -1;
        const int KindMissing = 0;
        const int KindFile = 1;
        const int KindDirectory = 2;

        static VfsPath Resolve(byte[] path) => JVM.Vfs.Resolve(Encoding.UTF8.GetString(path));

#endif

        public static int kind0(byte[] path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(path);
            if (p.IsVfs == false)
                return KindHost;

            return JVM.Vfs.GetEntry(p) switch
            {
                VfsDirectory => KindDirectory,
                VfsFile => KindFile,
                _ => KindMissing,
            };
#endif
        }

        public static string[] list0(byte[] path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(path);
            return p.IsVfs ? JVM.Vfs.List(p) : null;
#endif
        }

        public static string mount0(byte[] path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(path);
            return p.IsVfs ? JVM.Vfs.Resolve(p.Mount.Path).Path : null;
#endif
        }

        public static int open0(object fd, byte[] path, int flags)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // -1 for a path of the host, else 0 or the errno of the failure
            var p = Resolve(path);
            if (p.IsVfs == false)
                return -1;

            var c = UnixNativeDispatcher.UnixConstantsAccessor;
            var status = JVM.Vfs.CheckOpen(p, (flags & (c.O_WRONLY | c.O_RDWR)) != 0, (flags & c.O_CREAT) != 0, (flags & c.O_EXCL) != 0, (flags & c.O_TRUNC) != 0);

            // a channel of a directory, whose read(2) is EISDIR, is not supported
            if (status == VfsStatus.Ok && JVM.Vfs.GetEntry(p) is VfsDirectory)
                status = VfsStatus.IsDirectory;
            if (status != VfsStatus.Ok)
                return UnixNativeDispatcher.ToErrno(status);

            try
            {
                global::IKVM.Java.Externs.java.io.FileDescriptor.SetVfsStream(fd, p, JVM.Vfs.Open(p, FileMode.Open, FileAccess.Read));
                return 0;
            }
            catch (FileNotFoundException)
            {
                return UnixNativeDispatcher.ToErrno(VfsStatus.NotFound);
            }
#endif
        }

    }

}
