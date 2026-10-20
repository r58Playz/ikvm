using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

using IKVM.Runtime.Extensions;

namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// Collection of mounted virtual file systems.
    /// </summary>
    internal class VfsTable
    {

        /// <summary>
        /// Builds the default IKVM mount table.
        /// </summary>
        /// <returns></returns>
        public static VfsTable BuildDefaultTable(VfsContext context, string ikvmHome)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (context is null)
                throw new ArgumentNullException(nameof(context));

            if (Directory.Exists(ikvmHome) == false)
                throw new DirectoryNotFoundException("Could not locate ikvm.home when establishing VFS.");

            var table = new VfsTable(context);
            table.AddMount(Path.Combine(ikvmHome, "assembly"), new VfsAssemblyDirectory(context));
            return table;
#endif
        }

        readonly VfsContext context;
        readonly LinkedList<VfsMount> mounts = new LinkedList<VfsMount>();
        readonly ConcurrentDictionary<string, long> fileIds = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        long nextFileId;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="context"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public VfsTable(VfsContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Gets the <see cref="VfsContext"/> which hosts this table.
        /// </summary>
        public VfsContext Context => context;

        /// <summary>
        /// Gets the mounted virtual file systems.
        /// </summary>
        public IReadOnlyCollection<VfsMount> Mounts => mounts;

        /// <summary>
        /// Adds the a new mount to the table.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="item"></param>
        /// <returns></returns>
        public VfsMount AddMount(string path, VfsEntry item)
        {
            return mounts.AddFirst(new VfsMount(path, item)).Value;
        }

        /// <summary>
        /// Removes the given mount from the table.
        /// </summary>
        /// <param name="path"></param>
        public void RemoveMount(string path)
        {
            var mount = mounts.FirstOrDefault(i => i.Path == path);
            if (mount != null)
                mounts.Remove(mount);
        }

        /// <summary>
        /// Gets the mount for the given path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsMount GetMount(string path) => Resolve(path).Mount;

        /// <summary>
        /// Returns <c>true</c> if the given path is a mounted virtual path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public bool IsPath(string path) => Resolve(path).IsVfs;

        /// <summary>
        /// Resolves a path as the host walks it: a relative path against the current directory, and '.' and '..' as
        /// they come. There are no links in the virtual file system, so within it they resolve lexically.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsPath Resolve(string path)
        {
            if (string.IsNullOrEmpty(path))
                return new VfsPath(null, path, false);

            // the common case: an absolute path without '.' or '..', which the host walks as written
            if (IsNormalAbsolute(path))
            {
                var mount = FindMount(path);
                return mount != null ? new VfsPath(mount, TrimEndingSeparator(path), false) : new VfsPath(null, path, false);
            }

            string full;
            try
            {
                full = TrimEndingSeparator(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return new VfsPath(null, path, false);
            }

            var m = FindMount(full);
            if (m != null)
                return new VfsPath(m, full, false);

            // a path that enters the virtual file system and leaves it by '..' is the host's, but the host cannot
            // walk it as written
            if (EntersMount(path))
                return new VfsPath(null, full, true);

            return new VfsPath(null, path, false);
        }

        /// <summary>
        /// Gets the mount the given absolute, normalized path is in.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        VfsMount FindMount(string path)
        {
            foreach (var mount in mounts)
                if (mount.IsPath(path))
                    return mount;

            return null;
        }

        /// <summary>
        /// Returns <c>true</c> if a directory the host walks through on its way to the given path, which has a '..', is
        /// in the virtual file system.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        bool EntersMount(string path)
        {
            if (HasSegment(path, "..") == false)
                return false;

            try
            {
                var full = Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
                for (int i = 1; i < full.Length; i++)
                    if (IsSeparator(full[i]) && IsSeparator(full[i - 1]) == false)
                        if (FindMount(TrimEndingSeparator(Path.GetFullPath(full.Substring(0, i)))) != null)
                            return true;
            }
            catch (Exception)
            {
                // a path the host fails on
            }

            return false;
        }

        static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

        /// <summary>
        /// Returns <c>true</c> if the given path is absolute and has no '.', '..' or empty segment (a trailing
        /// separator aside).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        static bool IsNormalAbsolute(string path)
        {
            if (Path.IsPathRooted(path) == false)
                return false;

            var start = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i < path.Length && IsSeparator(path[i]) == false)
                    continue;

                var length = i - start;
                if (length == 0 && i > 0 && i < path.Length)
                    return false;
                if (length == 1 && path[start] == '.')
                    return false;
                if (length == 2 && path[start] == '.' && path[start + 1] == '.')
                    return false;

                start = i + 1;
            }

            return true;
        }

        /// <summary>
        /// Returns <c>true</c> if one of the segments of the given path is the given name.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        static bool HasSegment(string path, string name)
        {
            var start = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i < path.Length && IsSeparator(path[i]) == false)
                    continue;

                if (i - start == name.Length && string.CompareOrdinal(path, start, name, 0, name.Length) == 0)
                    return true;

                start = i + 1;
            }

            return false;
        }

        /// <summary>
        /// Removes the trailing separators of a path, but not those of its root.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        static string TrimEndingSeparator(string path)
        {
            var n = path.Length;
            if (n < 2 || IsSeparator(path[n - 1]) == false)
                return path;

            var root = Path.GetPathRoot(path)?.Length ?? 0;
            while (n > 1 && n > root && IsSeparator(path[n - 1]))
                n--;

            return path.Substring(0, n);
        }

        /// <summary>
        /// Gets the entry for the given path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsEntry GetEntry(string path) => GetEntry(Resolve(path));

        /// <summary>
        /// Gets the entry for the given resolved path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsEntry GetEntry(VfsPath path) => path.IsVfs ? path.Mount.GetEntry(path.Mount.GetRelativePath(path.Path)) : null;

        /// <summary>
        /// Gets the entry of the directory the given resolved path is in, if that is in the virtual file system.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        VfsEntry GetParentEntry(VfsPath path)
        {
            if (path.IsVfs == false)
                return null;

            var i = path.Path.Length - 1;
            while (i > 0 && IsSeparator(path.Path[i]) == false)
                i--;
            if (i <= 0)
                return null;

            var parent = path.Path.Substring(0, i);
            var mount = FindMount(parent);
            return mount != null ? mount.GetEntry(mount.GetRelativePath(parent)) : null;
        }

        /// <summary>
        /// Gets an identifier of the given resolved path of the virtual file system, unique among its entries and
        /// stable for the life of the table (st_ino: a file key, which is what ZipFile caches by and isSameFile compares).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public long GetFileId(VfsPath path)
        {
            if (fileIds.TryGetValue(path.Path, out var id))
                return id;

            return fileIds.GetOrAdd(path.Path, Interlocked.Increment(ref nextFileId));
        }

        /// <summary>
        /// Gets the names of the entries within the directory.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public string[] List(string path) => List(Resolve(path));

        /// <summary>
        /// Gets the names of the entries within the directory at the given resolved path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public string[] List(VfsPath path) => GetEntry(path) is VfsDirectory directory ? directory.List() : null;

        /// <summary>
        /// Opens a file at the specified path.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <returns></returns>
        /// <exception cref="IOException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        public Stream Open(string path, FileMode mode, FileAccess access) => Open(Resolve(path), mode, access);

        /// <summary>
        /// Opens a file at the specified resolved path.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <returns></returns>
        /// <exception cref="IOException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        public Stream Open(VfsPath path, FileMode mode, FileAccess access)
        {
            // VFS is currently completely read-only
            if (mode != FileMode.Open || access != FileAccess.Read)
                throw new UnauthorizedAccessException("Virtual file system is read-only.");

            // search for the entry in the file system and open it
            return GetEntry(path) switch
            {
                VfsFile file => file.Open(mode, access) ?? throw new FileNotFoundException("File not found.", path.Path),
                VfsDirectory => throw new UnauthorizedAccessException($"Access to '{path.Path}' was denied."),
                _ => throw new FileNotFoundException("File not found.", path.Path)
            };
        }

        /// <summary>
        /// Gets the length of the file at the specified path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public long GetLength(string path) => GetLength(Resolve(path));

        /// <summary>
        /// Gets the length of the file at the specified resolved path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public long GetLength(VfsPath path) => GetEntry(path) is VfsFile entry ? entry.Size : 0;

        /// <summary>
        /// Gets the boolean attributes of the specified path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public int GetBooleanAttributes(string path) => GetBooleanAttributes(Resolve(path));

        /// <summary>
        /// Gets the boolean attributes of the specified resolved path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public int GetBooleanAttributes(VfsPath path)
        {
            const int BA_EXISTS = 0x01;
            const int BA_REGULAR = 0x02;
            const int BA_DIRECTORY = 0x04;

            return GetEntry(path) switch
            {
                VfsDirectory => BA_EXISTS | BA_DIRECTORY,
                VfsFile => BA_EXISTS | BA_REGULAR,
                _ => 0,
            };
        }

        /// <summary>
        /// Checks the creation of an entry at the given resolved path (mkdir, mknod, symlink, open with O_CREAT).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsStatus CheckCreate(VfsPath path) => GetEntry(path) != null ? VfsStatus.Exists : CheckParent(path);

        /// <summary>
        /// Checks an entry to be created at the given resolved path, which does not exist, by the directory it would be
        /// in: the file system being read-only is only reached once that is found.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        VfsStatus CheckParent(VfsPath path) => GetParentEntry(path) switch
        {
            VfsDirectory => VfsStatus.ReadOnly,
            VfsFile => VfsStatus.NotDirectory,
            _ => VfsStatus.NotFound,
        };

        /// <summary>
        /// Checks a change to the entry at the given resolved path (its mode, owner or times).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsStatus CheckChange(VfsPath path) => GetEntry(path) != null ? VfsStatus.ReadOnly : VfsStatus.NotFound;

        /// <summary>
        /// Checks the removal of the entry at the given resolved path: by rmdir if <paramref name="directory"/>, else
        /// by unlink.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="directory"></param>
        /// <returns></returns>
        public VfsStatus CheckRemove(VfsPath path, bool directory) => GetEntry(path) switch
        {
            null => VfsStatus.NotFound,
            VfsDirectory => directory ? VfsStatus.ReadOnly : VfsStatus.IsDirectory,
            _ => directory ? VfsStatus.NotDirectory : VfsStatus.ReadOnly,
        };

        /// <summary>
        /// Checks opening the entry at the given resolved path. <see cref="VfsStatus.Ok"/> is a file or a directory
        /// that a read-only file system lets be opened as asked.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="write"></param>
        /// <param name="create"></param>
        /// <param name="exclusive"></param>
        /// <param name="truncate"></param>
        /// <returns></returns>
        public VfsStatus CheckOpen(VfsPath path, bool write, bool create, bool exclusive, bool truncate)
        {
            var entry = GetEntry(path);
            if (entry == null)
                return create ? CheckParent(path) : VfsStatus.NotFound;
            if (create && exclusive)
                return VfsStatus.Exists;
            if (entry is VfsDirectory && write)
                return VfsStatus.IsDirectory;
            if (write || truncate)
                return VfsStatus.ReadOnly;

            return VfsStatus.Ok;
        }

        /// <summary>
        /// Checks opening the directory at the given resolved path for reading its entries (opendir).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsStatus CheckOpenDirectory(VfsPath path) => GetEntry(path) switch
        {
            null => VfsStatus.NotFound,
            VfsDirectory => VfsStatus.Ok,
            _ => VfsStatus.NotDirectory,
        };

        /// <summary>
        /// Checks reading the given resolved path as a symbolic link: there are none.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public VfsStatus CheckReadLink(VfsPath path) => GetEntry(path) != null ? VfsStatus.NotLink : VfsStatus.NotFound;

        /// <summary>
        /// Checks the hard link of a new path to an existing one, at least one of which is in the virtual file system.
        /// </summary>
        /// <param name="existing"></param>
        /// <param name="link"></param>
        /// <returns></returns>
        public VfsStatus CheckLink(VfsPath existing, VfsPath link)
        {
            if (existing.IsVfs && GetEntry(existing) == null)
                return VfsStatus.NotFound;

            if (link.IsVfs)
            {
                var status = CheckCreate(link);
                if (status != VfsStatus.ReadOnly)
                    return status;
            }

            return existing.IsVfs != link.IsVfs ? VfsStatus.CrossDevice : VfsStatus.ReadOnly;
        }

        /// <summary>
        /// Checks the rename of a path to another, at least one of which is in the virtual file system.
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <returns></returns>
        public VfsStatus CheckRename(VfsPath from, VfsPath to)
        {
            if (from.IsVfs && GetEntry(from) == null)
                return VfsStatus.NotFound;

            if (to.IsVfs && GetEntry(to) == null)
            {
                var status = CheckParent(to);
                if (status != VfsStatus.ReadOnly)
                    return status;
            }

            return from.IsVfs != to.IsVfs ? VfsStatus.CrossDevice : VfsStatus.ReadOnly;
        }

        /// <summary>
        /// Gets the path within the virtual file system where the given assembly is mapped.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <param name="ikvmHome"></param>
        /// <returns></returns>
        public static string GetAssemblyClassesPath(VfsContext context, Assembly assembly, string ikvmHome)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (assembly is null)
                throw new ArgumentNullException(nameof(assembly));

            if (Directory.Exists(ikvmHome) == false)
                throw new DirectoryNotFoundException("Could not locate IkvmHome when finding VFS.");

            return PathExtensions.EnsureEndingDirectorySeparator(Path.Combine(ikvmHome, "assembly", GetAssemblyDirectoryName(context, assembly), "classes"));
#endif
        }

        /// <summary>
        /// Gets the path within the default virtual file system where the given assembly resources are mapped.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <param name="ikvmHome"></param>
        /// <returns></returns>
        public static string GetAssemblyResourcesPath(VfsContext context, Assembly assembly, string ikvmHome)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (assembly is null)
                throw new ArgumentNullException(nameof(assembly));

            if (Directory.Exists(ikvmHome) == false)
                throw new DirectoryNotFoundException("Could not locate IkvmHome when finding VFS.");

            return PathExtensions.EnsureEndingDirectorySeparator(Path.Combine(ikvmHome, "assembly", GetAssemblyDirectoryName(context, assembly), "resources"));
#endif
        }

        /// <summary>
        /// Gets the path within the default virtual file system where the module-info.class files of the JPMS modules
        /// the given assembly holds are mapped, each in the directory named after its module.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <param name="ikvmHome"></param>
        /// <returns></returns>
        public static string GetAssemblyModulesPath(VfsContext context, Assembly assembly, string ikvmHome)
        {
#if FIRST_PASS || IMPORTER || EXPORTER
            throw new NotImplementedException();
#else
            if (assembly is null)
                throw new ArgumentNullException(nameof(assembly));

            if (Directory.Exists(ikvmHome) == false)
                throw new DirectoryNotFoundException("Could not locate IkvmHome when finding VFS.");

            return PathExtensions.EnsureEndingDirectorySeparator(Path.Combine(ikvmHome, "assembly", GetAssemblyDirectoryName(context, assembly), "modules"));
#endif
        }

        /// <summary>
        /// Returns <c>true</c> if the assembly is loadable by name.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="assembly"></param>
        /// <returns></returns>
        static bool IsLoadableAssembly(VfsContext context, Assembly assembly)
        {
            if (assembly.ReflectionOnly)
                return false;

#if NETFRAMEWORK
            if (assembly.GlobalAssemblyCache)
                return true;
#endif

            if (assembly.IsDynamic || assembly.Location == "")
                return false;

#if NETFRAMEWORK
            // optimization for the common case where the assembly was loaded from the base directory
            if (Path.GetDirectoryName(assembly.Location) == Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory))
                return true;
#endif

            // attempt to load the assembly from the context
            if (context.GetAssembly(assembly.GetName()) != null)
                return true;

            return false;
        }

        /// <summary>
        /// Gets the dirctory name of the given assembly.
        /// </summary>
        /// <param name="assembly"></param>
        /// <returns></returns>
        public static string GetAssemblyDirectoryName(VfsContext context, Assembly assembly)
        {
            // if we can't load the assembly by name, then use the MVID as the directory
            if (IsLoadableAssembly(context, assembly) == false)
                return assembly.ManifestModule.ModuleVersionId.ToString("N");

            var name = assembly.GetName();
            var simpleName = name.Name;
            var path = new ValueStringBuilder(simpleName.Length + 4);
            for (var i = 0; i < simpleName.Length; i++)
            {
                if (simpleName[i] == '_')
                    path.Append("_!");
                else
                    path.Append(simpleName[i]);
            }

            var publicKeyToken = name.GetPublicKeyToken();
            if (publicKeyToken != null && publicKeyToken.Length != 0)
            {
                var v = name.Version.ToString();
                path.EnsureCapacity(2 + v.Length + 2 + publicKeyToken.Length * 2);

                path.Append("__");
                path.Append(v);
                path.Append("__");
                for (int i = 0; i < publicKeyToken.Length; i++)
                    path.Append(string.Format("{0:x2}", publicKeyToken[i]));
            }

            if (name.CultureInfo != null && !string.IsNullOrEmpty(name.CultureInfo.Name))
            {
                var n = name.CultureInfo.Name;
                path.EnsureCapacity(2 + n.Length);
                path.Append("__");
                path.Append(name.CultureInfo.Name);
            }

            return path.ToString();
        }

    }

}
