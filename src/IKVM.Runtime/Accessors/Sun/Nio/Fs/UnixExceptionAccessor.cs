using System;

namespace IKVM.Runtime.Accessors.Sun.Nio.Fs
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'sun.nio.fs.UnixException' type.
    /// </summary>
    internal sealed class UnixExceptionAccessor : Accessor<object>
    {

        MethodAccessor<Func<int, object>> init;
        MethodAccessor<Func<object, int>> errno;
        MethodAccessor<Action<object, int>> setError;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public UnixExceptionAccessor(AccessorTypeResolver resolver) :
            base(resolver, "sun.nio.fs.UnixException")
        {

        }

        /// <summary>
        /// Creates a new 'sun.nio.fs.UnixException' for the given errno.
        /// </summary>
        /// <param name="errno"></param>
        /// <returns></returns>
        public object Init(int errno) => GetConstructor(ref init, typeof(int)).Invoker(errno);

        /// <summary>
        /// Gets the errno carried by the given 'sun.nio.fs.UnixException'.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public int InvokeErrno(object self) => GetMethod(ref errno, "errno", typeof(int)).Invoker(self);

        /// <summary>
        /// Replaces the errno carried by the given 'sun.nio.fs.UnixException'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="value"></param>
        public void InvokeSetError(object self, int value) => GetMethod(ref setError, "setError", typeof(void), typeof(int)).Invoker(self, value);

    }

#endif

}
