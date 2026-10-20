using System;
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
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var resource = Context.Context.AssemblyClassLoaderFactory.FromAssembly(assembly).GetModuleInfoResource(name);
            if (resource == null)
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
            return Context.Context.AssemblyClassLoaderFactory.FromAssembly(assembly).ModuleInfoModules.ToArray();
#endif
        }

    }

}
