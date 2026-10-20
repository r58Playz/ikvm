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

import java.io.FileDescriptor;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.AsynchronousFileChannel;
import java.nio.channels.ClosedChannelException;
import java.nio.channels.CompletionHandler;
import java.nio.channels.FileLock;
import java.nio.channels.NonReadableChannelException;
import java.nio.channels.NonWritableChannelException;
import java.nio.channels.ShutdownChannelGroupException;
import java.util.Objects;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
import java.util.concurrent.RejectedExecutionException;

/**
 * IKVM: the asynchronous file channel of a file of IKVM's virtual file system: a VfsFileChannel whose operations run on
 * the channel's executor, as SimpleAsynchronousFileChannelImpl's do. map.xml has SimpleAsynchronousFileChannelImpl.open
 * return one for the file descriptor of such a file. The virtual file system is read-only, so the channel never is
 * writable.
 */
public final class VfsAsynchronousFileChannel extends AsynchronousFileChannel {

    // the executor of a channel opened without one (SimpleAsynchronousFileChannelImpl's)
    private static class DefaultExecutorHolder {
        static final ExecutorService defaultExecutor = ThreadPool.createDefault().executor();
    }

    private final VfsFileChannel channel;
    private final boolean reading;
    private final ExecutorService executor;

    private VfsAsynchronousFileChannel(VfsFileChannel channel, boolean reading, ExecutorService executor) {
        this.channel = channel;
        this.reading = reading;
        this.executor = executor;
    }

    /**
     * Returns an asynchronous channel of the file of the virtual file system the file descriptor is open on, or null if
     * the file descriptor is the host's.
     */
    public static AsynchronousFileChannel open(FileDescriptor fdo, String path, boolean reading, boolean writing, ThreadPool pool) {
        VfsFileChannel channel = (VfsFileChannel) VfsFileChannel.open(fdo, path, reading, writing, null);
        if (channel == null)
            return null;

        ExecutorService executor = (pool == null) ? DefaultExecutorHolder.defaultExecutor : pool.executor();
        return new VfsAsynchronousFileChannel(channel, reading, executor);
    }

    private void ensureOpen() throws ClosedChannelException {
        if (!isOpen())
            throw new ClosedChannelException();
    }

    // runs the operation on the executor, completing the handler with its outcome, or the future returned if none
    private <V,A> Future<V> submit(Callable<V> task, A attachment, CompletionHandler<V,? super A> handler) {
        final CompletableFuture<V> future = (handler == null) ? new CompletableFuture<>() : null;
        Runnable r = () -> {
            V result = null;
            Throwable exc = null;
            try {
                result = task.call();
            } catch (Throwable x) {
                exc = x;
            }
            if (handler == null) {
                if (exc != null)
                    future.completeExceptionally(exc);
                else
                    future.complete(result);
            } else {
                Invoker.invokeUnchecked(handler, attachment, result, exc);
            }
        };
        try {
            executor.execute(r);
        } catch (RejectedExecutionException x) {
            throw new ShutdownChannelGroupException();
        }
        return future;
    }

    @Override
    public boolean isOpen() {
        return channel.isOpen();
    }

    @Override
    public void close() throws IOException {
        channel.close();
    }

    @Override
    public long size() throws IOException {
        ensureOpen();
        return channel.size();
    }

    @Override
    public AsynchronousFileChannel truncate(long size) throws IOException {
        if (size < 0L)
            throw new IllegalArgumentException("Negative size");
        throw new NonWritableChannelException();
    }

    @Override
    public void force(boolean metaData) throws IOException {
        ensureOpen();
    }

    @Override
    public <A> void lock(long position, long size, boolean shared, A attachment, CompletionHandler<FileLock,? super A> handler) {
        Objects.requireNonNull(handler, "'handler' is null");
        implLock(position, size, shared, attachment, handler);
    }

    @Override
    public Future<FileLock> lock(long position, long size, boolean shared) {
        return implLock(position, size, shared, null, null);
    }

    private <A> Future<FileLock> implLock(long position, long size, boolean shared, A attachment, CompletionHandler<FileLock,? super A> handler) {
        if (shared && !reading)
            throw new NonReadableChannelException();
        if (!shared)
            throw new NonWritableChannelException();
        return submit(() -> channel.tryLock(this, position, size, shared), attachment, handler);
    }

    @Override
    public FileLock tryLock(long position, long size, boolean shared) throws IOException {
        if (shared && !reading)
            throw new NonReadableChannelException();
        if (!shared)
            throw new NonWritableChannelException();
        return channel.tryLock(this, position, size, shared);
    }

    @Override
    public <A> void read(ByteBuffer dst, long position, A attachment, CompletionHandler<Integer,? super A> handler) {
        Objects.requireNonNull(handler, "'handler' is null");
        implRead(dst, position, attachment, handler);
    }

    @Override
    public Future<Integer> read(ByteBuffer dst, long position) {
        return implRead(dst, position, null, null);
    }

    private <A> Future<Integer> implRead(ByteBuffer dst, long position, A attachment, CompletionHandler<Integer,? super A> handler) {
        if (position < 0)
            throw new IllegalArgumentException("Negative position");
        if (!reading)
            throw new NonReadableChannelException();
        if (dst.isReadOnly())
            throw new IllegalArgumentException("Read-only buffer");
        return submit(() -> channel.read(dst, position), attachment, handler);
    }

    @Override
    public <A> void write(ByteBuffer src, long position, A attachment, CompletionHandler<Integer,? super A> handler) {
        Objects.requireNonNull(handler, "'handler' is null");
        implWrite(src, position);
    }

    @Override
    public Future<Integer> write(ByteBuffer src, long position) {
        return implWrite(src, position);
    }

    private Future<Integer> implWrite(ByteBuffer src, long position) {
        Objects.requireNonNull(src);
        if (position < 0)
            throw new IllegalArgumentException("Negative position");
        throw new NonWritableChannelException();
    }

}
