namespace IKVM.Runtime.Vfs
{

    /// <summary>
    /// A path as the host walks it, resolved against the mounts of a <see cref="VfsTable"/>.
    /// </summary>
    internal readonly struct VfsPath
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="mount"></param>
        /// <param name="path"></param>
        /// <param name="rewritten"></param>
        public VfsPath(VfsMount mount, string path, bool rewritten)
        {
            Mount = mount;
            Path = path;
            Rewritten = rewritten;
        }

        /// <summary>
        /// Gets the mount the path is in, or <c>null</c> if the path is the host's.
        /// </summary>
        public VfsMount Mount { get; }

        /// <summary>
        /// Gets the path: for a path of the virtual file system, absolute and normalized; for a path of the host, the
        /// one to give the host, which is the original unless <see cref="Rewritten"/>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets whether the path is in the virtual file system.
        /// </summary>
        public bool IsVfs => Mount != null;

        /// <summary>
        /// Gets whether the path is the host's but walks through the virtual file system, leaving it by '..': the host
        /// cannot walk the original, as the mount point does not exist there, so it must be given <see cref="Path"/>.
        /// </summary>
        public bool Rewritten { get; }

    }

}
