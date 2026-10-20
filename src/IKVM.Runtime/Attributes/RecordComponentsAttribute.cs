using System;

namespace IKVM.Attributes
{

    /// <summary>
    /// Marks a type compiled from a Java record class, and lists the components its Record attribute declares: their
    /// names, descriptors, generic signatures, annotations and type annotations (JVMS 4.7.30).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RecordComponentsAttribute : Attribute
    {

        readonly string[] names;
        readonly string[] descriptors;
        readonly string[] signatures;
        readonly object[] annotations;
        readonly object[] typeAnnotations;

        public RecordComponentsAttribute(string[] names, string[] descriptors, string[] signatures)
        {
            this.names = names ?? throw new ArgumentNullException(nameof(names));
            this.descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            this.signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        }

        /// <summary>
        /// Initializes a new instance for components of which some have annotations.
        /// </summary>
        /// <param name="names"></param>
        /// <param name="descriptors"></param>
        /// <param name="signatures"></param>
        /// <param name="annotations">For each component, its RuntimeVisibleAnnotations as they are in the class file
        /// (a byte array), whose constants the type's <see cref="ConstantPoolAttribute"/> holds.</param>
        /// <param name="typeAnnotations">For each component, its RuntimeVisibleTypeAnnotations as they are in the
        /// class file (a byte array), whose constants the type's <see cref="ConstantPoolAttribute"/> holds.</param>
        public RecordComponentsAttribute(string[] names, string[] descriptors, string[] signatures, object[] annotations, object[] typeAnnotations) :
            this(names, descriptors, signatures)
        {
            this.annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
            this.typeAnnotations = typeAnnotations ?? throw new ArgumentNullException(nameof(typeAnnotations));
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

        /// <summary>
        /// For each component, the bytes of its RuntimeVisibleAnnotations attribute (an empty array for a component that
        /// has none), or null if no component has any annotations.
        /// </summary>
        public object[] Annotations => annotations;

        /// <summary>
        /// For each component, the bytes of its RuntimeVisibleTypeAnnotations attribute (an empty array for a component
        /// that has none), or null if no component has any annotations.
        /// </summary>
        public object[] TypeAnnotations => typeAnnotations;

    }

}
