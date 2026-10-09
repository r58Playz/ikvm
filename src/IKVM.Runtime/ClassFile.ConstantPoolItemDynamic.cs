using IKVM.ByteCode;
using IKVM.ByteCode.Decoding;

namespace IKVM.Runtime
{

    sealed partial class ClassFile
    {

        /// <summary>
        /// Type-model representation of a dynamic constant (CONSTANT_Dynamic, JVMS 4.4.10): a name, a field type and a
        /// bootstrap method, which is invoked with (Lookup, name, Class type, static arguments...) when the constant is
        /// first loaded.
        /// </summary>
        internal sealed class ConstantPoolItemDynamic : ConstantPoolItem
        {

            readonly ushort _bootstrapMethodAttributeIndex;
            readonly NameAndTypeConstantHandle _nameAndTypeHandle;

            string _name;
            string _descriptor;
            RuntimeJavaType _fieldType;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="context"></param>
            /// <param name="data"></param>
            public ConstantPoolItemDynamic(RuntimeContext context, DynamicConstantData data) :
                base(context)
            {
                _bootstrapMethodAttributeIndex = data.BootstrapMethodAttributeIndex;
                _nameAndTypeHandle = data.NameAndType;
            }

            /// <inheritdoc />
            public override void Resolve(ClassFile classFile, string[] utf8_cp, ClassFileParseOptions options)
            {
                var nameAndType = (ConstantPoolItemNameAndType)classFile.GetConstantPoolItem(_nameAndTypeHandle);
                if (nameAndType == null)
                    throw new ClassFormatError("Bad index in constant pool");

                _name = string.Intern(classFile.GetConstantPoolUtf8String(utf8_cp, nameAndType.NameHandle));
                var descriptor = classFile.GetConstantPoolUtf8String(utf8_cp, nameAndType.DescriptorHandle);
                if (IsValidFieldDescriptor(descriptor) == false)
                    throw new ClassFormatError("Invalid dynamic constant type \"{0}\"", descriptor);

                _descriptor = string.Intern(descriptor.Replace('/', '.'));
            }

            /// <inheritdoc />
            public override void Link(RuntimeJavaType thisType, LoadMode mode)
            {
                lock (this)
                    if (_fieldType != null)
                        return;

                var fieldType = thisType.ClassLoader.FieldTypeWrapperFromSig(_descriptor, mode);

                lock (this)
                    _fieldType ??= fieldType;
            }

            /// <inheritdoc />
            public override ConstantType GetConstantType() => ConstantType.Dynamic;

            /// <summary>
            /// The type of the constant's value.
            /// </summary>
            public RuntimeJavaType GetFieldType() => _fieldType;

            /// <summary>
            /// Whether the constant is a long or a double, loaded by ldc2_w and taking two stack slots.
            /// </summary>
            public bool IsWide => _descriptor == "J" || _descriptor == "D";

            public string Name => _name;

            public string Descriptor => _descriptor;

            public ushort BootstrapMethod => _bootstrapMethodAttributeIndex;

        }

    }

}
