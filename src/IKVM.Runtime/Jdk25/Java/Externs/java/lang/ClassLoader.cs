using System;

using IKVM.ByteCode;
using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.ClassLoader'.
    /// </summary>
    static class ClassLoader
    {

        // the flags of defineClass0 (java.lang.invoke.MethodHandleNatives.Constants)
        const int NESTMATE_CLASS = 0x00000001;
        const int HIDDEN_CLASS = 0x00000002;

#if FIRST_PASS == false

        static ClassAccessor classAccessor;

        static ClassAccessor ClassAccessor => JVM.Internal.BaseAccessors.Get(ref classAccessor);

#endif

        public static void registerNatives()
        {

        }

        public static global::java.lang.Class defineClass1(global::java.lang.ClassLoader loader, string name, byte[] b, int off, int len, global::java.security.ProtectionDomain pd, string source)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var buf = new byte[len];
            Array.Copy(b, off, buf, 0, len);
            return DefineClass(loader, name, ReadClass(buf), pd, source);
#endif
        }

        public static global::java.lang.Class defineClass2(global::java.lang.ClassLoader loader, string name, global::java.nio.ByteBuffer bb, int off, int len, global::java.security.ProtectionDomain pd, string source)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var buf = new byte[len];
            bb.get(off, buf);
            return DefineClass(loader, name, ReadClass(buf), pd, source);
#endif
        }

        /// <summary>
        /// Implements the native method 'defineClass0', with which Lookup defines classes, hidden ones included.
        /// </summary>
        public static global::java.lang.Class defineClass0(global::java.lang.ClassLoader loader, global::java.lang.Class lookup, string name, byte[] b, int off, int len, global::java.security.ProtectionDomain pd, bool initialize, int flags, object classData)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var buf = new byte[len];
            Array.Copy(b, off, buf, 0, len);

            global::java.lang.Class clazz;
            if ((flags & HIDDEN_CLASS) != 0)
            {
                // a hidden class is defined like a VM anonymous class with the lookup class as its host, which gives it
                // access to the host's private members (a superset of a nestmate's)
                try
                {
                    var host = RuntimeJavaType.FromClass(lookup);
                    var cl = host.ClassLoader;
                    var cf = new IKVM.Runtime.ClassFile(JVM.Context, JVM.Context.Diagnostics, ReadClass(buf), name ?? "<Unknown>", cl.ClassFileParseOptions, null);
                    var type = cl.GetTypeWrapperFactory().DefineClassImpl(null, host, cf, cl, pd ?? lookup.pd);

                    // Java's own checks of private access go by nests: a nestmate joins the lookup class's
                    if ((flags & NESTMATE_CLASS) != 0 && type is RuntimeByteCodeJavaType hidden)
                        hidden.SetHiddenNestmateOf(host);

                    clazz = type.ClassObject;
                }
                catch (RetargetableJavaException e)
                {
                    throw e.ToJava();
                }
            }
            else
            {
                clazz = DefineClass(loader, name, ReadClass(buf), pd, null);
            }

            if (classData != null)
                ClassAccessor.SetClassData(clazz, classData);

            if (initialize)
            {
                var type = RuntimeJavaType.FromClass(clazz);
                type.Finish();
                type.RunClassInit();
            }

            return clazz;
#endif
        }

#if FIRST_PASS == false

        /// <summary>
        /// Attempts to read the class, handling exceptions.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        static IKVM.ByteCode.Decoding.ClassFile ReadClass(byte[] buffer)
        {
            try
            {
                return IKVM.ByteCode.Decoding.ClassFile.Read(buffer);
            }
            catch (InvalidClassMagicException)
            {
                throw new global::java.lang.ClassFormatError("Incompatible magic value");
            }
            catch (ByteCodeException e)
            {
                throw new global::java.lang.ClassFormatError(e.Message);
            }
        }

        /// <summary>
        /// Defines a new class with the specified information.
        /// </summary>
        static global::java.lang.Class DefineClass(global::java.lang.ClassLoader loader, string name, IKVM.ByteCode.Decoding.ClassFile clazz, global::java.security.ProtectionDomain pd, string source)
        {
            // a binary name, or (from Lookup.defineClass) an internal one
            name = name?.Replace('/', '.');

            try
            {
                var runtimeClassLoader = JVM.Context.ClassLoaderFactory.GetClassLoaderWrapper(loader);
                var classFile = new IKVM.Runtime.ClassFile(JVM.Context, JVM.Context.Diagnostics, clazz, name, runtimeClassLoader.ClassFileParseOptions, null);
                if (name != null && classFile.Name != name)
                    throw new global::java.lang.NoClassDefFoundError(name + " (wrong name: " + classFile.Name + ")");

                var type = runtimeClassLoader.DefineClass(classFile, pd);
                if (type == null)
                    throw new InternalException();

                return type.ClassObject;
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
        }

#endif

        public static global::java.lang.Class findBootstrapClass(string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return JVM.Context.ClassLoaderFactory.GetBootstrapClassLoader().TryLoadClassByName(name)?.ClassObject;
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

        public static global::java.lang.Class findLoadedClass0(global::java.lang.ClassLoader self, string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return name != null ? (JVM.Context.ClassLoaderFactory.GetClassLoaderWrapper(self).FindLoadedClass(name)?.ClassObject) : null;
#endif
        }

        public static object retrieveDirectives()
        {
            return IKVM.Runtime.Assertions.RetrieveDirectives();
        }

    }

}
