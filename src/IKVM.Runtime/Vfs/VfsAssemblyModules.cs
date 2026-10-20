using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// The JPMS modules an assembly holds, each as an exploded run-time image has it: its module-info.class, and in the
    /// directory of each of its packages the stubs of its classes and its resources, which the JARs ikvmc stores in the
    /// assembly have. A class the JARs have no stub of, a remapped class, is stubbed from its type.
    /// </summary>
    internal sealed class VfsAssemblyModules
    {

        /// <summary>
        /// A JPMS module of the assembly.
        /// </summary>
        internal sealed class JpmsModule
        {

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="name"></param>
            /// <param name="moduleInfo"></param>
            /// <param name="packages"></param>
            public JpmsModule(string name, string moduleInfo, HashSet<string> packages)
            {
                Name = name ?? throw new ArgumentNullException(nameof(name));
                ModuleInfo = moduleInfo ?? throw new ArgumentNullException(nameof(moduleInfo));
                Packages = packages ?? throw new ArgumentNullException(nameof(packages));

                // the directories on the way to each package, and the package's own
                Directories = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [""] = new HashSet<string>(StringComparer.Ordinal) };
                foreach (var package in packages)
                {
                    var parent = "";
                    foreach (var segment in package.Split('/'))
                    {
                        Directories[parent].Add(segment);
                        parent = parent.Length == 0 ? segment : parent + "/" + segment;
                        if (Directories.ContainsKey(parent) == false)
                            Directories.Add(parent, new HashSet<string>(StringComparer.Ordinal));
                    }
                }
            }

            /// <summary>
            /// Gets the name of the module.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// Gets the name of the manifest resource holding the module's module-info.class.
            /// </summary>
            public string ModuleInfo { get; }

            /// <summary>
            /// Gets the packages of the module, '/' separated.
            /// </summary>
            public HashSet<string> Packages { get; }

            /// <summary>
            /// Gets the subdirectories of each directory of the module ('/' separated, "" being the module's own): those
            /// on the way to a package.
            /// </summary>
            public Dictionary<string, HashSet<string>> Directories { get; }

        }

        readonly VfsContext context;
        readonly Assembly assembly;
        readonly Lazy<Dictionary<string, JpmsModule>> modules;
        readonly Lazy<Dictionary<string, string>> unstoredClasses;
        readonly ConcurrentDictionary<string, VfsEntry> unstoredClassFiles = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public VfsAssemblyModules(VfsContext context, Assembly assembly)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            this.modules = new Lazy<Dictionary<string, JpmsModule>>(CreateModules, true);
            this.unstoredClasses = new Lazy<Dictionary<string, string>>(CreateUnstoredClasses, true);
        }

        /// <summary>
        /// Gets the assembly.
        /// </summary>
        public Assembly Assembly => assembly;

        /// <summary>
        /// Gets the names of the modules.
        /// </summary>
        public IEnumerable<string> ModuleNames => modules.Value.Keys;

        /// <summary>
        /// Gets the module of the given name, or <c>null</c>.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public JpmsModule GetModule(string name) => modules.Value.TryGetValue(name, out var module) ? module : null;

        /// <summary>
        /// Reads the modules: those whose module-info.class the JavaModuleAttribute of each of the assembly's own modules
        /// names (the class loader of an assembly in a shared class loader group is that of another assembly), with the
        /// packages their module-info.class has. Those of the one module of an assembly are also all those its JARs have,
        /// as a module-info.class compiled by javac lists only those it exports and opens.
        /// </summary>
        /// <returns></returns>
        Dictionary<string, JpmsModule> CreateModules()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var infos = new Dictionary<string, string>(StringComparer.Ordinal);
            if (assembly.IsDynamic == false)
                foreach (var module in assembly.GetModules(false))
                    foreach (IKVM.Attributes.JavaModuleAttribute javaModule in context.Context.AttributeHelper.GetJavaModuleAttributes(module))
                        if (javaModule.ModuleInfos is string[] list)
                            for (int i = 0; i + 1 < list.Length; i += 2)
                                if (assembly.GetManifestResourceInfo(list[i + 1]) != null)
                                    infos[list[i]] = list[i + 1];

            var map = new Dictionary<string, JpmsModule>(StringComparer.Ordinal);
            foreach (var info in infos)
            {
                var packages = new HashSet<string>(ReadPackages(info.Value), StringComparer.Ordinal);
                if (infos.Count == 1)
                    foreach (var directory in context.GetAssemblyJars(assembly).Directories)
                        if (directory.Length > 0 && directory != "META-INF" && directory.StartsWith("META-INF/", StringComparison.Ordinal) == false)
                            packages.Add(directory);

                map.Add(info.Key, new JpmsModule(info.Key, info.Value, packages));
            }

            return map;
#endif
        }

        /// <summary>
        /// Reads the packages of a module from its module-info.class, '/' separated: those its ModulePackages attribute
        /// has (as jlink and the jar tool write it), and those it exports and opens.
        /// </summary>
        /// <param name="resource"></param>
        /// <returns></returns>
        IEnumerable<string> ReadPackages(string resource)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                using var clazz = IKVM.ByteCode.Decoding.ClassFile.Read(buffer.ToArray());
                return IKVM.CoreLib.Modules.ModuleDescriptor.Read(clazz).Packages.Select(i => i.Replace('.', '/')).ToList();
            }
            catch (Exception)
            {
                // a module-info.class that cannot be read has no packages here, as the class loader does not read it
                return Array.Empty<string>();
            }
#endif
        }

        /// <summary>
        /// Finds the classes the JARs have no stub of: the remapped classes, which map.xml defines. By the name of their
        /// class file.
        /// </summary>
        /// <returns></returns>
        Dictionary<string, string> CreateUnstoredClasses()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var jars = context.GetAssemblyJars(assembly);
            foreach (var remapped in context.Context.AttributeHelper.GetRemappedClasses(assembly))
            {
                var file = remapped.Name.Replace('.', '/') + ".class";
                if (jars.Contains(file) == false)
                    map[file] = remapped.Name;
            }

            return map;
#endif
        }

        /// <summary>
        /// Gets the names of the files in the directory of the given package.
        /// </summary>
        /// <param name="package"></param>
        /// <returns></returns>
        public IEnumerable<string> ListFiles(string package)
        {
            foreach (var name in context.GetAssemblyJars(assembly).GetEntries(package))
                yield return name.Substring(package.Length + 1);

            foreach (var file in unstoredClasses.Value.Keys)
                if (file.Length > package.Length + 1 && file[package.Length] == '/' && file.StartsWith(package, StringComparison.Ordinal) && file.IndexOf('/', package.Length + 1) < 0)
                    yield return file.Substring(package.Length + 1);
        }

        /// <summary>
        /// Gets the file of the given name ('/' separated) in the directory of its package, or <c>null</c>.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public VfsEntry GetFile(string name)
        {
            var jars = context.GetAssemblyJars(assembly);
            if (jars.Contains(name))
                return new VfsAssemblyJarFile(context, jars, name);

            if (unstoredClasses.Value.TryGetValue(name, out var className))
                return unstoredClassFiles.GetOrAdd(name, _ => CreateClassFile(className));

            return null;
        }

        /// <summary>
        /// Creates the file of a class the JARs have no stub of, which is stubbed from its type. It holds its stub once
        /// written, and so is kept.
        /// </summary>
        /// <param name="className"></param>
        /// <returns></returns>
        VfsEntry CreateClassFile(string className)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            try
            {
                var tw = context.Context.AssemblyClassLoaderFactory.FromAssembly(assembly).TryLoadClassByName(className);
                return tw != null && tw.IsArray == false ? new VfsAssemblyClassFile(context, tw) : null;
            }
            catch
            {
                return null;
            }
#endif
        }

    }

}
