namespace IKVM.Runtime
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// The JDK 8 java.lang.invoke.MemberName methods the runtime uses, which JDK 9+ no longer has.
    /// </summary>
    static class MemberNameExtensions
    {

        /// <summary>
        /// Gets the member's type descriptor, such as "(I)Ljava/lang/String;".
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        internal static string getSignature(this global::java.lang.invoke.MemberName self)
        {
            return self.isField() ? self.getFieldType().descriptorString() : self.getMethodType().toMethodDescriptorString();
        }

        /// <summary>
        /// Returns whether invoking the member dispatches on the receiver's type.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        internal static bool hasReceiverTypeDispatch(this global::java.lang.invoke.MemberName self)
        {
            return global::java.lang.invoke.MethodHandleNatives.refKindDoesDispatch(self.getReferenceKind());
        }

    }

#endif

}
