using System;
using System.Text;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Sun.Nio.Fs;
using IKVM.Runtime.JNI;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.sun.nio.fs
{

    /// <summary>
    /// Implements the metadata natives of JDK 25's 'sun.nio.fs.UnixNativeDispatcher' for the paths of IKVM's virtual
    /// file system, which libnio does not know of (realpath0, stat0, lstat0, access0), and calls libnio's for the rest.
    /// Reading a file of the virtual file system is java.io's (as ZipFile does); a NIO channel is a file descriptor.
    /// The natives not defined here are libnio's.
    /// </summary>
    static class UnixNativeDispatcher
    {

#if FIRST_PASS == false

        // the read-only virtual file system: r-x for directories, r-- for files
        const int VfsDirectoryPermissions = 365;  // 0555
        const int VfsFilePermissions = 292;       // 0444

        // the device of every entry of the virtual file system, which no real one has
        const long VfsDeviceId = 0x76667300;

        static UnixFileAttributesAccessor unixFileAttributesAccessor;
        static UnixExceptionAccessor unixExceptionAccessor;
        static UnixConstantsAccessor unixConstantsAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint realpath0Ptr, stat0Ptr, lstat0Ptr, access0Ptr;

        static UnixFileAttributesAccessor UnixFileAttributesAccessor => JVM.Internal.BaseAccessors.Get(ref unixFileAttributesAccessor);

        static UnixExceptionAccessor UnixExceptionAccessor => JVM.Internal.BaseAccessors.Get(ref unixExceptionAccessor);

        static UnixConstantsAccessor UnixConstantsAccessor => JVM.Internal.BaseAccessors.Get(ref unixConstantsAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::sun.nio.fs.UnixNativeDispatcher).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::sun.nio.fs.UnixNativeDispatcher).TypeHandle);

        const string Name = "sun/nio/fs/UnixNativeDispatcher";

        /// <summary>
        /// Reads the bytes of the NUL-terminated string at an address.
        /// </summary>
        static unsafe byte[] ReadCStringBytes(long address)
        {
            return System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)address).ToArray();
        }

        static Exception NewUnixException(int errno) => (Exception)UnixExceptionAccessor.Init(errno);

        /// <summary>
        /// Sets the attributes of the entry of the virtual file system at the path, or returns ENOENT.
        /// </summary>
        static int FillVfsAttributes(string path, object attrs)
        {
            var a = UnixFileAttributesAccessor;
            switch (JVM.Vfs.GetEntry(path))
            {
                case VfsDirectory:
                    a.SetStMode(attrs, UnixConstantsAccessor.S_IFDIR | VfsDirectoryPermissions);
                    a.SetStSize(attrs, 0);
                    break;
                case VfsFile f:
                    a.SetStMode(attrs, UnixConstantsAccessor.S_IFREG | VfsFilePermissions);
                    a.SetStSize(attrs, f.Size);
                    break;
                default:
                    return UnixConstantsAccessor.ENOENT;
            }

            a.SetStNlink(attrs, 1);
            a.SetStDev(attrs, VfsDeviceId);

            // stable within a run, so that a file's key stays the same
            a.SetStIno(attrs, (uint)path.GetHashCode());
            return 0;
        }

#endif

        public static unsafe byte[] realpath0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var bytes = ReadCStringBytes(pathAddress);
            var path = Encoding.UTF8.GetString(bytes);
            if (JVM.Vfs.IsPath(path))
            {
                // the virtual file system has no links, and toRealPath makes the path absolute first
                if (JVM.Vfs.GetEntry(path) is null)
                    throw NewUnixException(UnixConstantsAccessor.ENOENT);

                return bytes;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                return (byte[])jni.Unwrap(((delegate* unmanaged<nint, nint, long, nint>)JniForward.Resolve(ref realpath0Ptr, CallerID, Name, "realpath0", "(J)[B"))(jni.Env, jni.Ref(Class), pathAddress));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int stat0(long pathAddress, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = Encoding.UTF8.GetString(ReadCStringBytes(pathAddress));
            if (JVM.Vfs.IsPath(path))
                return FillVfsAttributes(path, attrs);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, nint, int>)JniForward.Resolve(ref stat0Ptr, CallerID, Name, "stat0", "(JLsun/nio/fs/UnixFileAttributes;)I"))(jni.Env, jni.Ref(Class), pathAddress, jni.Ref(attrs));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void lstat0(long pathAddress, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = Encoding.UTF8.GetString(ReadCStringBytes(pathAddress));
            if (JVM.Vfs.IsPath(path))
            {
                // the virtual file system has no links
                var errno = FillVfsAttributes(path, attrs);
                if (errno != 0)
                    throw NewUnixException(errno);

                return;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, nint, void>)JniForward.Resolve(ref lstat0Ptr, CallerID, Name, "lstat0", "(JLsun/nio/fs/UnixFileAttributes;)V"))(jni.Env, jni.Ref(Class), pathAddress, jni.Ref(attrs));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int access0(long pathAddress, int amode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = Encoding.UTF8.GetString(ReadCStringBytes(pathAddress));
            if (JVM.Vfs.IsPath(path))
            {
                if (JVM.Vfs.GetEntry(path) is null)
                    return UnixConstantsAccessor.ENOENT;

                // the virtual file system is read-only
                return (amode & UnixConstantsAccessor.W_OK) != 0 ? UnixConstantsAccessor.EACCES : 0;
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, int, int>)JniForward.Resolve(ref access0Ptr, CallerID, Name, "access0", "(JI)I"))(jni.Env, jni.Ref(Class), pathAddress, amode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

}
