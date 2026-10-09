using System.Runtime.CompilerServices;

using IKVM.Runtime.JNI;

namespace IKVM.Runtime
{

    public static partial class JVM
    {

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

        /// <summary>
        /// Initializes the JDK 25 class library, in the order HotSpot's Threads::create_vm does: the core classes, the
        /// system and main thread groups and the main thread, then System.initPhase1 (system properties, standard
        /// streams, reference handler), initPhase2 (the module system) and initPhase3 (the system class loader).
        /// </summary>
        static void InitClassLibrary()
        {
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.String).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.System).TypeHandle);

            // the VM creates and returns objects of this class
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.Class).TypeHandle);

            // the thread that initializes the VM is the main thread
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.ThreadGroup).TypeHandle);
            var mainThreadGroup = MainThreadGroup;
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.Thread).TypeHandle);
            IKVM.Java.Externs.java.lang.Thread.Attach(mainThreadGroup, "main", false);

            // the VM creates objects of this class, and preresolves methods to these
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.Module).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.reflect.Method).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.@ref.Finalizer).TypeHandle);

            Internal.SystemAccessor.InvokeInitPhase1();

            // initialize certain classes after System properties are available
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.OutOfMemoryError).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.NullPointerException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.ClassCastException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.ArrayStoreException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.ArithmeticException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.ArrayIndexOutOfBoundsException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.StackOverflowError).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.IllegalMonitorStateException).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.IllegalArgumentException).TypeHandle);

            if (Internal.SystemAccessor.InvokeInitPhase2(true, true) != 0)
                throw new java.lang.InternalError("Initialization of the module system failed");

            // the java.lang.invoke core classes
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MethodHandle).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MemberName).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(java.lang.invoke.MethodHandleNatives).TypeHandle);

            Internal.SystemAccessor.InvokeInitPhase3();
        }

        /// <summary>
        /// Calls libjava's InitializeEncoding, which it exports for the VM: libjava's natives convert strings with the
        /// platform's encoding (sun.jnu.encoding). It initializes Charset, so it must not run before System's
        /// initPhase1 has set JavaLangAccess.
        /// </summary>
        /// <param name="encoding"></param>
        internal static unsafe void InitializeLibJavaEncoding(string encoding)
        {
            var initializeEncoding = LibJvm.Instance.JVM_FindLibraryEntry(LibJava.Instance.Handle, "InitializeEncoding");
            if (initializeEncoding == 0)
                throw new InternalException("libiava has no InitializeEncoding.");

            var name = System.Text.Encoding.UTF8.GetBytes(encoding + "\0");
            var frame = new JNIFrame();
            var env = frame.Enter(ikvm.@internal.CallerID.create(typeof(java.lang.System).TypeHandle));
            try
            {
                fixed (byte* p = name)
                    ((delegate* unmanaged<nint, byte*, void>)initializeEncoding)(env, p);
            }
            finally
            {
                frame.Leave();
            }
        }

#endif

    }

}
