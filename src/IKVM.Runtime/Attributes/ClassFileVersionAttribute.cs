using System;

namespace IKVM.Attributes
{

    /// <summary>
    /// Records the version of the class file a type is compiled from, when it is not that of the Java SE release IKVM
    /// implements (69.0 for Java 25), which a type without the attribute has.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Delegate)]
    public sealed class ClassFileVersionAttribute : Attribute
    {

        readonly ushort major;
        readonly ushort minor;

        public ClassFileVersionAttribute(ushort major, ushort minor)
        {
            this.major = major;
            this.minor = minor;
        }

        /// <summary>
        /// The class file's major version.
        /// </summary>
        public ushort Major => major;

        /// <summary>
        /// The class file's minor version.
        /// </summary>
        public ushort Minor => minor;

    }

}
