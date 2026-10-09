using System;
using System.IO;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Io;
using IKVM.Runtime.JNI;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.java.io
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileDescriptor'. A file of IKVM's virtual file system is a
    /// .NET stream (map.xml adds the 'stream' field), and its file descriptor's number stays -1, so that no cleaner
    /// closes a file of the file system with it. Any other file is the file system's, and libjava's natives do the rest.
    /// </summary>
    static class FileDescriptor
    {

#if FIRST_PASS == false

        static FileDescriptorAccessor fileDescriptorAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint initIDsPtr, getHandlePtr, getAppendPtr, close0Ptr, sync0Ptr;

        static FileDescriptorAccessor FileDescriptorAccessor => JVM.Internal.BaseAccessors.Get(ref fileDescriptorAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::java.io.FileDescriptor).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::java.io.FileDescriptor).TypeHandle);

        const string Name = "java/io/FileDescriptor";

        /// <summary>
        /// Gets the stream of a file descriptor of a file of the virtual file system, or null.
        /// </summary>
        /// <param name="fd"></param>
        /// <returns></returns>
        internal static Stream GetVfsStream(object fd)
        {
            return fd != null ? FileDescriptorAccessor.GetStream(fd) : null;
        }

        /// <summary>
        /// Opens a file of the virtual file system for a file descriptor; the virtual file system is read-only.
        /// </summary>
        /// <param name="fd"></param>
        /// <param name="path"></param>
        /// <param name="write"></param>
        internal static void OpenVfs(object fd, string path, bool write)
        {
            if (JVM.Vfs.GetEntry(path) is VfsDirectory)
                throw new global::java.io.FileNotFoundException(path + " (Is a directory)");
            if (write)
                throw new global::java.io.FileNotFoundException(path + " (Read-only file system)");

            try
            {
                FileDescriptorAccessor.SetStream(fd, JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read));
            }
            catch (FileNotFoundException)
            {
                throw new global::java.io.FileNotFoundException(path + " (No such file or directory)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new global::java.io.FileNotFoundException(path + " (" + e.Message + ")");
            }
        }

        /// <summary>
        /// Reads from a stream of the virtual file system as the JDK's natives read a file.
        /// </summary>
        internal static int ReadBytes(Stream stream, byte[] b, int off, int len)
        {
            if (b == null)
                throw new global::java.lang.NullPointerException();
            if (off < 0 || len < 0 || b.Length - off < len)
                throw new global::java.lang.IndexOutOfBoundsException();
            if (len == 0)
                return 0;

            try
            {
                var n = stream.Read(b, off, len);
                return n == 0 ? -1 : n;
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
        }

#endif

        public static unsafe void initIDs()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref initIDsPtr, CallerID, Name, "initIDs", "()V"))(jni.Env, jni.Ref(Class));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getHandle(int fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int, long>)JniForward.Resolve(ref getHandlePtr, CallerID, Name, "getHandle", "(I)J"))(jni.Env, jni.Ref(Class), fd);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool getAppend(int fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int, byte>)JniForward.Resolve(ref getAppendPtr, CallerID, Name, "getAppend", "(I)Z"))(jni.Env, jni.Ref(Class), fd) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void close0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
            {
                FileDescriptorAccessor.SetStream(self, null);
                stream.Dispose();
                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref close0Ptr, CallerID, Name, "close0", "()V"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void sync0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the virtual file system is read-only
            if (GetVfsStream(self) != null)
                return;

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref sync0Ptr, CallerID, Name, "sync0", "()V"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileInputStream': files of the virtual file system here, the
    /// file system's with libjava's natives.
    /// </summary>
    static class FileInputStream
    {

#if FIRST_PASS == false

        static FileInputStreamAccessor fileInputStreamAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint initIDsPtr, open0Ptr, read0Ptr, readBytesPtr, length0Ptr, position0Ptr, skip0Ptr, available0Ptr, isRegularFile0Ptr;

        static FileInputStreamAccessor FileInputStreamAccessor => JVM.Internal.BaseAccessors.Get(ref fileInputStreamAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::java.io.FileInputStream).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::java.io.FileInputStream).TypeHandle);

        static Stream GetVfsStream(object self) => FileDescriptor.GetVfsStream(FileInputStreamAccessor.GetFd(self));

        const string Name = "java/io/FileInputStream";

#endif

        public static unsafe void initIDs()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref initIDsPtr, CallerID, Name, "initIDs", "()V"))(jni.Env, jni.Ref(Class));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void open0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.Vfs.IsPath(path))
            {
                FileDescriptor.OpenVfs(FileInputStreamAccessor.GetFd(self), path, false);
                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, nint, void>)JniForward.Resolve(ref open0Ptr, CallerID, Name, "open0", "(Ljava/lang/String;)V"))(jni.Env, jni.Ref(self), jni.Ref(path));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int read0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.ReadByte();

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int>)JniForward.Resolve(ref read0Ptr, CallerID, Name, "read0", "()I"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int readBytes(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return FileDescriptor.ReadBytes(stream, b, off, len);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int, int, int>)JniForward.Resolve(ref readBytesPtr, CallerID, Name, "readBytes", "([BII)I"))(jni.Env, jni.Ref(self), jni.Ref(b), off, len);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long length0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.CanSeek ? stream.Length : 0;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long>)JniForward.Resolve(ref length0Ptr, CallerID, Name, "length0", "()J"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long position0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.CanSeek ? stream.Position : 0;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long>)JniForward.Resolve(ref position0Ptr, CallerID, Name, "position0", "()J"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long skip0(object self, long n)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
            {
                if (stream.CanSeek)
                {
                    var position = stream.Position;
                    stream.Seek(n, SeekOrigin.Current);
                    return stream.Position - position;
                }

                var buffer = new byte[(int)Math.Min(n, 8192)];
                long skipped = 0;
                while (skipped < n)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, n - skipped));
                    if (read <= 0)
                        break;
                    skipped += read;
                }

                return skipped;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, long>)JniForward.Resolve(ref skip0Ptr, CallerID, Name, "skip0", "(J)J"))(jni.Env, jni.Ref(self), n);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int available0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.CanSeek ? (int)Math.Min(int.MaxValue, Math.Max(0, stream.Length - stream.Position)) : 0;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int>)JniForward.Resolve(ref available0Ptr, CallerID, Name, "available0", "()I"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool isRegularFile0(object self, object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (FileDescriptor.GetVfsStream(fd) != null)
                return true;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, byte>)JniForward.Resolve(ref isRegularFile0Ptr, CallerID, Name, "isRegularFile0", "(Ljava/io/FileDescriptor;)Z"))(jni.Env, jni.Ref(self), jni.Ref(fd)) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileOutputStream': the virtual file system is read-only, and
    /// libjava's natives write the file system's files.
    /// </summary>
    static class FileOutputStream
    {

#if FIRST_PASS == false

        static global::ikvm.@internal.CallerID callerID;
        static nint initIDsPtr, open0Ptr, writePtr, writeBytesPtr;

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::java.io.FileOutputStream).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::java.io.FileOutputStream).TypeHandle);

        const string Name = "java/io/FileOutputStream";

#endif

        public static unsafe void initIDs()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref initIDsPtr, CallerID, Name, "initIDs", "()V"))(jni.Env, jni.Ref(Class));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void open0(object self, string path, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.Vfs.IsPath(path))
            {
                FileDescriptor.OpenVfs(null, path, true);
                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, nint, byte, void>)JniForward.Resolve(ref open0Ptr, CallerID, Name, "open0", "(Ljava/lang/String;Z)V"))(jni.Env, jni.Ref(self), jni.Ref(path), append ? (byte)1 : (byte)0);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void write(object self, int b, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, byte, void>)JniForward.Resolve(ref writePtr, CallerID, Name, "write", "(IZ)V"))(jni.Env, jni.Ref(self), b, append ? (byte)1 : (byte)0);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void writeBytes(object self, byte[] b, int off, int len, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, nint, int, int, byte, void>)JniForward.Resolve(ref writeBytesPtr, CallerID, Name, "writeBytes", "([BIIZ)V"))(jni.Env, jni.Ref(self), jni.Ref(b), off, len, append ? (byte)1 : (byte)0);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.RandomAccessFile': files of the virtual file system here
    /// (read-only), the file system's with libjava's natives.
    /// </summary>
    static class RandomAccessFile
    {

        const int O_RDWR = 2;

#if FIRST_PASS == false

        static RandomAccessFileAccessor randomAccessFileAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint initIDsPtr, open0Ptr, read0Ptr, readBytes0Ptr, write0Ptr, writeBytes0Ptr, getFilePointerPtr, seek0Ptr, length0Ptr, setLength0Ptr;

        static RandomAccessFileAccessor RandomAccessFileAccessor => JVM.Internal.BaseAccessors.Get(ref randomAccessFileAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::java.io.RandomAccessFile).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::java.io.RandomAccessFile).TypeHandle);

        static Stream GetVfsStream(object self) => FileDescriptor.GetVfsStream(RandomAccessFileAccessor.GetFd(self));

        const string Name = "java/io/RandomAccessFile";

#endif

        public static unsafe void initIDs()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref initIDsPtr, CallerID, Name, "initIDs", "()V"))(jni.Env, jni.Ref(Class));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void open0(object self, string path, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (JVM.Vfs.IsPath(path))
            {
                FileDescriptor.OpenVfs(RandomAccessFileAccessor.GetFd(self), path, (mode & O_RDWR) != 0);
                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, nint, int, void>)JniForward.Resolve(ref open0Ptr, CallerID, Name, "open0", "(Ljava/lang/String;I)V"))(jni.Env, jni.Ref(self), jni.Ref(path), mode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int read0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.ReadByte();

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int>)JniForward.Resolve(ref read0Ptr, CallerID, Name, "read0", "()I"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int readBytes0(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return FileDescriptor.ReadBytes(stream, b, off, len);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int, int, int>)JniForward.Resolve(ref readBytes0Ptr, CallerID, Name, "readBytes0", "([BII)I"))(jni.Env, jni.Ref(self), jni.Ref(b), off, len);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void write0(object self, int b)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) != null)
                throw new global::java.io.IOException("Read-only file system");

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, void>)JniForward.Resolve(ref write0Ptr, CallerID, Name, "write0", "(I)V"))(jni.Env, jni.Ref(self), b);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void writeBytes0(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) != null)
                throw new global::java.io.IOException("Read-only file system");

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, nint, int, int, void>)JniForward.Resolve(ref writeBytes0Ptr, CallerID, Name, "writeBytes0", "([BII)V"))(jni.Env, jni.Ref(self), jni.Ref(b), off, len);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getFilePointer(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.Position;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long>)JniForward.Resolve(ref getFilePointerPtr, CallerID, Name, "getFilePointer", "()J"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void seek0(object self, long pos)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
            {
                if (pos < 0)
                    throw new global::java.io.IOException("Negative seek offset");

                stream.Position = pos;
                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, void>)JniForward.Resolve(ref seek0Ptr, CallerID, Name, "seek0", "(J)V"))(jni.Env, jni.Ref(self), pos);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long length0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) is Stream stream)
                return stream.Length;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long>)JniForward.Resolve(ref length0Ptr, CallerID, Name, "length0", "()J"))(jni.Env, jni.Ref(self));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void setLength0(object self, long newLength)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (GetVfsStream(self) != null)
                throw new global::java.io.IOException("Read-only file system");

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, void>)JniForward.Resolve(ref setLength0Ptr, CallerID, Name, "setLength0", "(J)V"))(jni.Env, jni.Ref(self), newLength);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

}
