namespace IKVM.Runtime
{

    /// <summary>
    /// A component of a record class (JVMS 4.7.30): its name, its field descriptor (dotted), its generic signature or
    /// null if it has none, the definitions of its RuntimeVisibleAnnotations in the internal annotation format or null,
    /// and its RuntimeVisibleTypeAnnotations as they are in the class file, against the class's constant pool, or null.
    /// </summary>
    readonly record struct RecordComponentInfo(string Name, string Descriptor, string Signature, object[] Annotations = null, byte[] TypeAnnotations = null);

}
