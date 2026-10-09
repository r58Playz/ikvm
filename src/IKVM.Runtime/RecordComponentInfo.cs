namespace IKVM.Runtime
{

    /// <summary>
    /// A component of a record class (JVMS 4.7.30): its name, its field descriptor (dotted) and its generic signature,
    /// or null if it has none.
    /// </summary>
    readonly record struct RecordComponentInfo(string Name, string Descriptor, string Signature);

}
