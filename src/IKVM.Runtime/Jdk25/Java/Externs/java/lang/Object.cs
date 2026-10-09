using System;
using System.Threading;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements java.lang.Object's wait methods, which map.xml calls: java.lang.Object is System.Object, so its
    /// natives are not bound by name. A wait is a <see cref="Monitor.Wait(object)"/> that Thread.interrupt() can end.
    /// </summary>
    static class Object
    {

        public static void wait(object self, long timeoutMillis)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (timeoutMillis < 0)
                throw new global::java.lang.IllegalArgumentException("timeout value is negative");

            if (Monitor.IsEntered(self) == false)
                throw new global::java.lang.IllegalMonitorStateException("current thread is not owner");

            var state = Thread.CurrentState;
            state.EnterInterruptibleWait(timeoutMillis == 0 ? Thread.STATUS_IN_OBJECT_WAIT : Thread.STATUS_IN_OBJECT_WAIT_TIMED);
            try
            {
                if (timeoutMillis == 0)
                {
                    Monitor.Wait(self);
                }
                else
                {
                    // Monitor.Wait takes at most int.MaxValue milliseconds; a longer wait may end early, which a
                    // Java wait is allowed to do
                    Monitor.Wait(self, (int)Math.Min(timeoutMillis, int.MaxValue));
                }
            }
            catch (ThreadInterruptedException)
            {
                // interrupted; Monitor.Wait has reacquired the monitor
            }
            finally
            {
                state.LeaveInterruptibleWait();
            }

            if (state.GetAndClearInterrupt())
                throw new global::java.lang.InterruptedException();
#endif
        }

        public static void wait(object self, long timeoutMillis, int nanos)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (timeoutMillis < 0)
                throw new global::java.lang.IllegalArgumentException("timeoutMillis value is negative");

            if (nanos < 0 || nanos > 999999)
                throw new global::java.lang.IllegalArgumentException("nanosecond timeout value out of range");

            if (nanos > 0 && timeoutMillis < long.MaxValue)
                timeoutMillis++;

            wait(self, timeoutMillis);
#endif
        }

    }

}
