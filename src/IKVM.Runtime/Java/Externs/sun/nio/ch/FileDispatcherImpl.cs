namespace IKVM.Java.Externs.sun.nio.ch
{

    /// <summary>
    /// Managed implementation of select <c>sun.nio.ch.FileDispatcherImpl</c> native methods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>force0</c> (the back-end of <see cref="java.nio.channels.FileChannel.force(bool)"/>) is
    /// intercepted; every other native method falls through to the original libnio implementation, so
    /// ordinary channel I/O is unaffected.
    /// </para>
    /// <para>
    /// In the WASM build every writable file lives on either the OPFS-backed WasmFS mount (<c>/libsdl</c>)
    /// or an in-memory MEMFS mount. The stock <c>force0</c> maps to <c>fdatasync</c> -&gt; WasmFS
    /// <c>__wasi_fd_sync</c> -&gt; <c>OPFSFile::flush</c> -&gt; <c>FileSystemSyncAccessHandle.flush()</c>,
    /// which forces bytes through to physical storage and is the single most expensive OPFS primitive. It
    /// is what stalls Minecraft's chunk save/close path (every cached region file is <c>force(true)</c>'d on
    /// flush and again on close, serialized on the single OPFS proxy thread).
    /// </para>
    /// <para>
    /// The flush is also unnecessary here: OPFS sync-access-handle writes are already durable without an
    /// explicit <c>flush()</c> (it only forces through to the device, which the browser/OS manage), and
    /// MEMFS has no backing store at all. So this no-ops the sync and returns success, removing the stall
    /// with no in-browser durability loss.
    /// </para>
    /// </remarks>
    static class FileDispatcherImpl
    {

        /// <summary>
        /// Implements the native method 'force0'. Returns 0 (success) without issuing an fsync/fdatasync.
        /// </summary>
        /// <param name="fd">the file descriptor to sync (unused)</param>
        /// <param name="metaData">whether file metadata should also be synced (unused)</param>
        /// <returns>0, indicating success</returns>
        public static int force0(object fd, bool metaData)
        {
            return 0;
        }

    }

}
