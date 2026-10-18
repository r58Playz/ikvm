/*
 * IKVM: the watch service of the browser, whose libnio has no inotify, so LinuxWatchService cannot start there
 * (map.xml has LinuxFileSystem.newWatchService return this one). It checks a registration as LinuxWatchService does
 * and gives each directory one key, but signals none: no events are ever delivered.
 */
/*
 * Copyright (c) 2008, 2024, Oracle and/or its affiliates. All rights reserved.
 * DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
 *
 * This code is free software; you can redistribute it and/or modify it
 * under the terms of the GNU General Public License version 2 only, as
 * published by the Free Software Foundation.  Oracle designates this
 * particular file as subject to the "Classpath" exception as provided
 * by Oracle in the LICENSE file that accompanied this code.
 *
 * This code is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
 * FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
 * version 2 for more details (a copy is included in the LICENSE file that
 * accompanied this code).
 *
 * You should have received a copy of the GNU General Public License version
 * 2 along with this work; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin St, Fifth Floor, Boston, MA 02110-1301 USA.
 *
 * Please contact Oracle, 500 Oracle Parkway, Redwood Shores, CA 94065 USA
 * or visit www.oracle.com if you need additional information or have any
 * questions.
 */

package sun.nio.fs;

import java.nio.file.*;
import java.io.IOException;
import java.util.HashMap;
import java.util.Map;

/**
 * Watch service whose keys are never signalled.
 */

final class InertWatchService
    extends AbstractWatchService
{
    // the key of each registered directory, by its file key (inotify's watch descriptor is per inode too)
    private final Map<Object,InertWatchKey> fileKeyToKey = new HashMap<>();

    InertWatchService() {
    }

    @Override
    WatchKey register(Path obj,
                      WatchEvent.Kind<?>[] events,
                      WatchEvent.Modifier... modifiers)
        throws IOException
    {
        // AbstractPoller.register's checks
        if (obj == null)
            throw new NullPointerException();
        boolean hasEvent = false;
        for (WatchEvent.Kind<?> event: events) {
            // standard events
            if (event == StandardWatchEventKinds.ENTRY_CREATE ||
                event == StandardWatchEventKinds.ENTRY_MODIFY ||
                event == StandardWatchEventKinds.ENTRY_DELETE)
            {
                hasEvent = true;
                continue;
            }

            // OVERFLOW is ignored
            if (event == StandardWatchEventKinds.OVERFLOW)
                continue;

            // null/unsupported
            if (event == null)
                throw new NullPointerException("An element in event set is 'null'");
            throw new UnsupportedOperationException(event.name());
        }
        if (!hasEvent)
            throw new IllegalArgumentException("No events to register");

        // LinuxWatchService's
        for (WatchEvent.Modifier modifier: modifiers) {
            if (modifier == null)
                throw new NullPointerException();
            if (!ExtendedOptions.SENSITIVITY_HIGH.matches(modifier) &&
                    !ExtendedOptions.SENSITIVITY_MEDIUM.matches(modifier) &&
                    !ExtendedOptions.SENSITIVITY_LOW.matches(modifier)) {
                throw new UnsupportedOperationException("Modifier not supported");
            }
        }

        // check file is directory
        UnixPath dir = (UnixPath)obj;
        UnixFileAttributes attrs;
        try {
            attrs = UnixFileAttributes.get(dir, true);
        } catch (UnixException x) {
            throw x.asIOException(dir);
        }
        if (!attrs.isDirectory()) {
            throw new NotDirectoryException(dir.getPathForExceptionMessage());
        }

        // a directory registered again keeps its key
        synchronized (closeLock()) {
            if (!isOpen())
                throw new ClosedWatchServiceException();
            Object fileKey = attrs.fileKey();
            InertWatchKey key = fileKeyToKey.get(fileKey);
            if (key == null) {
                key = new InertWatchKey(dir, this, fileKey);
                fileKeyToKey.put(fileKey, key);
            }
            return key;
        }
    }

    // close holds closeLock
    @Override
    void implClose() {
        for (InertWatchKey key: fileKeyToKey.values())
            key.invalidate();
        fileKeyToKey.clear();
    }

    /**
     * WatchKey implementation
     */
    private static class InertWatchKey extends AbstractWatchKey {
        private final Object fileKey;
        private volatile boolean valid;

        InertWatchKey(UnixPath dir, InertWatchService watcher, Object fileKey) {
            super(dir, watcher);
            this.fileKey = fileKey;
            this.valid = true;
        }

        void invalidate() {
            valid = false;
        }

        @Override
        public boolean isValid() {
            return valid;
        }

        @Override
        public void cancel() {
            InertWatchService watcher = (InertWatchService)watcher();
            synchronized (watcher.closeLock()) {
                if (valid) {
                    valid = false;
                    watcher.fileKeyToKey.remove(fileKey);
                }
            }
        }
    }
}
