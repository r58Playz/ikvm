using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Provides information to the VFS about the environment.
    /// </summary>
    internal abstract class VfsContext
    {

        readonly ConditionalWeakTable<Assembly, VfsAssemblyJars> jars = new();
        readonly ConditionalWeakTable<Assembly, VfsAssemblyModules> modules = new();

        /// <summary>
        /// Gets the runtime context that hosts the VFS.
        /// </summary>
        public abstract RuntimeContext Context { get; }

        /// <summary>
        /// Gets all of the assemblies known by the VFS.
        /// </summary>
        /// <returns></returns>
        public abstract IEnumerable<AssemblyName> GetAssemblyNames();

        /// <summary>
        /// Gets an assembly by name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public abstract Assembly GetAssembly(AssemblyName name);

        /// <summary>
        /// Gets the entries of the JARs ikvmc stores in the given assembly.
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
        public VfsAssemblyJars GetAssemblyJars(Assembly assembly) => jars.GetValue(assembly, a => new VfsAssemblyJars(this, a));

        /// <summary>
        /// Gets the JPMS modules the given assembly holds.
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
        public VfsAssemblyModules GetAssemblyModules(Assembly assembly) => modules.GetValue(assembly, a => new VfsAssemblyModules(this, a));

    }

}
