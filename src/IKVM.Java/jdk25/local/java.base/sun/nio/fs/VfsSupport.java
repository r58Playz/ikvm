/*
  Copyright (C) 2026 Jeroen Frijters, Jerome Haltom

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.
*/

package sun.nio.fs;

import java.io.FileDescriptor;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.DirectoryIteratorException;
import java.nio.file.DirectoryStream;
import java.nio.file.FileStore;
import java.nio.file.FileSystemException;
import java.nio.file.NoSuchFileException;
import java.nio.file.NotDirectoryException;
import java.nio.file.Path;
import java.nio.file.attribute.BasicFileAttributeView;
import java.nio.file.attribute.FileAttributeView;
import java.nio.file.attribute.FileOwnerAttributeView;
import java.nio.file.attribute.FileStoreAttributeView;
import java.nio.file.attribute.PosixFileAttributeView;
import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.concurrent.TimeUnit;

import jdk.internal.misc.Unsafe;
import sun.nio.ch.DirectBuffer;
import sun.nio.ch.VfsFileChannel;

import static sun.nio.fs.UnixConstants.*;

/**
 * IKVM: what the Unix file system provider does with a path of IKVM's virtual file system that libnio cannot do, as it
 * needs a file descriptor of the file: open a channel of it, read the entries of a directory of it, copy a file of it,
 * and tell the file store it is in. map.xml has the provider call these first; each returns null (or false) for a path
 * of the host, which the provider then goes on with. The runtime's UnixNativeDispatcher natives do the rest.
 */
final class VfsSupport {

    // the kinds of path kind0 tells apart
    private static final int HOST = -1;
    private static final int MISSING = 0;
    private static final int FILE = 1;
    private static final int DIRECTORY = 2;

    // the size of the buffer a file is copied through
    private static final int BUFFER_SIZE = 16384;

    private static final Unsafe unsafe = Unsafe.getUnsafe();

    private VfsSupport() {
    }

    /**
     * UnixChannelFactory.open: opens a file of the virtual file system, as open(2) of a read-only file system does,
     * for a channel: the file descriptor returned holds the stream of the file, which FileChannelImpl.open and
     * SimpleAsynchronousFileChannelImpl.open make a VfsFileChannel of. Returns null for a path of the host.
     */
    static FileDescriptor open(int dfd, UnixPath path, UnixChannelFactory.Flags flags) throws UnixException {
        // a path relative to a directory of the host is the host's
        if (dfd >= 0 && !path.isAbsolute())
            return null;

        // UnixChannelFactory.open's flags
        int oflags;
        if (flags.read && flags.write) {
            oflags = O_RDWR;
        } else {
            oflags = (flags.write) ? O_WRONLY : O_RDONLY;
        }
        if (flags.write) {
            if (flags.truncateExisting)
                oflags |= O_TRUNC;
            if (flags.createNew) {
                oflags |= (O_CREAT | O_EXCL);
            } else if (flags.create) {
                oflags |= O_CREAT;
            }
        }

        FileDescriptor fd = new FileDescriptor();
        int errno = open0(fd, path.getByteArrayForSysCalls(), oflags);
        if (errno < 0)
            return null;
        if (errno > 0)
            throw new UnixException(errno);

        return fd;
    }

    /**
     * UnixFileSystemProvider.newDirectoryStream: a stream of the entries of a directory of the virtual file system, or
     * null for a path of the host.
     */
    static DirectoryStream<Path> newDirectoryStream(Path obj, DirectoryStream.Filter<? super Path> filter) throws IOException {
        UnixPath dir = UnixPath.toUnixPath(obj);
        if (filter == null)
            return null;

        byte[] path = dir.getByteArrayForSysCalls();
        switch (kind0(path)) {
            case HOST:
                return null;
            case MISSING:
                throw new NoSuchFileException(dir.getPathForExceptionMessage());
            case FILE:
                throw new NotDirectoryException(dir.getPathForExceptionMessage());
        }

        String[] names = list0(path);
        return new VfsDirectoryStream(dir, (names != null) ? names : new String[0], filter);
    }

    /**
     * UnixFileSystem.copyFile: copies a file of the virtual file system, as copyFile does, but reading the file
     * through its stream. Returns false for a file of the host.
     */
    static boolean copyFile(UnixPath source, UnixFileAttributes attrs, UnixPath target, UnixFileSystem.Flags flags, long addressToPollForCancel) throws IOException {
        FileDescriptor fi = new FileDescriptor();
        int errno = open0(fi, source.getByteArrayForSysCalls(), O_RDONLY);
        if (errno < 0)
            return false;
        if (errno > 0)
            new UnixException(errno).rethrowAsIOException(source);

        try (FileChannel in = VfsFileChannel.open(fi, source.toString(), true, false, null)) {
            // open new file
            int fo = -1;
            try {
                fo = UnixNativeDispatcher.open(target, (O_WRONLY | O_CREAT | O_EXCL), attrs.mode());
            } catch (UnixException x) {
                if (x.errno() == EEXIST && flags.replaceExisting)
                    throw new FileSystemException(target.toString());
                x.rethrowAsIOException(target);
            }

            // set to true when file and attributes copied
            boolean complete = false;
            try {
                transfer(in, fo, source, target, addressToPollForCancel);

                // copy owner/permissions
                if (flags.copyPosixAttributes) {
                    try {
                        UnixNativeDispatcher.fchown(fo, attrs.uid(), attrs.gid());
                        UnixNativeDispatcher.fchmod(fo, attrs.mode());
                    } catch (UnixException x) {
                        if (flags.failIfUnableToCopyPosix)
                            x.rethrowAsIOException(target);
                    }
                }

                // the virtual file system has no attributes but POSIX's

                // copy time attributes
                if (flags.copyBasicAttributes) {
                    try {
                        UnixNativeDispatcher.futimens(fo,
                            attrs.lastAccessTime().to(TimeUnit.NANOSECONDS),
                            attrs.lastModifiedTime().to(TimeUnit.NANOSECONDS));
                    } catch (UnixException x) {
                        if (flags.failIfUnableToCopyBasic)
                            x.rethrowAsIOException(target);
                    }
                }
                complete = true;
            } finally {
                UnixNativeDispatcher.close(fo, e -> null);

                // copy of file or attributes failed so rollback
                if (!complete) {
                    try {
                        UnixNativeDispatcher.unlink(target);
                    } catch (UnixException ignore) { }
                }
            }
        }
        return true;
    }

    // writes what remains of the channel to the file descriptor, as bufferedCopy0 does
    private static void transfer(FileChannel in, int fo, UnixPath source, UnixPath target, long addressToPollForCancel) throws IOException {
        ByteBuffer buf = sun.nio.ch.Util.getTemporaryDirectBuffer(BUFFER_SIZE);
        try {
            long address = ((DirectBuffer) buf).address();
            for (;;) {
                if (addressToPollForCancel != 0 && unsafe.getIntVolatile(null, addressToPollForCancel) != 0)
                    throw new FileSystemException(source.getPathForExceptionMessage(), target.getPathForExceptionMessage(), "Operation canceled");

                buf.clear();
                int n = in.read(buf);
                if (n < 0)
                    break;

                int off = 0;
                while (off < n) {
                    try {
                        off += UnixNativeDispatcher.write(fo, address + off, n - off);
                    } catch (UnixException x) {
                        x.rethrowAsIOException(source, target);
                    }
                }
            }
        } finally {
            sun.nio.ch.Util.releaseTemporaryDirectBuffer(buf);
        }
    }

    /**
     * UnixFileSystemProvider.getFileStore: the file store of the virtual file system a path of it is in, or null for
     * a path of the host.
     */
    static FileStore getFileStore(Path obj) throws IOException {
        UnixPath file = UnixPath.toUnixPath(obj);
        byte[] path = file.getByteArrayForSysCalls();
        String mount = mount0(path);
        if (mount == null)
            return null;
        if (kind0(path) == MISSING)
            throw new NoSuchFileException(file.getPathForExceptionMessage());

        return new VfsFileStore(mount);
    }

    /**
     * The entries of a directory of the virtual file system, which never change: UnixDirectoryStream's behavior, over
     * a list of their names.
     */
    private static final class VfsDirectoryStream implements DirectoryStream<Path> {

        private final UnixPath dir;
        private final String[] names;
        private final DirectoryStream.Filter<? super Path> filter;
        private volatile boolean closed;
        private Iterator<Path> iterator;

        VfsDirectoryStream(UnixPath dir, String[] names, DirectoryStream.Filter<? super Path> filter) {
            this.dir = dir;
            this.names = names;
            this.filter = filter;
        }

        @Override
        public void close() {
            closed = true;
        }

        @Override
        public synchronized Iterator<Path> iterator() {
            if (closed)
                throw new IllegalStateException("Directory stream is closed");
            if (iterator != null)
                throw new IllegalStateException("Iterator already obtained");

            iterator = new Iterator<>() {

                private int index;
                private Path next;

                @Override
                public boolean hasNext() {
                    if (next != null)
                        return true;

                    // a closed stream has no more entries
                    while (!closed && index < names.length) {
                        Path entry = dir.resolve(names[index++]);
                        try {
                            if (filter.accept(entry)) {
                                next = entry;
                                return true;
                            }
                        } catch (IOException ioe) {
                            throw new DirectoryIteratorException(ioe);
                        }
                    }
                    return false;
                }

                @Override
                public Path next() {
                    if (!hasNext())
                        throw new NoSuchElementException();
                    Path result = next;
                    next = null;
                    return result;
                }

            };
            return iterator;
        }

    }

    /**
     * The file store of a mount of the virtual file system: read-only, with no space, and the attributes stat(2) of it
     * gives (UnixFileStore's views).
     */
    private static final class VfsFileStore extends FileStore {

        private final String mount;

        VfsFileStore(String mount) {
            this.mount = mount;
        }

        @Override
        public String name() {
            return "ikvm";
        }

        @Override
        public String type() {
            return "ikvm";
        }

        @Override
        public boolean isReadOnly() {
            return true;
        }

        @Override
        public long getTotalSpace() {
            return 0;
        }

        @Override
        public long getUsableSpace() {
            return 0;
        }

        @Override
        public long getUnallocatedSpace() {
            return 0;
        }

        @Override
        public boolean supportsFileAttributeView(Class<? extends FileAttributeView> type) {
            if (type == null)
                throw new NullPointerException();
            return type == BasicFileAttributeView.class || type == PosixFileAttributeView.class || type == FileOwnerAttributeView.class;
        }

        @Override
        public boolean supportsFileAttributeView(String name) {
            return name.equals("basic") || name.equals("posix") || name.equals("unix") || name.equals("owner");
        }

        @Override
        public <V extends FileStoreAttributeView> V getFileStoreAttributeView(Class<V> type) {
            if (type == null)
                throw new NullPointerException();
            return null;
        }

        @Override
        public Object getAttribute(String attribute) throws IOException {
            if (attribute.equals("totalSpace") || attribute.equals("usableSpace") || attribute.equals("unallocatedSpace"))
                return 0L;
            throw new UnsupportedOperationException("'" + attribute + "' not recognized");
        }

        @Override
        public boolean equals(Object ob) {
            return (ob instanceof VfsFileStore other) && mount.equals(other.mount);
        }

        @Override
        public int hashCode() {
            return mount.hashCode();
        }

        @Override
        public String toString() {
            return mount + " (ikvm)";
        }

    }

    // -- Native methods --

    // HOST, MISSING, FILE or DIRECTORY
    private static native int kind0(byte[] path);

    // the names of the entries of a directory
    private static native String[] list0(byte[] path);

    // the root of the mount a path is in, or null if it is the host's
    private static native String mount0(byte[] path);

    // opens a file for the file descriptor with open(2)'s flags: -1 for a path of the host, else 0 or the errno
    private static native int open0(FileDescriptor fd, byte[] path, int flags);

}
