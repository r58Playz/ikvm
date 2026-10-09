using System;
using System.Threading.Tasks;

#if FIRST_PASS == false

using java.util.concurrent;

#endif

namespace IKVM.Runtime.Util.Java.Util.Concurrent
{

#if FIRST_PASS == false

    /// <summary>
    /// Implements the Java Future interface around a .NET Task.
    /// </summary>
    class TaskFuture<T> : Future
    {

        readonly Task<T> task;
        readonly Func<bool> cancellation;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="task"></param>
        /// <param name="cancellation"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public TaskFuture(Task<T> task, Func<bool> cancellation)
        {
            this.task = task ?? throw new ArgumentNullException(nameof(task));
            this.cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="task"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public TaskFuture(Task<T> task)
        {
            this.task = task ?? throw new ArgumentNullException(nameof(task));
        }

        public bool cancel(bool mayInterruptIfRunning)
        {
            return cancellation != null && mayInterruptIfRunning != false && cancellation();
        }

        public bool isCancelled()
        {
            return task.IsCanceled;
        }

        public bool isDone()
        {
            return task.IsCompleted;
        }

        public object get()
        {
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException e)
            {
                throw new CancellationException(e.Message);
            }
        }

        public object get(long timeout, TimeUnit unit)
        {
            try
            {
                if (task.Wait(TimeSpan.FromMilliseconds(unit.toMillis(timeout))) == false)
                    throw new global::java.util.concurrent.TimeoutException();

                return get();
            }
            catch (OperationCanceledException e)
            {
                throw new CancellationException(e.Message);
            }
        }

#if JDK25

        public object resultNow()
        {
            return state().ToString() switch
            {
                "SUCCESS" => task.Result,
                "RUNNING" => throw new global::java.lang.IllegalStateException("Task has not completed"),
                "FAILED" => throw new global::java.lang.IllegalStateException("Task completed with exception"),
                _ => throw new global::java.lang.IllegalStateException("Task was cancelled"),
            };
        }

        public Exception exceptionNow()
        {
            return state().ToString() switch
            {
                "FAILED" => global::ikvm.runtime.Util.mapException(task.Exception.InnerException ?? task.Exception),
                "RUNNING" => throw new global::java.lang.IllegalStateException("Task has not completed"),
                "SUCCESS" => throw new global::java.lang.IllegalStateException("Task completed with a result"),
                _ => throw new global::java.lang.IllegalStateException("Task was cancelled"),
            };
        }

        public Future.State state()
        {
            if (task.IsCompleted == false)
                return Future.State.RUNNING;
            if (task.IsCanceled)
                return Future.State.CANCELLED;
            if (task.IsFaulted)
                return task.Exception.InnerException is OperationCanceledException ? Future.State.CANCELLED : Future.State.FAILED;

            return Future.State.SUCCESS;
        }

#endif

    }

#endif

}
