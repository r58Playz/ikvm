using System;
using System.IO;

using IKVM.Runtime;

namespace IKVM.Java.Externs.sun.nio.ch
{

    /// <summary>
    /// Implements the native methods of 'sun.nio.ch.VfsFileChannel' (IKVM's): the file channel of a file of the virtual
    /// file system, whose file descriptor holds the .NET stream of the file.
    /// </summary>
    static class VfsFileChannel
    {

#if FIRST_PASS == false

        static Stream GetStream(object fd) => global::IKVM.Java.Externs.java.io.FileDescriptor.GetVfsStream(fd) ?? throw new global::java.io.IOException("Stream Closed");

#endif

        public static bool isVfs(object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return global::IKVM.Java.Externs.java.io.FileDescriptor.GetVfsStream(fd) != null;
#endif
        }

        public static int read0(object fd, byte[] b, int off, int len, long position)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(fd);
            return position < 0 ? global::IKVM.Java.Externs.java.io.FileDescriptor.ReadBytes(stream, b, off, len) : global::IKVM.Java.Externs.java.io.FileDescriptor.ReadBytes(stream, b, off, len, position);
#endif
        }

        public static long position0(object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return global::IKVM.Java.Externs.java.io.FileDescriptor.GetPosition(GetStream(fd));
#endif
        }

        public static void seek0(object fd, long position)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            global::IKVM.Java.Externs.java.io.FileDescriptor.SetPosition(GetStream(fd), position);
#endif
        }

        public static long size0(object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return global::IKVM.Java.Externs.java.io.FileDescriptor.GetLength(GetStream(fd));
#endif
        }

        public static string key0(object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return global::IKVM.Java.Externs.java.io.FileDescriptor.GetVfsPath(GetStream(fd));
#endif
        }

    }

}
