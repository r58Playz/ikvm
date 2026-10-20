namespace IKVM.Runtime
{

    /// <summary>
    /// A component of a record class (JVMS 4.7.30): its name, its field descriptor (dotted), its generic signature or
    /// null if it has none, and its RuntimeVisibleAnnotations and RuntimeVisibleTypeAnnotations as they are in the class
    /// file, against the class's constant pool, or null. RecordComponent parses them as it does on HotSpot: when they are
    /// asked for.
    /// </summary>
    readonly record struct RecordComponentInfo(string Name, string Descriptor, string Signature, byte[] Annotations = null, byte[] TypeAnnotations = null);

}
