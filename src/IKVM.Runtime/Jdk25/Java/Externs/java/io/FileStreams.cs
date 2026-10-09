using System;
using System.IO;
using System.Threading;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Io;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.java.io
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileDescriptor'. A file descriptor's file is a .NET stream
    /// (map.xml adds the 'stream' field); 0, 1 and 2 are the console's standard streams, and files of IKVM's virtual
    /// file system are its streams. The descriptor number of a file the runtime opens only tells FileDescriptor that it
    /// is valid.
    /// </summary>
    static class FileDescriptor
    {

#if FIRST_PASS == false

        static FileDescriptorAccessor fileDescriptorAccessor;

        static FileDescriptorAccessor FileDescriptorAccessor => JVM.Internal.BaseAccessors.Get(ref fileDescriptorAccessor);

        static int nextFd = 2;

        /// <summary>
        /// Gets the stream of the file descriptor, the console's for the standard ones.
        /// </summary>
        /// <param name="fd"></param>
        /// <returns></returns>
        internal static Stream GetStream(object fd)
        {
            if (fd == null)
                throw new global::java.io.IOException("Stream Closed");

            if (FileDescriptorAccessor.GetStream(fd) is Stream stream)
                return stream;

            stream = FileDescriptorAccessor.GetFdField(fd) switch
            {
                0 => global::System.Console.OpenStandardInput(),
                1 => global::System.Console.OpenStandardOutput(),
                2 => global::System.Console.OpenStandardError(),
                _ => throw new global::java.io.IOException("Stream Closed"),
            };

            FileDescriptorAccessor.SetStream(fd, stream);
            return stream;
        }

        /// <summary>
        /// Makes the file descriptor that of the stream.
        /// </summary>
        /// <param name="fd"></param>
        /// <param name="stream"></param>
        internal static void SetStream(object fd, Stream stream)
        {
            FileDescriptorAccessor.SetStream(fd, stream);
            FileDescriptorAccessor.SetFdField(fd, Interlocked.Increment(ref nextFd));
        }

        /// <summary>
        /// Opens a file for a file descriptor, from IKVM's virtual file system if it has the path, and fails as the JDK's
        /// natives do.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        internal static Stream Open(string path, FileMode mode, FileAccess access, FileOptions options)
        {
            if (JVM.Vfs.IsPath(path))
            {
                if (JVM.Vfs.GetEntry(path) is VfsDirectory)
                    throw new global::java.io.FileNotFoundException(path + " (Is a directory)");

                // the virtual file system is read-only
                if (access != FileAccess.Read)
                    throw new global::java.io.FileNotFoundException(path + " (Read-only file system)");

                try
                {
                    return JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read);
                }
                catch (FileNotFoundException)
                {
                    throw new global::java.io.FileNotFoundException(path + " (No such file or directory)");
                }
            }

            if (Directory.Exists(path))
                throw new global::java.io.FileNotFoundException(path + " (Is a directory)");

            try
            {
                // no buffering: what a stream writes, the file has
                return new FileStream(path, mode, access, FileShare.ReadWrite | FileShare.Delete, 1, options);
            }
            catch (FileNotFoundException)
            {
                throw new global::java.io.FileNotFoundException(path + " (No such file or directory)");
            }
            catch (DirectoryNotFoundException)
            {
                throw new global::java.io.FileNotFoundException(path + " (No such file or directory)");
            }
            catch (UnauthorizedAccessException)
            {
                throw new global::java.io.FileNotFoundException(path + " (Permission denied)");
            }
            catch (IOException e)
            {
                throw new global::java.io.FileNotFoundException(path + " (" + e.Message + ")");
            }
            catch (ArgumentException e)
            {
                throw new global::java.io.FileNotFoundException(path + " (" + e.Message + ")");
            }
        }

        /// <summary>
        /// Returns whether the file descriptor is one of the standard streams, which closing does not close.
        /// </summary>
        static bool IsStandard(object fd)
        {
            var n = FileDescriptorAccessor.GetFdField(fd);
            return n >= 0 && n <= 2;
        }

#endif

        public static void initIDs()
        {

        }

        public static long getHandle(int fd)
        {
            // the Windows handle; none elsewhere
            return -1;
        }

        public static bool getAppend(int fd)
        {
            return false;
        }

        public static void close0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = FileDescriptorAccessor.GetStream(self);
            var standard = IsStandard(self);
            FileDescriptorAccessor.SetStream(self, null);
            FileDescriptorAccessor.SetFdField(self, -1);

            if (stream != null && standard == false)
                stream.Dispose();
#endif
        }

        public static void sync0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(self);
            if (stream is FileStream fs)
                fs.Flush(true);
            else
                stream.Flush();
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileInputStream'.
    /// </summary>
    static class FileInputStream
    {

#if FIRST_PASS == false

        static FileInputStreamAccessor fileInputStreamAccessor;

        static FileInputStreamAccessor FileInputStreamAccessor => JVM.Internal.BaseAccessors.Get(ref fileInputStreamAccessor);

        static Stream GetStream(object self) => FileDescriptor.GetStream(FileInputStreamAccessor.GetFd(self));

#endif

        public static void initIDs()
        {

        }

        public static void open0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            FileDescriptor.SetStream(FileInputStreamAccessor.GetFd(self), FileDescriptor.Open(path, FileMode.Open, FileAccess.Read, FileOptions.None));
#endif
        }

        public static int read0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return GetStream(self).ReadByte();
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static int readBytes(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (b == null)
                throw new global::java.lang.NullPointerException();
            if (off < 0 || len < 0 || b.Length - off < len)
                throw new global::java.lang.IndexOutOfBoundsException();
            if (len == 0)
                return 0;

            try
            {
                var n = GetStream(self).Read(b, off, len);
                return n == 0 ? -1 : n;
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static long length0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(self);
            return stream.CanSeek ? stream.Length : 0;
#endif
        }

        public static long position0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(self);
            return stream.CanSeek ? stream.Position : 0;
#endif
        }

        public static long skip0(object self, long n)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(self);
            if (stream.CanSeek)
            {
                var position = stream.Position;
                stream.Seek(n, SeekOrigin.Current);
                return stream.Position - position;
            }

            // a stream that cannot seek is read past
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
#endif
        }

        public static int available0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stream = GetStream(self);
            if (stream.CanSeek)
                return (int)Math.Min(int.MaxValue, Math.Max(0, stream.Length - stream.Position));

            return 0;
#endif
        }

        public static bool isRegularFile0(object self, object fd)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return FileDescriptor.GetStream(fd) is FileStream;
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.FileOutputStream'.
    /// </summary>
    static class FileOutputStream
    {

#if FIRST_PASS == false

        static FileOutputStreamAccessor fileOutputStreamAccessor;

        static FileOutputStreamAccessor FileOutputStreamAccessor => JVM.Internal.BaseAccessors.Get(ref fileOutputStreamAccessor);

        static Stream GetStream(object self) => FileDescriptor.GetStream(FileOutputStreamAccessor.GetFd(self));

#endif

        public static void initIDs()
        {

        }

        public static void open0(object self, string path, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            FileDescriptor.SetStream(FileOutputStreamAccessor.GetFd(self), FileDescriptor.Open(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileOptions.None));
#endif
        }

        public static void write(object self, int b, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var stream = GetStream(self);
                stream.WriteByte((byte)b);
                stream.Flush();
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static void writeBytes(object self, byte[] b, int off, int len, bool append)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (b == null)
                throw new global::java.lang.NullPointerException();
            if (off < 0 || len < 0 || b.Length - off < len)
                throw new global::java.lang.IndexOutOfBoundsException();

            try
            {
                var stream = GetStream(self);
                stream.Write(b, off, len);
                stream.Flush();
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.RandomAccessFile'.
    /// </summary>
    static class RandomAccessFile
    {

        const int O_RDWR = 2;
        const int O_SYNC = 4;
        const int O_DSYNC = 8;
        const int O_TEMPORARY = 16;

#if FIRST_PASS == false

        static RandomAccessFileAccessor randomAccessFileAccessor;

        static RandomAccessFileAccessor RandomAccessFileAccessor => JVM.Internal.BaseAccessors.Get(ref randomAccessFileAccessor);

        static Stream GetStream(object self) => FileDescriptor.GetStream(RandomAccessFileAccessor.GetFd(self));

#endif

        public static void initIDs()
        {

        }

        public static void open0(object self, string path, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var write = (mode & O_RDWR) != 0;
            var options = FileOptions.None;
            if ((mode & (O_SYNC | O_DSYNC)) != 0)
                options |= FileOptions.WriteThrough;
            if ((mode & O_TEMPORARY) != 0)
                options |= FileOptions.DeleteOnClose;

            FileDescriptor.SetStream(RandomAccessFileAccessor.GetFd(self), FileDescriptor.Open(path, write ? FileMode.OpenOrCreate : FileMode.Open, write ? FileAccess.ReadWrite : FileAccess.Read, options));
#endif
        }

        public static int read0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return GetStream(self).ReadByte();
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static int readBytes0(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (b == null)
                throw new global::java.lang.NullPointerException();
            if (off < 0 || len < 0 || b.Length - off < len)
                throw new global::java.lang.IndexOutOfBoundsException();
            if (len == 0)
                return 0;

            try
            {
                var n = GetStream(self).Read(b, off, len);
                return n == 0 ? -1 : n;
            }
            catch (IOException e)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static void write0(object self, int b)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                GetStream(self).WriteByte((byte)b);
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static void writeBytes0(object self, byte[] b, int off, int len)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (b == null)
                throw new global::java.lang.NullPointerException();
            if (off < 0 || len < 0 || b.Length - off < len)
                throw new global::java.lang.IndexOutOfBoundsException();

            try
            {
                GetStream(self).Write(b, off, len);
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

        public static long getFilePointer(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return GetStream(self).Position;
#endif
        }

        public static void seek0(object self, long pos)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (pos < 0)
                throw new global::java.io.IOException("Negative seek offset");

            GetStream(self).Position = pos;
#endif
        }

        public static long length0(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return GetStream(self).Length;
#endif
        }

        public static void setLength0(object self, long newLength)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (newLength < 0)
                throw new global::java.io.IOException("Invalid argument");

            try
            {
                // as the JDK, a file pointer past the new end moves to it
                GetStream(self).SetLength(newLength);
            }
            catch (Exception e) when (e is IOException or NotSupportedException)
            {
                throw new global::java.io.IOException(e.Message);
            }
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.Console'.
    /// </summary>
    static class Console
    {

        public static int ttyStatus()
        {
            // which of stdin (1), stdout (2) and stderr (4) are a terminal
            int status = 0;
            if (global::System.Console.IsInputRedirected == false)
                status |= 1;
            if (global::System.Console.IsOutputRedirected == false)
                status |= 2;
            if (global::System.Console.IsErrorRedirected == false)
                status |= 4;
            return status;
        }

    }

}
