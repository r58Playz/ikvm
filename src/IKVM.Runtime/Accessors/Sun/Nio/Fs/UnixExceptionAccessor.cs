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

    }

#endif

}
