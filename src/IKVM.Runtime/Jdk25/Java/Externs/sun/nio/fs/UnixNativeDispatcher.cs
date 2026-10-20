using System;
using System.Runtime.InteropServices;
using System.Text;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Sun.Nio.Fs;
using IKVM.Runtime.JNI;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.sun.nio.fs
{

    /// <summary>
    /// Implements the natives of JDK 25's 'sun.nio.fs.UnixNativeDispatcher' that take a path, for the paths of IKVM's
    /// virtual file system, which libnio does not know of, and calls libnio's for the rest. The virtual file system is a
    /// read-only file system without file descriptors: its metadata is answered here, a change to it fails as a read-only
    /// mount fails it, and an open of it fails with ENXIO. A channel or directory stream of it is VfsSupport's, which
    /// map.xml has the provider ask first. A path that walks through the virtual file system and leaves it by '..' is
    /// given to libnio normalized, as the mount point does not exist on the host. strerror describes its argument, which
    /// libnio's does not. The natives not defined here are libnio's.
    /// </summary>
    static class UnixNativeDispatcher
    {

#if FIRST_PASS == false

        // the read-only virtual file system: r-x for directories, r-- for files
        const int VfsDirectoryPermissions = 365;  // 0555
        const int VfsFilePermissions = 292;       // 0444

        // the device of every entry of the virtual file system, which no real one has
        internal const long VfsDeviceId = 0x76667300;

        static UnixFileAttributesAccessor unixFileAttributesAccessor;
        static UnixExceptionAccessor unixExceptionAccessor;
        static UnixConstantsAccessor unixConstantsAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint realpath0Ptr, stat0Ptr, lstat0Ptr, access0Ptr, open0Ptr, openat0Ptr, link0Ptr, unlink0Ptr, unlinkat0Ptr;
        static nint mknod0Ptr, rename0Ptr, renameat0Ptr, mkdir0Ptr, rmdir0Ptr, readlink0Ptr, symlink0Ptr, fstatat0Ptr;
        static nint chown0Ptr, lchown0Ptr, chmod0Ptr, fchmodat0Ptr, utimensat0Ptr, opendir0Ptr, statvfs0Ptr, strerrorPtr;

        static UnixFileAttributesAccessor UnixFileAttributesAccessor => JVM.Internal.BaseAccessors.Get(ref unixFileAttributesAccessor);

        static UnixExceptionAccessor UnixExceptionAccessor => JVM.Internal.BaseAccessors.Get(ref unixExceptionAccessor);

        internal static UnixConstantsAccessor UnixConstantsAccessor => JVM.Internal.BaseAccessors.Get(ref unixConstantsAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::sun.nio.fs.UnixNativeDispatcher).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::sun.nio.fs.UnixNativeDispatcher).TypeHandle);

        const string Name = "sun/nio/fs/UnixNativeDispatcher";

        /// <summary>
        /// Reads the bytes of the NUL-terminated string at an address.
        /// </summary>
        static unsafe byte[] ReadCStringBytes(long address)
        {
            return MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)address).ToArray();
        }

        /// <summary>
        /// Resolves the NUL-terminated path at an address.
        /// </summary>
        internal static VfsPath Resolve(long pathAddress) => JVM.Vfs.Resolve(Encoding.UTF8.GetString(ReadCStringBytes(pathAddress)));

        /// <summary>
        /// Resolves the NUL-terminated path at an address that the *at natives take with a directory: a relative path
        /// is relative to that directory, which is the host's, unless it is AT_FDCWD.
        /// </summary>
        static VfsPath ResolveAt(int dfd, long pathAddress)
        {
            var path = Encoding.UTF8.GetString(ReadCStringBytes(pathAddress));
            if (dfd != UnixConstantsAccessor.AT_FDCWD && System.IO.Path.IsPathRooted(path) == false)
                return new VfsPath(null, path, false);

            return JVM.Vfs.Resolve(path);
        }

        /// <summary>
        /// Gets the errno of a status, in the numbering of the JDK image.
        /// </summary>
        internal static int ToErrno(VfsStatus status)
        {
            var c = UnixConstantsAccessor;
            return status switch
            {
                VfsStatus.NotFound => c.ENOENT,
                VfsStatus.NotDirectory => c.ENOTDIR,
                VfsStatus.IsDirectory => c.EISDIR,
                VfsStatus.Exists => c.EEXIST,
                VfsStatus.ReadOnly => c.EROFS,
                VfsStatus.CrossDevice => c.EXDEV,
                VfsStatus.NotLink => c.EINVAL,
                VfsStatus.NoDescriptor => c.ENXIO,
                _ => 0,
            };
        }

        static Exception NewUnixException(int errno) => (Exception)UnixExceptionAccessor.Init(errno);

        internal static Exception NewUnixException(VfsStatus status) => NewUnixException(ToErrno(status));

        /// <summary>
        /// Checks an open(2) of a path of the virtual file system with the given flags: even one a read-only file
        /// system allows fails, with ENXIO, as there is no file descriptor to give.
        /// </summary>
        internal static VfsStatus CheckOpen(VfsPath path, int flags)
        {
            var c = UnixConstantsAccessor;
            var status = JVM.Vfs.CheckOpen(path, (flags & (c.O_WRONLY | c.O_RDWR)) != 0, (flags & c.O_CREAT) != 0, (flags & c.O_EXCL) != 0, (flags & c.O_TRUNC) != 0);
            return status == VfsStatus.Ok ? VfsStatus.NoDescriptor : status;
        }

        /// <summary>
        /// Sets the attributes of the entry of the virtual file system at the path, or returns ENOENT.
        /// </summary>
        static int FillVfsAttributes(VfsPath path, object attrs)
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

            // unique and stable within a run, so that a file's key identifies it
            a.SetStIno(attrs, JVM.Vfs.GetFileId(path));
            return 0;
        }

        /// <summary>
        /// The address of the path to give libnio: the original's, or a copy of the one the virtual file system
        /// rewrote.
        /// </summary>
        internal ref struct HostPath
        {

            nint memory;

            HostPath(nint memory, long address)
            {
                this.memory = memory;
                Address = address;
            }

            /// <summary>
            /// Gets the address.
            /// </summary>
            public long Address { get; }

            /// <summary>
            /// Gets the path to give libnio for a resolved path whose original is at the given address.
            /// </summary>
            public static HostPath Of(VfsPath path, long address)
            {
                if (path.Rewritten == false)
                    return new HostPath(0, address);

                var bytes = Encoding.UTF8.GetBytes(path.Path);
                var memory = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                Marshal.WriteByte(memory, bytes.Length, 0);
                return new HostPath(memory, memory);
            }

            /// <summary>
            /// Frees the copy, if any.
            /// </summary>
            public void Dispose()
            {
                if (memory != 0)
                {
                    Marshal.FreeHGlobal(memory);
                    memory = 0;
                }
            }

        }

#endif

        public static unsafe byte[] strerror(int errnum)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#elif NET7_0_OR_GREATER
            // libnio's describes the thread's errno rather than its argument, which is the error only right after the
            // call that failed: not once managed code has run since, nor for an error of the virtual file system, where
            // errno is likely 0 and the description it gives is whatever its buffer held
            return Encoding.UTF8.GetBytes(Marshal.GetPInvokeErrorMessage(UnixErrnoTranslation.ToHost(errnum)));
#else
            var jni = JniForward.Enter(CallerID);
            try
            {
                return (byte[])jni.Unwrap(((delegate* unmanaged<nint, nint, int, nint>)JniForward.Resolve(ref strerrorPtr, CallerID, Name, "strerror", "(I)[B"))(jni.Env, jni.Ref(Class), errnum));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe byte[] realpath0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
            {
                // the virtual file system has no links
                if (JVM.Vfs.GetEntry(p) is null)
                    throw NewUnixException(VfsStatus.NotFound);

                return Encoding.UTF8.GetBytes(p.Path);
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return (byte[])jni.Unwrap(((delegate* unmanaged<nint, nint, long, nint>)JniForward.Resolve(ref realpath0Ptr, CallerID, Name, "realpath0", "(J)[B"))(jni.Env, jni.Ref(Class), h.Address));
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
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                return FillVfsAttributes(p, attrs);

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, nint, int>)JniForward.Resolve(ref stat0Ptr, CallerID, Name, "stat0", "(JLsun/nio/fs/UnixFileAttributes;)I"))(jni.Env, jni.Ref(Class), h.Address, jni.Ref(attrs));
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
            var p = Resolve(pathAddress);
            if (p.IsVfs)
            {
                // the virtual file system has no links
                var errno = FillVfsAttributes(p, attrs);
                if (errno != 0)
                    throw NewUnixException(errno);

                return;
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, nint, void>)JniForward.Resolve(ref lstat0Ptr, CallerID, Name, "lstat0", "(JLsun/nio/fs/UnixFileAttributes;)V"))(jni.Env, jni.Ref(Class), h.Address, jni.Ref(attrs));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void fstatat0(int dfd, long pathAddress, int flag, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = ResolveAt(dfd, pathAddress);
            if (p.IsVfs)
            {
                var errno = FillVfsAttributes(p, attrs);
                if (errno != 0)
                    throw NewUnixException(errno);

                return;
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, long, int, nint, void>)JniForward.Resolve(ref fstatat0Ptr, CallerID, Name, "fstatat0", "(IJILsun/nio/fs/UnixFileAttributes;)V"))(jni.Env, jni.Ref(Class), dfd, h.Address, flag, jni.Ref(attrs));
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
            var p = Resolve(pathAddress);
            if (p.IsVfs)
            {
                var c = UnixConstantsAccessor;
                var entry = JVM.Vfs.GetEntry(p);
                if (entry is null)
                    return c.ENOENT;

                // the virtual file system is read-only, and its files are not executable (0444)
                if ((amode & c.W_OK) != 0)
                    return c.EROFS;
                if ((amode & c.X_OK) != 0 && entry is VfsFile)
                    return c.EACCES;

                return 0;
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, int, int>)JniForward.Resolve(ref access0Ptr, CallerID, Name, "access0", "(JI)I"))(jni.Env, jni.Ref(Class), h.Address, amode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int open0(long pathAddress, int flags, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(CheckOpen(p, flags));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, int, int, int>)JniForward.Resolve(ref open0Ptr, CallerID, Name, "open0", "(JII)I"))(jni.Env, jni.Ref(Class), h.Address, flags, mode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int openat0(int dfd, long pathAddress, int flags, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = ResolveAt(dfd, pathAddress);
            if (p.IsVfs)
                throw NewUnixException(CheckOpen(p, flags));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int, long, int, int, int>)JniForward.Resolve(ref openat0Ptr, CallerID, Name, "openat0", "(IJII)I"))(jni.Env, jni.Ref(Class), dfd, h.Address, flags, mode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long opendir0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
            {
                var status = JVM.Vfs.CheckOpenDirectory(p);
                throw NewUnixException(status == VfsStatus.Ok ? VfsStatus.NoDescriptor : status);
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, long, long>)JniForward.Resolve(ref opendir0Ptr, CallerID, Name, "opendir0", "(J)J"))(jni.Env, jni.Ref(Class), h.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe byte[] readlink0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckReadLink(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return (byte[])jni.Unwrap(((delegate* unmanaged<nint, nint, long, nint>)JniForward.Resolve(ref readlink0Ptr, CallerID, Name, "readlink0", "(J)[B"))(jni.Env, jni.Ref(Class), h.Address));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void statvfs0(long pathAddress, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
            {
                // the virtual file system has no space: the attributes stay zero
                if (JVM.Vfs.GetEntry(p) is null)
                    throw NewUnixException(VfsStatus.NotFound);

                return;
            }

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, nint, void>)JniForward.Resolve(ref statvfs0Ptr, CallerID, Name, "statvfs0", "(JLsun/nio/fs/UnixFileStoreAttributes;)V"))(jni.Env, jni.Ref(Class), h.Address, jni.Ref(attrs));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void mkdir0(long pathAddress, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckCreate(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, int, void>)JniForward.Resolve(ref mkdir0Ptr, CallerID, Name, "mkdir0", "(JI)V"))(jni.Env, jni.Ref(Class), h.Address, mode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void mknod0(long pathAddress, int mode, long dev)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckCreate(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, int, long, void>)JniForward.Resolve(ref mknod0Ptr, CallerID, Name, "mknod0", "(JIJ)V"))(jni.Env, jni.Ref(Class), h.Address, mode, dev);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void symlink0(long name1, long name2)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // name1 is the content of the link, which is not resolved
            var p = Resolve(name2);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckCreate(p));

            using var h = HostPath.Of(p, name2);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, long, void>)JniForward.Resolve(ref symlink0Ptr, CallerID, Name, "symlink0", "(JJ)V"))(jni.Env, jni.Ref(Class), name1, h.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void link0(long existingAddress, long newAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var pe = Resolve(existingAddress);
            var pn = Resolve(newAddress);
            if (pe.IsVfs || pn.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckLink(pe, pn));

            using var he = HostPath.Of(pe, existingAddress);
            using var hn = HostPath.Of(pn, newAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, long, void>)JniForward.Resolve(ref link0Ptr, CallerID, Name, "link0", "(JJ)V"))(jni.Env, jni.Ref(Class), he.Address, hn.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void unlink0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckRemove(p, false));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, void>)JniForward.Resolve(ref unlink0Ptr, CallerID, Name, "unlink0", "(J)V"))(jni.Env, jni.Ref(Class), h.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void unlinkat0(int dfd, long pathAddress, int flag)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = ResolveAt(dfd, pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckRemove(p, (flag & UnixConstantsAccessor.AT_REMOVEDIR) != 0));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, long, int, void>)JniForward.Resolve(ref unlinkat0Ptr, CallerID, Name, "unlinkat0", "(IJI)V"))(jni.Env, jni.Ref(Class), dfd, h.Address, flag);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void rmdir0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckRemove(p, true));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, void>)JniForward.Resolve(ref rmdir0Ptr, CallerID, Name, "rmdir0", "(J)V"))(jni.Env, jni.Ref(Class), h.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void rename0(long fromAddress, long toAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var pf = Resolve(fromAddress);
            var pt = Resolve(toAddress);
            if (pf.IsVfs || pt.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckRename(pf, pt));

            using var hf = HostPath.Of(pf, fromAddress);
            using var ht = HostPath.Of(pt, toAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, long, void>)JniForward.Resolve(ref rename0Ptr, CallerID, Name, "rename0", "(JJ)V"))(jni.Env, jni.Ref(Class), hf.Address, ht.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void renameat0(int fromfd, long fromAddress, int tofd, long toAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var pf = ResolveAt(fromfd, fromAddress);
            var pt = ResolveAt(tofd, toAddress);
            if (pf.IsVfs || pt.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckRename(pf, pt));

            using var hf = HostPath.Of(pf, fromAddress);
            using var ht = HostPath.Of(pt, toAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, long, int, long, void>)JniForward.Resolve(ref renameat0Ptr, CallerID, Name, "renameat0", "(IJIJ)V"))(jni.Env, jni.Ref(Class), fromfd, hf.Address, tofd, ht.Address);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void chown0(long pathAddress, int uid, int gid)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckChange(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, int, int, void>)JniForward.Resolve(ref chown0Ptr, CallerID, Name, "chown0", "(JII)V"))(jni.Env, jni.Ref(Class), h.Address, uid, gid);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void lchown0(long pathAddress, int uid, int gid)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckChange(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, int, int, void>)JniForward.Resolve(ref lchown0Ptr, CallerID, Name, "lchown0", "(JII)V"))(jni.Env, jni.Ref(Class), h.Address, uid, gid);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void chmod0(long pathAddress, int mode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckChange(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, long, int, void>)JniForward.Resolve(ref chmod0Ptr, CallerID, Name, "chmod0", "(JI)V"))(jni.Env, jni.Ref(Class), h.Address, mode);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void fchmodat0(int fd, long pathAddress, int mode, int flag)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = ResolveAt(fd, pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckChange(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, long, int, int, void>)JniForward.Resolve(ref fchmodat0Ptr, CallerID, Name, "fchmodat0", "(IJII)V"))(jni.Env, jni.Ref(Class), fd, h.Address, mode, flag);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe void utimensat0(int fd, long pathAddress, long times0, long times1, int flags)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = ResolveAt(fd, pathAddress);
            if (p.IsVfs)
                throw NewUnixException(JVM.Vfs.CheckChange(p));

            using var h = HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                ((delegate* unmanaged<nint, nint, int, long, long, long, int, void>)JniForward.Resolve(ref utimensat0Ptr, CallerID, Name, "utimensat0", "(IJJJI)V"))(jni.Env, jni.Ref(Class), fd, h.Address, times0, times1, flags);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

    /// <summary>
    /// Implements 'sun.nio.fs.LinuxWatchService.inotifyAddWatch' for the directories of IKVM's virtual file system,
    /// which inotify does not know of: they never change, so each is watched by a watch descriptor no event carries
    /// (negative, which inotify's never are, and not -1, which is an invalid key's). Removing one fails, which
    /// LinuxWatchService ignores. Libnio's watches the rest.
    /// </summary>
    static class LinuxWatchService
    {

#if FIRST_PASS == false

        static global::ikvm.@internal.CallerID callerID;
        static nint inotifyAddWatchPtr;

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::sun.nio.fs.LinuxWatchService).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::sun.nio.fs.LinuxWatchService).TypeHandle);

        const string Name = "sun/nio/fs/LinuxWatchService";

#endif

        public static unsafe int inotifyAddWatch(int fd, long pathAddress, int mask)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = UnixNativeDispatcher.Resolve(pathAddress);
            if (p.IsVfs)
            {
                if (JVM.Vfs.GetEntry(p) is null)
                    throw UnixNativeDispatcher.NewUnixException(VfsStatus.NotFound);

                return (int)-Math.Min(JVM.Vfs.GetFileId(p) + 1, int.MaxValue);
            }

            using var h = UnixNativeDispatcher.HostPath.Of(p, pathAddress);
            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, int, long, int, int>)JniForward.Resolve(ref inotifyAddWatchPtr, CallerID, Name, "inotifyAddWatch", "(IJI)I"))(jni.Env, jni.Ref(Class), fd, h.Address, mask);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

}
