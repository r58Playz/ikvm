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
    /// Managed implementations of select <c>sun.nio.fs.UnixNativeDispatcher</c> native methods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paths within the IKVM virtual file system (e.g. <c>/ikvm/assembly/&lt;id&gt;/classes</c>) only exist in the
    /// managed <see cref="JVM.Vfs"/> table; the underlying (emscripten) file system that backs libnio knows
    /// nothing about them. <c>java.io.File</c> operations already account for this in
    /// <see cref="IKVM.Java.Externs.java.io.UnixFileSystem"/>; this class brings the same VFS awareness to the
    /// <c>java.nio.file</c> stack so that, for example, <c>Path.toRealPath</c> on a VFS path no longer fails with
    /// <c>NoSuchFileException</c>.
    /// </para>
    /// <para>
    /// Only metadata operations are intercepted: <c>realpath0</c>, <c>stat0</c>/<c>lstat0</c> and <c>access0</c>.
    /// These cover the common NIO queries (<c>Path.toRealPath</c>, <c>Files.exists</c>/<c>isDirectory</c>/
    /// <c>size</c>/<c>getLastModifiedTime</c>, <c>Files.isReadable</c>). Content reads (<c>open0</c>) and directory
    /// enumeration (<c>opendir0</c>/<c>readdir</c>) over VFS paths are deliberately left to native code: a NIO
    /// open returns an OS file descriptor that is subsequently serviced by native pread/pwrite, which cannot be
    /// backed by a managed VFS stream without also intercepting the channel/dispatcher layer. Any native method
    /// not defined here continues to bind directly to libnio.
    /// </para>
    /// <para>
    /// For paths that are not VFS paths every method falls through to the original native implementation, so
    /// ordinary file-system access is unaffected.
    /// </para>
    /// </remarks>
    static class UnixNativeDispatcher
    {

#if FIRST_PASS == false

        // standard POSIX permission bits OR'd into the synthetic mode (the read-only VFS exposes r-x for
        // directories and r-- for files); the file-type bits come from UnixConstants
        const int VfsDirectoryPermissions = 365;  // 0555
        const int VfsFilePermissions = 292;       // 0444

        // synthetic device id shared by all VFS entries; chosen to not collide with a real st_dev
        const long VfsDeviceId = 0x76667300;       // 'vfs\0'

        static UnixNativeDispatcherAccessor unixNativeDispatcherAccessor;
        static UnixFileAttributesAccessor unixFileAttributesAccessor;
        static UnixExceptionAccessor unixExceptionAccessor;
        static UnixConstantsAccessor unixConstantsAccessor;

        static UnixNativeDispatcherAccessor UnixNativeDispatcherAccessor => JVM.Internal.BaseAccessors.Get(ref unixNativeDispatcherAccessor);
        static UnixFileAttributesAccessor UnixFileAttributesAccessor => JVM.Internal.BaseAccessors.Get(ref unixFileAttributesAccessor);
        static UnixExceptionAccessor UnixExceptionAccessor => JVM.Internal.BaseAccessors.Get(ref unixExceptionAccessor);
        static UnixConstantsAccessor UnixConstantsAccessor => JVM.Internal.BaseAccessors.Get(ref unixConstantsAccessor);

        static global::ikvm.@internal.CallerID __callerID;
        delegate IntPtr __jniDelegate__realpath0(IntPtr jniEnv, IntPtr clazz, long pathAddress);
        static __jniDelegate__realpath0 __jniPtr__realpath0;
        delegate void __jniDelegate__stat0(IntPtr jniEnv, IntPtr clazz, long pathAddress, IntPtr attrs);
        static __jniDelegate__stat0 __jniPtr__stat0;
        delegate void __jniDelegate__lstat0(IntPtr jniEnv, IntPtr clazz, long pathAddress, IntPtr attrs);
        static __jniDelegate__lstat0 __jniPtr__lstat0;
        delegate void __jniDelegate__access0(IntPtr jniEnv, IntPtr clazz, long pathAddress, int amode);
        static __jniDelegate__access0 __jniPtr__access0;

        /// <summary>
        /// Reads the bytes of the NUL-terminated C string at the given native address.
        /// </summary>
        /// <remarks>
        /// <see cref="Marshal.PtrToStringUTF8(IntPtr)"/> is not available on all of the target frameworks, so the
        /// string is read by hand.
        /// </remarks>
        static unsafe byte[] ReadCStringBytes(long address)
        {
            var p = (byte*)address;
            var len = 0;
            while (p[len] != 0)
                len++;

            var bytes = new byte[len];
            Marshal.Copy((IntPtr)address, bytes, 0, len);
            return bytes;
        }

        /// <summary>
        /// Reads the NUL-terminated C string at the given native address as a managed string.
        /// </summary>
        static string ReadCString(long address) => Encoding.UTF8.GetString(ReadCStringBytes(address));

        /// <summary>
        /// Creates (but does not throw) a 'sun.nio.fs.UnixException' for the given errno.
        /// </summary>
        static Exception NewUnixException(int errno) => (Exception)UnixExceptionAccessor.Init(errno);

        /// <summary>
        /// Gets a local reference to the 'sun.nio.fs.UnixNativeDispatcher' class for use as the 'jclass' argument
        /// when invoking the original (static) native method.
        /// </summary>
        static IntPtr MakeClassLocalRef(JNIFrame jniFrm) => jniFrm.MakeLocalRef(typeof(ClassLiteral<>).MakeGenericType(UnixNativeDispatcherAccessor.Type));

        /// <summary>
        /// Populates the given 'sun.nio.fs.UnixFileAttributes' from the VFS entry at the given path, throwing a
        /// 'sun.nio.fs.UnixException' (ENOENT) if no such entry exists.
        /// </summary>
        static void FillVfsAttributes(string path, object attrs)
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
                    throw NewUnixException(UnixConstantsAccessor.ENOENT);
            }

            a.SetStNlink(attrs, 1);
            a.SetStDev(attrs, VfsDeviceId);
            // synthetic but stable (within a run) inode so that UnixFileKey identity is consistent
            a.SetStIno(attrs, (uint)path.GetHashCode());
        }

#endif

        /// <summary>
        /// Implements the native method 'realpath0'.
        /// </summary>
        /// <param name="pathAddress"></param>
        /// <returns></returns>
        public static byte[] realpath0(long pathAddress)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var bytes = ReadCStringBytes(pathAddress);
            var path = Encoding.UTF8.GetString(bytes);
            if (JVM.Vfs.IsPath(path))
            {
                // the VFS has no symbolic links and toRealPath resolves to an absolute path before calling
                // realpath, so the canonical form is the path itself provided the entry actually exists
                if (JVM.Vfs.GetEntry(path) is null)
                    throw NewUnixException(UnixConstantsAccessor.ENOENT);

                return bytes;
            }
            else
            {
                __callerID ??= global::ikvm.@internal.CallerID.create(UnixNativeDispatcherAccessor.Type.TypeHandle);
                __jniPtr__realpath0 ??= Marshal.GetDelegateForFunctionPointer<__jniDelegate__realpath0>(JNIFrame.GetFuncPtr(__callerID, "sun/nio/fs/UnixNativeDispatcher", nameof(realpath0), "(J)[B"));
                var jniFrm = new JNIFrame();
                var jniEnv = jniFrm.Enter(__callerID);
                try
                {
                    return (byte[])jniFrm.UnwrapLocalRef(__jniPtr__realpath0(jniEnv, MakeClassLocalRef(jniFrm), pathAddress));
                }
                catch (Exception ex)
                {
                    global::System.Console.WriteLine("*** exception in native code ***");
                    global::System.Console.WriteLine(ex);
                    throw;
                }
                finally
                {
                    jniFrm.Leave();
                }
            }
#endif
        }

        /// <summary>
        /// Implements the native method 'stat0'.
        /// </summary>
        /// <param name="pathAddress"></param>
        /// <param name="attrs"></param>
        public static void stat0(long pathAddress, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = ReadCString(pathAddress);
            if (JVM.Vfs.IsPath(path))
            {
                FillVfsAttributes(path, attrs);
            }
            else
            {
                __callerID ??= global::ikvm.@internal.CallerID.create(UnixNativeDispatcherAccessor.Type.TypeHandle);
                __jniPtr__stat0 ??= Marshal.GetDelegateForFunctionPointer<__jniDelegate__stat0>(JNIFrame.GetFuncPtr(__callerID, "sun/nio/fs/UnixNativeDispatcher", nameof(stat0), "(JLsun/nio/fs/UnixFileAttributes;)V"));
                var jniFrm = new JNIFrame();
                var jniEnv = jniFrm.Enter(__callerID);
                try
                {
                    __jniPtr__stat0(jniEnv, MakeClassLocalRef(jniFrm), pathAddress, jniFrm.MakeLocalRef(attrs));
                }
                catch (Exception ex)
                {
                    global::System.Console.WriteLine("*** exception in native code ***");
                    global::System.Console.WriteLine(ex);
                    throw;
                }
                finally
                {
                    jniFrm.Leave();
                }
            }
#endif
        }

        /// <summary>
        /// Implements the native method 'lstat0'.
        /// </summary>
        /// <param name="pathAddress"></param>
        /// <param name="attrs"></param>
        public static void lstat0(long pathAddress, object attrs)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = ReadCString(pathAddress);
            if (JVM.Vfs.IsPath(path))
            {
                // the VFS has no symbolic links, so lstat and stat are equivalent
                FillVfsAttributes(path, attrs);
            }
            else
            {
                __callerID ??= global::ikvm.@internal.CallerID.create(UnixNativeDispatcherAccessor.Type.TypeHandle);
                __jniPtr__lstat0 ??= Marshal.GetDelegateForFunctionPointer<__jniDelegate__lstat0>(JNIFrame.GetFuncPtr(__callerID, "sun/nio/fs/UnixNativeDispatcher", nameof(lstat0), "(JLsun/nio/fs/UnixFileAttributes;)V"));
                var jniFrm = new JNIFrame();
                var jniEnv = jniFrm.Enter(__callerID);
                try
                {
                    __jniPtr__lstat0(jniEnv, MakeClassLocalRef(jniFrm), pathAddress, jniFrm.MakeLocalRef(attrs));
                }
                catch (Exception ex)
                {
                    global::System.Console.WriteLine("*** exception in native code ***");
                    global::System.Console.WriteLine(ex);
                    throw;
                }
                finally
                {
                    jniFrm.Leave();
                }
            }
#endif
        }

        /// <summary>
        /// Implements the native method 'access0'.
        /// </summary>
        /// <param name="pathAddress"></param>
        /// <param name="amode"></param>
        public static void access0(long pathAddress, int amode)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var path = ReadCString(pathAddress);
            if (JVM.Vfs.IsPath(path))
            {
                if (JVM.Vfs.GetEntry(path) is null)
                    throw NewUnixException(UnixConstantsAccessor.ENOENT);

                // the VFS is read-only: deny write access, permit existence/read/execute checks
                if ((amode & UnixConstantsAccessor.W_OK) != 0)
                    throw NewUnixException(UnixConstantsAccessor.EACCES);
            }
            else
            {
                __callerID ??= global::ikvm.@internal.CallerID.create(UnixNativeDispatcherAccessor.Type.TypeHandle);
                __jniPtr__access0 ??= Marshal.GetDelegateForFunctionPointer<__jniDelegate__access0>(JNIFrame.GetFuncPtr(__callerID, "sun/nio/fs/UnixNativeDispatcher", nameof(access0), "(JI)V"));
                var jniFrm = new JNIFrame();
                var jniEnv = jniFrm.Enter(__callerID);
                try
                {
                    __jniPtr__access0(jniEnv, MakeClassLocalRef(jniFrm), pathAddress, amode);
                }
                catch (Exception ex)
                {
                    global::System.Console.WriteLine("*** exception in native code ***");
                    global::System.Console.WriteLine(ex);
                    throw;
                }
                finally
                {
                    jniFrm.Leave();
                }
            }
#endif
        }

    }

}
