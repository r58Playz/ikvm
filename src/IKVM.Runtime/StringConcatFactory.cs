using System;
using System.Collections.Generic;
using System.Text;

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

    /// <summary>
    /// IL implementation of the invokedynamic call sites javac 9+ emits for string concatenation, bootstrapped by
    /// java.lang.invoke.StringConcatFactory.makeConcat or makeConcatWithConstants: the arguments are converted with
    /// String.valueOf, left to right, and joined with String.Concat. The bootstrap method is recognized by its
    /// constant pool reference and is never called, so such class files also run against a class library that has
    /// no StringConcatFactory.
    /// </summary>
    static class StringConcatFactory
    {

        const string ClassName = "java.lang.invoke.StringConcatFactory";
        const string MakeConcatSignature = "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.MethodType;)Ljava.lang.invoke.CallSite;";
        const string MakeConcatWithConstantsSignature = "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.MethodType;Ljava.lang.String;[Ljava.lang.Object;)Ljava.lang.invoke.CallSite;";

        /// <summary>
        /// Recipe tag for the next dynamic argument.
        /// </summary>
        const char TagArgument = '\u0001';

        /// <summary>
        /// Recipe tag for the next bootstrap method argument after the recipe.
        /// </summary>
        const char TagConstant = '\u0002';

        /// <summary>
        /// Emits the concatenation for an invokedynamic call site, or returns false (having emitted nothing) when the
        /// call site is not a string concatenation this handles, for the generic invokedynamic path to deal with --
        /// including recipes the bootstrap method would reject with StringConcatException.
        /// </summary>
        internal static bool Emit(RuntimeByteCodeJavaType.FinishContext context, ClassFile classFile, ClassFile.ConstantPoolItemInvokeDynamic cpi, CodeEmitter ilgen)
        {
            var bsm = classFile.GetBootstrapMethod(cpi.BootstrapMethod);
            var mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex);
            if (mh.Kind != MethodHandleKind.InvokeStatic || mh.MemberConstantPoolItem is not ClassFile.ConstantPoolItemMI mi || mi.Class != ClassName)
                return false;

            var javaBase = context.Context.JavaBase;
            if (cpi.GetRetType() != javaBase.TypeOfJavaLangString)
                return false;

            var argTypes = cpi.GetArgTypes();
            string recipe;
            if (mi.Name == "makeConcat" && mi.Signature == MakeConcatSignature && bsm.ArgumentCount == 0)
                recipe = new string(TagArgument, argTypes.Length);
            else if (mi.Name == "makeConcatWithConstants" && mi.Signature == MakeConcatWithConstantsSignature && bsm.ArgumentCount >= 1 && classFile.GetConstantPoolConstantType(bsm.GetArgument(0)) == ClassFile.ConstantType.String)
                recipe = classFile.GetConstantPoolConstantString(new StringConstantHandle(bsm.GetArgument(0).Slot));
            else
                return false;

            // a ghost interface value is a wrapper struct on the stack, not an object String.valueOf could take
            foreach (var argType in argTypes)
                if (argType.IsGhost)
                    return false;

            // split the recipe into literal text and argument references (null literal = argument)
            var pieces = new List<(string Literal, int Argument)>();
            var literal = new StringBuilder();
            int nextArgument = 0;
            int nextConstant = 1;
            foreach (var c in recipe)
            {
                if (c == TagArgument)
                {
                    if (nextArgument == argTypes.Length)
                        return false;

                    if (literal.Length > 0)
                        pieces.Add((literal.ToString(), -1));

                    literal.Clear();
                    pieces.Add((null, nextArgument++));
                }
                else if (c == TagConstant)
                {
                    if (nextConstant == bsm.ArgumentCount)
                        return false;

                    var constant = ConstantToString(classFile, bsm.GetArgument(nextConstant++));
                    if (constant == null)
                        return false;

                    literal.Append(constant);
                }
                else
                {
                    literal.Append(c);
                }
            }

            if (literal.Length > 0)
                pieces.Add((literal.ToString(), -1));

            if (nextArgument != argTypes.Length || nextConstant != bsm.ArgumentCount)
                return false;

            // the arguments are on the stack, last on top
            var arguments = new CodeEmitterLocal[argTypes.Length];
            for (int i = argTypes.Length - 1; i >= 0; i--)
            {
                arguments[i] = ilgen.DeclareLocal(argTypes[i].TypeAsLocalOrStackType);
                ilgen.Emit(OpCodes.Stloc, arguments[i]);
            }

            // convert them to strings left to right, as the bootstrap method's implementation would
            var strings = new CodeEmitterLocal[argTypes.Length];
            for (int i = 0; i < argTypes.Length; i++)
            {
                ilgen.Emit(OpCodes.Ldloc, arguments[i]);
                EmitValueOf(javaBase.TypeOfJavaLangString, argTypes[i], ilgen);
                strings[i] = ilgen.DeclareLocal(context.Context.Types.String);
                ilgen.Emit(OpCodes.Stloc, strings[i]);
            }

            var stringType = context.Context.Types.String;
            if (pieces.Count == 0)
            {
                ilgen.Emit(OpCodes.Ldstr, "");
            }
            else if (pieces.Count == 1)
            {
                EmitPiece(ilgen, pieces[0], strings);
            }
            else if (pieces.Count <= 4)
            {
                var parameters = new Type[pieces.Count];
                for (int i = 0; i < pieces.Count; i++)
                {
                    parameters[i] = stringType;
                    EmitPiece(ilgen, pieces[i], strings);
                }

                ilgen.Emit(OpCodes.Call, stringType.GetMethod("Concat", parameters));
            }
            else
            {
                ilgen.EmitLdc_I4(pieces.Count);
                ilgen.Emit(OpCodes.Newarr, stringType);
                for (int i = 0; i < pieces.Count; i++)
                {
                    ilgen.Emit(OpCodes.Dup);
                    ilgen.EmitLdc_I4(i);
                    EmitPiece(ilgen, pieces[i], strings);
                    ilgen.Emit(OpCodes.Stelem_Ref);
                }

                ilgen.Emit(OpCodes.Call, stringType.GetMethod("Concat", new[] { stringType.MakeArrayType() }));
            }

            return true;
        }

        static void EmitPiece(CodeEmitter ilgen, (string Literal, int Argument) piece, CodeEmitterLocal[] strings)
        {
            if (piece.Literal != null)
                ilgen.Emit(OpCodes.Ldstr, piece.Literal);
            else
                ilgen.Emit(OpCodes.Ldloc, strings[piece.Argument]);
        }

        /// <summary>
        /// Converts the value on the stack to its string, with the String.valueOf overload Java picks for its type.
        /// </summary>
        static void EmitValueOf(RuntimeJavaType javaLangString, RuntimeJavaType type, CodeEmitter ilgen)
        {
            var parameter = type.IsPrimitive ? type.SigName switch
            {
                // byte and short are concatenated as the int they were promoted to
                "B" or "S" => "I",
                var sig => sig,
            } : "Ljava.lang.Object;";

            var valueOf = javaLangString.GetMethod("valueOf", "(" + parameter + ")Ljava.lang.String;", false);
            valueOf.Link();
            valueOf.EmitCall(ilgen);
        }

        /// <summary>
        /// The string a recipe constant contributes, or null if it is not one whose Java string form is known here.
        /// </summary>
        static string ConstantToString(ClassFile classFile, ConstantHandle constant)
        {
            return classFile.GetConstantPoolConstantType(constant) switch
            {
                ClassFile.ConstantType.String => classFile.GetConstantPoolConstantString(new StringConstantHandle(constant.Slot)),
                ClassFile.ConstantType.Integer => classFile.GetConstantPoolConstantInteger(new IntegerConstantHandle(constant.Slot)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClassFile.ConstantType.Long => classFile.GetConstantPoolConstantLong(new LongConstantHandle(constant.Slot)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                // float, double (Java's formatting differs from .NET's) and the rest go the generic way
                _ => null,
            };
        }

    }

}
