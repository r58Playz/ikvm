using System;

using IKVM.ByteCode;
using IKVM.ByteCode.Buffers;
using IKVM.ByteCode.Decoding;
using IKVM.ByteCode.Encoding;

namespace IKVM.Runtime.StubGen
{

    /// <summary>
    /// Writes the stub of a class from its original class file: the class file as it is, but for its method bodies,
    /// which throw. This is what a class loader reading the class from the class path sees, so it is what the class file
    /// would show on OpenJDK as far as anything but code goes: its version, flags, members in their order and every
    /// attribute -- InnerClasses, EnclosingMethod, NestHost, NestMembers, PermittedSubclasses, Record, SourceFile,
    /// SourceDebugExtension, Signature, MethodParameters, Exceptions and the visible and invisible annotations among them.
    /// </summary>
    static class ClassFileStubWriter
    {

        /// <summary>
        /// Writes the stub of the class whose class file is given.
        /// </summary>
        /// <param name="classFile">The original class file.</param>
        /// <param name="assemblyName">The full name of the assembly the class is compiled into, which the stub names.</param>
        /// <returns></returns>
        public static byte[] Write(byte[] classFile, string assemblyName)
        {
            if (classFile is null)
                throw new ArgumentNullException(nameof(classFile));
            if (assemblyName is null)
                throw new ArgumentNullException(nameof(assemblyName));

            using var clazz = IKVM.ByteCode.Decoding.ClassFile.Read(classFile);
            var constants = clazz.Constants;

            var builder = new ClassFileBuilder(
                clazz.Version,
                clazz.AccessFlags,
                constants.Get(clazz.This).Name,
                clazz.Super.IsNotNil ? constants.Get(clazz.Super).Name : null);

            foreach (var iface in clazz.Interfaces)
                builder.AddInterface(constants.Get(iface.Class).Name);

            foreach (var field in clazz.Fields)
            {
                var attributes = new AttributeTableBuilder(builder.Constants);
                CopyAttributes(constants, builder, field.Attributes, attributes);
                builder.AddField(field.AccessFlags, constants.Get(field.Name).Value, constants.Get(field.Descriptor).Value, attributes);
            }

            foreach (var method in clazz.Methods)
            {
                var descriptor = constants.Get(method.Descriptor).Value;
                var attributes = new AttributeTableBuilder(builder.Constants);
                if ((method.AccessFlags & (AccessFlag.Abstract | AccessFlag.Native)) == 0)
                    StubGenerator.AddThrowingCode(builder, attributes, GetParameterSlots(descriptor) + ((method.AccessFlags & AccessFlag.Static) == 0 ? 1 : 0));

                CopyAttributes(constants, builder, method.Attributes, attributes);
                builder.AddMethod(method.AccessFlags, constants.Get(method.Name).Value, descriptor, attributes);
            }

            CopyAttributes(constants, builder, clazz.Attributes, builder.Attributes);
            StubGenerator.AddAssemblyAttribute(builder, assemblyName);

            var blob = new BlobBuilder();
            builder.Serialize(blob);
            return blob.ToArray();
        }

        /// <summary>
        /// Copies the attributes of a class, field or method to the stub. Code, and the BootstrapMethods its
        /// invokedynamic and ldc instructions use, are left out, and so are the attributes not in the JVMS, whose
        /// constant pool references could not be followed.
        /// </summary>
        /// <param name="constants"></param>
        /// <param name="builder"></param>
        /// <param name="source"></param>
        /// <param name="target"></param>
        static void CopyAttributes(ConstantTable constants, ClassFileBuilder builder, AttributeTable source, AttributeTableBuilder target)
        {
            foreach (var attribute in source)
            {
                switch (constants.Get(attribute.Name).Value)
                {
                    case AttributeName.Code:
                    case AttributeName.BootstrapMethods:
                        break;
                    case AttributeName.ConstantValue:
                    case AttributeName.Exceptions:
                    case AttributeName.InnerClasses:
                    case AttributeName.EnclosingMethod:
                    case AttributeName.Synthetic:
                    case AttributeName.Signature:
                    case AttributeName.SourceFile:
                    case AttributeName.SourceDebugExtension:
                    case AttributeName.Deprecated:
                    case AttributeName.RuntimeVisibleAnnotations:
                    case AttributeName.RuntimeInvisibleAnnotations:
                    case AttributeName.RuntimeVisibleParameterAnnotations:
                    case AttributeName.RuntimeInvisibleParameterAnnotations:
                    case AttributeName.RuntimeVisibleTypeAnnotations:
                    case AttributeName.RuntimeInvisibleTypeAnnotations:
                    case AttributeName.AnnotationDefault:
                    case AttributeName.MethodParameters:
                    case AttributeName.Module:
                    case AttributeName.ModulePackages:
                    case AttributeName.ModuleMainClass:
                    case AttributeName.NestHost:
                    case AttributeName.NestMembers:
                    case AttributeName.Record:
                    case AttributeName.PermittedSubclasses:
                        attribute.CopyTo(constants, builder.Constants, ref target.Encoder);
                        break;
                }
            }
        }

        /// <summary>
        /// Gets the local variable slots taken by the parameters of a method descriptor.
        /// </summary>
        /// <param name="descriptor"></param>
        /// <returns></returns>
        static int GetParameterSlots(string descriptor)
        {
            var slots = 0;
            for (int i = 1; descriptor[i] != ')'; i++)
            {
                switch (descriptor[i])
                {
                    case 'J':
                    case 'D':
                        slots += 2;
                        break;
                    case '[':
                        while (descriptor[i] == '[')
                            i++;
                        if (descriptor[i] == 'L')
                            i = descriptor.IndexOf(';', i);
                        slots++;
                        break;
                    case 'L':
                        i = descriptor.IndexOf(';', i);
                        slots++;
                        break;
                    default:
                        slots++;
                        break;
                }
            }

            return slots;
        }

    }

}
