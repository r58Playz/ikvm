using System;

namespace IKVM.Runtime.Accessors.Sun.Nio.Fs
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'sun.nio.fs.UnixNativeDispatcher' type.
    /// </summary>
    internal sealed class UnixNativeDispatcherAccessor : Accessor<object>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public UnixNativeDispatcherAccessor(AccessorTypeResolver resolver) :
            base(resolver, "sun.nio.fs.UnixNativeDispatcher")
        {

        }

    }

#endif

}
