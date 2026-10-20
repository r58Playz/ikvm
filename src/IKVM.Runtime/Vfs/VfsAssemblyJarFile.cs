using System;
using System.IO;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Represents an entry of the JARs ikvmc stores in an assembly: the stub of a class, or a resource.
    /// </summary>
    internal sealed class VfsAssemblyJarFile : VfsFile
    {

        readonly VfsAssemblyJars jars;
        readonly string name;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="jars"></param>
        /// <param name="name"></param>
        /// <exception cref="ArgumentNullException"></exception>
        internal VfsAssemblyJarFile(VfsContext context, VfsAssemblyJars jars, string name) :
            base(context)
        {
            this.jars = jars ?? throw new ArgumentNullException(nameof(jars));
            this.name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>
        /// Opens the entry.
        /// </summary>
        /// <returns></returns>
        protected override Stream OpenRead() => new MemoryStream(jars.Read(name) ?? throw new FileNotFoundException("File not found.", name), false);

        /// <summary>
        /// Gets the length of the entry, which the JAR's central directory has.
        /// </summary>
        public override long Size => Math.Max(jars.GetLength(name), 0);

    }

}
