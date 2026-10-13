namespace IKVM.Runtime.Accessors.Java.Lang
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.ProcessImpl' type.
    /// </summary>
    internal sealed class ProcessImplAccessor : Accessor<object>
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public ProcessImplAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.ProcessImpl")
        {

        }

    }

#endif

}
