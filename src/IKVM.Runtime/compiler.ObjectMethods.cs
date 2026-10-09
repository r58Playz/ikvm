using System;
using System.Collections.Generic;

using IKVM.ByteCode;

#if IMPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;

using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif

namespace IKVM.Runtime
{

    partial class Compiler
    {

        const string ObjectMethodsClassName = "java.lang.runtime.ObjectMethods";
        const string ObjectMethodsSignature = "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.TypeDescriptor;Ljava.lang.Class;Ljava.lang.String;[Ljava.lang.invoke.MethodHandle;)Ljava.lang.Object;";

        /// <summary>
        /// Emits IL for an invokedynamic bootstrapped by java.lang.runtime.ObjectMethods.bootstrap, which javac emits for
        /// a record's implicit toString, hashCode and equals, or returns false (having emitted nothing) for the generic
        /// invokedynamic path to deal with it. The bootstrap method is recognized by its constant pool reference and never
        /// called. The arguments are the record class, its component names and a getter (a REF_getField handle) per
        /// component.
        /// </summary>
        /// <remarks>
        /// The results are those of the JDK's implementation: toString is the class's simple name followed by
        /// [name=value, ...] with String.valueOf values; hashCode starts at 0 and is multiplied by 31 before each
        /// component's hash (the wrapper class's hashCode for a primitive, Objects.hashCode otherwise) is added; equals
        /// holds for the same object, and for an instance of the record class whose components all compare equal (with
        /// Float.compare or Double.compare for floating point, == for the other primitives, Objects.equals otherwise).
        /// </remarks>
        bool EmitObjectMethodsBootstrap(ClassFile.ConstantPoolItemInvokeDynamic cpi)
        {
            var bsm = classFile.GetBootstrapMethod(cpi.BootstrapMethod);
            var mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex);
            if (mh.Kind != MethodHandleKind.InvokeStatic || mh.MemberConstantPoolItem is not ClassFile.ConstantPoolItemMI mi || mi.Class != ObjectMethodsClassName || mi.Name != "bootstrap" || mi.Signature != ObjectMethodsSignature)
                return false;

            if (bsm.ArgumentCount < 2 || classFile.GetConstantPoolConstantType(bsm.GetArgument(0)) != ClassFile.ConstantType.Class || classFile.GetConstantPoolConstantType(bsm.GetArgument(1)) != ClassFile.ConstantType.String)
                return false;

            var recordType = classFile.GetConstantPoolClassType(new ClassConstantHandle(bsm.GetArgument(0).Slot));
            if (recordType.IsUnloadable || recordType.IsGhost)
                return false;

            var names = classFile.GetConstantPoolConstantString(new StringConstantHandle(bsm.GetArgument(1).Slot));
            var componentNames = names.Length == 0 ? Array.Empty<string>() : names.Split(';');
            if (componentNames.Length != bsm.ArgumentCount - 2)
                return false;

            // each getter must read an accessible field of the record class
            var fields = new RuntimeJavaField[componentNames.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var arg = bsm.GetArgument(i + 2);
                if (classFile.GetConstantPoolConstantType(arg) != ClassFile.ConstantType.MethodHandle)
                    return false;

                var getter = classFile.GetConstantPoolConstantMethodHandle(new MethodHandleConstantHandle(arg.Slot));
                if (getter.Kind != MethodHandleKind.GetField || getter.Member is not RuntimeJavaField field || field.IsStatic || field.DeclaringType != recordType)
                    return false;

                if (field.FieldTypeWrapper.IsUnloadable || field.FieldTypeWrapper.IsGhost || field.IsAccessibleFrom(recordType, clazz, recordType) == false)
                    return false;

                field.Link();
                fields[i] = field;
            }

            var context = finish.Context;
            var argTypes = cpi.GetArgTypes();
            var retType = cpi.GetRetType();
            switch (cpi.Name)
            {
                case "toString" when argTypes.Length == 1 && argTypes[0] == recordType && retType == context.JavaBase.TypeOfJavaLangString:
                    EmitRecordToString(recordType, componentNames, fields);
                    return true;
                case "hashCode" when argTypes.Length == 1 && argTypes[0] == recordType && retType == context.PrimitiveJavaTypeFactory.INT:
                    EmitRecordHashCode(recordType, fields);
                    return true;
                case "equals" when argTypes.Length == 2 && argTypes[0] == recordType && argTypes[1] == context.JavaBase.TypeOfJavaLangObject && retType == context.PrimitiveJavaTypeFactory.BOOLEAN:
                    EmitRecordEquals(recordType, fields);
                    return true;
                default:
                    return false;
            }
        }

        void EmitRecordToString(RuntimeJavaType recordType, string[] names, RuntimeJavaField[] fields)
        {
            var context = finish.Context;
            var stringType = context.Types.String;
            var receiver = ilGenerator.DeclareLocal(recordType.TypeAsLocalOrStackType);
            ilGenerator.Emit(OpCodes.Stloc, receiver);

            var getSimpleName = context.JavaBase.TypeOfJavaLangClass.GetMethod("getSimpleName", "()Ljava.lang.String;", false);
            getSimpleName.Link();

            // simple name, "[", then name=value pieces separated by ", ", then "]"
            var pieceCount = 2 + fields.Length * 2;
            ilGenerator.EmitLdc_I4(pieceCount);
            ilGenerator.Emit(OpCodes.Newarr, stringType);
            int piece = 0;

            ilGenerator.Emit(OpCodes.Dup);
            ilGenerator.EmitLdc_I4(piece++);
            EmitLoadClass(ilGenerator, recordType);
            getSimpleName.EmitCallvirt(ilGenerator);
            ilGenerator.Emit(OpCodes.Stelem_Ref);

            for (int i = 0; i < fields.Length; i++)
            {
                ilGenerator.Emit(OpCodes.Dup);
                ilGenerator.EmitLdc_I4(piece++);
                ilGenerator.Emit(OpCodes.Ldstr, (i == 0 ? "[" : ", ") + names[i] + "=");
                ilGenerator.Emit(OpCodes.Stelem_Ref);

                ilGenerator.Emit(OpCodes.Dup);
                ilGenerator.EmitLdc_I4(piece++);
                ilGenerator.Emit(OpCodes.Ldloc, receiver);
                fields[i].EmitGet(ilGenerator);
                EmitStringValueOf(fields[i].FieldTypeWrapper);
                ilGenerator.Emit(OpCodes.Stelem_Ref);
            }

            ilGenerator.Emit(OpCodes.Dup);
            ilGenerator.EmitLdc_I4(piece++);
            ilGenerator.Emit(OpCodes.Ldstr, fields.Length == 0 ? "[]" : "]");
            ilGenerator.Emit(OpCodes.Stelem_Ref);

            ilGenerator.Emit(OpCodes.Call, stringType.GetMethod("Concat", new[] { stringType.MakeArrayType() }));
        }

        void EmitRecordHashCode(RuntimeJavaType recordType, RuntimeJavaField[] fields)
        {
            var receiver = ilGenerator.DeclareLocal(recordType.TypeAsLocalOrStackType);
            ilGenerator.Emit(OpCodes.Stloc, receiver);
            ilGenerator.EmitLdc_I4(0);
            foreach (var field in fields)
            {
                ilGenerator.EmitLdc_I4(31);
                ilGenerator.Emit(OpCodes.Mul);
                ilGenerator.Emit(OpCodes.Ldloc, receiver);
                field.EmitGet(ilGenerator);
                EmitComponentHashCode(field.FieldTypeWrapper);
                ilGenerator.Emit(OpCodes.Add);
            }
        }

        void EmitRecordEquals(RuntimeJavaType recordType, RuntimeJavaField[] fields)
        {
            var context = finish.Context;
            var receiver = ilGenerator.DeclareLocal(recordType.TypeAsLocalOrStackType);
            var other = ilGenerator.DeclareLocal(context.Types.Object);
            ilGenerator.Emit(OpCodes.Stloc, other);
            ilGenerator.Emit(OpCodes.Stloc, receiver);

            var isTrue = ilGenerator.DefineLabel();
            var isFalse = ilGenerator.DefineLabel();
            var end = ilGenerator.DefineLabel();

            ilGenerator.Emit(OpCodes.Ldloc, receiver);
            ilGenerator.Emit(OpCodes.Ldloc, other);
            ilGenerator.EmitBeq(isTrue);

            ilGenerator.Emit(OpCodes.Ldloc, other);
            recordType.EmitInstanceOf(ilGenerator);
            ilGenerator.EmitBrfalse(isFalse);

            var that = ilGenerator.DeclareLocal(recordType.TypeAsLocalOrStackType);
            ilGenerator.Emit(OpCodes.Ldloc, other);
            recordType.EmitCheckcast(ilGenerator);
            ilGenerator.Emit(OpCodes.Stloc, that);

            foreach (var field in fields)
            {
                ilGenerator.Emit(OpCodes.Ldloc, receiver);
                field.EmitGet(ilGenerator);
                ilGenerator.Emit(OpCodes.Ldloc, that);
                field.EmitGet(ilGenerator);
                EmitComponentEquals(field.FieldTypeWrapper);
                ilGenerator.EmitBrfalse(isFalse);
            }

            ilGenerator.MarkLabel(isTrue);
            ilGenerator.EmitLdc_I4(1);
            ilGenerator.EmitBr(end);
            ilGenerator.MarkLabel(isFalse);
            ilGenerator.EmitLdc_I4(0);
            ilGenerator.MarkLabel(end);
        }

        /// <summary>
        /// Converts the value on the stack to a string with the String.valueOf overload Java picks for its type.
        /// </summary>
        void EmitStringValueOf(RuntimeJavaType type)
        {
            var parameter = type.IsPrimitive ? (type.SigName is "B" or "S" ? "I" : type.SigName) : "Ljava.lang.Object;";
            var valueOf = finish.Context.JavaBase.TypeOfJavaLangString.GetMethod("valueOf", "(" + parameter + ")Ljava.lang.String;", false);
            valueOf.Link();
            valueOf.EmitCall(ilGenerator);
        }

        /// <summary>
        /// Replaces the component value on the stack with its hash code.
        /// </summary>
        void EmitComponentHashCode(RuntimeJavaType type)
        {
            RuntimeJavaMethod hashCode;
            if (type.IsPrimitive)
                hashCode = GetBoxType(finish.Context, type, out _).GetMethod("hashCode", "(" + type.SigName + ")I", false);
            else
                hashCode = finish.Context.ClassLoaderFactory.LoadClassCritical("java.util.Objects").GetMethod("hashCode", "(Ljava.lang.Object;)I", false);

            hashCode.Link();
            hashCode.EmitCall(ilGenerator);
        }

        /// <summary>
        /// Replaces the two component values on the stack with whether they are equal.
        /// </summary>
        void EmitComponentEquals(RuntimeJavaType type)
        {
            if (type.IsPrimitive && type.SigName is "F" or "D")
            {
                var compare = GetBoxType(finish.Context, type, out _).GetMethod("compare", "(" + type.SigName + type.SigName + ")I", false);
                compare.Link();
                compare.EmitCall(ilGenerator);
                ilGenerator.EmitLdc_I4(0);
                ilGenerator.Emit(OpCodes.Ceq);
            }
            else if (type.IsPrimitive)
            {
                ilGenerator.Emit(OpCodes.Ceq);
            }
            else
            {
                var equals = finish.Context.ClassLoaderFactory.LoadClassCritical("java.util.Objects").GetMethod("equals", "(Ljava.lang.Object;Ljava.lang.Object;)Z", false);
                equals.Link();
                equals.EmitCall(ilGenerator);
            }
        }

    }

}
