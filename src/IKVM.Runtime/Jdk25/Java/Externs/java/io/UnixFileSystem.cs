using System;
using System.IO;

using IKVM.Runtime;
using IKVM.Runtime.JNI;
using IKVM.Runtime.Vfs;

namespace IKVM.Java.Externs.java.io
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.io.UnixFileSystem': the paths of IKVM's virtual file system (which
    /// is read-only) here, the file system's with libjava's natives.
    /// </summary>
    static class UnixFileSystem
    {

        const int ACCESS_WRITE = 0x02;

#if FIRST_PASS == false

        static global::ikvm.@internal.CallerID callerID;
        static nint initIDsPtr, canonicalize0Ptr, getBooleanAttributes0Ptr, checkAccess0Ptr, getLastModifiedTime0Ptr, getLength0Ptr;
        static nint setPermission0Ptr, createFileExclusively0Ptr, delete0Ptr, list0Ptr, createDirectory0Ptr, rename0Ptr;
        static nint setLastModifiedTime0Ptr, setReadOnly0Ptr, getSpace0Ptr, getNameMax0Ptr;

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(typeof(global::java.io.File).TypeHandle);

        static object Class => global::ikvm.runtime.Util.getClassFromTypeHandle(typeof(global::java.io.UnixFileSystem).TypeHandle);

        const string Name = "java/io/UnixFileSystem";

        static VfsPath Resolve(object f) => JVM.Vfs.Resolve(((global::java.io.File)f).getPath());

        /// <summary>
        /// Gets the file to give libjava: the original, unless the virtual file system rewrote its path.
        /// </summary>
        static object HostFile(VfsPath p, object f) => p.Rewritten ? new global::java.io.File(p.Path) : f;

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

        public static unsafe string canonicalize0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the virtual file system has no links
            var p = JVM.Vfs.Resolve(path);
            if (p.IsVfs)
                return p.Path;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return (string)jni.Unwrap(((delegate* unmanaged<nint, nint, nint, nint>)JniForward.Resolve(ref canonicalize0Ptr, CallerID, Name, "canonicalize0", "(Ljava/lang/String;)Ljava/lang/String;"))(jni.Env, jni.Ref(self), jni.Ref(p.Path)));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe int getBooleanAttributes0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return JVM.Vfs.GetBooleanAttributes(p);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int>)JniForward.Resolve(ref getBooleanAttributes0Ptr, CallerID, Name, "getBooleanAttributes0", "(Ljava/io/File;)I"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool checkAccess0(object self, object f, int access)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
            {
                // read, and execute (search) of a directory
                return JVM.Vfs.GetEntry(p) switch
                {
                    VfsDirectory => access != ACCESS_WRITE,
                    VfsFile => access != ACCESS_WRITE && access != 0x01,
                    _ => false,
                };
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int, byte>)JniForward.Resolve(ref checkAccess0Ptr, CallerID, Name, "checkAccess0", "(Ljava/io/File;I)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)), access) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getLastModifiedTime0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return 0;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, long>)JniForward.Resolve(ref getLastModifiedTime0Ptr, CallerID, Name, "getLastModifiedTime0", "(Ljava/io/File;)J"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getLength0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return JVM.Vfs.GetLength(p);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, long>)JniForward.Resolve(ref getLength0Ptr, CallerID, Name, "getLength0", "(Ljava/io/File;)J"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool setPermission0(object self, object f, int access, bool enable, bool owneronly)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int, byte, byte, byte>)JniForward.Resolve(ref setPermission0Ptr, CallerID, Name, "setPermission0", "(Ljava/io/File;IZZ)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)), access, enable ? (byte)1 : (byte)0, owneronly ? (byte)1 : (byte)0) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool createFileExclusively0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // false if the file exists, as libjava's (O_EXCL fails with EEXIST), else the error the read-only file system reports
            var p = JVM.Vfs.Resolve(path);
            if (p.IsVfs)
            {
                var status = JVM.Vfs.CheckCreate(p);
                if (status == VfsStatus.Exists)
                    return false;

                throw new global::java.io.IOException(status.GetMessage());
            }

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, byte>)JniForward.Resolve(ref createFileExclusively0Ptr, CallerID, Name, "createFileExclusively0", "(Ljava/lang/String;)Z"))(jni.Env, jni.Ref(self), jni.Ref(p.Path)) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool delete0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, byte>)JniForward.Resolve(ref delete0Ptr, CallerID, Name, "delete0", "(Ljava/io/File;)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f))) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe string[] list0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return JVM.Vfs.List(p);

            var jni = JniForward.Enter(CallerID);
            try
            {
                return (string[])jni.Unwrap(((delegate* unmanaged<nint, nint, nint, nint>)JniForward.Resolve(ref list0Ptr, CallerID, Name, "list0", "(Ljava/io/File;)[Ljava/lang/String;"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f))));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool createDirectory0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, byte>)JniForward.Resolve(ref createDirectory0Ptr, CallerID, Name, "createDirectory0", "(Ljava/io/File;)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f))) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool rename0(object self, object f1, object f2)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p1 = Resolve(f1);
            var p2 = Resolve(f2);
            if (p1.IsVfs || p2.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, nint, byte>)JniForward.Resolve(ref rename0Ptr, CallerID, Name, "rename0", "(Ljava/io/File;Ljava/io/File;)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p1, f1)), jni.Ref(HostFile(p2, f2))) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool setLastModifiedTime0(object self, object f, long time)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, long, byte>)JniForward.Resolve(ref setLastModifiedTime0Ptr, CallerID, Name, "setLastModifiedTime0", "(Ljava/io/File;J)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)), time) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe bool setReadOnly0(object self, object f)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return false;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, byte>)JniForward.Resolve(ref setReadOnly0Ptr, CallerID, Name, "setReadOnly0", "(Ljava/io/File;)Z"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f))) != 0;
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getSpace0(object self, object f, int t)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = Resolve(f);
            if (p.IsVfs)
                return 0;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, int, long>)JniForward.Resolve(ref getSpace0Ptr, CallerID, Name, "getSpace0", "(Ljava/io/File;I)J"))(jni.Env, jni.Ref(self), jni.Ref(HostFile(p, f)), t);
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

        public static unsafe long getNameMax0(object self, string path)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var p = JVM.Vfs.Resolve(path);
            if (p.IsVfs)
                return 255;

            var jni = JniForward.Enter(CallerID);
            try
            {
                return ((delegate* unmanaged<nint, nint, nint, long>)JniForward.Resolve(ref getNameMax0Ptr, CallerID, Name, "getNameMax0", "(Ljava/lang/String;)J"))(jni.Env, jni.Ref(self), jni.Ref(p.Path));
            }
            finally
            {
                jni.Leave();
            }
#endif
        }

    }

}
