using System;
using System.Collections.Generic;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Represents a directory of a JPMS module an assembly holds: the module's own, which has its module-info.class, that
    /// of one of its packages, which has the stubs of its classes and its resources, or one on the way to a package.
    /// </summary>
    internal sealed class VfsAssemblyModulePackageDirectory : VfsDirectory
    {

        const string ModuleInfoFileName = "module-info.class";

        readonly VfsAssemblyModules modules;
        readonly VfsAssemblyModules.JpmsModule module;
        readonly string path;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="modules"></param>
        /// <param name="module"></param>
        /// <param name="path">The path of the directory in the module, '/' separated, "" being the module's own.</param>
        /// <exception cref="ArgumentNullException"></exception>
        internal VfsAssemblyModulePackageDirectory(VfsContext context, VfsAssemblyModules modules, VfsAssemblyModules.JpmsModule module, string path) :
            base(context)
        {
            this.modules = modules ?? throw new ArgumentNullException(nameof(modules));
            this.module = module ?? throw new ArgumentNullException(nameof(module));
            this.path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>
        /// Gets the entry of the given name in the directory.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public override VfsEntry GetEntry(string name)
        {
            if (path.Length == 0 && name == ModuleInfoFileName)
                return new VfsAssemblyResourceFile(Context, modules.Assembly, module.ModuleInfo);

            var child = path.Length == 0 ? name : path + "/" + name;
            if (module.Directories.TryGetValue(path, out var directories) && directories.Contains(name))
                return new VfsAssemblyModulePackageDirectory(Context, modules, module, child);

            return module.Packages.Contains(path) ? modules.GetFile(child) : null;
        }

        /// <summary>
        /// Gets the names of the entries in the directory.
        /// </summary>
        /// <returns></returns>
        public override string[] List()
        {
            var list = new List<string>();
            if (path.Length == 0)
                list.Add(ModuleInfoFileName);

            if (module.Directories.TryGetValue(path, out var directories))
                list.AddRange(directories);

            if (module.Packages.Contains(path))
                list.AddRange(modules.ListFiles(path));

            return list.ToArray();
        }

    }

}
