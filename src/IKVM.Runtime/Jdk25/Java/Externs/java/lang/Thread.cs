using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.Thread', which the class library uses as it is. A platform
    /// thread runs on a .NET thread; virtual threads are bound to one too, as JDK 25 does where continuations are not
    /// supported.
    /// </summary>
    /// <remarks>
    /// Java's interrupt status is Thread's own 'interrupted' field. The runtime only has to wake an interrupted thread
    /// that is blocked in a wait, sleep or park, which .NET's <see cref="global::System.Threading.Thread.Interrupt"/>
    /// does. But a .NET interrupt stays pending until the thread next blocks, so it is only sent while the thread is in
    /// one of those waits, and consumed when the wait ends (see <see cref="State"/>).
    /// </remarks>
    static class Thread
    {

        // JVMTI thread state bits, which java.lang.Thread$FieldHolder.threadStatus holds (see VM.toThreadState)
        const int JVMTI_THREAD_STATE_ALIVE = 0x0001;
        const int JVMTI_THREAD_STATE_TERMINATED = 0x0002;
        const int JVMTI_THREAD_STATE_RUNNABLE = 0x0004;
        const int JVMTI_THREAD_STATE_WAITING_INDEFINITELY = 0x0010;
        const int JVMTI_THREAD_STATE_WAITING_WITH_TIMEOUT = 0x0020;
        const int JVMTI_THREAD_STATE_SLEEPING = 0x0040;
        const int JVMTI_THREAD_STATE_WAITING = 0x0080;
        const int JVMTI_THREAD_STATE_IN_OBJECT_WAIT = 0x0100;
        const int JVMTI_THREAD_STATE_PARKED = 0x0200;

        // the thread statuses HotSpot uses (JavaThreadStatus)
        internal const int STATUS_NEW = 0;
        internal const int STATUS_RUNNABLE = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_RUNNABLE;
        internal const int STATUS_SLEEPING = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_WAITING | JVMTI_THREAD_STATE_WAITING_WITH_TIMEOUT | JVMTI_THREAD_STATE_SLEEPING;
        internal const int STATUS_IN_OBJECT_WAIT = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_WAITING | JVMTI_THREAD_STATE_WAITING_INDEFINITELY | JVMTI_THREAD_STATE_IN_OBJECT_WAIT;
        internal const int STATUS_IN_OBJECT_WAIT_TIMED = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_WAITING | JVMTI_THREAD_STATE_WAITING_WITH_TIMEOUT | JVMTI_THREAD_STATE_IN_OBJECT_WAIT;
        internal const int STATUS_PARKED = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_WAITING | JVMTI_THREAD_STATE_WAITING_INDEFINITELY | JVMTI_THREAD_STATE_PARKED;
        internal const int STATUS_PARKED_TIMED = JVMTI_THREAD_STATE_ALIVE | JVMTI_THREAD_STATE_WAITING | JVMTI_THREAD_STATE_WAITING_WITH_TIMEOUT | JVMTI_THREAD_STATE_PARKED;
        internal const int STATUS_TERMINATED = JVMTI_THREAD_STATE_TERMINATED;

#if FIRST_PASS == false

        static ThreadAccessor threadAccessor;
        static ThreadFieldHolderAccessor fieldHolderAccessor;

        static ThreadAccessor ThreadAccessor => JVM.Internal.BaseAccessors.Get(ref threadAccessor);

        static ThreadFieldHolderAccessor FieldHolderAccessor => JVM.Internal.BaseAccessors.Get(ref fieldHolderAccessor);

        [ThreadStatic]
        static global::java.lang.Thread current;

        [ThreadStatic]
        static object[] currentScopedValueCache;

        // the live threads, for getThreads
        static readonly HashSet<global::java.lang.Thread> threads = new HashSet<global::java.lang.Thread>();

        // the next thread id: Thread.ThreadIdentifiers increments it with Unsafe at this address. The primordial thread
        // has id 3, the first one HotSpot hands out.
        static readonly Lazy<IntPtr> nextThreadId = new Lazy<IntPtr>(() =>
        {
            var p = Marshal.AllocHGlobal(sizeof(long));
            Marshal.WriteInt64(p, 4);
            return p;
        });

        // stands in for HotSpot's JavaThread pointer in Thread.eetop, which is non-zero while the thread is alive
        static long nextEetop;

        /// <summary>
        /// The runtime's state of a Java thread, in its 'vmThread' field (added by map.xml).
        /// </summary>
        internal sealed class State
        {

            readonly global::java.lang.Thread javaThread;
            readonly object interruptLock = new object();
            global::System.Threading.Thread nativeThread;
            bool inInterruptibleWait;
            bool nativeInterruptPending;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="javaThread"></param>
            /// <param name="nativeThread"></param>
            internal State(global::java.lang.Thread javaThread, global::System.Threading.Thread nativeThread)
            {
                this.javaThread = javaThread;
                this.nativeThread = nativeThread;
                Eetop = Interlocked.Increment(ref nextEetop);
            }

            /// <summary>
            /// Gets the Java thread.
            /// </summary>
            internal global::java.lang.Thread JavaThread => javaThread;

            /// <summary>
            /// Gets or sets the .NET thread the Java thread runs on.
            /// </summary>
            internal global::System.Threading.Thread NativeThread
            {
                get => nativeThread;
                set => nativeThread = value;
            }

            /// <summary>
            /// Gets the value of Thread.eetop while the thread is alive.
            /// </summary>
            internal long Eetop { get; }

            /// <summary>
            /// Clears the thread's interrupt status, returning whether it was set (Thread.getAndClearInterrupt).
            /// </summary>
            /// <returns></returns>
            internal bool GetAndClearInterrupt()
            {
                if (ThreadAccessor.GetInterrupted(javaThread))
                {
                    ThreadAccessor.SetInterrupted(javaThread, false);
                    return true;
                }

                return false;
            }

            /// <summary>
            /// Called by a thread before it blocks in an interruptible wait. Throws InterruptedException, clearing the
            /// interrupt, if the thread has been interrupted.
            /// </summary>
            /// <param name="status"></param>
            internal void EnterInterruptibleWait(int status)
            {
                lock (interruptLock)
                {
                    if (GetAndClearInterrupt())
                        throw new global::java.lang.InterruptedException();

                    inInterruptibleWait = true;
                }

                SetStatus(status);
            }

            /// <summary>
            /// Called by a thread after an interruptible wait, however it ended. Consumes a .NET interrupt that was
            /// sent but did not end the wait.
            /// </summary>
            internal void LeaveInterruptibleWait()
            {
                SetStatus(STATUS_RUNNABLE);

                while (true)
                {
                    try
                    {
                        lock (interruptLock)
                        {
                            inInterruptibleWait = false;

                            if (nativeInterruptPending)
                            {
                                nativeInterruptPending = false;

                                // the pending interrupt is thrown when the thread next blocks: waste a time slice to get it
                                global::System.Threading.Thread.CurrentThread.Join(1);
                            }
                        }

                        break;
                    }
                    catch (ThreadInterruptedException)
                    {
                        // our interrupt, or one in the lock statement above
                    }
                }
            }

            /// <summary>
            /// Wakes the thread if it is blocked in an interruptible wait (Thread.interrupt0).
            /// </summary>
            internal void Interrupt()
            {
                lock (interruptLock)
                {
                    if (inInterruptibleWait && nativeThread != null && nativeInterruptPending == false)
                    {
                        nativeInterruptPending = true;
                        nativeThread.Interrupt();
                    }
                }
            }

            /// <summary>
            /// Sets the thread status in the thread's holder.
            /// </summary>
            /// <param name="status"></param>
            internal void SetStatus(int status)
            {
                var holder = ThreadAccessor.GetHolder(javaThread);
                if (holder != null)
                    FieldHolderAccessor.SetThreadStatus(holder, status);
            }

        }

        /// <summary>
        /// Gets the state of the Java thread of the current .NET thread, attaching it if needed.
        /// </summary>
        internal static State CurrentState => GetState(currentThread());

        /// <summary>
        /// Gets the runtime's state of the Java thread.
        /// </summary>
        /// <param name="thread"></param>
        /// <returns></returns>
        internal static State GetState(global::java.lang.Thread thread)
        {
            return (State)thread.vmThread;
        }

        /// <summary>
        /// Attaches the current .NET thread to Java: creates its java.lang.Thread the way HotSpot does when a native
        /// thread attaches, the thread being current while its constructor runs.
        /// </summary>
        /// <param name="group">the thread group, or null for the main thread group</param>
        /// <param name="name">the name, or null for a generated one</param>
        /// <param name="daemon"></param>
        /// <returns></returns>
        internal static global::java.lang.Thread Attach(object group, string name, bool daemon)
        {
            if (current != null)
                return current;

            var thread = (global::java.lang.Thread)RuntimeHelpers.GetUninitializedObject(typeof(global::java.lang.Thread));
            var state = new State(thread, global::System.Threading.Thread.CurrentThread);
            thread.vmThread = state;
            current = thread;
            ThreadAccessor.SetEetop(thread, state.Eetop);
            Register(thread);

            try
            {
                ThreadAccessor.InvokeAttachConstructor(thread, group ?? JVM.MainThreadGroup, name);
            }
            catch
            {
                ThreadAccessor.SetEetop(thread, 0);
                Unregister(thread);
                current = null;
                throw;
            }

            var holder = ThreadAccessor.GetHolder(thread);
            FieldHolderAccessor.SetDaemon(holder, daemon);
            FieldHolderAccessor.SetThreadStatus(holder, STATUS_RUNNABLE);
            return thread;
        }

        /// <summary>
        /// Detaches the current .NET thread from Java, as when a thread ends.
        /// </summary>
        internal static void Detach()
        {
            var thread = current;
            if (thread == null)
                return;

            try
            {
                ThreadAccessor.InvokeExit(thread);
            }
            catch (Exception)
            {
                // HotSpot ignores what Thread.exit() throws
            }
            finally
            {
                Terminate(thread);
            }
        }

        /// <summary>
        /// Attaches the current thread for JNI's AttachCurrentThread, in the given thread group.
        /// </summary>
        /// <param name="threadGroup"></param>
        internal static void AttachThreadFromJni(object threadGroup)
        {
            Attach(threadGroup, null, false);
        }

        /// <summary>
        /// Waits until the current thread is the last non-daemon thread, for JNI's DestroyJavaVM.
        /// </summary>
        internal static void WaitUntilLastJniThread()
        {
            while (true)
            {
                var others = false;
                lock (threads)
                {
                    foreach (var thread in threads)
                    {
                        if (thread != current && FieldHolderAccessor.GetDaemon(ThreadAccessor.GetHolder(thread)) == false)
                        {
                            others = true;
                            break;
                        }
                    }
                }

                if (others == false)
                    return;

                global::System.Threading.Thread.Sleep(1);
            }
        }

        // for java.lang.management's ThreadMXBean (VMManagementImpl): the threads started, and the most alive at once
        static long startedThreads;
        static int peakThreads;

        static void Register(global::java.lang.Thread thread)
        {
            lock (threads)
            {
                threads.Add(thread);
                startedThreads++;
                if (threads.Count > peakThreads)
                    peakThreads = threads.Count;
            }
        }

        internal static int LiveThreadCount
        {
            get { lock (threads) return threads.Count; }
        }

        internal static int PeakThreadCount
        {
            get { lock (threads) return peakThreads; }
        }

        internal static long StartedThreadCount
        {
            get { lock (threads) return startedThreads; }
        }

        internal static int DaemonThreadCount
        {
            get
            {
                lock (threads)
                {
                    var n = 0;
                    foreach (var thread in threads)
                        if (FieldHolderAccessor.GetDaemon(ThreadAccessor.GetHolder(thread)))
                            n++;

                    return n;
                }
            }
        }

        internal static void ResetPeakThreadCount()
        {
            lock (threads)
                peakThreads = threads.Count;
        }

        static void Unregister(global::java.lang.Thread thread)
        {
            lock (threads)
                threads.Remove(thread);
        }

        /// <summary>
        /// Ends the thread's life as HotSpot's ensure_join does: marks it terminated and wakes the threads joining it.
        /// </summary>
        /// <param name="thread"></param>
        static void Terminate(global::java.lang.Thread thread)
        {
            lock (thread)
            {
                GetState(thread).SetStatus(STATUS_TERMINATED);
                ThreadAccessor.SetEetop(thread, 0);
                Monitor.PulseAll(thread);
            }

            Unregister(thread);
            current = null;
        }

        /// <summary>
        /// The body of a started thread: runs Thread.run(), dispatches what it throws to the uncaught exception
        /// handler and calls Thread.exit(), as HotSpot does.
        /// </summary>
        /// <param name="state"></param>
        static void Run(State state)
        {
            var thread = state.JavaThread;
            current = thread;

            try
            {
                try
                {
                    ThreadAccessor.InvokeRun(thread);
                }
                catch (Exception e)
                {
                    try
                    {
                        ThreadAccessor.InvokeDispatchUncaughtException(thread, global::ikvm.runtime.Util.mapException(e));
                    }
                    catch (Exception)
                    {
                        // HotSpot ignores what the handler throws
                    }
                }

                try
                {
                    ThreadAccessor.InvokeExit(thread);
                }
                catch (Exception)
                {
                    // HotSpot ignores what Thread.exit() throws
                }
            }
            finally
            {
                Terminate(thread);
            }
        }

        static ThreadPriority MapPriority(int priority)
        {
            return priority switch
            {
                <= 2 => ThreadPriority.Lowest,
                <= 4 => ThreadPriority.BelowNormal,
                5 => ThreadPriority.Normal,
                <= 7 => ThreadPriority.AboveNormal,
                _ => ThreadPriority.Highest,
            };
        }

        static void SetName(global::System.Threading.Thread nativeThread, string name)
        {
            try
            {
                nativeThread.Name = name;
            }
            catch (InvalidOperationException)
            {
                // the runtime only allows the name to be set once
            }
        }

        static global::java.lang.StackTraceElement[] GetStackTrace(StackTrace stack)
        {
            var stackTrace = new List<global::java.lang.StackTraceElement>();
            ExceptionHelper.ExceptionInfoHelper.Append(JVM.Context.ExceptionHelper, stackTrace, stack, 0, true);
            return stackTrace.ToArray();
        }

#endif

        public static void registerNatives()
        {

        }

        public static object findScopedValueBindings()
        {
            // scoped value bindings are found by walking the stack for ScopedValue.runWith frames, which we don't do:
            // Thread falls back to the thread's own bindings
            return null;
        }

        public static global::java.lang.Thread currentCarrierThread()
        {
            return currentThread();
        }

        public static global::java.lang.Thread currentThread()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return current ?? Attach(null, null, true);
#endif
        }

        public static void setCurrentThread(global::java.lang.Thread self, global::java.lang.Thread thread)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // only mounting a virtual thread on its carrier sets another thread current, and with continuations not
            // supported virtual threads run on their own .NET thread
            current = thread;
#endif
        }

        public static object[] scopedValueCache()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return currentScopedValueCache;
#endif
        }

        public static void setScopedValueCache(object[] cache)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            currentScopedValueCache = cache;
#endif
        }

        public static void ensureMaterializedForStackWalk(object o)
        {

        }

        public static void yield0()
        {
            global::System.Threading.Thread.Yield();
        }

        public static void sleepNanos0(long nanos)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var state = CurrentState;
            state.EnterInterruptibleWait(STATUS_SLEEPING);
            try
            {
                var deadline = Stopwatch.GetTimestamp() + (long)(nanos / (1_000_000_000.0 / Stopwatch.Frequency));
                while (true)
                {
                    var remaining = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                    if (remaining <= 0)
                        break;

                    global::System.Threading.Thread.Sleep((int)Math.Min(remaining, int.MaxValue));
                }
            }
            catch (ThreadInterruptedException)
            {
                // interrupted
            }
            finally
            {
                state.LeaveInterruptibleWait();
            }

            if (state.GetAndClearInterrupt())
                throw new global::java.lang.InterruptedException("sleep interrupted");
#endif
        }

        public static void start0(global::java.lang.Thread self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var holder = ThreadAccessor.GetHolder(self);
            var stackSize = FieldHolderAccessor.GetStackSize(holder);

            var state = new State(self, null);
            var nativeThread = stackSize > 0 ? new global::System.Threading.Thread(() => Run(state), (int)Math.Min(stackSize, int.MaxValue)) : new global::System.Threading.Thread(() => Run(state));
            state.NativeThread = nativeThread;
            self.vmThread = state;

            nativeThread.IsBackground = FieldHolderAccessor.GetDaemon(holder);
            nativeThread.Priority = MapPriority(FieldHolderAccessor.GetPriority(holder));
            SetName(nativeThread, ThreadAccessor.GetName(self));

            // the thread is alive from now on, as with HotSpot's JVM_StartThread
            ThreadAccessor.SetEetop(self, state.Eetop);
            FieldHolderAccessor.SetThreadStatus(holder, STATUS_RUNNABLE);
            Register(self);

            try
            {
                nativeThread.Start();
            }
            catch (OutOfMemoryException)
            {
                FieldHolderAccessor.SetThreadStatus(holder, STATUS_NEW);
                ThreadAccessor.SetEetop(self, 0);
                Unregister(self);
                throw new global::java.lang.OutOfMemoryError("unable to create native thread: possibly out of memory or process/resource limits reached");
            }
#endif
        }

        public static bool holdsLock(object obj)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (obj == null)
                throw new global::java.lang.NullPointerException();

            return Monitor.IsEntered(obj);
#endif
        }

        public static object getStackTrace0(global::java.lang.Thread self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // .NET can only walk the current thread's stack
            if (self == current)
                return GetStackTrace(new StackTrace(true));

            return null;
#endif
        }

        public static global::java.lang.StackTraceElement[][] dumpThreads(global::java.lang.Thread[] threads)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var stacks = new global::java.lang.StackTraceElement[threads.Length][];
            for (int i = 0; i < threads.Length; i++)
                stacks[i] = threads[i] == current ? GetStackTrace(new StackTrace(true)) : Array.Empty<global::java.lang.StackTraceElement>();

            return stacks;
#endif
        }

        public static global::java.lang.Thread[] getThreads()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            lock (threads)
            {
                var a = new global::java.lang.Thread[threads.Count];
                threads.CopyTo(a);
                return a;
            }
#endif
        }

        public static void setPriority0(global::java.lang.Thread self, int priority)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var nativeThread = (self.vmThread as State)?.NativeThread;
            if (nativeThread != null)
            {
                try
                {
                    nativeThread.Priority = MapPriority(priority);
                }
                catch (ThreadStateException)
                {
                    // the thread has ended
                }
            }
#endif
        }

        public static void interrupt0(global::java.lang.Thread self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (self.vmThread is State state)
                state.Interrupt();

            // HotSpot also unparks the thread
            IKVM.Java.Externs.jdk.@internal.misc.Unsafe.Unpark(self);
#endif
        }

        public static void clearInterruptEvent()
        {
            // only Windows HotSpot has an interrupt event to clear
        }

        public static void setNativeName(global::java.lang.Thread self, string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var nativeThread = (self.vmThread as State)?.NativeThread;
            if (nativeThread != null)
                SetName(nativeThread, name);
#endif
        }

        public static long getNextThreadIdOffset()
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return (long)nextThreadId.Value;
#endif
        }

    }

}
