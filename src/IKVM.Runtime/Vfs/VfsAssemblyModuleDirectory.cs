using System;
using System.Linq;
using System.Reflection;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Represents a virtual directory holding, in a directory named after each JPMS module the assembly holds the
    /// module-info.class of, that module as an exploded run-time image has it: its module-info.class, and the stubs of
    /// its classes and its resources in the directories of its packages. That of the class library is the run-time
    /// image's modules directory, which jrt:/ reads.
    /// </summary>
    internal sealed class VfsAssemblyModuleDirectory : VfsDirectory
    {

        readonly Assembly assembly;

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

        /// <summary>
        /// Gets the directory of the named module.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public override VfsEntry GetEntry(string name)
        {
            var modules = Context.GetAssemblyModules(assembly);
            return modules.GetModule(name) is { } module ? new VfsAssemblyModulePackageDirectory(Context, modules, module, "") : null;
        }

        /// <summary>
        /// Gets the names of the modules.
        /// </summary>
        /// <returns></returns>
        public override string[] List() => Context.GetAssemblyModules(assembly).ModuleNames.ToArray();

    }

}
