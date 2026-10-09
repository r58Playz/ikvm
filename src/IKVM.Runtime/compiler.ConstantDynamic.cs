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

        const string ConstantBootstrapsClassName = "java.lang.invoke.ConstantBootstraps";
        const string ConstantBootstrapPrefix = "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.Class;";

        /// <summary>
        /// Loads a dynamic constant (CONSTANT_Dynamic, JDK 11) as a value of its type.
        /// </summary>
        void EmitLoadDynamicConstant(CodeEmitter ilgen, ConstantHandle handle)
        {
            var constant = classFile.GetConstantPoolConstantDynamic(handle);
            if (EmitConstantBootstrapsIntrinsic(ilgen, constant))
                return;

            finish.GetValue<DynamicConstant>(handle.Slot).Emit(this, ilgen, handle);
            EmitConvertDynamicConstant(ilgen, constant.GetFieldType());
        }

        /// <summary>
        /// Converts the resolved value of a dynamic constant, an object (boxed for a primitive type), to the stack
        /// representation of its type. The resolution already checked that it is one.
        /// </summary>
        void EmitConvertDynamicConstant(CodeEmitter ilgen, RuntimeJavaType type)
        {
            if (type.IsUnloadable)
                return;

            if (type.IsPrimitive)
            {
                var wrapper = GetBoxType(finish.Context, type, out var unbox);
                ilgen.EmitCastclass(wrapper.TypeAsBaseType);
                unbox.Link();
                unbox.EmitCallvirt(ilgen);
            }
            else
            {
                type.EmitCheckcast(ilgen);
            }
        }

        /// <summary>
        /// Emits the value of a dynamic constant bootstrapped by one of the java.lang.invoke.ConstantBootstraps methods
        /// whose result follows from its arguments alone -- nullConstant, primitiveClass, enumConstant and getStaticFinal
        /// -- or returns false (having emitted nothing). The bootstrap method is recognized by its constant pool reference
        /// and not called, so this works against a class library without ConstantBootstraps.
        /// </summary>
        bool EmitConstantBootstrapsIntrinsic(CodeEmitter ilgen, ClassFile.ConstantPoolItemDynamic constant)
        {
            var bsm = classFile.GetBootstrapMethod(constant.BootstrapMethod);
            var mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex);
            if (mh.Kind != MethodHandleKind.InvokeStatic || mh.MemberConstantPoolItem is not ClassFile.ConstantPoolItemMI mi || mi.Class != ConstantBootstrapsClassName)
                return false;

            var type = constant.GetFieldType();
            if (type.IsUnloadable)
                return false;

            var javaBase = finish.Context.JavaBase;
            switch (mi.Name)
            {
                case "nullConstant" when mi.Signature == ConstantBootstrapPrefix + ")Ljava.lang.Object;" && bsm.ArgumentCount == 0 && type.IsPrimitive == false:
                    ilgen.Emit(OpCodes.Ldnull);
                    return true;
                case "primitiveClass" when mi.Signature == ConstantBootstrapPrefix + ")Ljava.lang.Class;" && bsm.ArgumentCount == 0 && type == javaBase.TypeOfJavaLangClass:
                    {
                        var primitive = constant.Name.Length == 1 ? PrimitiveFromDescriptor(constant.Name[0]) : null;
                        if (primitive == null)
                            return false;

                        EmitPrimitiveClass(finish.Context, ilgen, primitive);
                        return true;
                    }
                case "enumConstant" when mi.Signature == ConstantBootstrapPrefix + ")Ljava.lang.Enum;" && bsm.ArgumentCount == 0 && type.IsPrimitive == false:
                    {
                        var valueOf = finish.Context.ClassLoaderFactory.LoadClassCritical("java.lang.Enum").GetMethod("valueOf", "(Ljava.lang.Class;Ljava.lang.String;)Ljava.lang.Enum;", false);
                        valueOf.Link();
                        type.EmitClassLiteral(ilgen);
                        ilgen.Emit(OpCodes.Ldstr, constant.Name);
                        valueOf.EmitCall(ilgen);
                        type.EmitCheckcast(ilgen);
                        return true;
                    }
                case "getStaticFinal" when mi.Signature == ConstantBootstrapPrefix + ")Ljava.lang.Object;" && bsm.ArgumentCount == 0:
                    // the field is declared by the constant's type, or by its wrapper class for a primitive type
                    return EmitGetStaticFinal(ilgen, constant, type.IsPrimitive ? GetBoxType(finish.Context, type, out _) : type);
                case "getStaticFinal" when mi.Signature == ConstantBootstrapPrefix + "Ljava.lang.Class;)Ljava.lang.Object;" && bsm.ArgumentCount == 1 && classFile.GetConstantPoolConstantType(bsm.GetArgument(0)) == ClassFile.ConstantType.Class:
                    return EmitGetStaticFinal(ilgen, constant, classFile.GetConstantPoolClassType(new ClassConstantHandle(bsm.GetArgument(0).Slot)));
                default:
                    return false;
            }
        }

        bool EmitGetStaticFinal(CodeEmitter ilgen, ClassFile.ConstantPoolItemDynamic constant, RuntimeJavaType declaringType)
        {
            if (declaringType.IsUnloadable)
                return false;

            var field = declaringType.GetFieldWrapper(constant.Name, constant.GetFieldType().SigName);
            if (field == null || field.IsStatic == false || field.IsFinal == false || field.IsAccessibleFrom(declaringType, clazz, clazz) == false)
                return false;

            field.Link();
            field.EmitGet(ilgen);
            return true;
        }

        /// <summary>
        /// Pushes the Class of a primitive type (int.class, ...), which is its wrapper class's TYPE field: the class
        /// literal of a primitive RuntimeJavaType is the Class of the CLR value type, not Java's primitive class.
        /// </summary>
        static void EmitPrimitiveClass(RuntimeContext context, CodeEmitter ilgen, RuntimeJavaType primitive)
        {
            var wrapper = primitive == context.PrimitiveJavaTypeFactory.VOID ? context.ClassLoaderFactory.LoadClassCritical("java.lang.Void") : GetBoxType(context, primitive, out _);
            var field = wrapper.GetFieldWrapper("TYPE", "Ljava.lang.Class;");
            field.Link();
            field.EmitGet(ilgen);
        }

        /// <summary>
        /// The wrapper class of a primitive type (void excluded), and its method that unboxes a value of it.
        /// </summary>
        static RuntimeJavaType GetBoxType(RuntimeContext context, RuntimeJavaType primitive, out RuntimeJavaMethod unbox)
        {
            var (name, accessor) = primitive.SigName switch
            {
                "Z" => ("java.lang.Boolean", "booleanValue"),
                "B" => ("java.lang.Byte", "byteValue"),
                "S" => ("java.lang.Short", "shortValue"),
                "C" => ("java.lang.Character", "charValue"),
                "I" => ("java.lang.Integer", "intValue"),
                "J" => ("java.lang.Long", "longValue"),
                "F" => ("java.lang.Float", "floatValue"),
                "D" => ("java.lang.Double", "doubleValue"),
                _ => throw new InvalidOperationException(),
            };

            var wrapper = context.ClassLoaderFactory.LoadClassCritical(name);
            unbox = wrapper.GetMethod(accessor, "()" + primitive.SigName, false);
            return wrapper;
        }

        RuntimeJavaType PrimitiveFromDescriptor(char descriptor)
        {
            var p = finish.Context.PrimitiveJavaTypeFactory;
            return descriptor switch
            {
                'Z' => p.BOOLEAN,
                'B' => p.BYTE,
                'S' => p.SHORT,
                'C' => p.CHAR,
                'I' => p.INT,
                'J' => p.LONG,
                'F' => p.FLOAT,
                'D' => p.DOUBLE,
                'V' => p.VOID,
                _ => null,
            };
        }

        /// <summary>
        /// The resolution of one dynamic constant of the class being compiled: a nested type whose static
        /// ConstantDynamicSite calls a resolve stub on first use. Shared by every ldc of the constant.
        /// </summary>
        sealed class DynamicConstant
        {

            FieldBuilder site;
            MethodInfo bridge;

            /// <summary>
            /// Pushes the constant's resolved value as an object.
            /// </summary>
            internal void Emit(Compiler compiler, CodeEmitter ilgen, ConstantHandle handle)
            {
                if (compiler.finish.UseLazySiteBridges == false)
                {
                    site ??= DefineSite(compiler, handle);
                    EmitLoad(compiler, ilgen);
                    return;
                }

                // wasm fork: a lazily compiled body calls a bridge, which a recompile of the body finds again
                if (bridge == null)
                {
                    if (compiler.finish.IsLazyRecompile)
                    {
                        bridge = compiler.finish.GetBakedLazySiteBridge("condy", handle.Slot, Type.EmptyTypes);
                    }
                    else
                    {
                        site ??= DefineSite(compiler, handle);
                        var mb = compiler.finish.DefineLazySiteBridge("condy", handle.Slot, compiler.finish.Context.Types.Object, Type.EmptyTypes);
                        var body = compiler.finish.Context.CodeEmitterFactory.Create(mb);
                        EmitLoad(compiler, body);
                        body.Emit(OpCodes.Ret);
                        body.DoEmit();
                        bridge = mb;
                    }
                }

                ilgen.Emit(OpCodes.Call, bridge);
            }

            void EmitLoad(Compiler compiler, CodeEmitter ilgen)
            {
                ilgen.Emit(OpCodes.Ldsfld, site);
                ilgen.Emit(OpCodes.Call, site.FieldType.GetMethod("GetValue"));
            }

            static FieldBuilder DefineSite(Compiler compiler, ConstantHandle handle)
            {
                var context = compiler.finish.Context;
                var siteType = context.Resolver.ResolveRuntimeType("IKVM.Runtime.ConstantDynamicSite").AsReflection();
                var funcType = context.Resolver.ResolveCoreType(typeof(Func<>).FullName).AsReflection().MakeGenericType(context.Types.Object);

                var tb = compiler.finish.DefineIndyCallSiteType();
                var fb = tb.DefineField("value", siteType, FieldAttributes.Static | FieldAttributes.Assembly);
                var ilgen = context.CodeEmitterFactory.Create(ReflectUtil.DefineTypeInitializer(tb, compiler.clazz.ClassLoader));
                ilgen.Emit(OpCodes.Ldnull);
                ilgen.Emit(OpCodes.Ldftn, DefineResolveStub(compiler, tb, handle));
                ilgen.Emit(OpCodes.Newobj, funcType.GetConstructor(new[] { context.Types.Object, context.Types.IntPtr }));
                ilgen.Emit(OpCodes.Newobj, siteType.GetConstructor(new[] { funcType }));
                ilgen.Emit(OpCodes.Stsfld, fb);
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
                return fb;
            }

            /// <summary>
            /// Defines the method that resolves the constant: it calls the bootstrap method and checks that the result
            /// is a value of the constant's type. An exception from either is wrapped as ConstantBootstraps.makeConstant
            /// does; one raised before the call (resolving the bootstrap method or its arguments) propagates as is.
            /// </summary>
            static MethodBuilder DefineResolveStub(Compiler compiler, TypeBuilder tb, ConstantHandle handle)
            {
                var context = compiler.finish.Context;
                var constant = compiler.classFile.GetConstantPoolConstantDynamic(handle);
                var type = constant.GetFieldType();

                var mb = tb.DefineMethod("ResolveStub", MethodAttributes.Static | MethodAttributes.Assembly, context.Types.Object, Type.EmptyTypes);
                var ilgen = context.CodeEmitterFactory.Create(mb);
                var result = ilgen.DeclareLocal(context.Types.Object);
                var ex = ilgen.DeclareLocal(context.Types.Exception);
                var ok = ilgen.DeclareLocal(context.Types.Boolean);
                var done = ilgen.DefineLabel();
                ilgen.BeginExceptionBlock();

                void EmitType(CodeEmitter il)
                {
                    if (type.IsPrimitive)
                        EmitPrimitiveClass(context, il, type);
                    else
                        compiler.EmitLoadClass(il, type);
                }

                if (InvokeDynamicBuilder.EmitCallBootstrapMethod(compiler, constant.BootstrapMethod, constant.Name, context.JavaBase.TypeOfJavaLangClass, EmitType, ilgen, ok))
                {
                    if (type.IsPrimitive)
                        ilgen.EmitCastclass(GetBoxType(context, type, out _).TypeAsBaseType);
                    else if (type.IsUnloadable == false && type.IsGhost == false && type.IsGhostArray == false)
                        type.EmitCheckcast(ilgen);

                    ilgen.Emit(OpCodes.Stloc, result);
                }

                ilgen.EmitLeave(done);
                ilgen.BeginCatchBlock(context.Types.Exception);
                ilgen.Emit(OpCodes.Stloc, ex);
                var wrap = ilgen.DefineLabel();
                ilgen.Emit(OpCodes.Ldloc, ok);
                ilgen.EmitBrtrue(wrap);
                ilgen.Emit(OpCodes.Rethrow);
                ilgen.MarkLabel(wrap);
                ilgen.Emit(OpCodes.Ldloc, ex);
                ilgen.Emit(OpCodes.Call, context.ByteCodeHelperMethods.WrapConstantBootstrapException);
                ilgen.Emit(OpCodes.Throw);
                ilgen.EndExceptionBlock();
                ilgen.MarkLabel(done);
                ilgen.Emit(OpCodes.Ldloc, result);
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
                return mb;
            }

        }

    }

}
