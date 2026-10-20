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

package sun.nio.ch;

import java.io.Closeable;
import java.io.FileDescriptor;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.MappedByteBuffer;
import java.nio.channels.AsynchronousFileChannel;
import java.nio.channels.ClosedChannelException;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.channels.NonReadableChannelException;
import java.nio.channels.NonWritableChannelException;
import java.nio.channels.OverlappingFileLockException;
import java.nio.channels.ReadableByteChannel;
import java.nio.channels.WritableByteChannel;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;

import jdk.internal.access.JavaIOFileDescriptorAccess;
import jdk.internal.access.SharedSecrets;
import jdk.internal.misc.ExtendedMapMode;

/**
 * IKVM: the file channel of a file of IKVM's virtual file system, which libnio cannot read: its file descriptor is not
 * the host's but holds the .NET stream of the file (see the runtime's java.io natives). map.xml has FileChannelImpl.open,
 * which FileInputStream, RandomAccessFile and the Unix file system provider get their channels from, return one for such
 * a file descriptor. The virtual file system is read-only, so the channel never is writable; otherwise it behaves as
 * FileChannelImpl does.
 */
public final class VfsFileChannel extends FileChannel {

    private static final JavaIOFileDescriptorAccess fdAccess = SharedSecrets.getJavaIOFileDescriptorAccess();

    // the most read at once into a buffer that has no array
    private static final int TRANSFER_SIZE = 8192;

    // the locks held in this VM, by the file they are of (FileLockTable's role)
    private static final Map<String, List<VfsFileLock>> locks = new HashMap<>();

    private final FileDescriptor fd;
    private final boolean readable;
    private final Closeable parent;
    private final String lockKey;

    // the position is the stream's, which the parent stream shares
    private final Object positionLock = new Object();

    private VfsFileChannel(FileDescriptor fd, String path, boolean readable, Closeable parent) {
        this.fd = fd;
        this.readable = readable;
        this.parent = parent;
        String key = key0(fd);
        this.lockKey = (key != null) ? key : path;
    }

    /**
     * Returns a channel of the file of the virtual file system the file descriptor is open on, or null if the file
     * descriptor is the host's. A file of the virtual file system is never open for writing.
     */
    public static FileChannel open(FileDescriptor fd, String path, boolean readable, boolean writable, Closeable parent) {
        if (fd == null || !isVfs(fd))
            return null;

        return new VfsFileChannel(fd, path, readable, parent);
    }

    private void ensureOpen() throws IOException {
        if (!isOpen())
            throw new ClosedChannelException();
    }

    private void ensureReadable() {
        if (!readable)
            throw new NonReadableChannelException();
    }

    // reads into the buffer at the position, or at the channel's if negative
    private int readInternal(ByteBuffer dst, long position) throws IOException {
        if (dst.isReadOnly())
            throw new IllegalArgumentException("Read-only buffer");

        int len = dst.remaining();
        if (len == 0)
            return 0;

        boolean completed = false;
        try {
            begin();
            int n;
            if (dst.hasArray()) {
                n = read0(fd, dst.array(), dst.arrayOffset() + dst.position(), len, position);
                if (n > 0)
                    dst.position(dst.position() + n);
            } else {
                byte[] b = new byte[Math.min(len, TRANSFER_SIZE)];
                n = read0(fd, b, 0, b.length, position);
                if (n > 0)
                    dst.put(b, 0, n);
            }
            completed = true;
            return n;
        } finally {
            end(completed);
        }
    }

    // -- Standard channel operations --

    @Override
    protected void implCloseChannel() throws IOException {
        // release and invalidate any locks that this channel holds
        synchronized (locks) {
            List<VfsFileLock> held = locks.get(lockKey);
            if (held != null) {
                held.removeIf(l -> {
                    if (l.owner != this)
                        return false;
                    l.invalidate();
                    return true;
                });
                if (held.isEmpty())
                    locks.remove(lockKey);
            }
        }

        // close the file descriptor via the parent stream's close method, which closes this channel too (it is closed
        // already, so that does nothing)
        if (parent != null)
            parent.close();
        else
            fdAccess.close(fd);
    }

    @Override
    public int read(ByteBuffer dst) throws IOException {
        Objects.requireNonNull(dst);
        ensureOpen();
        ensureReadable();
        synchronized (positionLock) {
            return readInternal(dst, -1L);
        }
    }

    @Override
    public long read(ByteBuffer[] dsts, int offset, int length) throws IOException {
        Objects.checkFromIndexSize(offset, length, dsts.length);
        ensureOpen();
        ensureReadable();
        synchronized (positionLock) {
            long total = 0;
            for (int i = offset; i < offset + length; i++) {
                ByteBuffer dst = dsts[i];
                if (!dst.hasRemaining())
                    continue;
                int n = readInternal(dst, -1L);
                if (n < 0)
                    return (total == 0) ? -1 : total;
                total += n;
                if (dst.hasRemaining())
                    break;
            }
            return total;
        }
    }

    @Override
    public int write(ByteBuffer src) throws IOException {
        Objects.requireNonNull(src);
        ensureOpen();
        throw new NonWritableChannelException();
    }

    @Override
    public long write(ByteBuffer[] srcs, int offset, int length) throws IOException {
        Objects.checkFromIndexSize(offset, length, srcs.length);
        ensureOpen();
        throw new NonWritableChannelException();
    }

    // -- Other operations --

    @Override
    public long position() throws IOException {
        ensureOpen();
        synchronized (positionLock) {
            return position0(fd);
        }
    }

    @Override
    public FileChannel position(long newPosition) throws IOException {
        ensureOpen();
        if (newPosition < 0)
            throw new IllegalArgumentException();
        synchronized (positionLock) {
            seek0(fd, newPosition);
            return this;
        }
    }

    @Override
    public long size() throws IOException {
        ensureOpen();
        return size0(fd);
    }

    @Override
    public FileChannel truncate(long newSize) throws IOException {
        ensureOpen();
        if (newSize < 0)
            throw new IllegalArgumentException("Negative size");
        throw new NonWritableChannelException();
    }

    @Override
    public void force(boolean metaData) throws IOException {
        ensureOpen();
    }

    @Override
    public long transferTo(long position, long count, WritableByteChannel target) throws IOException {
        ensureOpen();
        if (!target.isOpen())
            throw new ClosedChannelException();
        ensureReadable();
        if ((position < 0) || (count < 0))
            throw new IllegalArgumentException();

        long sz = size();
        if (position > sz)
            return 0;
        count = Math.min(count, sz - position);

        // as FileChannelImpl transfers to an arbitrary channel
        ByteBuffer bb = ByteBuffer.allocate((int) Math.min(count, TRANSFER_SIZE));
        long tw = 0;
        long pos = position;
        try {
            while (tw < count) {
                bb.limit((int) Math.min(count - tw, TRANSFER_SIZE));
                int nr = read(bb, pos);
                if (nr <= 0)
                    break;
                bb.flip();
                int nw = target.write(bb);
                tw += nw;
                if (nw != nr)
                    break;
                pos += nw;
                bb.clear();
            }
            return tw;
        } catch (IOException x) {
            if (tw > 0)
                return tw;
            throw x;
        }
    }

    @Override
    public long transferFrom(ReadableByteChannel src, long position, long count) throws IOException {
        ensureOpen();
        if (!src.isOpen())
            throw new ClosedChannelException();
        throw new NonWritableChannelException();
    }

    @Override
    public int read(ByteBuffer dst, long position) throws IOException {
        if (dst == null)
            throw new NullPointerException();
        if (position < 0)
            throw new IllegalArgumentException("Negative position");
        ensureOpen();
        ensureReadable();
        return readInternal(dst, position);
    }

    @Override
    public int write(ByteBuffer src, long position) throws IOException {
        if (src == null)
            throw new NullPointerException();
        if (position < 0)
            throw new IllegalArgumentException("Negative position");
        ensureOpen();
        throw new NonWritableChannelException();
    }

    // -- Memory-mapped buffers --

    @Override
    public MappedByteBuffer map(MapMode mode, long position, long size) throws IOException {
        if (size > Integer.MAX_VALUE)
            throw new IllegalArgumentException("Size exceeds Integer.MAX_VALUE");
        Objects.requireNonNull(mode, "Mode is null");
        ensureOpen();
        if (position < 0L)
            throw new IllegalArgumentException("Negative position");
        if (size < 0L)
            throw new IllegalArgumentException("Negative size");
        if (position + size < 0)
            throw new IllegalArgumentException("Position + size overflow");

        // read-only is all there is; a SYNC mapping needs writeback, which the file has not
        if (mode != MapMode.READ_ONLY) {
            if (mode == MapMode.READ_WRITE || mode == MapMode.PRIVATE || mode == ExtendedMapMode.READ_WRITE_SYNC)
                throw new NonWritableChannelException();
            throw new UnsupportedOperationException();
        }
        ensureReadable();

        if (size() < position + size)
            throw new IOException("Channel not open for writing - cannot extend file to required size");

        // the file has no pages to map, but it never changes, so a copy of the region is the same thing
        ByteBuffer bb = ByteBuffer.allocateDirect((int) size);
        long pos = position;
        while (bb.hasRemaining()) {
            int n = readInternal(bb, pos);
            if (n <= 0)
                break;
            pos += n;
        }
        bb.clear();
        return (MappedByteBuffer) bb.asReadOnlyBuffer();
    }

    // -- Locks --

    @Override
    public FileLock lock(long position, long size, boolean shared) throws IOException {
        // no lock this VM holds is exclusive, and no other process sees the file, so a lock never waits
        return tryLock(null, position, size, shared);
    }

    @Override
    public FileLock tryLock(long position, long size, boolean shared) throws IOException {
        return tryLock(null, position, size, shared);
    }

    /**
     * Acquires a shared lock of a region of the file for this channel, or for the given asynchronous channel that wraps
     * this one.
     */
    FileLock tryLock(AsynchronousFileChannel async, long position, long size, boolean shared) throws IOException {
        ensureOpen();
        if (shared && !readable)
            throw new NonReadableChannelException();
        if (!shared)
            throw new NonWritableChannelException();
        if (size == 0)
            size = Long.MAX_VALUE - Math.max(0, position);

        VfsFileLock lock = (async != null) ? new VfsFileLock(async, this, position, size) : new VfsFileLock(this, position, size);
        synchronized (locks) {
            List<VfsFileLock> held = locks.computeIfAbsent(lockKey, k -> new ArrayList<>());
            for (VfsFileLock l : held)
                if (l.overlaps(position, size))
                    throw new OverlappingFileLockException();
            held.add(lock);
        }
        return lock;
    }

    private void release(VfsFileLock lock) {
        synchronized (locks) {
            if (!lock.isValid())
                return;
            List<VfsFileLock> held = locks.get(lockKey);
            if (held != null) {
                held.remove(lock);
                if (held.isEmpty())
                    locks.remove(lockKey);
            }
            lock.invalidate();
        }
    }

    /**
     * A shared lock of a region of a file of the virtual file system.
     */
    private static final class VfsFileLock extends FileLock {

        final VfsFileChannel owner;
        private volatile boolean invalid;

        VfsFileLock(VfsFileChannel owner, long position, long size) {
            super(owner, position, size, true);
            this.owner = owner;
        }

        VfsFileLock(AsynchronousFileChannel channel, VfsFileChannel owner, long position, long size) {
            super(channel, position, size, true);
            this.owner = owner;
        }

        @Override
        public boolean isValid() {
            return !invalid;
        }

        void invalidate() {
            invalid = true;
        }

        @Override
        public void release() throws IOException {
            if (!acquiredBy().isOpen())
                throw new ClosedChannelException();
            owner.release(this);
        }

    }

    // -- Native methods --

    // whether the file descriptor holds the stream of a file of the virtual file system
    private static native boolean isVfs(FileDescriptor fd);

    // reads at the position, or at the stream's (moving it) if negative; -1 at the end
    private static native int read0(FileDescriptor fd, byte[] b, int off, int len, long position) throws IOException;

    private static native long position0(FileDescriptor fd) throws IOException;

    private static native void seek0(FileDescriptor fd, long position) throws IOException;

    private static native long size0(FileDescriptor fd) throws IOException;

    // the normalized path of the file
    private static native String key0(FileDescriptor fd);

}
