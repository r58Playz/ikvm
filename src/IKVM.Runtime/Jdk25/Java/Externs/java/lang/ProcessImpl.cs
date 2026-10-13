using System;
using System.Diagnostics;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang;
using IKVM.Runtime.JNI;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.ProcessImpl' (Unix) that the runtime takes part in.
    /// </summary>
    static class ProcessImpl
    {

#if FIRST_PASS == false

        static ProcessImplAccessor processImplAccessor;
        static global::ikvm.@internal.CallerID callerID;
        static nint initPtr;

        static ProcessImplAccessor ProcessImplAccessor => JVM.Internal.BaseAccessors.Get(ref processImplAccessor);

        static global::ikvm.@internal.CallerID CallerID => callerID ??= global::ikvm.@internal.CallerID.create(ProcessImplAccessor.Type.TypeHandle);

        const string Name = "java/lang/ProcessImpl";

#endif

        /// <summary>
        /// Implements the native method 'init', libjava's, which sets the SIGCHLD handler to SIG_DFL. .NET reaps its
        /// child processes in a handler of its own, so the handler is restored afterwards: else no
        /// System.Diagnostics.Process started after the first Java process ever sees its process exit. This isn't thread
        /// safe but should suffice since it's limited to static initialization.
        /// </summary>
        public static unsafe void init()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var restore = OperatingSystem.IsWindows() == false && OperatingSystem.IsBrowser() == false;

            // get size of sigaction structure and allocate space
            var save = stackalloc byte[restore ? LibIkvm.Instance.sig_get_size_sigaction() : 0];
            Debug.Assert(restore == false || save != null);

            // save old SIGCHLD structure
            if (restore && LibIkvm.Instance.sig_get_chld_action(save) != 0)
                throw new InternalException("Could not save SIGCHLD handler.");

            try
            {
                var jni = JniForward.Enter(CallerID);
                try
                {
                    ((delegate* unmanaged<nint, nint, void>)JniForward.Resolve(ref initPtr, CallerID, Name, "init", "()V"))(jni.Env, jni.Ref(global::ikvm.runtime.Util.getClassFromTypeHandle(ProcessImplAccessor.Type.TypeHandle)));
                }
                finally
                {
                    jni.Leave();
                }
            }
            finally
            {
                // restore old SIGCHLD structure
                if (restore && LibIkvm.Instance.sig_set_chld_action(save) != 0)
                    throw new InternalException("Could not restore SIGCHLD handler.");
            }
#endif
        }

    }

}
