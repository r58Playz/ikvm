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

        const AccessFlag AccessMask = AccessFlag.Public | AccessFlag.Private | AccessFlag.Protected;

        /// <summary>
        /// What ikvmc changes of a class as it compiles it, which its stub shows: an internal class or member (in a
        /// -privatepackage package, or annotated ikvm.lang.Internal) is not public, protected or private, and a member
        /// hidden from Java is not there.
        /// </summary>
        sealed class Compiled
        {

            public bool IsInternal;
            public bool[] InternalFields;
            public bool[] HiddenFields;
            public bool[] InternalMethods;
            public bool[] HiddenMethods;

        }

#if !EXPORTER

        /// <summary>
        /// Writes the stub of a class ikvmc compiles, from its class file as IKVM parsed it.
        /// </summary>
        /// <param name="classFile">The parsed class file.</param>
        /// <param name="assemblyName">The full name of the assembly the class is compiled into, which the stub names.</param>
        /// <returns></returns>
        public static byte[] Write(IKVM.Runtime.ClassFile classFile, string assemblyName)
        {
            if (classFile is null)
                throw new ArgumentNullException(nameof(classFile));
            if (assemblyName is null)
                throw new ArgumentNullException(nameof(assemblyName));

            var compiled = new Compiled()
            {
                IsInternal = classFile.IsInternal,
                InternalFields = new bool[classFile.Fields.Length],
                HiddenFields = new bool[classFile.Fields.Length],
                InternalMethods = new bool[classFile.Methods.Length],
                HiddenMethods = new bool[classFile.Methods.Length],
            };

            for (int i = 0; i < classFile.Fields.Length; i++)
            {
                compiled.InternalFields[i] = classFile.Fields[i].IsInternal;
                compiled.HiddenFields[i] = IsHiddenFromJava(classFile.Fields[i].Annotations);
            }

            for (int i = 0; i < classFile.Methods.Length; i++)
            {
                compiled.InternalMethods[i] = classFile.Methods[i].IsInternal;
                compiled.HiddenMethods[i] = IsHiddenFromJava(classFile.Methods[i].Annotations);
            }

            return Write(classFile.Decoded, assemblyName, compiled);
        }

        /// <summary>
        /// Returns <c>true</c> if a member's annotations hide it from Java code or reflection, as the HideFromJava
        /// attribute applied as an annotation does when it is given no flags, or Code or Reflection among them.
        /// </summary>
        /// <param name="annotations"></param>
        /// <returns></returns>
        static bool IsHiddenFromJava(object[] annotations)
        {
            if (annotations == null)
                return false;

            foreach (object[] annotation in annotations)
            {
                if (annotation[1] as string != "Lcli/IKVM/Attributes/HideFromJavaAttribute$Annotation;")
                    continue;

                if (annotation.Length <= 2)
                    return true;

                for (int i = 3; i < annotation.Length; i += 2)
                    if (NamesHidingFlag(annotation[i]))
                        return true;
            }

            return false;
        }

        /// <summary>
        /// Returns <c>true</c> if an annotation element value names the All, Code or Reflection flag of HideFromJavaFlags.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        static bool NamesHidingFlag(object value)
        {
            if (value is string s)
                return s is "All" or "Code" or "Reflection";

            if (value is object[] array)
                foreach (var element in array)
                    if (NamesHidingFlag(element))
                        return true;

            return false;
        }

#endif

        /// <summary>
        /// Writes a stub from a class file as it is, such as a stub to name another assembly.
        /// </summary>
        /// <param name="classFile">The class file.</param>
        /// <param name="assemblyName">The full name of the assembly the class is compiled into, which the stub names.</param>
        /// <returns></returns>
        public static byte[] Write(byte[] classFile, string assemblyName)
        {
            if (classFile is null)
                throw new ArgumentNullException(nameof(classFile));
            if (assemblyName is null)
                throw new ArgumentNullException(nameof(assemblyName));

            using var clazz = IKVM.ByteCode.Decoding.ClassFile.Read(classFile);
            return Write(clazz, assemblyName, null);
        }

        static byte[] Write(IKVM.ByteCode.Decoding.ClassFile clazz, string assemblyName, Compiled compiled)
        {
            var constants = clazz.Constants;
            var isInternal = compiled != null && compiled.IsInternal;

            var builder = new ClassFileBuilder(
                clazz.Version,
                isInternal ? clazz.AccessFlags & ~AccessMask : clazz.AccessFlags,
                constants.Get(clazz.This).Name,
                clazz.Super.IsNotNil ? constants.Get(clazz.Super).Name : null);

            foreach (var iface in clazz.Interfaces)
                builder.AddInterface(constants.Get(iface.Class).Name);

            for (int i = 0; i < clazz.Fields.Count; i++)
            {
                var field = clazz.Fields[i];
                if (compiled != null && compiled.HiddenFields[i])
                    continue;

                var attributes = new AttributeTableBuilder(builder.Constants);
                CopyAttributes(clazz, builder, field.Attributes, attributes, false);

                var accessFlags = compiled != null && compiled.InternalFields[i] ? field.AccessFlags & ~AccessMask : field.AccessFlags;
                builder.AddField(accessFlags, constants.Get(field.Name).Value, constants.Get(field.Descriptor).Value, attributes);
            }

            for (int i = 0; i < clazz.Methods.Count; i++)
            {
                var method = clazz.Methods[i];
                if (compiled != null && compiled.HiddenMethods[i])
                    continue;

                var descriptor = constants.Get(method.Descriptor).Value;
                var attributes = new AttributeTableBuilder(builder.Constants);
                if ((method.AccessFlags & (AccessFlag.Abstract | AccessFlag.Native)) == 0)
                    StubGenerator.AddThrowingCode(builder, attributes, GetParameterSlots(descriptor) + ((method.AccessFlags & AccessFlag.Static) == 0 ? 1 : 0));

                CopyAttributes(clazz, builder, method.Attributes, attributes, false);

                var accessFlags = compiled != null && compiled.InternalMethods[i] ? method.AccessFlags & ~AccessMask : method.AccessFlags;
                builder.AddMethod(accessFlags, constants.Get(method.Name).Value, descriptor, attributes);
            }

            CopyAttributes(clazz, builder, clazz.Attributes, builder.Attributes, isInternal);
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
        /// <param name="clazz"></param>
        /// <param name="builder"></param>
        /// <param name="source"></param>
        /// <param name="target"></param>
        /// <param name="isInternal">Whether the class is internal, which its own InnerClasses entry is then made to show.</param>
        static void CopyAttributes(IKVM.ByteCode.Decoding.ClassFile clazz, ClassFileBuilder builder, AttributeTable source, AttributeTableBuilder target, bool isInternal)
        {
            var constants = clazz.Constants;

            foreach (var attribute in source)
            {
                switch (constants.Get(attribute.Name).Value)
                {
                    case AttributeName.Code:
                    case AttributeName.BootstrapMethods:
                        break;
                    case AttributeName.InnerClasses when isInternal:
                        CopyInnerClassesOfInternalClass(clazz, builder, (InnerClassesAttribute)attribute, target);
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
        /// Copies the InnerClasses attribute of an internal class, whose own entry is then not public, protected or private.
        /// </summary>
        /// <param name="clazz"></param>
        /// <param name="builder"></param>
        /// <param name="attribute"></param>
        /// <param name="target"></param>
        static void CopyInnerClassesOfInternalClass(IKVM.ByteCode.Decoding.ClassFile clazz, ClassFileBuilder builder, InnerClassesAttribute attribute, AttributeTableBuilder target)
        {
            var constants = clazz.Constants;
            var self = constants.Get(clazz.This).Name;
            var table = attribute.Table;

            target.InnerClasses(e =>
            {
                for (int i = 0; i < table.Count; i++)
                {
                    var entry = table[i];
                    var inner = entry.Inner.IsNotNil ? constants.Get(entry.Inner).Name : null;
                    e.InnerClass(
                        inner != null ? builder.Constants.GetOrAddClass(inner) : default,
                        entry.Outer.IsNotNil ? builder.Constants.GetOrAddClass(constants.Get(entry.Outer).Name) : default,
                        entry.InnerName.IsNotNil ? builder.Constants.GetOrAddUtf8(constants.Get(entry.InnerName).Value) : default,
                        inner == self ? entry.InnerAccessFlags & ~AccessMask : entry.InnerAccessFlags);
                }
            });
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
