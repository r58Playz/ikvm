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

import jdk.internal.access.JavaLangRefAccess;
import jdk.internal.access.SharedSecrets;
import jdk.internal.ref.Cleaner;
import jdk.internal.vm.annotation.ForceInline;

/**
 * IKVM: the JDK 25 API of Reference on top of .NET's weak references. A reference holds its referent in a
 * System.WeakReference, and a ReferenceQueue finds the references whose referent has been collected itself; nothing
 * is left for a reference handler thread to do. Ported from IKVM's JDK 8 version.
 */
public abstract sealed class Reference<T>
    permits PhantomReference, SoftReference, WeakReference, FinalReference {

    private final cli.System.WeakReference weakRef;
    T strongRef;

    volatile ReferenceQueue<? super T> queue;

    /* When active:   NULL
     *     pending:   this
     *    Enqueued:   next reference in queue (or this if last)
     *    Inactive:   this
     */
    @SuppressWarnings("rawtypes")
    volatile Reference next;

    // a jdk.internal.ref.Cleaner runs its action when its referent has been collected
    private final class CleanerGuard {
        @SuppressWarnings("removal")
        protected void finalize() {
            if (isActive()) {
                cli.System.GC.ReRegisterForFinalize(this);
            } else {
                ((Cleaner)Reference.this).clean();
            }
        }
    }

    final boolean isActive() {
        return strongRef != null || (weakRef != null && weakRef.get_IsAlive());
    }

    final boolean isStrongOrNullRef() {
        return weakRef == null;
    }

    static {
        SharedSecrets.setJavaLangRefAccess(new JavaLangRefAccess() {
            @Override
            public void startThreads() {
                // there is no reference handler: the queues find collected references themselves; and the runtime
                // runs finalize() from the .NET finalizer
            }

            @Override
            public boolean waitForReferenceProcessing() {
                return false;
            }

            @Override
            public void runFinalization() {
                cli.System.GC.WaitForPendingFinalizers();
            }
        });
    }

    /* -- Referent accessor and setters -- */

    public T get() {
        return referent();
    }

    @SuppressWarnings("unchecked")
    private T referent() {
        if (weakRef == null) {
            return strongRef;
        }
        return (T)weakRef.get_Target();
    }

    public final boolean refersTo(T obj) {
        return refersToImpl(obj);
    }

    boolean refersToImpl(T obj) {
        return referent() == obj;
    }

    public void clear() {
        clearImpl();
    }

    void clearImpl() {
        if (weakRef != null) {
            try {
                if (false) throw new cli.System.InvalidOperationException();
                weakRef.set_Target(null);
            } catch (cli.System.InvalidOperationException x) {
                // we were already finalized
            }
        }
        strongRef = null;
        // a cleared reference is not enqueued when its referent would have been collected
        queue.clear(this);
    }

    T getFromInactiveFinalReference() {
        return referent();
    }

    void clearInactiveFinalReference() {
        clearImpl();
    }

    @Deprecated(since="16")
    @SuppressWarnings("unchecked")
    public boolean isEnqueued() {
        if (queue == ReferenceQueue.ENQUEUED) {
            return true;
        }
        if (queue == ReferenceQueue.NULL_QUEUE) {
            return false;
        }
        if (isStrongOrNullRef() || isActive()) {
            return false;
        }
        ((ReferenceQueue<T>)queue).enqueue(this);
        return queue == ReferenceQueue.ENQUEUED;
    }

    public boolean enqueue() {
        clearImpl(); // Intentionally clearImpl() to dispatch to overridden method, if needed
        return this.queue.enqueue(this);
    }

    @Override
    protected Object clone() throws CloneNotSupportedException {
        throw new CloneNotSupportedException();
    }

    /* -- Constructors -- */

    Reference(T referent) {
        this(referent, null);
    }

    @SuppressWarnings("unchecked")
    Reference(T referent, ReferenceQueue<? super T> queue) {
        this.queue = (queue == null) ? ReferenceQueue.NULL_QUEUE : queue;
        if (referent instanceof Class) {
            // classes are not collected, so a weak reference to one would be pointless
            weakRef = null;
            strongRef = referent;
        } else if (referent == null) {
            weakRef = null;
        } else {
            weakRef = new cli.System.WeakReference(referent, this instanceof PhantomReference);
            if (queue != null) {
                ((ReferenceQueue<T>)queue).addToActiveList(this);
            }
            if (this instanceof Cleaner) {
                new CleanerGuard();
            }
        }
    }

    @ForceInline
    public static void reachabilityFence(Object ref) {
        // the call keeps ref reachable until it returns
    }

}
