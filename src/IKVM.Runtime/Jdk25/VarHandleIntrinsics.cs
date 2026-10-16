using System;

#if IMPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;

using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif

using IKVM.ByteCode;
using IKVM.ByteCode.Decoding;

using InstructionFlags = IKVM.Runtime.ClassFile.Method.InstructionFlags;

namespace IKVM.Runtime
{

#if EMITTERS

    /// <summary>
    /// Compiles a call of a VarHandle access mode on a constant VarHandle to the access itself, as JDK 8's
    /// IntrinsicAtomicReferenceFieldUpdater does for an updater, instead of linking the call site at run time
    /// (ByteCodeHelper.GetDelegateForVarHandle).
    /// </summary>
    /// <remarks>
    /// A constant VarHandle is a static final field of the calling class that its static initializer sets, once, from
    /// <c>Lookup.findVarHandle</c>, <c>Lookup.findStaticVarHandle</c>, <c>MhUtil.findVarHandle</c> (the JDK's) or
    /// <c>MethodHandles.arrayElementVarHandle</c> with constant arguments: the handle can only be that field's or that
    /// array type's, so the call is that access. That is how the JDK's own classes hold theirs (CompletableFuture,
    /// AtomicReference, ConcurrentLinkedQueue, FutureTask, ...). The call site's types must be ones the handle takes
    /// without conversion, except for the casts its own checks would make; anything else, and every mode this does not
    /// know (the bitwise ones), keeps the dynamic call. IKVM_VH_DIRECT=0 turns it off.
    ///
    /// <c>MethodHandles.byteArrayViewVarHandle(T[].class, ByteOrder.BIG_ENDIAN|LITTLE_ENDIAN)</c> is one too -- directly,
    /// with <c>.withInvokeExactBehavior()</c> (sun.security.provider.ByteArrayAccess, which every digest reads its
    /// blocks through), or returned by a static method of the class that is only that call (jdk.internal.util.ByteArray
    /// and ByteArrayLittleEndian's <c>create</c>, behind DataInput/OutputStream). Its plain get and set, at a call site
    /// of exactly the handle's types, become a call of a ByteCodeHelper.ByteArrayView* method. R496.
    /// </remarks>
    static class VarHandleIntrinsics
    {

        static readonly bool enabled = Environment.GetEnvironmentVariable("IKVM_VH_DIRECT") != "0";

        // IKVM_TRACE_VH: name each constant VarHandle site that keeps the dynamic call
        static readonly bool trace = Environment.GetEnvironmentVariable("IKVM_TRACE_VH") != null;

        /// <summary>
        /// The number of call sites compiled to the access.
        /// </summary>
        internal static int Direct;

        /// <summary>
        /// The number of call sites on a constant VarHandle that kept the dynamic call.
        /// </summary>
        internal static int Kept;

        internal enum Kind
        {
            InstanceField,
            StaticField,
            ArrayElement,
            ByteArrayView,
        }

        sealed class ConstantVarHandle
        {

            internal Kind Kind;
            internal RuntimeJavaField Field;

            /// <summary>
            /// The array type of an ArrayElement handle; the view array type (int[] for an int view) of a ByteArrayView.
            /// </summary>
            internal RuntimeJavaType ArrayType;

            internal bool BigEndian;

        }

        /// <summary>
        /// Emits the access mode call at the instruction of <paramref name="eic"/> as the access itself, if the VarHandle
        /// is a constant one; returns false, having emitted nothing, if not.
        /// </summary>
        internal static bool TryEmit(EmitIntrinsicContext eic, ClassFile.ConstantPoolItemMI cpi)
        {
            if (enabled == false || MethodHandleUtil.IsVarHandleAccessMode(cpi.Class, cpi.Name) == false)
                return false;

            var args = cpi.GetArgTypes();
            var ret = cpi.GetRetType();
            if (FindReceiver(eic, args.Length) is not ClassFile.ConstantPoolItemFieldref receiver)
            {
                if (trace)
                    Console.WriteLine($"[IKVM vh] no constant receiver {eic.Caller.DeclaringType.Name}.{eic.Caller.Name}: {cpi.Name}{cpi.Signature}");

                return false;
            }

            if (Resolve(eic, receiver) is not ConstantVarHandle vh)
            {
                if (trace)
                    Console.WriteLine($"[IKVM vh] unresolved {eic.Caller.DeclaringType.Name}.{eic.Caller.Name}: {receiver.Class}.{receiver.Name}.{cpi.Name}");

                return false;
            }

            var emitted = vh.Kind switch
            {
                Kind.ArrayElement => EmitArrayElement(eic, cpi.Name, vh.ArrayType, args, ret),
                Kind.ByteArrayView => EmitByteArrayView(eic, cpi.Name, vh, args, ret),
                _ => EmitField(eic, cpi.Name, vh, args, ret),
            };
            System.Threading.Interlocked.Increment(ref emitted ? ref Direct : ref Kept);
            if (emitted == false && trace)
                Console.WriteLine($"[IKVM vh] kept {eic.Caller.DeclaringType.Name}.{eic.Caller.Name}: {receiver.Name}.{cpi.Name}{cpi.Signature} ({vh.Kind} {vh.Field?.DeclaringType.Name}.{vh.Field?.Name} {vh.Field?.GetType().Name} {vh.ArrayType?.Name})");

            return emitted;
        }

        /// <summary>
        /// Finds the getstatic that pushed the call's receiver: before the call, with nothing between that reaches under
        /// it, and control joining between them only from branches between them (as a conditional expression among the
        /// arguments does).
        /// </summary>
        static ClassFile.ConstantPoolItemFieldref FindReceiver(EmitIntrinsicContext eic, int argCount)
        {
            // the stack height before the receiver was pushed
            var height = eic.GetStackHeight(0) - argCount - 1;
            var joins = 0;

            for (int i = -1; eic.OpcodeIndex + i >= 0; i--)
            {
                if ((eic.Flags[eic.OpcodeIndex + i + 1] & InstructionFlags.BranchTarget) != 0)
                    joins++;

                var h = eic.GetStackHeight(i);
                if (h < height)
                    return null;

                if (h == height)
                {
                    if (eic.Match(i, NormalizedByteCode.__getstatic) == false || eic.GetStackHeight(i + 1) != height + 1)
                        return null;

                    if (joins > 0 && JoinsFromWithin(eic, eic.OpcodeIndex + i) == false)
                        return null;

                    return eic.GetFieldref(i);
                }

                switch (eic.Code[eic.OpcodeIndex + i].NormalizedOpCode)
                {
                    // these reach under the values they move
                    case NormalizedByteCode.__dup_x1:
                    case NormalizedByteCode.__dup_x2:
                    case NormalizedByteCode.__dup2_x1:
                    case NormalizedByteCode.__dup2_x2:
                    case NormalizedByteCode.__swap:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether every jump to an instruction after <paramref name="receiver"/> up to the call is from one after the
        /// receiver and before the call, and none of them starts an exception handler.
        /// </summary>
        static bool JoinsFromWithin(EmitIntrinsicContext eic, int receiver)
        {
            var call = eic.OpcodeIndex;
            bool Within(int index) => index > receiver && index < call;
            bool Joins(int target) => target > receiver && target <= call;

            for (int k = 0; k < eic.Code.Length; k++)
            {
                ref var instr = ref eic.Code[k];
                switch (ByteCodeMetaData.GetFlowControl(instr.NormalizedOpCode))
                {
                    case ByteCodeFlowControl.Branch:
                    case ByteCodeFlowControl.CondBranch:
                        if (Joins(instr.TargetIndex) && Within(k) == false)
                            return false;
                        break;
                    case ByteCodeFlowControl.Switch:
                        if (Joins(instr.DefaultTarget) && Within(k) == false)
                            return false;
                        for (int j = 0; j < instr.SwitchEntryCount; j++)
                            if (Joins(instr.GetSwitchTargetIndex(j)) && Within(k) == false)
                                return false;
                        break;
                }
            }

            // the method's exception handlers
            foreach (var method in eic.ClassFile.Methods)
            {
                if (method.Name != eic.Caller.Name || method.Signature != eic.Caller.Signature)
                    continue;

                foreach (var entry in method.ExceptionTable)
                    if (Joins(entry.handlerIndex))
                        return false;

                return true;
            }

            return false;
        }

        /// <summary>
        /// What a class's static initializer makes one of its constant VarHandle fields, by name: found by
        /// <see cref="Scan"/> as the class is defined, resolved to a field or array type at a call site, which may be in
        /// another class (ConcurrentLinkedQueue's Node uses the queue's handles).
        /// </summary>
        internal sealed class Spec
        {

            internal Kind Kind;

            /// <summary>
            /// The class whose field it is, or the array class; null for the class itself.
            /// </summary>
            internal string Class;

            internal string Name;

            /// <summary>
            /// The field's type: a class name, or a primitive's descriptor.
            /// </summary>
            internal string Type;

            /// <summary>
            /// A ByteArrayView's byte order (its view array class is <see cref="Class"/>).
            /// </summary>
            internal bool BigEndian;

        }

        /// <summary>
        /// Finds the constant VarHandles of a class: the static final VarHandle fields its static initializer sets once,
        /// from a factory call with constant arguments.
        /// </summary>
        internal static System.Collections.Generic.Dictionary<string, Spec> Scan(ClassFile classFile)
        {
            System.Collections.Generic.Dictionary<string, Spec> specs = null;

            // only a class with a static final VarHandle has its initializer looked at
            var any = false;
            foreach (var f in classFile.Fields)
                if (f.IsStatic && f.IsFinal && f.Signature == "Ljava.lang.invoke.VarHandle;")
                    any = true;

            if (any == false)
                return null;

            foreach (var method in classFile.Methods)
            {
                if (method.IsClassInitializer == false)
                    continue;

                var code = method.Instructions;
                var stores = new System.Collections.Generic.Dictionary<string, int>();
                for (int i = 0; i < code.Length; i++)
                {
                    if (code[i].NormalizedOpCode != NormalizedByteCode.__putstatic)
                        continue;

                    var f = classFile.GetFieldref(code[i].Arg1);
                    if (f.Class != classFile.Name || f.Signature != "Ljava.lang.invoke.VarHandle;")
                        continue;

                    // set more than once: not a constant this can see
                    stores[f.Name] = stores.ContainsKey(f.Name) ? -1 : i;
                }

                foreach (var store in stores)
                    if (store.Value > 0 && Recognize(classFile, code, store.Value) is Spec spec)
                        (specs ??= new())[store.Key] = spec;
            }

            return specs;
        }

        /// <summary>
        /// Recognizes the factory call whose result the putstatic at <paramref name="store"/> stores.
        /// </summary>
        static Spec Recognize(ClassFile classFile, ClassFile.Method.Instruction[] code, int store)
        {
            var call = store - 1;

            // MethodHandles.arrayElementVarHandle(T[].class)
            if (IsInvoke(classFile, code[call], NormalizedByteCode.__invokestatic, "java.lang.invoke.MethodHandles", "arrayElementVarHandle", "(Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;"))
                return call >= 1 && ClassLiteral(classFile, code[call - 1]) is string arrayType && arrayType.StartsWith("[") ? new Spec() { Kind = Kind.ArrayElement, Class = arrayType } : null;

            // lookup.find[Static]VarHandle(recv, name, type), MhUtil.findVarHandle(lookup, recv, name, type)
            if (call >= 3 && (IsInvoke(classFile, code[call], NormalizedByteCode.__invokevirtual, "java.lang.invoke.MethodHandles$Lookup", "findVarHandle", "(Ljava.lang.Class;Ljava.lang.String;Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;") ||
                IsInvoke(classFile, code[call], NormalizedByteCode.__invokevirtual, "java.lang.invoke.MethodHandles$Lookup", "findStaticVarHandle", "(Ljava.lang.Class;Ljava.lang.String;Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;") ||
                IsInvoke(classFile, code[call], NormalizedByteCode.__invokestatic, "jdk.internal.invoke.MhUtil", "findVarHandle", "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.Class;Ljava.lang.String;Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;")))
            {
                var spec = new Spec()
                {
                    Kind = classFile.GetMethodref(code[call].Arg1).Name == "findStaticVarHandle" ? Kind.StaticField : Kind.InstanceField,
                    Type = ClassLiteral(classFile, code[call - 1]),
                    Name = StringLiteral(classFile, code[call - 2]),
                    Class = ClassLiteral(classFile, code[call - 3]),
                };

                return spec.Type != null && spec.Name != null && spec.Class != null && spec.Class.StartsWith("[") == false ? spec : null;
            }

            // MhUtil.findVarHandle(MethodHandles.lookup(), name, type): a field of the class itself
            if (call >= 3 && IsInvoke(classFile, code[call], NormalizedByteCode.__invokestatic, "jdk.internal.invoke.MhUtil", "findVarHandle", "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;") &&
                IsOwnLookup(classFile, code, call - 3))
            {
                var spec = new Spec()
                {
                    Kind = Kind.InstanceField,
                    Type = ClassLiteral(classFile, code[call - 1]),
                    Name = StringLiteral(classFile, code[call - 2]),
                };

                return spec.Type != null && spec.Name != null ? spec : null;
            }

            // MethodHandles.byteArrayViewVarHandle(T[].class, ByteOrder.X), optionally .withInvokeExactBehavior(): an exact
            // handle throws for a call site of any other type, and only a call site of exactly its types is compiled
            var view = call;
            if (IsInvoke(classFile, code[view], NormalizedByteCode.__invokevirtual, "java.lang.invoke.VarHandle", "withInvokeExactBehavior", "()Ljava.lang.invoke.VarHandle;"))
                view--;

            if (view >= 2 && IsByteArrayViewCall(classFile, code[view]) && ByteOrderLiteral(classFile, code[view - 1]) is bool bigEndian)
                return ClassLiteral(classFile, code[view - 2]) is string viewArray && viewArray.StartsWith("[") ? new Spec() { Kind = Kind.ByteArrayView, Class = viewArray, BigEndian = bigEndian } : null;

            // ByteArray.create(T[].class): a static method of the class that is only that call on its argument
            if (view >= 1 && code[view].NormalizedOpCode == NormalizedByteCode.__invokestatic && ByteArrayViewFactory(classFile, classFile.GetMethodref(code[view].Arg1)) is bool factoryBigEndian)
                return ClassLiteral(classFile, code[view - 1]) is string factoryViewArray && factoryViewArray.StartsWith("[") ? new Spec() { Kind = Kind.ByteArrayView, Class = factoryViewArray, BigEndian = factoryBigEndian } : null;

            return null;
        }

        static bool IsByteArrayViewCall(ClassFile classFile, ClassFile.Method.Instruction instr)
        {
            return IsInvoke(classFile, instr, NormalizedByteCode.__invokestatic, "java.lang.invoke.MethodHandles", "byteArrayViewVarHandle", "(Ljava.lang.Class;Ljava.nio.ByteOrder;)Ljava.lang.invoke.VarHandle;");
        }

        /// <summary>
        /// The byte order an instruction pushes, true for big endian: a getstatic of ByteOrder.BIG_ENDIAN or LITTLE_ENDIAN.
        /// </summary>
        static bool? ByteOrderLiteral(ClassFile classFile, ClassFile.Method.Instruction instr)
        {
            if (instr.NormalizedOpCode != NormalizedByteCode.__getstatic)
                return null;

            var f = classFile.GetFieldref(instr.Arg1);
            if (f.Class != "java.nio.ByteOrder" || f.Signature != "Ljava.nio.ByteOrder;")
                return null;

            return f.Name switch
            {
                "BIG_ENDIAN" => true,
                "LITTLE_ENDIAN" => false,
                _ => null,
            };
        }

        /// <summary>
        /// The byte order of the handles a static method of the class makes, if its code is exactly
        /// <c>return MethodHandles.byteArrayViewVarHandle(arg0, ByteOrder.X);</c>
        /// </summary>
        static bool? ByteArrayViewFactory(ClassFile classFile, ClassFile.ConstantPoolItemMI m)
        {
            if (m.Class != classFile.Name || m.Signature != "(Ljava.lang.Class;)Ljava.lang.invoke.VarHandle;")
                return null;

            foreach (var method in classFile.Methods)
            {
                if (method.Name != m.Name || method.Signature != m.Signature)
                    continue;

                // aload_0, getstatic ByteOrder.X, invokestatic byteArrayViewVarHandle, areturn, and the terminating nop
                var code = method.IsStatic ? method.Instructions : null;
                if (code == null || code.Length != 5 || code[0].NormalizedOpCode != NormalizedByteCode.__aload || code[0].NormalizedArg1 != 0 ||
                    IsByteArrayViewCall(classFile, code[2]) == false || code[3].NormalizedOpCode != NormalizedByteCode.__areturn)
                    return null;

                return ByteOrderLiteral(classFile, code[1]);
            }

            return null;
        }

        /// <summary>
        /// Resolves a static final VarHandle field to what its class's static initializer made it.
        /// </summary>
        static ConstantVarHandle Resolve(EmitIntrinsicContext eic, ClassFile.ConstantPoolItemFieldref receiver)
        {
            if (receiver.Signature != "Ljava.lang.invoke.VarHandle;")
                return null;

            var vhField = receiver.GetField();
            if (vhField == null || vhField.IsStatic == false || vhField.IsFinal == false || vhField.DeclaringType is not RuntimeByteCodeJavaType declaring)
                return null;

            if (declaring.GetConstantVarHandle(vhField.Name) is not Spec spec)
                return null;

            try
            {
                var loader = declaring.ClassLoader;
                if (spec.Kind == Kind.ArrayElement)
                {
                    var arrayType = loader.LoadClassByName(spec.Class);
                    return arrayType.IsArray && arrayType.IsUnloadable == false ? new ConstantVarHandle() { Kind = Kind.ArrayElement, ArrayType = arrayType } : null;
                }

                if (spec.Kind == Kind.ByteArrayView)
                {
                    var viewType = loader.LoadClassByName(spec.Class);
                    return viewType.IsArray && viewType.IsUnloadable == false && viewType.ArrayRank == 1 ? new ConstantVarHandle() { Kind = Kind.ByteArrayView, ArrayType = viewType, BigEndian = spec.BigEndian } : null;
                }

                var recv = spec.Class == null ? declaring : loader.LoadClassByName(spec.Class);
                var type = spec.Type.Length == 1 ? PrimitiveType(eic, spec.Type[0]) : loader.LoadClassByName(spec.Type);
                if (recv == null || type == null || recv.IsUnloadable || type.IsUnloadable)
                    return null;

                var field = recv.GetFieldWrapper(spec.Name, type.SigName);
                if (field == null || field.IsStatic != (spec.Kind == Kind.StaticField))
                    return null;

                // a field of a class being compiled is linked as its methods compile
                field.Link();
                if (field.FieldTypeWrapper != type)
                    return null;

                // the handle was made with an access check of its own, which the field access then skips
                if (field.IsAccessibleFrom(field.DeclaringType, eic.Caller.DeclaringType, field.DeclaringType) == false)
                    return null;

                return new ConstantVarHandle() { Kind = spec.Kind, Field = field };
            }
            catch (RetargetableJavaException)
            {
                return null;
            }
        }

        static RuntimeJavaType PrimitiveType(EmitIntrinsicContext eic, char descriptor)
        {
            var p = eic.Context.Context.PrimitiveJavaTypeFactory;
            return descriptor switch
            {
                'Z' => p.BOOLEAN,
                'B' => p.BYTE,
                'C' => p.CHAR,
                'S' => p.SHORT,
                'I' => p.INT,
                'J' => p.LONG,
                'F' => p.FLOAT,
                'D' => p.DOUBLE,
                _ => null,
            };
        }

        /// <summary>
        /// Whether the instruction at <paramref name="index"/> pushes MethodHandles.lookup() of the class: the call, or a
        /// local that it was stored in (and nothing since).
        /// </summary>
        static bool IsOwnLookup(ClassFile classFile, ClassFile.Method.Instruction[] code, int index)
        {
            const string sig = "()Ljava.lang.invoke.MethodHandles$Lookup;";
            if (IsInvoke(classFile, code[index], NormalizedByteCode.__invokestatic, "java.lang.invoke.MethodHandles", "lookup", sig))
                return true;

            if (code[index].NormalizedOpCode != NormalizedByteCode.__aload)
                return false;

            // the store to the local the lookup is loaded from
            var local = code[index].NormalizedArg1;
            for (int i = index - 1; i >= 1; i--)
            {
                if (code[i].NormalizedOpCode == NormalizedByteCode.__astore && code[i].NormalizedArg1 == local)
                    return IsInvoke(classFile, code[i - 1], NormalizedByteCode.__invokestatic, "java.lang.invoke.MethodHandles", "lookup", sig);
            }

            return false;
        }

        static bool IsInvoke(ClassFile classFile, ClassFile.Method.Instruction instr, NormalizedByteCode opcode, string clazz, string name, string sig)
        {
            if (instr.NormalizedOpCode != opcode)
                return false;

            var m = classFile.GetMethodref(instr.Arg1);
            return m.Class == clazz && m.Name == name && m.Signature == sig;
        }

        /// <summary>
        /// The class an instruction pushes, by name: an ldc of a class, or the TYPE of a primitive's wrapper (which is how
        /// javac compiles int.class), by its descriptor.
        /// </summary>
        static string ClassLiteral(ClassFile classFile, ClassFile.Method.Instruction instr)
        {
            if (instr.NormalizedOpCode is NormalizedByteCode.__ldc or NormalizedByteCode.__ldc_nothrow)
                return classFile.GetConstantPoolConstantType(instr.Arg1) == ClassFile.ConstantType.Class ? classFile.GetConstantPoolClass(new ClassConstantHandle(checked((ushort)instr.Arg1))) : null;

            if (instr.NormalizedOpCode == NormalizedByteCode.__getstatic)
            {
                var f = classFile.GetFieldref(instr.Arg1);
                if (f.Name != "TYPE" || f.Signature != "Ljava.lang.Class;")
                    return null;

                return f.Class switch
                {
                    "java.lang.Boolean" => "Z",
                    "java.lang.Byte" => "B",
                    "java.lang.Character" => "C",
                    "java.lang.Short" => "S",
                    "java.lang.Integer" => "I",
                    "java.lang.Long" => "J",
                    "java.lang.Float" => "F",
                    "java.lang.Double" => "D",
                    _ => null,
                };
            }

            return null;
        }

        static string StringLiteral(ClassFile classFile, ClassFile.Method.Instruction instr)
        {
            if (instr.NormalizedOpCode is NormalizedByteCode.__ldc or NormalizedByteCode.__ldc_nothrow && classFile.GetConstantPoolConstantType(instr.Arg1) == ClassFile.ConstantType.String)
                return classFile.GetConstantPoolConstantString(instr.Arg1);

            return null;
        }

        /// <summary>
        /// Whether a call site value of type <paramref name="arg"/> is one a handle whose type is <paramref name="type"/>
        /// takes: the same primitive, or a reference, which <see cref="EmitArgCast"/> casts as the handle would.
        /// </summary>
        static bool Takes(RuntimeJavaType arg, RuntimeJavaType type)
        {
            if (type.IsPrimitive || arg.IsPrimitive)
                return arg == type;

            return arg.IsUnloadable == false && type.IsGhost == false && type.IsNonPrimitiveValueType == false;
        }

        /// <summary>
        /// Casts the reference on the stack, of the call site's type <paramref name="arg"/>, to <paramref name="type"/>,
        /// which the handle checks it is (ClassCastException).
        /// </summary>
        static void EmitArgCast(EmitIntrinsicContext eic, RuntimeJavaType arg, RuntimeJavaType type)
        {
            if (type.IsPrimitive == false && arg.IsAssignableTo(type) == false)
                type.EmitCheckcast(eic.Emitter);
        }

        /// <summary>
        /// Whether the call site's return type <paramref name="ret"/> takes the handle's value of <paramref name="type"/>:
        /// void (discarded), the same primitive, or a reference (cast by <see cref="EmitReturn"/>).
        /// </summary>
        static bool Returns(RuntimeJavaType ret, RuntimeJavaType type)
        {
            if (ret == ret.Context.PrimitiveJavaTypeFactory.VOID)
                return true;

            if (type.IsPrimitive || ret.IsPrimitive)
                return ret == type;

            return ret.IsUnloadable == false;
        }

        static void EmitReturn(EmitIntrinsicContext eic, RuntimeJavaType ret, RuntimeJavaType type)
        {
            if (ret == ret.Context.PrimitiveJavaTypeFactory.VOID)
                eic.Emitter.Emit(OpCodes.Pop);
            else if (ret.IsPrimitive == false && type.IsAssignableTo(ret) == false)
                ret.EmitCheckcast(eic.Emitter);
        }

        static bool IsGet(string mode) => mode is "get" or "getVolatile" or "getAcquire" or "getOpaque";

        static bool IsSet(string mode) => mode is "set" or "setVolatile" or "setRelease" or "setOpaque";

        static bool IsCompareAndSet(string mode) => mode is "compareAndSet" or "weakCompareAndSet" or "weakCompareAndSetPlain" or "weakCompareAndSetAcquire" or "weakCompareAndSetRelease";

        static bool IsCompareAndExchange(string mode) => mode is "compareAndExchange" or "compareAndExchangeAcquire" or "compareAndExchangeRelease";

        static bool IsGetAndSet(string mode) => mode is "getAndSet" or "getAndSetAcquire" or "getAndSetRelease";

        static bool IsGetAndAdd(string mode) => mode is "getAndAdd" or "getAndAddAcquire" or "getAndAddRelease";

        /// <summary>
        /// Whether the field's value has a single location the interlocked operations can take the address of, and the
        /// type they have one for (int, long or a reference).
        /// </summary>
        static bool IsInterlockable(RuntimeJavaField field, out RuntimeJavaType type)
        {
            type = field.FieldTypeWrapper;
            var p = type.Context.PrimitiveJavaTypeFactory;
            return field is RuntimeSimpleJavaField && field.DeclaringType.IsNonPrimitiveValueType == false && (type == p.INT || type == p.LONG || type.IsPrimitive == false);
        }

        static void EmitFieldAddress(EmitIntrinsicContext eic, RuntimeJavaField field)
        {
            eic.Emitter.Emit(field.IsStatic ? OpCodes.Ldsflda : OpCodes.Ldflda, field.GetField());
        }

        static bool EmitField(EmitIntrinsicContext eic, string mode, ConstantVarHandle vh, RuntimeJavaType[] args, RuntimeJavaType ret)
        {
            var field = vh.Field;
            var type = field.FieldTypeWrapper;
            var context = eic.Context.Context;
            var p = context.PrimitiveJavaTypeFactory;
            var coordinates = vh.Kind == Kind.InstanceField ? 1 : 0;

            if (type.IsGhost || type.IsNonPrimitiveValueType || field.DeclaringType.IsNonPrimitiveValueType)
                return false;

            // the receiver: an instance of the field's class (cast as the handle checks)
            if (coordinates == 1 && (args.Length == 0 || args[0].IsPrimitive || args[0].IsUnloadable))
                return false;

            // the values the mode takes after the coordinates, and what it returns
            int values;
            if (IsGet(mode))
            {
                values = 0;
                if (Returns(ret, type) == false)
                    return false;
            }
            else if (IsSet(mode))
            {
                values = 1;
                if (field.IsFinal || ret != p.VOID)
                    return false;
            }
            else if (IsCompareAndSet(mode))
            {
                values = 2;
                if (field.IsFinal || (ret != p.BOOLEAN && ret != p.VOID) || (field is not RuntimeSimpleJavaField && field is not RuntimeVolatileLongDoubleJavaField))
                    return false;
            }
            else if (IsCompareAndExchange(mode))
            {
                values = 2;
                if (field.IsFinal || IsInterlockable(field, out _) == false || Returns(ret, type) == false)
                    return false;
            }
            else if (IsGetAndSet(mode))
            {
                values = 1;
                if (field.IsFinal || IsInterlockable(field, out _) == false || Returns(ret, type) == false)
                    return false;
            }
            else if (IsGetAndAdd(mode))
            {
                values = 1;
                if (field.IsFinal || (type != p.INT && type != p.LONG) || field is not RuntimeSimpleJavaField || Returns(ret, type) == false)
                    return false;
            }
            else
            {
                return false;
            }

            if (args.Length != coordinates + values)
                return false;

            for (int i = coordinates; i < args.Length; i++)
                if (Takes(args[i], type) == false)
                    return false;

            // consume the call site: the values, the receiver, the handle
            var il = eic.Emitter;
            var temps = new CodeEmitterLocal[args.Length];
            for (int i = args.Length - 1; i >= 0; i--)
            {
                temps[i] = il.AllocTempLocal(args[i].TypeAsLocalOrStackType);
                il.Emit(OpCodes.Stloc, temps[i]);
            }

            il.EmitNullCheck();

            if (coordinates == 1)
            {
                il.Emit(OpCodes.Ldloc, temps[0]);
                EmitArgCast(eic, args[0], field.DeclaringType);
            }

            for (int i = coordinates; i < args.Length; i++)
            {
                il.Emit(OpCodes.Ldloc, temps[i]);
                EmitArgCast(eic, args[i], type);
            }

            for (int i = 0; i < temps.Length; i++)
                il.ReleaseTempLocal(temps[i]);

            if (IsGet(mode))
            {
                // a volatile field's plain read is already volatile, and stronger than get needs
                if (mode == "get" || field.IsVolatile)
                    field.EmitGet(il);
                else
                    field.EmitUnsafeVolatileGet(il);

                EmitReturn(eic, ret, type);
            }
            else if (IsSet(mode))
            {
                if (mode == "set" || field.IsVolatile)
                    field.EmitSet(il);
                else
                    field.EmitUnsafeVolatileSet(il);
            }
            else if (IsCompareAndSet(mode))
            {
                field.EmitUnsafeCompareAndSwap(il);
                if (ret == p.VOID)
                    il.Emit(OpCodes.Pop);
            }
            else
            {
                // the interlocked operations, on the field's address: (ref location, value[, comparand])
                var value = il.AllocTempLocal(type.TypeAsLocalOrStackType);
                CodeEmitterLocal comparand = null;
                if (IsCompareAndExchange(mode))
                {
                    il.Emit(OpCodes.Stloc, value);
                    comparand = il.AllocTempLocal(type.TypeAsLocalOrStackType);
                    il.Emit(OpCodes.Stloc, comparand);
                }
                else
                {
                    il.Emit(OpCodes.Stloc, value);
                }

                EmitFieldAddress(eic, field);
                il.Emit(OpCodes.Ldloc, value);

                var interlocked = context.InterlockedMethods;
                if (IsCompareAndExchange(mode))
                {
                    il.Emit(OpCodes.Ldloc, comparand);
                    il.Emit(OpCodes.Call, type == p.INT ? interlocked.CompareExchangeInt32 : type == p.LONG ? interlocked.CompareExchangeInt64 : AtomicReferenceFieldUpdaterEmitter.MakeCompareExchange(context, type.TypeAsLocalOrStackType));
                }
                else if (IsGetAndSet(mode))
                {
                    il.Emit(OpCodes.Call, type == p.INT ? interlocked.ExchangeInt32 : type == p.LONG ? interlocked.ExchangeInt64 : Intrinsics.MakeExchange(context, type.TypeAsLocalOrStackType));
                }
                else
                {
                    // Interlocked.Add returns the new value; the mode returns the old one
                    il.Emit(OpCodes.Call, type == p.INT ? interlocked.AddInt32 : interlocked.AddInt64);
                    il.Emit(OpCodes.Ldloc, value);
                    il.Emit(OpCodes.Sub);
                }

                il.ReleaseTempLocal(value);
                if (comparand != null)
                    il.ReleaseTempLocal(comparand);

                EmitReturn(eic, ret, type);
            }

            eic.NonLeaf = false;
            return true;
        }

        static bool EmitArrayElement(EmitIntrinsicContext eic, string mode, RuntimeJavaType arrayType, RuntimeJavaType[] args, RuntimeJavaType ret)
        {
            var context = eic.Context.Context;
            var p = context.PrimitiveJavaTypeFactory;
            var element = arrayType.ElementTypeWrapper;
            var isReference = element.IsPrimitive == false;

            // int, long and reference elements (of a type that is no ghost or value type)
            if (isReference ? element.IsGhost || element.IsNonPrimitiveValueType || element.IsUnloadable || arrayType.IsGhostArray : element != p.INT && element != p.LONG)
                return false;

            // (array, index, values...): the array, of the handle's array type (a reference element type's handle
            // takes any array the array type is assignable from)
            if (args.Length < 2 || args[0].IsArray == false || args[1] != p.INT || (isReference ? args[0].IsAssignableTo(arrayType) == false : args[0] != arrayType))
                return false;

            int values;
            if (IsGet(mode))
            {
                values = 0;
                if (Returns(ret, element) == false)
                    return false;
            }
            else if (IsSet(mode))
            {
                values = 1;
                if (ret != p.VOID)
                    return false;
            }
            else if (isReference == false && (IsCompareAndSet(mode) || IsCompareAndExchange(mode)))
            {
                // the address of a reference array's element is only typed for an array of exactly that type, so only
                // primitive elements compare and exchange here
                values = 2;
                if (IsCompareAndSet(mode) ? ret != p.BOOLEAN && ret != p.VOID : Returns(ret, element) == false)
                    return false;
            }
            else if (isReference == false && (IsGetAndSet(mode) || IsGetAndAdd(mode)))
            {
                values = 1;
                if (Returns(ret, element) == false)
                    return false;
            }
            else
            {
                return false;
            }

            if (args.Length != 2 + values)
                return false;

            for (int i = 2; i < args.Length; i++)
                if (Takes(args[i], element) == false)
                    return false;

            var il = eic.Emitter;
            var temps = new CodeEmitterLocal[args.Length];
            for (int i = args.Length - 1; i >= 0; i--)
            {
                temps[i] = il.AllocTempLocal(args[i].TypeAsLocalOrStackType);
                il.Emit(OpCodes.Stloc, temps[i]);
            }

            il.EmitNullCheck();

            il.Emit(OpCodes.Ldloc, temps[0]);
            il.Emit(OpCodes.Ldloc, temps[1]);

            if (IsGet(mode))
            {
                if (isReference)
                    il.Emit(OpCodes.Ldelem_Ref);
                else
                    il.Emit(element == p.INT ? OpCodes.Ldelem_I4 : OpCodes.Ldelem_I8);

                // acquire: the element is read before what follows
                if (mode != "get")
                    il.EmitMemoryBarrier();

                EmitReturn(eic, ret, element);
            }
            else if (IsSet(mode))
            {
                il.Emit(OpCodes.Ldloc, temps[2]);
                EmitArgCast(eic, args[2], element);

                // release: what precedes is written before the element (and volatile: the element before what follows)
                if (mode != "set")
                    il.EmitMemoryBarrier();

                if (isReference)
                    il.Emit(OpCodes.Stelem_Ref);
                else
                    il.Emit(element == p.INT ? OpCodes.Stelem_I4 : OpCodes.Stelem_I8);

                if (mode == "setVolatile")
                    il.EmitMemoryBarrier();
            }
            else
            {
                var interlocked = context.InterlockedMethods;
                il.Emit(OpCodes.Ldelema, element.TypeAsArrayType);
                if (IsCompareAndSet(mode) || IsCompareAndExchange(mode))
                {
                    // (ref location, value, comparand): the update, then the expected value
                    il.Emit(OpCodes.Ldloc, temps[3]);
                    il.Emit(OpCodes.Ldloc, temps[2]);
                    il.Emit(OpCodes.Call, element == p.INT ? interlocked.CompareExchangeInt32 : interlocked.CompareExchangeInt64);
                    if (IsCompareAndSet(mode))
                    {
                        il.Emit(OpCodes.Ldloc, temps[2]);
                        il.Emit(OpCodes.Ceq);
                        if (ret == p.VOID)
                            il.Emit(OpCodes.Pop);
                    }
                    else
                    {
                        EmitReturn(eic, ret, element);
                    }
                }
                else if (IsGetAndSet(mode))
                {
                    il.Emit(OpCodes.Ldloc, temps[2]);
                    il.Emit(OpCodes.Call, element == p.INT ? interlocked.ExchangeInt32 : interlocked.ExchangeInt64);
                    EmitReturn(eic, ret, element);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, temps[2]);
                    il.Emit(OpCodes.Call, element == p.INT ? interlocked.AddInt32 : interlocked.AddInt64);
                    il.Emit(OpCodes.Ldloc, temps[2]);
                    il.Emit(OpCodes.Sub);
                    EmitReturn(eic, ret, element);
                }
            }

            for (int i = 0; i < temps.Length; i++)
                il.ReleaseTempLocal(temps[i]);

            eic.NonLeaf = false;
            return true;
        }

        /// <summary>
        /// A byte array view handle's plain get, (byte[], int) to the view's type, and set, (byte[], int, value) to
        /// void, as a call of the ByteCodeHelper method that does the access with the handle's index check and byte
        /// order. Other modes (the volatile and atomic ones, which also check alignment) and other call site types keep
        /// the dynamic call.
        /// </summary>
        static bool EmitByteArrayView(EmitIntrinsicContext eic, string mode, ConstantVarHandle vh, RuntimeJavaType[] args, RuntimeJavaType ret)
        {
            var context = eic.Context.Context;
            var p = context.PrimitiveJavaTypeFactory;
            var helpers = context.ByteCodeHelperMethods;
            var view = vh.ArrayType.ElementTypeWrapper;

            MethodInfo get, set;
            if (view == p.SHORT)
                (get, set) = (helpers.ByteArrayViewGetShort, helpers.ByteArrayViewSetShort);
            else if (view == p.CHAR)
                (get, set) = (helpers.ByteArrayViewGetChar, helpers.ByteArrayViewSetChar);
            else if (view == p.INT)
                (get, set) = (helpers.ByteArrayViewGetInt, helpers.ByteArrayViewSetInt);
            else if (view == p.LONG)
                (get, set) = (helpers.ByteArrayViewGetLong, helpers.ByteArrayViewSetLong);
            else if (view == p.FLOAT)
                (get, set) = (helpers.ByteArrayViewGetFloat, helpers.ByteArrayViewSetFloat);
            else if (view == p.DOUBLE)
                (get, set) = (helpers.ByteArrayViewGetDouble, helpers.ByteArrayViewSetDouble);
            else
                return false;

            if (args.Length < 2 || args[0].IsArray == false || args[0].ArrayRank != 1 || args[0].ElementTypeWrapper != p.BYTE || args[1] != p.INT)
                return false;

            MethodInfo helper;
            if (mode == "get" && args.Length == 2 && ret == view)
                helper = get;
            else if (mode == "set" && args.Length == 3 && args[2] == view && ret == p.VOID)
                helper = set;
            else
                return false;

            // consume the call site: the arguments, then the handle
            var il = eic.Emitter;
            var temps = new CodeEmitterLocal[args.Length];
            for (int i = args.Length - 1; i >= 0; i--)
            {
                temps[i] = il.AllocTempLocal(args[i].TypeAsLocalOrStackType);
                il.Emit(OpCodes.Stloc, temps[i]);
            }

            il.EmitNullCheck();

            for (int i = 0; i < temps.Length; i++)
            {
                il.Emit(OpCodes.Ldloc, temps[i]);
                il.ReleaseTempLocal(temps[i]);
            }

            il.EmitLdc_I4(vh.BigEndian ? 1 : 0);
            il.Emit(OpCodes.Call, helper);

            eic.NonLeaf = true;
            return true;
        }

    }

#endif

}
