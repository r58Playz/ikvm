using System;

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

        const string SwitchBootstrapsClassName = "java.lang.runtime.SwitchBootstraps";
        const string SwitchBootstrapSignature = "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.MethodType;[Ljava.lang.Object;)Ljava.lang.invoke.CallSite;";

        /// <summary>
        /// Emits IL for an invokedynamic bootstrapped by java.lang.runtime.SwitchBootstraps.typeSwitch or enumSwitch,
        /// which javac 21+ emits for pattern switches, or returns false (having emitted nothing) for the generic
        /// invokedynamic path to deal with it. The call site takes (target, restart) and returns the index of the first
        /// label from restart on that matches the target, -1 for a null target, and the label count when none matches.
        /// The bootstrap method is recognized by its constant pool reference and never called.
        /// </summary>
        /// <remarks>
        /// Labels are tested as the JDK's implementation does: a Class label by instanceof, an Integer label against a
        /// Number's intValue() or a Character's charValue(), a String label by equals in typeSwitch and against the enum
        /// constant's name() in enumSwitch. EnumDesc labels (condy) and primitive selectors are left to the generic path.
        /// </remarks>
        bool EmitSwitchBootstrap(ClassFile.ConstantPoolItemInvokeDynamic cpi)
        {
            var bsm = classFile.GetBootstrapMethod(cpi.BootstrapMethod);
            var mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex);
            if (mh.Kind != MethodHandleKind.InvokeStatic || mh.MemberConstantPoolItem is not ClassFile.ConstantPoolItemMI mi || mi.Class != SwitchBootstrapsClassName || mi.Signature != SwitchBootstrapSignature)
                return false;

            bool isEnumSwitch;
            if (mi.Name == "typeSwitch")
                isEnumSwitch = false;
            else if (mi.Name == "enumSwitch")
                isEnumSwitch = true;
            else
                return false;

            var context = finish.Context;
            var argTypes = cpi.GetArgTypes();
            var INT = context.PrimitiveJavaTypeFactory.INT;
            if (argTypes.Length != 2 || argTypes[1] != INT || cpi.GetRetType() != INT || argTypes[0].IsPrimitive || argTypes[0].IsGhost)
                return false;

            for (int i = 0; i < bsm.ArgumentCount; i++)
            {
                switch (classFile.GetConstantPoolConstantType(bsm.GetArgument(i)))
                {
                    case ClassFile.ConstantType.Class:
                    case ClassFile.ConstantType.String:
                        break;
                    case ClassFile.ConstantType.Integer when isEnumSwitch == false:
                        break;
                    default:
                        return false;
                }
            }

            var target = ilGenerator.DeclareLocal(argTypes[0].TypeAsLocalOrStackType);
            var restart = ilGenerator.DeclareLocal(context.Types.Int32);
            ilGenerator.Emit(OpCodes.Stloc, restart);
            ilGenerator.Emit(OpCodes.Stloc, target);

            var end = ilGenerator.DefineLabel();
            var isNull = ilGenerator.DefineLabel();
            ilGenerator.Emit(OpCodes.Ldloc, target);
            ilGenerator.EmitBrfalse(isNull);

            // jump to the test for label number restart; each test falls through to the next on a mismatch
            var tests = new CodeEmitterLabel[bsm.ArgumentCount + 1];
            for (int i = 0; i < tests.Length; i++)
                tests[i] = ilGenerator.DefineLabel();

            if (bsm.ArgumentCount > 0)
            {
                var jump = new CodeEmitterLabel[bsm.ArgumentCount];
                Array.Copy(tests, jump, jump.Length);
                ilGenerator.Emit(OpCodes.Ldloc, restart);
                ilGenerator.EmitSwitch(jump);
            }

            ilGenerator.EmitBr(tests[bsm.ArgumentCount]);

            for (int i = 0; i < bsm.ArgumentCount; i++)
            {
                ilGenerator.MarkLabel(tests[i]);
                EmitSwitchLabelTest(bsm.GetArgument(i), target, isEnumSwitch);
                ilGenerator.EmitBrfalse(tests[i + 1]);
                ilGenerator.EmitLdc_I4(i);
                ilGenerator.EmitBr(end);
            }

            ilGenerator.MarkLabel(tests[bsm.ArgumentCount]);
            ilGenerator.EmitLdc_I4(bsm.ArgumentCount);
            ilGenerator.EmitBr(end);

            ilGenerator.MarkLabel(isNull);
            ilGenerator.EmitLdc_I4(-1);
            ilGenerator.MarkLabel(end);
            return true;
        }

        /// <summary>
        /// Pushes whether the (non-null) target matches the switch label.
        /// </summary>
        void EmitSwitchLabelTest(ConstantHandle label, CodeEmitterLocal target, bool isEnumSwitch)
        {
            var context = finish.Context;
            switch (classFile.GetConstantPoolConstantType(label))
            {
                case ClassFile.ConstantType.Class:
                    {
                        var tw = classFile.GetConstantPoolClassType(new ClassConstantHandle(label.Slot));
                        ilGenerator.Emit(OpCodes.Ldloc, target);
                        if (tw.IsUnloadable)
                            EmitDynamicInstanceOf(tw);
                        else
                            tw.EmitInstanceOf(ilGenerator);

                        break;
                    }
                case ClassFile.ConstantType.String when isEnumSwitch:
                    {
                        // the target is of the switch's enum type, so its constant's name identifies it
                        var javaLangEnum = context.ClassLoaderFactory.LoadClassCritical("java.lang.Enum");
                        var name = javaLangEnum.GetMethod("name", "()Ljava.lang.String;", false);
                        name.Link();
                        ilGenerator.Emit(OpCodes.Ldstr, classFile.GetConstantPoolConstantString(new StringConstantHandle(label.Slot)));
                        ilGenerator.Emit(OpCodes.Ldloc, target);
                        ilGenerator.EmitCastclass(javaLangEnum.TypeAsBaseType);
                        name.EmitCall(ilGenerator);
                        ilGenerator.Emit(OpCodes.Call, context.Types.String.GetMethod("Equals", new[] { context.Types.String }));
                        break;
                    }
                case ClassFile.ConstantType.String:
                    {
                        ilGenerator.Emit(OpCodes.Ldstr, classFile.GetConstantPoolConstantString(new StringConstantHandle(label.Slot)));
                        ilGenerator.Emit(OpCodes.Ldloc, target);
                        ilGenerator.Emit(OpCodes.Call, context.Types.String.GetMethod("Equals", new[] { context.Types.Object }));
                        break;
                    }
                case ClassFile.ConstantType.Integer:
                    {
                        // an Integer label matches a boxed Byte, Short or Integer selector of that value, or a Character
                        var value = classFile.GetConstantPoolConstantInteger(new IntegerConstantHandle(label.Slot));
                        var notNumber = ilGenerator.DefineLabel();
                        var notCharacter = ilGenerator.DefineLabel();
                        var done = ilGenerator.DefineLabel();
                        EmitBoxedIntegerTest("java.lang.Number", "intValue", "()I", target, value, notNumber, done);
                        ilGenerator.MarkLabel(notNumber);
                        ilGenerator.Emit(OpCodes.Pop);
                        EmitBoxedIntegerTest("java.lang.Character", "charValue", "()C", target, value, notCharacter, done);
                        ilGenerator.MarkLabel(notCharacter);
                        ilGenerator.Emit(OpCodes.Pop);
                        ilGenerator.EmitLdc_I4(0);
                        ilGenerator.MarkLabel(done);
                        break;
                    }
                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>
        /// If the target is an instance of the class, pushes whether its value equals the label's and branches to done;
        /// otherwise branches to mismatch with a null reference on the stack.
        /// </summary>
        void EmitBoxedIntegerTest(string className, string accessor, string accessorSignature, CodeEmitterLocal target, int value, CodeEmitterLabel mismatch, CodeEmitterLabel done)
        {
            var tw = finish.Context.ClassLoaderFactory.LoadClassCritical(className);
            var get = tw.GetMethod(accessor, accessorSignature, false);
            get.Link();
            ilGenerator.Emit(OpCodes.Ldloc, target);
            ilGenerator.Emit(OpCodes.Isinst, tw.TypeAsBaseType);
            ilGenerator.Emit(OpCodes.Dup);
            ilGenerator.EmitBrfalse(mismatch);
            get.EmitCallvirt(ilGenerator);
            ilGenerator.EmitLdc_I4(value);
            ilGenerator.Emit(OpCodes.Ceq);
            ilGenerator.EmitBr(done);
        }

    }

}
