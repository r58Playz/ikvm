using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// The entries of the JARs ikvmc stores in an assembly, those its JavaModuleAttributes name: the stubs of its
    /// classes and its resources, by name. Each JAR is read once and kept open, and of a name the first JAR that has it
    /// gives the entry.
    /// </summary>
    internal sealed class VfsAssemblyJars
    {

        readonly VfsContext context;
        readonly Assembly assembly;
        readonly Lazy<Index> index;

        /// <summary>
        /// The entries of the JARs, by name and by the directory they are in.
        /// </summary>
        sealed class Index
        {

            public readonly Dictionary<string, ZipArchiveEntry> Entries = new(StringComparer.Ordinal);

            public readonly Dictionary<string, List<string>> Directories = new(StringComparer.Ordinal);

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public VfsAssemblyJars(VfsContext context, Assembly assembly)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            this.index = new Lazy<Index>(CreateIndex, true);
        }

        /// <summary>
        /// Reads the central directories of the JARs.
        /// </summary>
        /// <returns></returns>
        Index CreateIndex()
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            var index = new Index();
            if (assembly.IsDynamic)
                return index;

            foreach (var module in assembly.GetModules(false))
            {
                foreach (IKVM.Attributes.JavaModuleAttribute javaModule in context.Context.AttributeHelper.GetJavaModuleAttributes(module))
                {
                    foreach (var jar in javaModule.Jars ?? Array.Empty<string>())
                    {
                        var stream = assembly.GetManifestResourceStream(jar);
                        if (stream == null)
                            continue;

                        foreach (var entry in new ZipArchive(stream, ZipArchiveMode.Read, false).Entries)
                        {
                            // a directory has no entry of its own
                            var name = entry.FullName;
                            if (name.Length == 0 || name[name.Length - 1] == '/' || index.Entries.ContainsKey(name))
                                continue;

                            index.Entries.Add(name, entry);

                            var i = name.LastIndexOf('/');
                            var directory = i < 0 ? "" : name.Substring(0, i);
                            if (index.Directories.TryGetValue(directory, out var names) == false)
                                index.Directories.Add(directory, names = new List<string>());

                            names.Add(name);
                        }
                    }
                }
            }

            return index;
#endif
        }

        /// <summary>
        /// Returns <c>true</c> if a JAR has an entry of the given name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool Contains(string name) => index.Value.Entries.ContainsKey(name);

        /// <summary>
        /// Gets the uncompressed length of the entry of the given name, or -1 if there is none.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public long GetLength(string name) => index.Value.Entries.TryGetValue(name, out var entry) ? entry.Length : -1;

        /// <summary>
        /// Gets the names of the entries in the given directory ('/' separated, "" being the root), each the full name.
        /// </summary>
        /// <param name="directory"></param>
        /// <returns></returns>
        public IReadOnlyList<string> GetEntries(string directory) => index.Value.Directories.TryGetValue(directory, out var names) ? names : Array.Empty<string>();

        /// <summary>
        /// Gets the directories that have entries ('/' separated, "" being the root).
        /// </summary>
        public IEnumerable<string> Directories => index.Value.Directories.Keys;

        /// <summary>
        /// Reads the entry of the given name, or returns <c>null</c> if there is none.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public byte[] Read(string name)
        {
            if (index.Value.Entries.TryGetValue(name, out var entry) == false)
                return null;

            // a JAR reads its entries through its one stream
            lock (entry.Archive)
            {
                using var input = entry.Open();
                var buffer = new byte[entry.Length];
                var n = 0;
                while (n < buffer.Length)
                {
                    var r = input.Read(buffer, n, buffer.Length - n);
                    if (r <= 0)
                        throw new EndOfStreamException($"Entry '{name}' of a JAR of '{assembly.FullName}' ends early.");

                    n += r;
                }

                return buffer;
            }
        }

    }

}
