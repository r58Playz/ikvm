using System;

using IKVM.Runtime;

namespace IKVM.Attributes
{

    /// <summary>
    /// Marks a type compiled from a sealed Java class or interface, and names the classes its PermittedSubclasses
    /// attribute lists: the only ones that may directly extend or implement it (JVMS 5.3.5).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
    public sealed class PermittedSubclassesAttribute : Attribute
    {

        readonly string[] names;

        public PermittedSubclassesAttribute(string[] names)
        {
            this.names = UnicodeUtil.UnescapeInvalidSurrogates(names ?? throw new ArgumentNullException(nameof(names)));
        }

        /// <summary>
        /// The permitted subclasses, by Java name.
        /// </summary>
        public string[] Names => names;

    }

}
