/*
  Copyright (C) 2014 Jeroen Frijters

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

  Jeroen Frijters
  jeroen@frijters.net
  
*/
package java.lang.ref;

import java.util.function.Consumer;

/**
 * IKVM: the JDK 25 API of ReferenceQueue for IKVM's references. A queue keeps a weak list of the references registered
 * with it, and enqueues those whose referent has been collected when it is polled; a waiter wakes after a garbage
 * collection to look again. Ported from IKVM's JDK 8 version.
 */
public class ReferenceQueue<T> {

    private static class Null extends ReferenceQueue<Object> {
        @Override
        boolean enqueue(Reference<?> r) {
            return false;
        }
    }

    static final ReferenceQueue<Object> NULL_QUEUE = new Null();
    static final ReferenceQueue<Object> ENQUEUED = new Null();

    private volatile Link<T> activeHead;
    private volatile Reference<? extends T> head;
    private final Object lock = new Object();
    private volatile boolean waitingForGC;

    private final class GCNotification {
        @SuppressWarnings("removal")
        protected void finalize() {
            waitingForGC = false;
            synchronized (lock) {
                lock.notifyAll();
            }
        }
    }

    // NOTE a known problem with this approach is that the WeakReference will not be available
    // after we've become only finalizer reachable
    private static class Link<T> extends cli.System.WeakReference {
        Link<T> next;

        Link(Reference<? extends T> ref) {
            super(ref);
        }

        @SuppressWarnings("unchecked")
        Reference<? extends T> get() {
            return (Reference<? extends T>)get_Target();
        }
    }

    public ReferenceQueue() {
    }

    public Reference<? extends T> poll() {
        if (head == null && (activeHead == null || waitingForGC)) {
            return null;
        }
        synchronized (lock) {
            return pollImpl();
        }
    }

    @SuppressWarnings("unchecked")
    private Reference<? extends T> pollImpl() {
        if (head == null) {
            if (activeHead == null || waitingForGC) {
                return null;
            }
            scanActiveList();
            if (head == null) {
                waitingForGC = true;
                new GCNotification();
                return null;
            }
        }
        Reference<? extends T> ref = head;
        head = ref.next == ref ? null : (Reference<? extends T>)ref.next;
        ref.next = ref;
        ref.queue = NULL_QUEUE;
        return ref;
    }

    public Reference<? extends T> remove(long timeout) throws IllegalArgumentException, InterruptedException {
        if (timeout < 0)
            throw new IllegalArgumentException("Negative timeout value");

        synchronized (lock) {
            long expiration = 0;
            for (;;) {
                Reference<? extends T> ref = pollImpl();
                if (ref != null)
                    return ref;

                if (timeout == 0) {
                    lock.wait();
                } else {
                    long now = System.currentTimeMillis();
                    if (expiration == 0) {
                        expiration = now + timeout;
                        if (expiration < 0) {
                            expiration = Long.MAX_VALUE;
                        }
                    }
                    if (now >= expiration) {
                        return null;
                    }
                    lock.wait(expiration - now);
                }
            }
        }
    }

    public Reference<? extends T> remove() throws InterruptedException {
        return remove(0);
    }

    final void clear(Reference<?> ref) {
        synchronized (lock) {
            if (ref.queue != ENQUEUED) {
                ref.queue = NULL_QUEUE;
            }
        }
    }

    // called by Reference.enqueue and isEnqueued
    boolean enqueue(Reference<?> ref) {
        synchronized (lock) {
            if (ref.queue != ENQUEUED && ref.queue != NULL_QUEUE) {
                ref.queue = ENQUEUED;

                if (ref.isStrongOrNullRef()) {
                    push(ref);
                    return true;
                }

                Link<T> prev = null;
                Link<T> curr = activeHead;
                while (curr != null) {
                    if (curr.get() == ref) {
                        if (prev == null) {
                            activeHead = curr.next;
                        } else {
                            prev.next = curr.next;
                        }
                        push(ref);
                        return true;
                    }
                    prev = curr;
                    curr = curr.next;
                }
            }
        }
        return false;
    }

    @SuppressWarnings("unchecked")
    private void push(Reference<?> ref) {
        ref.next = head == null ? ref : head;
        head = (Reference<? extends T>)ref;
        lock.notifyAll();
    }

    final void addToActiveList(Reference<? extends T> ref) {
        Link<T> link = new Link<T>(ref);
        synchronized (lock) {
            link.next = activeHead;
            activeHead = link;
            if (link.next == null) {
                lock.notifyAll();
            }
        }
    }

    private void scanActiveList() {
        Link<T> prev = null;
        Link<T> curr = activeHead;

        while (curr != null) {
            Reference<? extends T> ref = curr.get();
            if (ref == null || !ref.isActive()) {
                Link<T> next = curr.next;
                if (prev == null) {
                    activeHead = next;
                } else {
                    prev.next = next;
                }
                if (ref != null) {
                    ref.queue = ENQUEUED;
                    ref.next = head == null ? ref : head;
                    head = ref;
                }
                curr = next;
                continue;
            }
            prev = curr;
            curr = curr.next;
        }
    }

    @SuppressWarnings("unchecked")
    void forEach(Consumer<? super Reference<? extends T>> action) {
        for (Reference<? extends T> r = head; r != null;) {
            action.accept(r);
            Reference<? extends T> rn = (Reference<? extends T>)r.next;
            r = rn == r ? null : rn;
        }
    }

}
