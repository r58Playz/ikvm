using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using IKVM.Runtime;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of 'java.lang.Module'. The runtime does not enforce module boundaries: of a
    /// module's definition it records the packages, from which it finds the module of a class, and of its reads and
    /// exports nothing.
    /// </summary>
    static class Module
    {

#if FIRST_PASS == false

        static readonly ConcurrentDictionary<string, global::java.lang.Module> bootPackages = new();
        static readonly ConditionalWeakTable<global::java.lang.ClassLoader, ConcurrentDictionary<string, global::java.lang.Module>> packages = new();
        static global::java.lang.Module bootUnnamedModule;
        static volatile bool javaBaseDefined;

        /// <summary>
        /// Gets the packages of the named modules defined to a class loader, and their modules.
        /// </summary>
        /// <param name="loader"></param>
        /// <returns></returns>
        static ConcurrentDictionary<string, global::java.lang.Module> GetPackages(global::java.lang.ClassLoader loader)
        {
            return loader == null ? bootPackages : packages.GetValue(loader, _ => new ConcurrentDictionary<string, global::java.lang.Module>());
        }

        /// <summary>
        /// Sets the unnamed module of the boot loader.
        /// </summary>
        /// <param name="module"></param>
        internal static void SetBootUnnamedModule(global::java.lang.Module module)
        {
            bootUnnamedModule = module;
        }

        /// <summary>
        /// Gets the packages of the named modules defined to the boot loader.
        /// </summary>
        internal static ICollection<string> BootPackageNames => bootPackages.Keys;

        /// <summary>
        /// Gets the named module of the boot loader a package is in, or null.
        /// </summary>
        /// <param name="pn"></param>
        /// <returns></returns>
        internal static global::java.lang.Module GetBootPackageModule(string pn)
        {
            return bootPackages.TryGetValue(pn, out var module) ? module : null;
        }

        /// <summary>
        /// Gets the module of a class: the named module of its class loader that has its package, or else the unnamed
        /// module of its class loader. Until java.base is defined classes have no module, as with HotSpot.
        /// </summary>
        /// <param name="tw"></param>
        /// <returns></returns>
        internal static global::java.lang.Module GetModule(RuntimeJavaType tw)
        {
            if (javaBaseDefined == false)
                return null;

            // arrays are in the module of their element type, primitive types in java.base
            if (tw.IsArray)
                tw = tw.GetUltimateElementTypeWrapper();
            if (tw.IsPrimitive)
                return GetBootPackageModule("java.lang");

            var loader = tw.ClassLoader.GetJavaClassLoader();
            var i = tw.Name.LastIndexOf('.');
            var pn = i < 0 ? "" : tw.Name.Substring(0, i);
            if (GetPackages(loader).TryGetValue(pn, out var module))
                return module;

            return loader == null ? bootUnnamedModule : loader.getUnnamedModule();
        }

#endif

        public static void defineModule0(global::java.lang.Module module, bool isOpen, string version, string location, object[] pns)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var loader = module.getClassLoader();
            var map = GetPackages(loader);
            foreach (string pn in pns)
                map[pn] = module;

            if (loader == null && module.getName() == "java.base")
                javaBaseDefined = true;
#endif
        }

        public static void addReads0(global::java.lang.Module from, global::java.lang.Module to)
        {

        }

        public static void addExports0(global::java.lang.Module from, string pn, global::java.lang.Module to)
        {

        }

        public static void addExportsToAll0(global::java.lang.Module from, string pn)
        {

        }

        public static void addExportsToAllUnnamed0(global::java.lang.Module from, string pn)
        {

        }

    }

}
