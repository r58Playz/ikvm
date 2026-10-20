namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// The outcome of an operation on a path of the virtual file system, as a read-only file system mounted on the host
    /// would report it. Each corresponds to an errno.
    /// </summary>
    internal enum VfsStatus
    {

        /// <summary>
        /// The operation succeeds.
        /// </summary>
        Ok,

        /// <summary>
        /// ENOENT: the path, or a directory of it, does not exist.
        /// </summary>
        NotFound,

        /// <summary>
        /// ENOTDIR: a directory of the path is a file.
        /// </summary>
        NotDirectory,

        /// <summary>
        /// EISDIR: the path is a directory, where a file is required.
        /// </summary>
        IsDirectory,

        /// <summary>
        /// EEXIST: the path to create exists.
        /// </summary>
        Exists,

        /// <summary>
        /// EROFS: the operation would change the file system.
        /// </summary>
        ReadOnly,

        /// <summary>
        /// EXDEV: the operation links or renames between the virtual file system and the host's.
        /// </summary>
        CrossDevice,

        /// <summary>
        /// EINVAL: the path is not a symbolic link.
        /// </summary>
        NotLink,

        /// <summary>
        /// ENXIO: the entry exists, but has no file descriptor to give the host's natives.
        /// </summary>
        NoDescriptor,

    }

    /// <summary>
    /// Extensions of <see cref="VfsStatus"/>.
    /// </summary>
    internal static class VfsStatusExtensions
    {

        /// <summary>
        /// Gets the message of the status: glibc's strerror of its errno, which is what java.io reports.
        /// </summary>
        /// <param name="status"></param>
        /// <returns></returns>
        public static string GetMessage(this VfsStatus status) => status switch
        {
            VfsStatus.NotFound => "No such file or directory",
            VfsStatus.NotDirectory => "Not a directory",
            VfsStatus.IsDirectory => "Is a directory",
            VfsStatus.Exists => "File exists",
            VfsStatus.ReadOnly => "Read-only file system",
            VfsStatus.CrossDevice => "Invalid cross-device link",
            VfsStatus.NotLink => "Invalid argument",
            VfsStatus.NoDescriptor => "No such device or address",
            _ => "Success",
        };

    }

}
