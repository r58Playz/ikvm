using System;

namespace IKVM.Runtime.Accessors.Java.Lang
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.System' type.
    /// </summary>
    internal sealed class SystemAccessor : Accessor<object>
    {

        Type ikvmInternalCallerID;
        Type javaLangSecurityManager;

        FieldAccessor<object> _in;
        FieldAccessor<object> _out;
        FieldAccessor<object> _err;
        MethodAccessor<Action> initializeSystemClass;
        MethodAccessor<Action> initPhase1;
        MethodAccessor<Func<bool, bool, int>> initPhase2;
        MethodAccessor<Action> initPhase3;
        MethodAccessor<Func<string, string>> getProperty;
        MethodAccessor<Func<string, string, string>> setProperty;
        MethodAccessor<Func<object>> getSecurityManager;
        MethodAccessor<Action<string, object>> loadLibrary;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public SystemAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.System")
        {

        }

        Type IkvmInternalCallerID => Resolve(ref ikvmInternalCallerID, "ikvm.internal.CallerID");

        Type JavaLangSecurityManager => Resolve(ref javaLangSecurityManager, "java.lang.SecurityManager");

        /// <summary>
        /// Sets the value of the 'in' field.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public void SetIn(object value) => GetField(ref _in, "in").SetValue(value);

        /// <summary>
        /// Sets the value of the 'out' field.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public void SetOut(object value) => GetField(ref _out, "out").SetValue(value);

        /// <summary>
        /// Sets the value of the 'err' field.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public void SetErr(object value) => GetField(ref _err, "err").SetValue(value);

        /// <summary>
        /// Sets the value of the 'initializeSystemClass' field.
        /// </summary>
        /// <returns></returns>
        public void InvokeInitializeSystemClass() => GetMethod(ref initializeSystemClass, nameof(initializeSystemClass), typeof(void)).Invoker();

        /// <summary>
        /// Invokes the 'initPhase1' method (JDK 9+): system properties, the standard streams and the reference handler.
        /// </summary>
        public void InvokeInitPhase1() => GetMethod(ref initPhase1, nameof(initPhase1), typeof(void)).Invoker();

        /// <summary>
        /// Invokes the 'initPhase2' method (JDK 9+), which boots the module system; returns 0 if it succeeds.
        /// </summary>
        public int InvokeInitPhase2(bool printToStderr, bool printStackTrace) => GetMethod(ref initPhase2, nameof(initPhase2), typeof(int), typeof(bool), typeof(bool)).Invoker(printToStderr, printStackTrace);

        /// <summary>
        /// Invokes the 'initPhase3' method (JDK 9+), which sets up the system class loader.
        /// </summary>
        public void InvokeInitPhase3() => GetMethod(ref initPhase3, nameof(initPhase3), typeof(void)).Invoker();

        /// <summary>
        /// Sets the value of the 'getProperty' field.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public string InvokeGetProperty(string key) => GetMethod(ref getProperty, nameof(getProperty), typeof(string), typeof(string)).Invoker(key);

        /// <summary>
        /// Sets the value of the 'setProperty' field.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public string InvokeSetProperty(string key, string value) => GetMethod(ref setProperty, nameof(setProperty), typeof(string), typeof(string), typeof(string)).Invoker(key, value);

        /// <summary>
        /// Invokes the 'getSecurityManager' method.
        /// </summary>
        /// <returns></returns>
        public object InvokeGetSecurityManager() => GetMethod(ref getSecurityManager, nameof(getSecurityManager), JavaLangSecurityManager).Invoker();

        /// <summary>
        /// Invokes the 'loadLibrary' method.
        /// </summary>
        /// <param name="libname"></param>
        /// <param name="callerID"></param>
        public void InvokeLoadLibrary(string libname, object callerID) => GetMethod(ref loadLibrary, nameof(loadLibrary), typeof(void), typeof(string), IkvmInternalCallerID).Invoker(libname, callerID);

    }

#endif

}
