using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Represents a virtual directory holding, in a directory named after each JPMS module the assembly holds the
    /// module-info.class of, that module-info.class.
    /// </summary>
    internal sealed class VfsAssemblyModuleDirectory : VfsDirectory
    {

        readonly Assembly assembly;
        Dictionary<string, string> moduleInfos;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        internal VfsAssemblyModuleDirectory(VfsContext context, Assembly assembly) :
            base(context)
        {
            this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        }

#if !FIRST_PASS && !IMPORTER && !EXPORTER

        /// <summary>
        /// The manifest resources holding the module-info.class files of the assembly, by module: those the
        /// JavaModuleAttribute of each of its own modules names. (The class loader of an assembly in a shared class
        /// loader group is that of another assembly.)
        /// </summary>
        Dictionary<string, string> ModuleInfos
        {
            get
            {
                if (moduleInfos == null)
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var module in assembly.GetModules(false))
                        foreach (IKVM.Attributes.JavaModuleAttribute javaModule in Context.Context.AttributeHelper.GetJavaModuleAttributes(module))
                            if (javaModule.ModuleInfos is string[] infos)
                                for (int i = 0; i + 1 < infos.Length; i += 2)
                                    if (assembly.GetManifestResourceInfo(infos[i + 1]) != null)
                                        map[infos[i]] = infos[i + 1];

                    moduleInfos = map;
                }

                return moduleInfos;
            }
        }

#endif

        /// <summary>
        /// Gets the directory of the named module.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public override VfsEntry GetEntry(string name)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (ModuleInfos.TryGetValue(name, out var resource) == false)
                return null;

            var d = new VfsEntryDirectory(Context);
            d.AddEntry("module-info.class", new VfsAssemblyResourceFile(Context, assembly, resource));
            return d;
#endif
        }

        /// <summary>
        /// Gets the names of the modules.
        /// </summary>
        /// <returns></returns>
        public override string[] List()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            return ModuleInfos.Keys.ToArray();
#endif
        }

    }

}
