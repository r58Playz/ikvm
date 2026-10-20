using System;

using IKVM.Runtime;

namespace IKVM.Attributes
{

    /// <summary>
    /// Names the classes the NestMembers attribute of the Java class a type is compiled from lists: the members of the
    /// nest it hosts (JVMS 4.7.29, 5.4.4).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Delegate)]
    public sealed class NestMembersAttribute : Attribute
    {

        readonly string[] names;

        public NestMembersAttribute(string[] names)
        {
            this.names = UnicodeUtil.UnescapeInvalidSurrogates(names ?? throw new ArgumentNullException(nameof(names)));
        }

        /// <summary>
        /// The nest members, by Java name.
        /// </summary>
        public string[] Names => names;

    }

}
