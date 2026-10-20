using System;

namespace IKVM.Attributes
{

    /// <summary>
    /// Names the nest host of a type compiled from a Java class that has a NestHost attribute: the class whose nest it
    /// claims to belong to, and whose private members it may then access (JVMS 4.7.28, 5.4.4).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Delegate)]
    public sealed class NestHostAttribute : Attribute
    {

        readonly string name;

        public NestHostAttribute(string name)
        {
            this.name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>
        /// The nest host, by Java name.
        /// </summary>
        public string Name => name;

    }

}
