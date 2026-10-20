using System;
using System.Linq;

using IKVM.Runtime;
using IKVM.Runtime.JNI;

namespace IKVM.Java.Externs.jdk.@internal.loader
{

    /// <summary>
    /// Implements the native methods of 'jdk.internal.loader.BootLoader'. The boot loader's classes and resources are
    /// those of its assemblies; the packages it reports are those of the named modules defined to it.
    /// </summary>
    static class BootLoader
    {

        public static string[] getSystemPackageNames()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return IKVM.Java.Externs.java.lang.Module.BootPackageNames.Select(pn => pn.Replace('.', '/')).ToArray();
#endif
        }

        public static string getSystemPackageLocation(string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // as HotSpot, the location of a package of a named module is the module's
            var module = IKVM.Java.Externs.java.lang.Module.GetBootPackageModule(name.Replace('/', '.'));
            return module != null ? "jrt:/" + module.getName() : null;
#endif
        }

        public static void setBootLoaderUnnamedModule0(global::java.lang.Module module)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            IKVM.Java.Externs.java.lang.Module.SetBootUnnamedModule(module);
#endif
        }

        public static global::java.net.URL getBootstrapResource(string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return JVM.Context.ClassLoaderFactory.GetBootstrapClassLoader().GetResources(name).FirstOrDefault();
#endif
        }

        /// <summary>
        /// Implements the native method 'getBootstrapModuleResource': a resource of the named module defined to the boot
        /// loader. Its modules' resources are all the boot loader's, but for their module-info.class, which is each
        /// module's own.
        /// </summary>
        public static global::java.net.URL getBootstrapModuleResource(string mn, string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var loader = JVM.Context.ClassLoaderFactory.GetBootstrapClassLoader();
            if (mn != null && name == "module-info.class")
                return loader.FindModuleInfo(mn);

            return loader.GetResources(name).FirstOrDefault();
#endif
        }

        public static global::java.util.Enumeration getBootstrapResources(string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return new global::ikvm.runtime.EnumerationWrapper(JVM.Context.ClassLoaderFactory.GetBootstrapClassLoader().GetResources(name));
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of 'jdk.internal.loader.NativeLibraries', loading JNI libraries through the
    /// runtime's JNI loader. No libraries are built into the runtime.
    /// </summary>
    static class NativeLibraries
    {

        public static bool load(object impl, string name, bool isBuiltin, bool throwExceptionIfFail)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var lib = (global::jdk.@internal.loader.NativeLibraries.NativeLibraryImpl)impl;
            var handle = JNINativeLoader.LoadLibrary(name, RuntimeJavaType.FromClass(lib.fromClass));
            if (handle == 0)
            {
                if (throwExceptionIfFail)
                    throw new global::java.lang.UnsatisfiedLinkError("Can't load library: " + name);

                return false;
            }

            lib.handle = handle;
            return true;
#endif
        }

        public static void unload(string name, bool isBuiltin, long handle)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // the class loader of the library is unreachable by now; JNI_OnUnload runs as the boot loader's
            if (isBuiltin == false && handle != 0)
                JNINativeLoader.UnloadLibrary(handle, JVM.Context.JavaBase.TypeOfJavaLangObject);
#endif
        }

        public static string findBuiltinLib(string name)
        {
            return null;
        }

    }

    /// <summary>
    /// Implements the native methods of 'jdk.internal.loader.NativeLibrary'.
    /// </summary>
    static class NativeLibrary
    {

        public static long findEntry0(long handle, string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return LibJvm.Instance.JVM_FindLibraryEntry((nint)handle, name);
#endif
        }

    }

}
