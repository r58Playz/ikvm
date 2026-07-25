using System;
using System.IO;
using System.IO.Compression;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Represents an assembly class file within the virtual file system.
    /// </summary>
    internal sealed class VfsAssemblyClassFile : VfsFile
    {

        readonly RuntimeJavaType type;
        readonly Lazy<byte[]> buff;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="type"></param>
        internal VfsAssemblyClassFile(VfsContext context, RuntimeJavaType type) :
            base(context)
        {
            this.type = type ?? throw new ArgumentNullException(nameof(type));
            this.buff = new Lazy<byte[]>(GenerateClassFile, true);
        }

        /// <summary>
        /// Gets the class file.
        /// </summary>
        /// <returns></returns>
        byte[] GenerateClassFile()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var stored = ReadStoredClassFile();
            if (stored != null)
                return stored;

            var stream = new MemoryStream();
            Context.Context.StubGenerator.Write(stream, type, true, true, true, true, false);
            return stream.ToArray();
#endif
        }

#if !FIRST_PASS && !IMPORTER && !EXPORTER

        /// <summary>
        /// Reads a pre-generated stub from one of the JAR resources embedded by ikvmc.
        /// </summary>
        byte[] ReadStoredClassFile()
        {
            if (type.ClassLoader is not RuntimeAssemblyClassLoader loader)
                return null;

            var assembly = loader.GetAssembly(type);
            var classFile = type.Name.Replace('.', '/') + ".class";

            foreach (var module in assembly.GetModules(false))
            {
                foreach (IKVM.Attributes.JavaModuleAttribute javaModule in Context.Context.AttributeHelper.GetJavaModuleAttributes(module))
                {
                    foreach (var jar in javaModule.Jars ?? Array.Empty<string>())
                    {
                        using var resource = assembly.GetManifestResourceStream(jar);
                        if (resource == null)
                            continue;

                        using var archive = new ZipArchive(resource, ZipArchiveMode.Read, false);
                        var entry = archive.GetEntry(classFile);
                        if (entry == null)
                            continue;

                        using var input = entry.Open();
                        using var output = new MemoryStream(entry.Length > int.MaxValue ? 0 : (int)entry.Length);
                        input.CopyTo(output);
                        return output.ToArray();
                    }
                }
            }

            return null;
        }

#endif

        /// <summary>
        /// Opens the class file.
        /// </summary>
        /// <returns></returns>
        protected override Stream OpenRead() => new MemoryStream(buff.Value);

        /// <summary>
        /// Gets the length of the class file.
        /// </summary>
        public override long Size => buff.Value.Length;

    }

}
