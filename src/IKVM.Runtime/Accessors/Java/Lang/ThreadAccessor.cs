using System;
using System.Reflection;
using System.Threading;

namespace IKVM.Runtime.Accessors.Java.Lang
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.Thread' type.
    /// </summary>
    internal sealed class ThreadAccessor : Accessor<object>
    {

        Type javaLangThread;
        Type javaLangThreadGroup;

        FieldAccessor<object> current;
        FieldAccessor<object, Thread> nativeThread;
        MethodAccessor<Func<object>> currentThread;
        MethodAccessor<Func<bool>> interrupted;

        MethodAccessor<Func<object, object>> init;
        MethodAccessor<Func<object, bool>> isDaemon;
        MethodAccessor<Func<object, bool>> isInterrupted;
        MethodAccessor<Action<object>> die;
        MethodAccessor<Func<object, object>> getThreadGroup;

        // JDK 25
        FieldAccessor<object, long> eetop;
        FieldAccessor<object, object> holder;
        FieldAccessor<object, bool> interruptedField;
        FieldAccessor<object, string> name;
        MethodAccessor<Action<object>> run;
        MethodAccessor<Action<object>> exit;
        MethodAccessor<Action<object, Exception>> dispatchUncaughtException;
        ConstructorInfo attachConstructor;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public ThreadAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.Thread")
        {

        }

        Type JavaLangThread => Resolve(ref javaLangThread, "java.lang.Thread");

        Type JavaLangThreadGroup => Resolve(ref javaLangThreadGroup, "java.lang.ThreadGroup");

        /// <summary>
        /// Gets the value of the 'current' field.
        /// </summary>
        /// <returns></returns>
        public object GetCurrent() => GetField(ref current, nameof(current)).GetValue();

        /// <summary>
        /// Gets the value of the 'nativeThread' field.
        /// </summary>
        /// <returns></returns>
        public Thread GetNativeThread(object self) => GetField(ref nativeThread, nameof(nativeThread)).GetValue(self);

        /// <summary>
        /// Invokes the 'currentThread' method.
        /// </summary>
        public object InvokeCurrentThread() => GetMethod(ref currentThread, nameof(currentThread), JavaLangThread).Invoker();

        /// <summary>
        /// Invokes the 'interrupted' method.
        /// </summary>
        /// <returns></returns>
        public bool InvokeIsInterrupted(object self) => GetMethod(ref isInterrupted, nameof(isInterrupted), typeof(bool)).Invoker(self);

        /// <summary>
        /// Invokes the constructor.
        /// </summary>
        /// <param name="threadGroup"></param>
        /// <returns></returns>
        public object Init(object threadGroup) => GetConstructor(ref init, JavaLangThreadGroup).Invoker(threadGroup);

        /// <summary>
        /// Invokes the 'isDaemon' method.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public bool InvokeIsDaemon(object self) => GetMethod(ref isDaemon, nameof(isDaemon), typeof(bool)).Invoker(self);

        /// <summary>
        /// Invokes the 'die' method.
        /// </summary>
        public void InvokeDie(object self) => GetMethod(ref die, nameof(die), typeof(void)).Invoker(self);

        /// <summary>
        /// Invokes the 'getThreadGroup' method.
        /// </summary>
        public object InvokeGetThreadGroup(object self) => GetMethod(ref getThreadGroup, nameof(getThreadGroup), JavaLangThreadGroup).Invoker(self);

        /// <summary>
        /// Gets the value of the 'eetop' field (JDK 25), which is non-zero while the thread is alive.
        /// </summary>
        public long GetEetop(object self) => GetField(ref eetop, nameof(eetop)).GetValue(self);

        /// <summary>
        /// Sets the value of the 'eetop' field (JDK 25).
        /// </summary>
        public void SetEetop(object self, long value) => GetField(ref eetop, nameof(eetop)).SetValue(self, value);

        /// <summary>
        /// Gets the value of the 'holder' field (JDK 25), the thread's java.lang.Thread$FieldHolder.
        /// </summary>
        public object GetHolder(object self) => GetField(ref holder, nameof(holder)).GetValue(self);

        /// <summary>
        /// Gets the value of the 'interrupted' field (JDK 25), the thread's interrupt status.
        /// </summary>
        public bool GetInterrupted(object self) => GetField(ref interruptedField, "interrupted").GetValue(self);

        /// <summary>
        /// Sets the value of the 'interrupted' field (JDK 25).
        /// </summary>
        public void SetInterrupted(object self, bool value) => GetField(ref interruptedField, "interrupted").SetValue(self, value);

        /// <summary>
        /// Gets the value of the 'name' field (JDK 25).
        /// </summary>
        public string GetName(object self) => GetField(ref name, nameof(name)).GetValue(self);

        /// <summary>
        /// Invokes the 'run' method.
        /// </summary>
        public void InvokeRun(object self) => GetMethod(ref run, nameof(run), typeof(void)).Invoker(self);

        /// <summary>
        /// Invokes the 'exit' method, which the VM calls when a thread ends (JDK 25).
        /// </summary>
        public void InvokeExit(object self) => GetMethod(ref exit, nameof(exit), typeof(void)).Invoker(self);

        /// <summary>
        /// Invokes the 'dispatchUncaughtException' method (JDK 25).
        /// </summary>
        public void InvokeDispatchUncaughtException(object self, Exception e) => GetMethod(ref dispatchUncaughtException, nameof(dispatchUncaughtException), typeof(void), typeof(Exception)).Invoker(self, e);

        /// <summary>
        /// Runs the 'Thread(ThreadGroup, String)' constructor on an allocated thread, as HotSpot does when a native thread
        /// attaches (JDK 25).
        /// </summary>
        public void InvokeAttachConstructor(object self, object group, string name)
        {
            attachConstructor ??= Type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { JavaLangThreadGroup, typeof(string) }, null) ?? throw new InvalidOperationException("No java.lang.Thread(ThreadGroup, String) constructor.");

            try
            {
                attachConstructor.Invoke(self, new[] { group, name });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }

        /// <summary>
        /// Invokes the 'interrupted' method.
        /// </summary>
        /// <returns></returns>
        public bool InvokeInterrupted() => GetMethod(ref interrupted, nameof(interrupted), typeof(bool)).Invoker();

    }

#endif

}
