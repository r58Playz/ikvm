using System;

namespace IKVM.Runtime.Accessors.Java.Lang.Invoke
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.invoke.CallSite' type.
    /// </summary>
    internal sealed class CallSiteAccessor : Accessor<object>
    {

        FieldAccessor<object, global::java.lang.invoke.MethodHandle> target;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public CallSiteAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.invoke.CallSite")
        {

        }

        /// <summary>
        /// Sets the value of the 'target' field, which is final in JDK 9+: the VM sets it.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="value"></param>
        public void SetTarget(object self, global::java.lang.invoke.MethodHandle value) => GetField(ref target, nameof(target)).SetValue(self, value);

    }

#endif

}
