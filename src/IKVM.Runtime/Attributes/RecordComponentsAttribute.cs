using System;

namespace IKVM.Attributes
{

    /// <summary>
    /// Marks a type compiled from a Java record class, and lists the components its Record attribute declares: their
    /// names, descriptors and generic signatures (JVMS 4.7.30).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RecordComponentsAttribute : Attribute
    {

        readonly string[] names;
        readonly string[] descriptors;
        readonly string[] signatures;

        public RecordComponentsAttribute(string[] names, string[] descriptors, string[] signatures)
        {
            this.names = names ?? throw new ArgumentNullException(nameof(names));
            this.descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            this.signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        }

        /// <summary>
        /// The components' names.
        /// </summary>
        public string[] Names => names;

        /// <summary>
        /// The components' field descriptors, in dotted form.
        /// </summary>
        public string[] Descriptors => descriptors;

        /// <summary>
        /// The components' generic signatures, or null for a component that has none.
        /// </summary>
        public string[] Signatures => signatures;

    }

}
