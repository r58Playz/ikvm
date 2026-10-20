/*
  Copyright (C) 2002-2015 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net
  
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;

using IKVM.Attributes;
using IKVM.ByteCode;
using IKVM.ByteCode.Decoding;
using IKVM.CoreLib.Diagnostics;


#if IMPORTER
using IKVM.Reflection;
using IKVM.Reflection.Emit;
using IKVM.Tools.Importer;

using Type = IKVM.Reflection.Type;
using DynamicOrAotTypeWrapper = IKVM.Tools.Importer.RuntimeImportByteCodeJavaType;
#else
using System.Reflection;
using System.Reflection.Emit;

using DynamicOrAotTypeWrapper = IKVM.Runtime.RuntimeByteCodeJavaType;
#endif

namespace IKVM.Runtime
{

    partial class RuntimeByteCodeJavaType
    {

        internal sealed class FinishContext
        {

            readonly RuntimeContext context;
            readonly RuntimeJavaType host;
            readonly ClassFile classFile;
            readonly DynamicOrAotTypeWrapper wrapper;
            readonly TypeBuilder typeBuilder;
            List<TypeBuilder> nestedTypeBuilders;
            MethodInfo callerIDMethod;
            List<Item> items;
            Dictionary<RuntimeJavaField, MethodBuilder> arfuMap;
            Dictionary<MethodKey, MethodInfo> invokespecialstubcache;
            Dictionary<string, MethodInfo> dynamicClassLiteral;
#if IMPORTER
            TypeBuilder interfaceHelperMethodsTypeBuilder;
#else
            List<object> liveObjects;
#endif

            private struct Item
            {
                internal int key;
                internal object value;
            }

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="context"></param>
            /// <param name="host"></param>
            /// <param name="classFile"></param>
            /// <param name="wrapper"></param>
            /// <param name="typeBuilder"></param>
            internal FinishContext(RuntimeContext context, RuntimeJavaType host, ClassFile classFile, DynamicOrAotTypeWrapper wrapper, TypeBuilder typeBuilder)
            {
                this.context = context;
                this.host = host;
                this.classFile = classFile;
                this.wrapper = wrapper;
                this.typeBuilder = typeBuilder;
            }

            /// <summary>
            /// Gets the <see cref="RuntimeContext"/> under which this instance is allocated.
            /// </summary>
            public RuntimeContext Context => context;

            /// <summary>
            /// True when this context is emitting a SECOND-generation body from
            /// <see cref="ByteCodeHelper.BuildLazyBody"/>, i.e. after the once-unloadable types have been
            /// resolved and after <c>typeBuilder</c> has been baked.
            /// </summary>
            /// <remarks>
            /// Two things change in that state, and both are load-bearing.
            ///
            /// The type builder can no longer accept Define*, so any path that would define a fresh field or
            /// helper throws InvalidOperation_TypeHasBeenCreated and the whole recompile is declined. Measured
            /// on a boot with IKVM_LAZY_SIG=1 (2026-09-07): fellBack 28 -> 489, and the logged stack frame
            /// names Compiler..ctor -- the prologue's unloadable-parameter cast, which reaches
            /// EmitDynamicCast -> DefineDynamicInstanceCheckCacheField -> typeBuilder.DefineField.
            ///
            /// And it is unnecessary work: the prologue cast only needs the dynamic form while the type is
            /// unresolved. By the time a second generation is compiled it is resolved, so a plain checkcast is
            /// both correct and ~1 instruction against the dynamic chain's ~10 -- that chain measured 94.22%
            /// of the type-check pool, 2.355% of the client render thread.
            /// </remarks>
            internal bool IsLazyRecompile { get; set; }

            /// <summary>True only while generation 1 is emitting an in-place candidate. Ordinary methods keep
            /// their original direct indy/MethodHandle emission and pay no bridge call.</summary>
            internal bool EmitLazySiteBridges { get; set; }

            internal bool UseLazySiteBridges => IsLazyRecompile || EmitLazySiteBridges;

            /// <summary>Defines an assembly-visible, content-keyed entry point around generation-1 indy or
            /// MethodHandle state. Generation 2 is emitted on another type and can call this method without
            /// naming the private nested type or cache that implements the site.</summary>
            internal MethodBuilder DefineLazySiteBridge(string kind, int siteKey, Type returnType, Type[] parameterTypes)
            {
                // Paired with the miss log in GetBakedLazySiteBridge. 344 generation-2 recompiles fail asking
                // for a bridge generation 1 did not emit, and every reading of the source says it should
                // have; diffing defined-vs-wanted is what settles which assumption is wrong.
                if (JVM.TraceLazyBodies)
                    Console.WriteLine($"[IKVM lazybody] bridgeDef {wrapper.Name} {kind}${siteKey:x} on={typeBuilder.Name}");
                return typeBuilder.DefineMethod(LazySiteBridgeName(kind, siteKey),
                    MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                    returnType, parameterTypes);
            }

            /// <summary>Returns generation 1's baked site bridge. Missing state is a safe Gen2 refusal, never
            /// permission to mint a second call site.</summary>
            internal MethodInfo GetBakedLazySiteBridge(string kind, int siteKey, Type[] parameterTypes)
            {
                var method = TryGetBakedLazySiteBridge(kind, siteKey, parameterTypes);
                if (method != null)
                    return method;

                System.Threading.Interlocked.Increment(ref LazyBodyStats.MissingBakedSite);
                if (JVM.TraceLazyBodies)
                    Console.WriteLine($"[IKVM lazybody] bridgeMiss {wrapper.Name} {kind}${siteKey:x} realType={(wrapper.TypeAsTBD != null ? wrapper.TypeAsTBD.Name : "null")}");
                throw new MissingMethodException(wrapper.Name, LazySiteBridgeName(kind, siteKey));
            }

            internal MethodInfo TryGetBakedLazySiteBridge(string kind, int siteKey, Type[] parameterTypes)
            {
                if (!IsLazyRecompile)
                    return null;

                try
                {
                    var real = wrapper.TypeAsTBD;
                    var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
                    var method = parameterTypes == null
                        ? real?.GetMethod(LazySiteBridgeName(kind, siteKey), flags)
                        : real?.GetMethod(LazySiteBridgeName(kind, siteKey), flags, null, parameterTypes, null);
                    if (method != null && method.DeclaringType == real)
                        return method;
                }
                catch { }

                return null;
            }

            static string LazySiteBridgeName(string kind, int siteKey) => "__<>lazySite$" + kind + "$" + siteKey.ToString("x");

            internal RuntimeByteCodeJavaType TypeWrapper
            {
                get { return wrapper; }
            }

            internal T GetValue<T>(int key)
                where T : class, new()
            {
                items ??= new List<Item>();
                for (int i = 0; i < items.Count; i++)
                    if (items[i].key == key && items[i].value is T value)
                        return value;

                Item item;
                item.key = key;
                T val = new T();
                item.value = val;
                items.Add(item);
                return val;
            }

            internal void EmitDynamicClassLiteral(CodeEmitter ilgen, RuntimeJavaType tw, bool dynamicCallerID)
            {
                Debug.Assert(tw.IsUnloadable);

                dynamicClassLiteral ??= new Dictionary<string, MethodInfo>();

                var cacheKey = tw.Name;
                if (dynamicCallerID)
                    cacheKey += ";dynamic";

                if (!dynamicClassLiteral.TryGetValue(cacheKey, out var method))
                {
                    var fb = typeBuilder.DefineField("__<>class" + helperNameCounter++, context.JavaBase.TypeOfJavaLangClass.TypeAsSignatureType, FieldAttributes.PrivateScope | FieldAttributes.Static);
                    var mb = DefineHelperMethod("__<>class", context.JavaBase.TypeOfJavaLangClass.TypeAsSignatureType, Type.EmptyTypes);
                    var ilgen2 = context.CodeEmitterFactory.Create(mb);
                    ilgen2.Emit(OpCodes.Ldsfld, fb);
                    var label = ilgen2.DefineLabel();
                    ilgen2.EmitBrtrue(label);
                    ilgen2.Emit(OpCodes.Ldstr, tw.Name);
                    EmitCallerID(ilgen2, dynamicCallerID);
                    ilgen2.Emit(OpCodes.Call, context.ByteCodeHelperMethods.DynamicClassLiteral);
                    ilgen2.Emit(OpCodes.Stsfld, fb);
                    ilgen2.MarkLabel(label);
                    ilgen2.Emit(OpCodes.Ldsfld, fb);
                    ilgen2.Emit(OpCodes.Ret);
                    ilgen2.DoEmit();
                    method = mb;
                    dynamicClassLiteral.Add(cacheKey, method);
                }

                ilgen.Emit(OpCodes.Call, method);
            }

            internal void EmitHostCallerID(CodeEmitter ilgen)
            {
#if IMPORTER || FIRST_PASS
                throw new InvalidOperationException();
#else
                EmitLiveObjectLoad(ilgen, DynamicCallerIDProvider.CreateCallerID(host));
                context.JavaBase.TypeOfIkvmInternalCallerID.EmitCheckcast(ilgen);
#endif
            }

            internal void EmitCallerID(CodeEmitter ilgen, bool dynamic)
            {
#if !FIRST_PASS && !IMPORTER
                if (dynamic)
                {
                    EmitLiveObjectLoad(ilgen, DynamicCallerIDProvider.Instance);
                    ilgen.Emit(OpCodes.Call, context.ByteCodeHelperMethods.DynamicCallerID);
                    return;
                }
#endif
                if (callerIDMethod == null)
                {
                    CreateGetCallerID();
                }

                ilgen.Emit(OpCodes.Call, callerIDMethod);
            }

            /// <summary>Scratch TypeBuilders that have already had a CallerID field defined on them by some
            /// OTHER FinishContext. Measures how often a recompile scratch is shared, which is the fact that
            /// made the old hard-coded field name unsafe. Only touched under a lazy recompile.</summary>
            static readonly HashSet<TypeBuilder> callerIDScratchSeen = new HashSet<TypeBuilder>();

            private void CreateGetCallerID()
            {
                RuntimeJavaType tw = context.JavaBase.TypeOfIkvmInternalCallerID;
                /* SCOPED like the nested types are. Under a lazy recompile `typeBuilder` is a scratch SHARED
                 * by every pending method in one MODULE (ByteCodeHelper: `foreach (var kv in byModule)`), and
                 * those methods come from many different classes -- each with its own FinishContext, each
                 * reaching here. The name was hard-coded, so every context defined `__<callerID>` on the one
                 * type: not PrivateScope (which is what makes duplicate HELPER-METHOD names harmless, since
                 * CompilerControlled members are referenced by token, never by name) and not routed through
                 * NestedName. `nestedNamePrefix` is empty for ordinary compilation, so this is a no-op there.
                 * Counted below rather than assumed -- see Gen2ScratchShared. */
                FieldBuilder callerIDField = typeBuilder.DefineField(NestedName("__<callerID>"), tw.TypeAsSignatureType, FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.SpecialName);
                if (IsLazyRecompile)
                {
                    lock (callerIDScratchSeen)
                        if (!callerIDScratchSeen.Add(typeBuilder))
                            System.Threading.Interlocked.Increment(ref LazyBodyStats.Gen2ScratchShared);
                }
                MethodBuilder mb = DefineHelperMethod("__<GetCallerID>", tw.TypeAsSignatureType, Type.EmptyTypes);
                callerIDMethod = mb;
                CodeEmitter ilgen = context.CodeEmitterFactory.Create(mb);
                ilgen.Emit(OpCodes.Ldsfld, callerIDField);
                CodeEmitterLabel done = ilgen.DefineLabel();
                ilgen.EmitBrtrue(done);
                EmitCallerIDInitialization(ilgen, callerIDField);
                ilgen.MarkLabel(done);
                ilgen.Emit(OpCodes.Ldsfld, callerIDField);
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
            }

            private void RegisterNestedTypeBuilder(TypeBuilder tb)
            {
                nestedTypeBuilders ??= new List<TypeBuilder>();
                nestedTypeBuilders.Add(tb);
            }

            /// <summary>
            /// The first-generation body of a method whose real body was moved into a static helper.
            /// </summary>
            private sealed class LazyBody
            {

                /// <summary>The static helper holding the first-generation body.</summary>
                internal MethodBuilder Helper;

                /// <summary>Delegate type matching (this?, args...) -> ret, used for both the cache field and
                /// the trampoline's invoke.</summary>
                internal Type DelegateType;

                /// <summary>Static field caching the bound delegate after the first execution.</summary>
                internal FieldBuilder Cache;

                /// <summary>Helper's name, passed to BuildLazyBody so it can bind the first generation as the
                /// fallback when a recompile is impossible.</summary>
                internal string HelperName;

                /// <summary>Index into LazyBodyRegistry; the only thing the emitted trampoline can carry.</summary>
                internal int RegistryId;

            }

            /// <summary>
            /// Bakes a scratch type used by a recompile, plus any nested types the recompile defined, and
            /// reports whether the result is usable.
            /// </summary>
            /// <returns>
            /// False if the recompile defined a CallerID nested type, which means the body it produced refers to
            /// storage that only exists on the scratch type rather than on the real one.
            /// </returns>
            /// <remarks>
            /// Detected by name because that is the only handle available at this point, and returned as a
            /// decline rather than thrown: the caller's correct response is to keep the first-generation body,
            /// not to fail the method.
            /// </remarks>
            /// <summary>
            /// Bakes <paramref name="scratch"/> and RETURNS the created type, which the in-place path needs
            /// in order to look up the emitted method and take its MonoMethod*.
            /// </summary>
            /// <remarks>
            /// Separate from BakeScratchType only because that one discards CreateType()'s result and
            /// TypeBuilder.CreateType() is not something to call twice on a hunch -- a decline here would be
            /// indistinguishable from the feature not working, which is the failure shape this file has paid
            /// for repeatedly.
            /// </remarks>
            /// <summary>
            /// Whether this context defined a CallerID nested type, which makes its body unusable on another
            /// method: a CallerID identifies the CALLING class, so one minted on a scratch type is the wrong
            /// identity.
            /// </summary>
            internal bool DefinedCallerID
            {
                get
                {
                    if (nestedTypeBuilders != null)
                        foreach (var tb in nestedTypeBuilders)
                            if (tb.Name != null && tb.Name.Contains(NestedTypeName.CallerID))
                                return true;

                    return false;
                }
            }

            /// <summary>
            /// Creates the nested types this context defined. Called AFTER the shared scratch type has been
            /// created, because a batch puts many methods -- and therefore many FinishContexts -- on one type
            /// and CreateType() must happen exactly once for it.
            /// </summary>
            internal void CreateNestedTypes()
            {
                if (nestedTypeBuilders != null)
                    foreach (var tb in nestedTypeBuilders)
                        tb.CreateType();
            }

            internal Type BakeScratchTypeCreate(TypeBuilder scratch, out bool needsCallerID)
            {
                needsCallerID = false;
                if (nestedTypeBuilders != null)
                    foreach (var tb in nestedTypeBuilders)
                        if (tb.Name != null && tb.Name.Contains(NestedTypeName.CallerID))
                            needsCallerID = true;

                var created = scratch.CreateType();
                if (nestedTypeBuilders != null)
                    foreach (var tb in nestedTypeBuilders)
                        tb.CreateType();

                return created;
            }

            internal bool BakeScratchType(TypeBuilder scratch)
            {
                var needsCallerID = false;
                if (nestedTypeBuilders != null)
                    foreach (var tb in nestedTypeBuilders)
                        if (tb.Name != null && tb.Name.Contains(NestedTypeName.CallerID))
                            needsCallerID = true;

                scratch.CreateType();
                if (nestedTypeBuilders != null)
                    foreach (var tb in nestedTypeBuilders)
                        tb.CreateType();

                return !needsCallerID;
            }

            /// <summary>
            /// Whether <paramref name="m"/> is worth compiling as a generational body.
            /// </summary>
            /// <remarks>
            /// Every rejection is counted by reason (see LazyBodyStats): a gate that rejects everything looks
            /// exactly like a gate that never ran, and that has already happened once here.
            /// </remarks>
            private bool IsLazyBodyCandidate(RuntimeJavaMethod mw, ClassFile.Method m, out string[] dependencies, bool inPlace = false)
            {
                dependencies = Array.Empty<string>();
                // No body to move, or a body that must run exactly where it is: a constructor's trampoline
                // would run before the object is initialised, and a static initialiser's before the type is.
                if (m.IsAbstract || m.IsNative || m.Instructions == null)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejShape);
                // <clinit> is excluded unconditionally: it runs once, so a relink buys no steady state.
                if (ReferenceEquals(m.Name, StringConstants.CLINIT))
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejShape);
                // <init> only when the IN-PLACE path is the one asking -- the trampoline objection is the
                // delegate path's, not this one's. See JVM.LazyInPlaceCtors.
                if (ReferenceEquals(m.Name, StringConstants.INIT)
                    && !(inPlace && JVM.LazyInPlaceCtors))
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejShape);

                // Decided from the constant pool, NOT from __dynamic_* opcodes: those are assigned by the
                // analyzer, which has not run yet at this point in FinishContext.
                dependencies = Compiler.GetDynamicDependencies(classFile, m, wrapper);
                if (dependencies.Length == 0)
                {
                    // wasm fork (plan2x F7a): nothing in the constant pool, but an unloadable parameter still costs
                    // generation 1's dynamic entry check on every call. See JVM.LazySigDeps.
                    var sigDeps = SignatureDependencies(mw);
                    if (sigDeps.Length != 0)
                    {
                        System.Threading.Interlocked.Increment(ref LazyBodyStats.NoDynSig);
                        if (JVM.LazySigDeps)
                        {
                            dependencies = sigDeps;
                            System.Threading.Interlocked.Increment(ref LazyBodyStats.SigOnly);
                        }
                    }
                }
                if (dependencies.Length == 0 && !JVM.LazyAll)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejNoDynamic);

                if (JVM.LazySkipExceptionHandlers && m.ExceptionTable.Length != 0)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejExceptionHandlers);

                // The delegate types only go up to a fixed arity; `this` counts against it. The IN-PLACE path builds
                // no delegate (its scratch method takes the real method's own signature), so the cap is the delegate
                // path's alone -- 266 in-place rejections per run (plan2x F7e, IKVM_LAZY_ARITY).
                var parameters = mw.GetParameters();
                if (!(inPlace && JVM.LazyInPlaceArity) && parameters.Length + (m.IsStatic ? 0 : 1) > 8)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejArity);

                // Signature screening, SPLIT BY CAUSE -- the two halves are not the same problem.
                //
                // Ghost, ghost-array, remapped and non-primitive value types genuinely need conversion at
                // the delegate boundary, which the trampoline does not do. An unloadable does not: it erases
                // to plain System.Object, so the delegate type is expressible and the boundary is a straight
                // reference pass. Refusing both together refused the population with the MOST to gain, since
                // an unloadable in the signature is the marker of the very condition a second generation
                // removes. See JVM.LazyUnloadableSig for the measurement.
                var sigUnloadable = false;
                var sigAwkward = false;
                ScreenSignatureType(mw.ReturnType, ref sigUnloadable, ref sigAwkward);
                for (int i = 0; i < parameters.Length; i++)
                    ScreenSignatureType(parameters[i], ref sigUnloadable, ref sigAwkward);
                if (!m.IsStatic)
                    ScreenSignatureType(wrapper, ref sigUnloadable, ref sigAwkward);

                // Awkward first: a signature that is both is refused either way, and attributing it to the
                // half that is NOT admissible keeps sigUnloadable an honest count of what the knob would add.
                // Only the DELEGATE path cares. Ghost, remapped and value types needed conversion because
                // the body was reached through a CreateMethodHandleDelegateType delegate, which cannot
                // express a ghost struct or a byref receiver -- hence DynamicBinder's MakeByRefType hack.
                // The in-place path has no delegate: the scratch method's signature comes from
                // GetParametersForDefineMethod(), the SAME function that built the real method's signature,
                // so it is identical and there is nothing to convert. Measured weight of this bucket is
                // small either way (ghost machinery is 0.0% of the client thread), but it is free to admit.
                if (sigAwkward && JVM.LazyInPlace == false)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejSigAwkward);
                if (sigUnloadable && JVM.LazyUnloadableSig == false)
                    return LazyBodyStats.Reject(ref LazyBodyStats.RejSigUnloadable);

                return LazyBodyStats.Accept();
            }

            /// <summary>
            /// The classes a generation 2 would need to resolve an unloadable PARAMETER's entry check: the element
            /// class of each unloadable parameter type, since the loader publishes element classes, never arrays.
            /// </summary>
            private static string[] SignatureDependencies(RuntimeJavaMethod mw)
            {
                HashSet<string> deps = null;
                foreach (var tw in mw.GetParameters())
                {
                    if (!tw.IsUnloadable)
                        continue;
                    var name = tw.Name;
                    var start = 0;
                    while (start < name.Length && name[start] == '[')
                        start++;
                    if (start > 0)
                        name = start < name.Length && name[start] == 'L' && name[name.Length - 1] == ';' ? name.Substring(start + 1, name.Length - start - 2) : null;
                    if (!string.IsNullOrEmpty(name))
                        (deps ??= new HashSet<string>()).Add(name);
                }
                if (deps == null)
                    return Array.Empty<string>();
                var result = new string[deps.Count];
                deps.CopyTo(result);
                return result;
            }

            /// <summary>
            /// Classifies one signature type into the two refusal causes. Unloadable is tested FIRST and
            /// exclusively, because an unloadable reports nothing useful for the other predicates.
            /// </summary>
            private static void ScreenSignatureType(RuntimeJavaType tw, ref bool unloadable, ref bool awkward)
            {
                if (tw.IsUnloadable)
                    unloadable = true;
                else if (tw.IsGhost || tw.IsGhostArray || tw.IsNonPrimitiveValueType || tw.IsRemapped)
                    awkward = true;
            }

            /// <summary>
            /// Compiles <paramref name="m"/>'s body into a static helper and registers what a later recompile
            /// will need, or returns null if the method is not a candidate.
            /// </summary>
            private LazyBody TryMoveBodyToHelper(RuntimeJavaMethod mw, ClassFile.Method m, int index, RuntimeJavaType host, ref bool nonleaf)
            {
                if (IsLazyBodyCandidate(mw, m, out var dependencies) == false)
                    return null;

                var parameters = mw.GetParameters();
                var parameterTypesForDefine = mw.GetParametersForDefineMethod();
                // An instance method's helper is static, so `this` becomes an explicit leading parameter.
                var parameterTypes = m.IsStatic ? parameterTypesForDefine : Prepend(wrapper.TypeAsSignatureType, parameterTypesForDefine);
                var args = m.IsStatic ? parameters : Prepend(wrapper, parameters);

                var helperName = "__<>lazybody_" + helperNameCounter++;
                var helper = typeBuilder.DefineMethod(helperName, MethodAttributes.Static, mw.ReturnTypeForDefineMethod, parameterTypes);
                var ilgen = context.CodeEmitterFactory.Create(helper);
                Compiler.Compile(this, host, wrapper, mw, classFile, m, ilgen, ref nonleaf);
                ilgen.CheckLabels();
                ilgen.DoEmit();

                var delegateType = context.MethodHandleUtil.CreateMethodHandleDelegateType(args, mw.ReturnType);

                // Keep the ClassFile itself, not its instructions: the recompile calls Reparse() precisely
                // because the analyzer has already rewritten this instance's opcode stream in place.
                var registryId = LazyBodyRegistry.Add(new LazyBodyRegistry.Entry
                {
                    ClassFile = JVM.LazySlim ? null : classFile,
                    Source = JVM.LazySlim ? classFile.GetReparseSource() : null,
                    MethodIndex = index,
                    Wrapper = wrapper,
                    Host = host,
                    TypeBuilder = typeBuilder,
                    Method = mw,
                    Dependencies = dependencies,
                });

                return new LazyBody
                {
                    RegistryId = registryId,
                    Helper = helper,
                    HelperName = helperName,
                    DelegateType = delegateType,
                    Cache = typeBuilder.DefineField("__<>lazybodyCache" + helperNameCounter++, delegateType, FieldAttributes.Static),
                };
            }

            private static Type[] Prepend(Type first, Type[] rest)
            {
                var a = new Type[rest.Length + 1];
                a[0] = first;
                Array.Copy(rest, 0, a, 1, rest.Length);
                return a;
            }

            private static RuntimeJavaType[] Prepend(RuntimeJavaType first, RuntimeJavaType[] rest)
            {
                var a = new RuntimeJavaType[rest.Length + 1];
                a[0] = first;
                Array.Copy(rest, 0, a, 1, rest.Length);
                return a;
            }

            /// <summary>
            /// Registers <paramref name="m"/> for an IN-PLACE second generation and returns the one-shot flag
            /// field the hook tests, or null if it is not a candidate.
            /// </summary>
            /// <remarks>
            /// Unlike the delegate path this moves NOTHING: the first-generation body is compiled straight
            /// into the method, preceded by a hook that fires once. So a method that is never relinked is
            /// byte-for-byte what it would have been with the whole feature off, plus three instructions --
            /// against the delegate path, which costs a load, a branch, a Delegate::Invoke and an extra frame
            /// on every call forever.
            /// </remarks>
            private FieldBuilder TryPrepareInPlaceRelink(RuntimeJavaMethod mw, ClassFile.Method m, int index, RuntimeJavaType host)
            {
                var candidate = IsLazyBodyCandidate(mw, m, out var dependencies, inPlace: true);
                if (LazyBodyRegistry.IsWatched(wrapper.Name, m.Name))
                    Console.WriteLine($"[IKVM lazybody] watch {wrapper.Name}.{m.Name}{m.Signature}: in-place candidate={candidate} deps=[{string.Join(", ", dependencies)}] handlers={m.ExceptionTable.Length}");
                if (candidate == false)
                    return null;

                // Same side table as the delegate path: the hook is emitted code and can only carry an
                // immediate, so it carries an integer id.
                var registryId = LazyBodyRegistry.Add(new LazyBodyRegistry.Entry
                {
                    ClassFile = JVM.LazySlim ? null : classFile,
                    Source = JVM.LazySlim ? classFile.GetReparseSource() : null,
                    MethodIndex = index,
                    Wrapper = wrapper,
                    Host = host,
                    TypeBuilder = typeBuilder,
                    Method = mw,
                    Dependencies = dependencies,
                });

                var flag = typeBuilder.DefineField("__<>relinked" + registryId, context.Types.Int32, FieldAttributes.Static | FieldAttributes.PrivateScope);
                inPlaceRelinkId = registryId;
                return flag;
            }

            /// <summary>The registry id assigned by the last <see cref="TryPrepareInPlaceRelink"/> call.</summary>
            private int inPlaceRelinkId;

            /// <summary>
            /// Emits the dependency-gated relink hook that precedes an in-place first-generation body.
            /// </summary>
            /// <remarks>
            /// Shape: test a flag, SET IT, then call. Setting before calling is not an optimisation -- it is
            /// what stops re-entry while RelinkBody performs its lock-free dependency probe and queues the
            /// background drain. The flag is written non-atomically on
            /// purpose: two threads racing here may both call RelinkBody, while the registry admits the method
            /// to its one recompile exactly once. The helper replaces the guard with either a retry countdown
            /// or the permanent -1 state before the body continues.
            ///
            /// Waiting and queued methods therefore reach the helper only periodically; after a successful swap
            /// the method's generation-2 IL contains no hook at all.
            /// </remarks>
            private void EmitInPlaceRelinkHook(CodeEmitter ilgen, FieldBuilder flag, int registryId)
            {
                var probe = ilgen.DefineLabel();
                var skip = ilgen.DefineLabel();
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Ldsfld, flag);
                ilgen.Emit(OpCodes.Dup);
                ilgen.EmitBrfalse(probe);
                ilgen.EmitLdc_I4(0);
                ilgen.EmitBlt(skip); // -1 means probing/terminal: never re-enter or retry

                // Positive values are a cheap Gen1 countdown. This avoids the helper call, registry lock and
                // dependency scan until the selected retry invocation.
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Ldsfld, flag);
                ilgen.EmitLdc_I4(1);
                ilgen.Emit(OpCodes.Sub);
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Stsfld, flag);
                ilgen.EmitBr(skip);

                ilgen.MarkLabel(probe);
                ilgen.Emit(OpCodes.Pop);
                ilgen.EmitLdc_I4(-1); // recursion guard during the dependency probe and queue operation
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Stsfld, flag);
                ilgen.EmitLdc_I4(registryId);
                ilgen.Emit(OpCodes.Call, context.ByteCodeHelperMethods.RelinkBody);
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Stsfld, flag);
                ilgen.MarkLabel(skip);
            }

            /// <summary>
            /// Emits the method as: use the cached delegate if present, otherwise build it once, then tail into
            /// it with the original arguments.
            /// </summary>
            /// <remarks>
            /// The cache is read once into the evaluation stack and duplicated rather than re-read after the
            /// store, so a concurrent build on another thread cannot make this invocation see null. Both threads
            /// may build; both results are equivalent, and the volatile store publishes whichever wins.
            /// </remarks>
            private void EmitLazyBodyTrampoline(CodeEmitter ilgen, LazyBody lazy, RuntimeJavaMethod mw, ClassFile.Method m)
            {
                var haveDelegate = ilgen.DefineLabel();
                ilgen.Emit(OpCodes.Ldsfld, lazy.Cache);
                ilgen.Emit(OpCodes.Dup);
                ilgen.EmitBrtrue(haveDelegate);
                ilgen.Emit(OpCodes.Pop);

                ilgen.EmitLdc_I4(lazy.RegistryId);
                ilgen.Emit(OpCodes.Ldtoken, typeBuilder);
                ilgen.Emit(OpCodes.Call, context.CompilerFactory.GetTypeFromHandleMethod);
                ilgen.Emit(OpCodes.Ldstr, lazy.HelperName);
                ilgen.Emit(OpCodes.Ldtoken, lazy.DelegateType);
                ilgen.Emit(OpCodes.Call, context.CompilerFactory.GetTypeFromHandleMethod);
                ilgen.Emit(OpCodes.Call, context.ByteCodeHelperMethods.BuildLazyBody);
                ilgen.Emit(OpCodes.Castclass, lazy.DelegateType);
                ilgen.Emit(OpCodes.Dup);
                ilgen.Emit(OpCodes.Volatile);
                ilgen.Emit(OpCodes.Stsfld, lazy.Cache);

                ilgen.MarkLabel(haveDelegate);
                var argc = mw.GetParameters().Length + (m.IsStatic ? 0 : 1);
                for (int i = 0; i < argc; i++)
                    ilgen.EmitLdarg(i);
                ilgen.Emit(OpCodes.Callvirt, context.MethodHandleUtil.GetDelegateInvokeMethod(lazy.DelegateType));
                ilgen.Emit(OpCodes.Ret);
            }

            internal Type FinishImpl()
            {
                var methods = wrapper.GetMethods();
                var fields = wrapper.GetFields();
#if IMPORTER
                wrapper.FinishGhost(typeBuilder, methods);
#endif

                if (!classFile.IsInterface)
                {
                    // set the base type (this needs to be done before we emit any methods, because in the static compiler
                    // GetBaseTypeForDefineType() has the side effect of inserting the __WorkaroundBaseClass__ when necessary)
                    typeBuilder.SetParent(wrapper.GetBaseTypeForDefineType());
                }

                // if we're not abstract make sure we don't inherit any abstract methods
                if (!wrapper.IsAbstract)
                {
                    var parent = wrapper.BaseTypeWrapper;

                    // if parent is not abstract, the .NET implementation will never have abstract methods (only
                    // stubs that throw AbstractMethodError)
                    // NOTE interfaces are supposed to be abstract, but the VM doesn't enforce this, so
                    // we have to check for a null parent (interfaces have no parent).
                    while (parent != null && parent.IsAbstract)
                    {
                        foreach (RuntimeJavaMethod mw in parent.GetMethods())
                        {
                            var mi = mw.GetMethod() as MethodInfo;
                            if (mi != null && mi.IsAbstract && !mi.DeclaringType.IsInterface)
                            {
                                bool needStub = false;
                                bool needRename = false;
                                if (mw.IsPublic || mw.IsProtected)
                                {
                                    var fmw = wrapper.GetMethod(mw.Name, mw.Signature, true);
                                    while (fmw != mw && (fmw.IsStatic || fmw.IsPrivate))
                                    {
                                        needRename = true;
                                        fmw = fmw.DeclaringType.BaseTypeWrapper.GetMethod(mw.Name, mw.Signature, true);
                                    }
                                    if (fmw == mw && fmw.DeclaringType != wrapper)
                                    {
                                        needStub = true;
                                    }
                                }
                                else
                                {
                                    var fmw = wrapper.GetMethod(mw.Name, mw.Signature, true);
                                    while (fmw != mw && (fmw.IsStatic || fmw.IsPrivate || !(mw.DeclaringType.IsPackageAccessibleFrom(fmw.DeclaringType) || (mw.IsInternal && mw.DeclaringType.InternalsVisibleTo(fmw.DeclaringType)))))
                                    {
                                        needRename = true;
                                        fmw = fmw.DeclaringType.BaseTypeWrapper.GetMethod(mw.Name, mw.Signature, true);
                                    }
                                    if (fmw == mw && fmw.DeclaringType != wrapper)
                                    {
                                        needStub = true;
                                    }
                                }
                                if (needStub)
                                {
                                    // NOTE in Sun's JRE 1.4.1 this method cannot be overridden by subclasses,
                                    // but I think this is a bug, so we'll support it anyway.
                                    string name = mi.Name;
                                    MethodAttributes attr = mi.Attributes & ~(MethodAttributes.Abstract | MethodAttributes.NewSlot);
                                    if (needRename)
                                    {
                                        name = "__<>" + name + "/" + mi.DeclaringType.FullName;
                                        attr = MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.NewSlot;
                                    }
                                    MethodBuilder mb = mw.GetDefineMethodHelper().DefineMethod(wrapper, typeBuilder, name, attr);
                                    if (needRename)
                                    {
                                        typeBuilder.DefineMethodOverride(mb, mi);
                                    }
                                    context.AttributeHelper.HideFromJava(mb);
                                    CodeEmitter ilgen = context.CodeEmitterFactory.Create(mb);
                                    ilgen.EmitThrow("java.lang.AbstractMethodError", mw.DeclaringType.Name + "." + mw.Name + mw.Signature);
                                    ilgen.DoEmit();
                                    wrapper.EmitLevel4Warning(HardError.AbstractMethodError, mw.DeclaringType.Name + "." + mw.Name + mw.Signature);
                                }
                            }
                        }

                        parent = parent.BaseTypeWrapper;
                    }
                }
#if IMPORTER
                TypeBuilder tbDefaultMethods = null;
#endif
                bool basehasclinit = wrapper.BaseTypeWrapper != null && wrapper.BaseTypeWrapper.HasStaticInitializer;
                int clinitIndex = -1;
                bool hasConstructor = false;
                for (int i = 0; i < classFile.Methods.Length; i++)
                {
                    var m = classFile.Methods[i];
                    var mb = (MethodBuilder)methods[i].GetMethod();
                    if (mb == null)
                    {
                        // method doesn't really exist (e.g. delegate constructor or <clinit> that is optimized away)
                        if (m.IsConstructor)
                        {
                            hasConstructor = true;
                        }
                    }
                    else if (m.IsClassInitializer)
                    {
                        // we handle the <clinit> after we've done the other methods,
                        // to make it easier to inject code needed by the other methods
                        clinitIndex = i;
                        continue;
                    }
                    else if (m.IsConstructor)
                    {
                        hasConstructor = true;
                        var ilGenerator = context.CodeEmitterFactory.Create(mb);

                        // RELINK CONSTRUCTORS TOO, under IKVM_LAZY_CTORS.
                        //
                        // Constructors are emitted on this branch and never reached TryPrepareInPlaceRelink,
                        // so they were silently exempt -- which is why `rej: shape=` reads 0 and why lifting
                        // the candidacy gate alone measured nothing (R279). The cost is real: the largest
                        // surviving lazy-link binder in the plateau is
                        // VoxelShapeSimpleCube:__<>DynamicBinder__...__<init> at 3.06 M instr/tick, and
                        // constructors run per allocation.
                        //
                        // The hook is emitted BEFORE the body and is static-only IL -- a volatile static
                        // field read, a branch, and a static call. It never touches `this`, so it is legal
                        // ahead of the base constructor call, which is the only ordering rule that binds
                        // here. The emitter buffers, so hook ops simply precede the body's in the same
                        // stream and CompileConstructorBody's own DoEmit flushes both.
                        //
                        // Hooked at the CALL SITE, not inside CompileConstructorBody: that helper also
                        // compiles <clinit> (the clinitIndex call below), and <clinit> must stay exempt --
                        // it runs once, so a relink buys no steady state, and the hook would fire inside
                        // type initialisation.
                        FieldBuilder ctorFlag = null;
                        if (JVM.LazyBodies && JVM.LazyInPlace && JVM.LazyInPlaceCtors)
                            ctorFlag = TryPrepareInPlaceRelink(methods[i], m, i, host);

                        if (ctorFlag != null)
                        {
                            EmitInPlaceRelinkHook(ilGenerator, ctorFlag, inPlaceRelinkId);
                            EmitLazySiteBridges = true;
                            try
                            {
                                CompileConstructorBody(this, ilGenerator, i);
                            }
                            finally
                            {
                                EmitLazySiteBridges = false;
                            }
                        }
                        else
                        {
                            CompileConstructorBody(this, ilGenerator, i);
                        }
                    }
                    else
                    {
#if IMPORTER
                        if (methods[i].GetParameters().Length > MethodHandleUtil.MaxArity && methods[i].RequiresNonVirtualDispatcher && wrapper.ClassLoader.EmitNoRefEmitHelpers)
                        {
                            wrapper.ClassLoader.GetTypeWrapperFactory().DefineDelegate(methods[i].GetParameters().Length, methods[i].ReturnType == context.PrimitiveJavaTypeFactory.VOID);
                        }
#endif
                        if (m.IsAbstract)
                        {
                            var stub = false;
                            if (!classFile.IsAbstract)
                            {
                                // NOTE in the JVM it is apparently legal for a non-abstract class to have abstract methods, but
                                // the CLR doens't allow this, so we have to emit a method that throws an AbstractMethodError
                                stub = true;
                                wrapper.EmitLevel4Warning(HardError.AbstractMethodError, classFile.Name + "." + m.Name + m.Signature);
                            }
                            else if (classFile.IsPublic && !classFile.IsFinal && !(m.IsPublic || m.IsProtected))
                            {
                                // We have an abstract package accessible method in our public class. To allow a class in another
                                // assembly to subclass this class, we must fake the abstractness of this method.
                                stub = true;
                            }

                            if (stub)
                            {
                                var ilGenerator = context.CodeEmitterFactory.Create(mb);
                                ilGenerator.EmitThrow("java.lang.AbstractMethodError", classFile.Name + "." + m.Name + m.Signature);
                                ilGenerator.DoEmit();
                            }
                        }
                        else if (m.IsNative)
                        {
                            if ((mb.Attributes & MethodAttributes.PinvokeImpl) != 0)
                            {
                                continue;
                            }

                            if (wrapper.IsDelegate)
                            {
                                mb.SetImplementationFlags(mb.GetMethodImplementationFlags() | MethodImplAttributes.Runtime);
                                continue;
                            }

                            Profiler.Enter("JavaTypeImpl.Finish.Native");
                            try
                            {
                                var ilGenerator = context.CodeEmitterFactory.Create(mb);
#if IMPORTER
                                // do we have an implementation in map.xml?
                                if (wrapper.EmitMapXmlMethodPrologueAndOrBody(ilGenerator, classFile, m))
                                {
                                    ilGenerator.DoEmit();
                                    continue;
                                }

                                if (m.InterlockedCompareAndSetField != null && EmitInterlockedCompareAndSet(methods[i], m.InterlockedCompareAndSetField, ilGenerator))
                                {
                                    ilGenerator.DoEmit();
                                    continue;
                                }
#endif

                                MethodInfo nativeMethod = null;
                                var args = methods[i].GetParameters();
                                var nargs = args;

#if IMPORTER
                                // see if there exists a "managed JNI" class for this type
                                var nativeCodeType = context.Resolver.ResolveRuntimeType("IKVM.Java.Externs." + classFile.Name.Replace("$", "+")).AsReflection();
                                if (nativeCodeType != null)
                                {
                                    if (!m.IsStatic)
                                        nargs = ArrayUtil.Concat(wrapper, args);

                                    if (methods[i].HasCallerID)
                                        nargs = ArrayUtil.Concat(nargs, context.JavaBase.TypeOfIkvmInternalCallerID);

                                    foreach (var method in nativeCodeType.GetMethods(BindingFlags.Static | BindingFlags.Public))
                                    {
                                        var param = method.GetParameters();
                                        int paramLength = param.Length;

                                        while (paramLength != 0 && (param[paramLength - 1].IsIn || param[paramLength - 1].ParameterType.IsByRef))
                                            paramLength--;

                                        var match = new RuntimeJavaType[paramLength];
                                        for (int j = 0; j < paramLength; j++)
                                            match[j] = context.ClassLoaderFactory.GetJavaTypeFromType(param[j].ParameterType);

                                        if (m.Name == method.Name && IsCompatibleArgList(nargs, match))
                                        {
                                            nativeMethod = method;
                                            break;
                                        }
                                    }
                                }

#endif

                                if (nativeMethod != null)
                                {
#if IMPORTER
                                    for (int j = 0; j < nargs.Length; j++)
                                        ilGenerator.EmitLdarg(j);

                                    var param = nativeMethod.GetParameters();
                                    for (int j = nargs.Length; j < param.Length; j++)
                                    {
                                        var paramType = param[j].ParameterType;
                                        var fieldTypeWrapper = context.ClassLoaderFactory.GetJavaTypeFromType(paramType.IsByRef ? paramType.GetElementType() : paramType);
                                        var field = wrapper.GetFieldWrapper(param[j].Name, fieldTypeWrapper.SigName);
                                        if (field == null)
                                        {
                                            wrapper.Context.Diagnostics.GenericCompilerError($"Error: Native method field binding not found: {classFile.Name}.{param[j].Name}{fieldTypeWrapper.SigName}");
                                            wrapper.Context.StaticCompiler.errorCount++;
                                            continue;
                                        }
                                        if (m.IsStatic && !field.IsStatic)
                                        {
                                            wrapper.Context.Diagnostics.GenericCompilerError($"Error: Native method field binding cannot access instance field from static method: {classFile.Name}.{param[j].Name}{fieldTypeWrapper.SigName}");
                                            wrapper.Context.StaticCompiler.errorCount++;
                                            continue;
                                        }
                                        if (!field.IsAccessibleFrom(wrapper, wrapper, wrapper))
                                        {
                                            wrapper.Context.Diagnostics.GenericCompilerError($"Error: Native method field binding not accessible: {classFile.Name}.{param[j].Name}{fieldTypeWrapper.SigName}");
                                            wrapper.Context.StaticCompiler.errorCount++;
                                            continue;
                                        }
                                        if (paramType.IsByRef && field.IsFinal)
                                        {
                                            wrapper.Context.Diagnostics.GenericCompilerError($"Error: Native method field binding cannot use ByRef for final field: {classFile.Name}.{param[j].Name}{fieldTypeWrapper.SigName}");
                                            wrapper.Context.StaticCompiler.errorCount++;
                                            continue;
                                        }
                                        field.Link();
                                        if (paramType.IsByRef && field.GetField() == null)
                                        {
                                            wrapper.Context.Diagnostics.GenericCompilerError($"Error: Native method field binding cannot use ByRef on field without backing field: {classFile.Name}.{param[j].Name}{fieldTypeWrapper.SigName}");
                                            wrapper.Context.StaticCompiler.errorCount++;
                                            continue;
                                        }
                                        if (!field.IsStatic)
                                        {
                                            ilGenerator.EmitLdarg(0);
                                        }
                                        if (paramType.IsByRef)
                                        {
                                            ilGenerator.Emit(field.IsStatic ? OpCodes.Ldsflda : OpCodes.Ldflda, field.GetField());
                                        }
                                        else
                                        {
                                            field.EmitGet(ilGenerator);
                                        }
                                    }
                                    ilGenerator.Emit(OpCodes.Call, nativeMethod);
                                    var retTypeWrapper = methods[i].ReturnType;
                                    if (!retTypeWrapper.TypeAsTBD.Equals(nativeMethod.ReturnType) && !retTypeWrapper.IsGhost)
                                        ilGenerator.Emit(OpCodes.Castclass, retTypeWrapper.TypeAsTBD);

                                    ilGenerator.Emit(OpCodes.Ret);
#endif
                                }
                                else
                                {
                                    if (wrapper.ClassLoader.NoJNI)
                                    {
                                        // since NoJniStubs can only be set when we're statically compiling, it is safe to use the "compiler" trace switch
                                        wrapper.ClassLoader.Diagnostics.GenericCompilerWarning($"Native method not implemented: {classFile.Name}.{m.Name}.{m.Signature}");
                                        ilGenerator.EmitThrow("java.lang.UnsatisfiedLinkError", "Native method not implemented (compiled with -nojni): " + classFile.Name + "." + m.Name + m.Signature);
                                    }
                                    else
                                    {
                                        new JniBuilder(context).Generate(this, ilGenerator, wrapper, methods[i], typeBuilder, classFile, m, args, false);
                                    }
                                }

                                ilGenerator.DoEmit();
                            }
                            finally
                            {
                                Profiler.Leave("JavaTypeImpl.Finish.Native");
                            }
                        }
                        else
                        {
                            if (m.IsVirtual && classFile.IsInterface)
                            {
                                mb = (MethodBuilder)RuntimeDefaultInterfaceJavaMethod.GetImpl(methods[i]);
#if IMPORTER
                                CreateDefaultMethodInterop(ref tbDefaultMethods, mb, methods[i]);
#endif
                            }

                            var ilGenerator = context.CodeEmitterFactory.Create(mb);
                            if (!m.IsStatic && !m.IsPublic && classFile.IsInterface)
                            {
                                // Java 8 non-virtual interface method that we compile as a static method,
                                // we need to make sure the passed in this reference isn't null
                                ilGenerator.EmitLdarg(0);
                                if (wrapper.IsGhost)
                                    ilGenerator.Emit(OpCodes.Ldfld, wrapper.GhostRefField);
                                ilGenerator.EmitNullCheck();
                            }

#if IMPORTER
                            // do we have an implementation in map.xml?
                            if (wrapper.EmitMapXmlMethodPrologueAndOrBody(ilGenerator, classFile, m))
                            {
                                ilGenerator.DoEmit();
                                continue;
                            }
#endif
                            var nonleaf = false;
                            // GENERATIONAL BODIES: if this method is a candidate, its body goes into a static
                            // helper and the method itself becomes a trampoline through a cached delegate, so
                            // first execution can recompile the body with its dynamic references now resolved.
                            // Not a candidate (or knob off) => compile straight into the method as before.
                            // Three shapes, in order of preference. In-place keeps the body where it is and
                            // adds a one-shot hook; the delegate path moves the body out and pays a hop on
                            // every call; plain Compile is the no-feature baseline.
                            FieldBuilder inPlaceFlag = null;
                            if (JVM.LazyBodies && JVM.LazyInPlace)
                                inPlaceFlag = TryPrepareInPlaceRelink(methods[i], m, i, host);

                            var lazyBody = (inPlaceFlag == null && JVM.LazyBodies && JVM.LazyInPlace == false)
                                ? TryMoveBodyToHelper(methods[i], m, i, host, ref nonleaf)
                                : null;

                            if (inPlaceFlag != null)
                            {
                                EmitInPlaceRelinkHook(ilGenerator, inPlaceFlag, inPlaceRelinkId);
                                EmitLazySiteBridges = true;
                                try
                                {
                                    Compiler.Compile(this, host, wrapper, methods[i], classFile, m, ilGenerator, ref nonleaf);
                                }
                                finally
                                {
                                    EmitLazySiteBridges = false;
                                }
                            }
                            else if (lazyBody != null)
                                EmitLazyBodyTrampoline(ilGenerator, lazyBody, methods[i], m);
                            else
                                Compiler.Compile(this, host, wrapper, methods[i], classFile, m, ilGenerator, ref nonleaf);
                            ilGenerator.CheckLabels();
                            ilGenerator.DoEmit();
                            if (nonleaf)
                                mb.SetImplementationFlags(mb.GetMethodImplementationFlags());
#if IMPORTER
                            ilGenerator.EmitLineNumberTable(mb);
#else
                            var linenumbers = ilGenerator.GetLineNumberTable();
                            if (linenumbers != null)
                            {
                                wrapper.lineNumberTables ??= new byte[methods.Length][];
                                wrapper.lineNumberTables[i] = linenumbers;
                            }
#endif // IMPORTER
                        }
                    }
                }

                AddInheritedDefaultInterfaceMethods(methods);

                if (clinitIndex != -1 || (basehasclinit && !classFile.IsInterface) || classFile.HasInitializedFields)
                {
                    MethodBuilder cb;
                    if (clinitIndex != -1)
                    {
                        cb = (MethodBuilder)methods[clinitIndex].GetMethod();
                    }
                    else
                    {
                        cb = ReflectUtil.DefineTypeInitializer(typeBuilder, wrapper.classLoader);
                        context.AttributeHelper.HideFromJava(cb);
                    }
                    var ilGenerator = context.CodeEmitterFactory.Create(cb);

                    // before we call the base class initializer, we need to set the non-final static ConstantValue fields
                    EmitConstantValueInitialization(fields, ilGenerator);
                    if (basehasclinit)
                        wrapper.BaseTypeWrapper.EmitRunClassConstructor(ilGenerator);

                    if (clinitIndex != -1)
                    {
                        CompileConstructorBody(this, ilGenerator, clinitIndex);
                    }
                    else
                    {
                        ilGenerator.Emit(OpCodes.Ret);
                        ilGenerator.DoEmit();
                    }

                    ilGenerator.CheckLabels();
                }

                // add all interfaces that we implement (including the magic ones) and handle ghost conversions
                ImplementInterfaces(wrapper.Interfaces, new List<RuntimeJavaType>());

                // NOTE non-final fields aren't allowed in interfaces so we don't have to initialize constant fields
                if (!classFile.IsInterface)
                {
                    // if a class has no constructor, we generate one otherwise Ref.Emit will create a default ctor
                    // and that has several problems:
                    // - base type may not have an accessible default constructor
                    // - Ref.Emit uses BaseType.GetConstructors() which may trigger a TypeResolve event
                    // - we don't want the synthesized constructor to show up in Java
                    if (!hasConstructor)
                    {
                        var ilgen = context.CodeEmitterFactory.Create(ReflectUtil.DefineConstructor(typeBuilder, MethodAttributes.PrivateScope, Type.EmptyTypes));
                        ilgen.Emit(OpCodes.Ldnull);
                        ilgen.Emit(OpCodes.Throw);
                        ilgen.DoEmit();
                    }

                    // here we loop thru all the interfaces to explicitly implement any methods that we inherit from
                    // base types that may have a different name from the name in the interface
                    // (e.g. interface that has an equals() method that should override System.Object.Equals())
                    // also deals with interface methods that aren't implemented (generate a stub that throws AbstractMethodError)
                    // and with methods that aren't public (generate a stub that throws IllegalAccessError)
                    var doneSet = new Dictionary<RuntimeJavaType, RuntimeJavaType>();
                    var interfaces = wrapper.Interfaces;
                    for (int i = 0; i < interfaces.Length; i++)
                        ImplementInterfaceMethodStubs(doneSet, interfaces[i], false);

                    // if any of our base classes has an incomplete interface implementation we need to look through all
                    // the base class interfaces to see if we've got an implementation now
                    var baseTypeWrapper = wrapper.BaseTypeWrapper;
                    while (baseTypeWrapper.HasIncompleteInterfaceImplementation)
                    {
                        for (int i = 0; i < baseTypeWrapper.Interfaces.Length; i++)
                            ImplementInterfaceMethodStubs(doneSet, baseTypeWrapper.Interfaces[i], true);

                        baseTypeWrapper = baseTypeWrapper.BaseTypeWrapper;
                    }

                    if (!wrapper.IsAbstract && wrapper.HasUnsupportedAbstractMethods)
                        AddUnsupportedAbstractMethods();

                    if (!wrapper.ClassLoader.NoAutomagicSerialization)
                        wrapper.automagicSerializationCtor = context.Serialization.AddAutomagicSerialization(wrapper, typeBuilder);
                }

#if IMPORTER

                // If we're an interface that has public/protected fields, we create an inner class
                // to expose these fields to C# (which stubbornly refuses to see fields in interfaces).
                AddInterfaceFieldsInterop(fields);

                // If we're a Java 8 interface with static methods, we create an inner class
                // to expose these methods to C#.
                AddInterfaceMethodsInterop(methods);

                // See if there is any additional metadata
                wrapper.EmitMapXmlMetadata(typeBuilder, classFile, fields, methods);

                // if we inherit public members from non-public base classes or have public members with non-public types in their signature, we need access stubs
                if (wrapper.IsPublic)
                    AddAccessStubs();

                AddConstantPoolAttributeIfNecessary(context, classFile, typeBuilder);

#endif

                for (int i = 0; i < classFile.Methods.Length; i++)
                {
                    var m = classFile.Methods[i];
                    var mb = (MethodBuilder)methods[i].GetMethod();
                    if (mb == null)
                        continue;

                    if (m.Annotations != null)
                    {
                        ParameterBuilder returnParameter = null;
                        foreach (object[] def in m.Annotations)
                        {
                            var annotation = Annotation.Load(wrapper, def);
                            if (annotation != null)
                            {
                                annotation.Apply(wrapper.ClassLoader, mb, def);
                                annotation.ApplyReturnValue(wrapper.ClassLoader, mb, ref returnParameter, def);
                            }
                        }
                    }

                    AddMethodParameterInfo(m, methods[i], mb, out var parameterNames);

#if IMPORTER
                    if (methods[i].HasCallerID)
                    {
                        context.AttributeHelper.SetEditorBrowsableNever(mb);
                        EmitCallerIDStub(methods[i], parameterNames);
                    }
#endif
                }

                for (int i = 0; i < classFile.Fields.Length; i++)
                {
                    if (classFile.Fields[i].Annotations != null)
                    {
                        foreach (object[] def in classFile.Fields[i].Annotations)
                        {
                            var annotation = Annotation.Load(wrapper, def);
                            if (annotation != null)
                            {
                                {
                                    var prop = fields[i] as RuntimeByteCodePropertyJavaField;
                                    if (prop != null)
                                    {
                                        annotation.Apply(wrapper.ClassLoader, prop.GetPropertyBuilder(), def);
                                    }
                                    else
                                    {
                                        annotation.Apply(wrapper.ClassLoader, (FieldBuilder)fields[i].GetField(), def);
                                    }
                                }
                            }
                        }
                    }
                }

                if (classFile.Annotations != null)
                {
                    foreach (object[] def in classFile.Annotations)
                    {
                        var annotation = Annotation.Load(wrapper, def);
                        if (annotation != null)
                            annotation.Apply(wrapper.ClassLoader, typeBuilder, def);
                    }
                }

#if IMPORTER
                AddImplementsAttribute();
#endif

                Type type;
                Profiler.Enter("TypeBuilder.CreateType");

                try
                {
                    type = typeBuilder.CreateType();
                    if (nestedTypeBuilders != null)
                    {
                        // the nested types may use these intrinsic helpers, which must be finished first -- unless this
                        // is one of their base classes (JDK 25's ThreadLocal has nested types), which finishing them
                        // would finish again
                        foreach (var helper in new[] { "ikvm.internal.IntrinsicAtomicReferenceFieldUpdater", "ikvm.internal.IntrinsicThreadLocal" })
                        {
                            var tw = context.ClassLoaderFactory.LoadClassCritical(helper);
                            if (tw.IsSubTypeOf(wrapper) == false)
                                tw.Finish();
                        }
                        foreach (TypeBuilder tb in nestedTypeBuilders)
                            tb.CreateType();
                    }
#if !IMPORTER
                    if (liveObjects != null)
                        typeof(IKVM.Runtime.LiveObjectHolder<>).MakeGenericType(type).GetField("values", BindingFlags.Static | BindingFlags.Public).SetValue(null, liveObjects.ToArray());
#endif
                }
                finally
                {
                    Profiler.Leave("TypeBuilder.CreateType");
                }
#if !IMPORTER
                // When we're statically compiling we don't need to set the wrapper here, because we've already done so for the typeBuilder earlier.
                context.ClassLoaderFactory.SetWrapperForType(type, wrapper);
#endif

#if IMPORTER
                wrapper.FinishGhostStep2();
#endif

                return type;
            }

#if IMPORTER

            static void AddConstantPoolAttributeIfNecessary(RuntimeContext context, ClassFile classFile, TypeBuilder typeBuilder)
            {
                object[] constantPool = null;
                bool[] inUse = null;

                ref readonly var runtimeVisibleTypeAnnotations = ref classFile.RuntimeVisibleTypeAnnotations;
                MarkConstantPoolUsage(classFile, in runtimeVisibleTypeAnnotations, ref constantPool, ref inUse);

                foreach (var method in classFile.Methods)
                {
                    ref readonly var methodRuntimeVisibleTypeAnnotations = ref method.RuntimeVisibleTypeAnnotations;
                    MarkConstantPoolUsage(classFile, in methodRuntimeVisibleTypeAnnotations, ref constantPool, ref inUse);
                }

                foreach (var field in classFile.Fields)
                {
                    ref readonly var fieldRuntimeVisibleTypeAnnotations = ref field.RuntimeVisibleTypeAnnotations;
                    MarkConstantPoolUsage(classFile, in field.RuntimeVisibleTypeAnnotations, ref constantPool, ref inUse);
                }

                // the type annotations of record components, which the RecordComponentsAttribute holds as they are
                if (classFile.RecordComponentTypeAnnotations is { } recordComponentTypeAnnotations)
                    for (int i = 0; i < recordComponentTypeAnnotations.Length; i++)
                        MarkConstantPoolUsage(classFile, in recordComponentTypeAnnotations[i], ref constantPool, ref inUse);

                if (constantPool != null)
                {
                    // to save space, we clear out the items that aren't used by the RuntimeVisibleTypeAnnotations and
                    // use an RLE for the empty slots
                    context.AttributeHelper.SetConstantPoolAttribute(typeBuilder, ConstantPoolAttribute.Compress(constantPool, inUse));
                }
            }

            /// <summary>
            /// Scans the specified <see cref="TypeAnnotationTable"/> for used constant handles and marks them as in-use.
            /// </summary>
            /// <param name="classFile"></param>
            /// <param name="runtimeVisibleTypeAnnotations"></param>
            /// <param name="constantPool"></param>
            /// <param name="inUse"></param>
            static void MarkConstantPoolUsage(ClassFile classFile, ref readonly TypeAnnotationTable runtimeVisibleTypeAnnotations, ref object[] constantPool, ref bool[] inUse)
            {
                if (runtimeVisibleTypeAnnotations.Count > 0)
                {
                    if (constantPool == null)
                    {
                        constantPool = classFile.GetConstantPool();
                        inUse = new bool[constantPool.Length];
                    }
                    try
                    {
                        foreach (ref readonly var annotation in runtimeVisibleTypeAnnotations)
                        {
                            ref readonly var i = ref annotation;
                            MarkConstantPoolUsageForTypeAnnotation(in i, inUse);
                        }

                        return;
                    }
                    catch (ClassFormatError)
                    {

                    }
                    catch (IndexOutOfRangeException)
                    {

                    }
                    catch (ByteCodeException)
                    {

                    }

                    // if we fail to parse the annotations (e.g. due to a malformed attribute), we simply keep all the constant pool entries
                    for (int i = 0; i < inUse.Length; i++)
                        inUse[i] = true;
                }
            }

            static void MarkConstantPoolUsageForAnnotation(ref readonly IKVM.ByteCode.Decoding.Annotation annotation, bool[] inUse)
            {
                inUse[annotation.Type.Slot] = true;

                for (int i = 0; i < annotation.Elements.Count; i++)
                {
                    ref readonly var element = ref annotation.Elements[i];
                    ref readonly var elementValue = ref element.Value;
                    inUse[element.Name.Slot] = true;
                    MarkConstantPoolUsageForAnnotationComponentValue(in elementValue, inUse);
                }
            }

            static void MarkConstantPoolUsageForTypeAnnotation(ref readonly TypeAnnotation annotation, bool[] inUse)
            {
                inUse[annotation.Type.Slot] = true;

                for (int i = 0; i < annotation.Elements.Count; i++)
                {
                    ref readonly var element = ref annotation.Elements[i];
                    ref readonly var elementValue = ref element.Value;
                    inUse[element.Name.Slot] = true;
                    MarkConstantPoolUsageForAnnotationComponentValue(in elementValue, inUse);
                }
            }

            static void MarkConstantPoolUsageForAnnotationComponentValue(ref readonly ElementValue element, bool[] inUse)
            {
                switch (element.Kind)
                {
                    case ElementValueKind.Boolean:
                    case ElementValueKind.Byte:
                    case ElementValueKind.Char:
                    case ElementValueKind.Double:
                    case ElementValueKind.Float:
                    case ElementValueKind.Integer:
                    case ElementValueKind.Long:
                    case ElementValueKind.Short:
                    case ElementValueKind.String:
                        inUse[((ConstantElementValue)element).Handle.Slot] = true;
                        break;
                    case ElementValueKind.Class:
                        inUse[((ClassElementValue)element).Class.Slot] = true;
                        break;
                    case ElementValueKind.Enum:
                        var enum_ = (EnumElementValue)element;
                        inUse[enum_.TypeName.Slot] = true;
                        inUse[enum_.ConstantName.Slot] = true;
                        break;
                    case ElementValueKind.Annotation:
                        var annotation = ((AnnotationElementValue)element).Annotation;
                        MarkConstantPoolUsageForAnnotation(ref annotation, inUse);
                        break;
                    case ElementValueKind.Array:
                        foreach (ref readonly var i in (ArrayElementValue)element)
                            MarkConstantPoolUsageForAnnotationComponentValue(in i, inUse);
                        break;
                }
            }

            private bool EmitInterlockedCompareAndSet(RuntimeJavaMethod method, string fieldName, CodeEmitter ilGenerator)
            {
                if (method.ReturnType != context.PrimitiveJavaTypeFactory.BOOLEAN)
                    return false;

                var parameters = method.GetParameters();
                RuntimeJavaType target;
                int firstValueIndex;

                if (method.IsStatic)
                {
                    if (parameters.Length != 3)
                        return false;

                    target = parameters[0];
                    firstValueIndex = 1;
                }
                else
                {
                    if (parameters.Length != 2)
                        return false;

                    target = method.DeclaringType;
                    firstValueIndex = 0;
                }

                if (target.IsUnloadable || target.IsPrimitive || target.IsNonPrimitiveValueType || target.IsGhost)
                {
                    return false;
                }

                var fieldType = parameters[firstValueIndex];
                if (fieldType != parameters[firstValueIndex + 1])
                {
                    return false;
                }
                if (fieldType.IsUnloadable || fieldType.IsNonPrimitiveValueType || fieldType.IsGhost)
                {
                    return false;
                }
                if (fieldType.IsPrimitive && fieldType != context.PrimitiveJavaTypeFactory.LONG && fieldType != context.PrimitiveJavaTypeFactory.INT)
                {
                    return false;
                }

                RuntimeJavaField casField = null;
                foreach (var fw in target.GetFields())
                {
                    if (fw.Name == fieldName)
                    {
                        if (casField != null)
                            return false;

                        casField = fw;
                    }
                }

                if (casField == null)
                    return false;

                if (casField.IsStatic)
                    return false;

                casField.Link();
                if (casField.FieldTypeWrapper != fieldType)
                    return false;

                if (casField.IsPropertyAccessor)
                    return false;

                if (casField.DeclaringType.TypeAsBaseType == typeBuilder.DeclaringType)
                {
                    // allow access to fields in outer class
                }
                else if (!casField.IsAccessibleFrom(casField.DeclaringType, wrapper, casField.DeclaringType))
                {
                    return false;
                }

                FieldInfo fi = casField.GetField();
                if (fi == null)
                {
                    return false;
                }
                ilGenerator.EmitLdarg(0);
                ilGenerator.Emit(OpCodes.Ldflda, fi);
                ilGenerator.EmitLdarg(2);
                ilGenerator.EmitLdarg(1);
                if (fieldType == context.PrimitiveJavaTypeFactory.LONG)
                {
                    ilGenerator.Emit(OpCodes.Call, context.InterlockedMethods.CompareExchangeInt64);
                }
                else if (fieldType == context.PrimitiveJavaTypeFactory.INT)
                {
                    ilGenerator.Emit(OpCodes.Call, context.InterlockedMethods.CompareExchangeInt32);
                }
                else
                {
                    ilGenerator.Emit(OpCodes.Call, AtomicReferenceFieldUpdaterEmitter.MakeCompareExchange(context, casField.FieldTypeWrapper.TypeAsSignatureType));
                }
                ilGenerator.EmitLdarg(1);
                ilGenerator.Emit(OpCodes.Ceq);
                ilGenerator.Emit(OpCodes.Ret);
                return true;
            }
#endif

            private void AddMethodParameterInfo(ClassFile.Method m, RuntimeJavaMethod mw, MethodBuilder mb, out string[] parameterNames)
            {
                parameterNames = null;
                ParameterBuilder[] parameterBuilders = null;

                if (wrapper.ClassLoader.EmitSymbols
#if IMPORTER
                    || (classFile.IsPublic && (m.IsPublic || m.IsProtected))
                    || (m.MethodParameters != null && !wrapper.ClassLoader.NoParameterReflection)
#endif
                    )
                {
                    parameterNames = new string[mw.GetParameters().Length];
                    GetParameterNamesFromMP(m, parameterNames);
#if IMPORTER
                    if (m.MethodParameters == null)
#endif
                    {
                        GetParameterNamesFromLVT(m, parameterNames);
                        GetParameterNamesFromSig(m.Signature, parameterNames);
                    }
#if IMPORTER
                    wrapper.GetParameterNamesFromXml(m.Name, m.Signature, parameterNames);
#endif
                    parameterBuilders = GetParameterBuilders(mb, parameterNames.Length, parameterNames);
                }

#if IMPORTER
                if ((m.Modifiers & Modifiers.VarArgs) != 0 && !mw.HasCallerID)
                {
                    parameterBuilders ??= GetParameterBuilders(mb, mw.GetParameters().Length, null);
                    if (parameterBuilders.Length > 0)
                        context.AttributeHelper.SetParamArrayAttribute(parameterBuilders[parameterBuilders.Length - 1]);
                }

                wrapper.AddXmlMapParameterAttributes(mb, classFile.Name, m.Name, m.Signature, ref parameterBuilders);
#endif

                if (m.ParameterAnnotations != null)
                {
                    parameterBuilders ??= GetParameterBuilders(mb, mw.GetParameters().Length, null);

                    object[][] defs = m.ParameterAnnotations;
                    for (int j = 0; j < defs.Length; j++)
                    {
                        foreach (object[] def in defs[j])
                        {
                            var annotation = Annotation.Load(wrapper, def);
                            if (annotation != null)
                                annotation.Apply(wrapper.ClassLoader, parameterBuilders[j], def);
                        }
                    }
                }
            }

#if IMPORTER

            private void AddImplementsAttribute()
            {
                var interfaces = wrapper.Interfaces;
                if (wrapper.BaseTypeWrapper == context.JavaBase.TypeOfJavaLangObject)
                {
                    // We special case classes extending java.lang.Object to optimize the metadata encoding
                    // for anonymous classes that implement an interface.
                    Type[] actualInterfaces = typeBuilder.GetInterfaces();
                    if (actualInterfaces.Length == 0)
                    {
                        return;
                    }
                    else if (actualInterfaces.Length == 1
                        && interfaces.Length == 1
                        && !interfaces[0].IsRemapped
                        && interfaces[0].TypeAsBaseType == actualInterfaces[0])
                    {
                        // We extend java.lang.Object and implement only a single (non-remapped) interface,
                        // in this case we can omit the ImplementAttribute since the runtime will be able
                        // to reliable reproduce the "list" of implemented interfaces.
                        return;
                    }
                }
                else if (interfaces.Length == 0)
                {
                    return;
                }
                context.AttributeHelper.SetImplementsAttribute(typeBuilder, interfaces);
            }

            private TypeBuilder DefineNestedInteropType(string name)
            {
                ImportClassLoader ccl = wrapper.classLoader;
                while (!ccl.ReserveName(classFile.Name + "$" + name))
                {
                    name += "_";
                }
                TypeBuilder tb = typeBuilder.DefineNestedType(NestedName(name), TypeAttributes.Class | TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Abstract);
                RegisterNestedTypeBuilder(tb);
                context.AttributeHelper.HideFromJava(tb);
                return tb;
            }

            void AddInterfaceFieldsInterop(RuntimeJavaField[] fields)
            {

            }

            void AddInterfaceMethodsInterop(RuntimeJavaMethod[] methods)
            {

            }

            private void CreateDefaultMethodInterop(ref TypeBuilder tbDefaultMethods, MethodBuilder defaultMethod, RuntimeJavaMethod mw)
            {
                if (!ParametersAreAccessible(mw))
                {
                    return;
                }

                if (tbDefaultMethods == null)
                {
                    tbDefaultMethods = DefineNestedInteropType(NestedTypeName.DefaultMethods);
                }

                var mb = mw.GetDefineMethodHelper().DefineMethod(wrapper.ClassLoader.GetTypeWrapperFactory(), tbDefaultMethods, mw.Name, MethodAttributes.Public | MethodAttributes.Static, wrapper.TypeAsSignatureType, true);
                var ilgen = context.CodeEmitterFactory.Create(mb);
                if (wrapper.IsGhost)
                {
                    ilgen.EmitLdarga(0);
                    ilgen.Emit(OpCodes.Ldfld, wrapper.GhostRefField);
                    ilgen.EmitNullCheck();
                    ilgen.EmitLdarga(0);
                }
                else
                {
                    ilgen.EmitLdarg(0);
                    ilgen.Emit(OpCodes.Dup);
                    ilgen.EmitNullCheck();
                }
                RuntimeJavaType[] parameters = mw.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    ilgen.EmitLdarg(i + 1);
                    if (!parameters[i].IsUnloadable && !parameters[i].IsPublic)
                    {
                        parameters[i].EmitCheckcast(ilgen);
                    }
                }
                ilgen.Emit(OpCodes.Call, defaultMethod);
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
            }
#endif

            private void AddInheritedDefaultInterfaceMethods(RuntimeJavaMethod[] methods)
            {
                // look at the miranda methods to see if we inherit any default interface methods
                for (int i = classFile.Methods.Length; i < methods.Length; i++)
                {
                    if (methods[i].IsMirandaMethod)
                    {
                        RuntimeMirandaJavaMethod mmw = (RuntimeMirandaJavaMethod)methods[i];
                        if (mmw.Error == null && !mmw.BaseMethod.IsAbstract)
                        {
                            // we inherited a default interface method, so we need to forward the miranda method to the default method
                            MethodBuilder mb = (MethodBuilder)mmw.GetMethod();
                            if (classFile.IsInterface)
                            {
                                // if we're an interface with a default miranda method, we need to create a new default method that forwards to the original
                                mb = methods[i].GetDefineMethodHelper().DefineMethod(wrapper.ClassLoader.GetTypeWrapperFactory(),
                                    typeBuilder, NamePrefix.DefaultMethod + mb.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, typeBuilder, false);
                            }
                            EmitCallDefaultInterfaceMethod(mb, mmw.BaseMethod);
                        }
                    }
                }
            }

            internal void EmitCallDefaultInterfaceMethod(MethodBuilder mb, RuntimeJavaMethod defaultMethod)
            {
                CodeEmitter ilgen = context.CodeEmitterFactory.Create(mb);
                if (defaultMethod.DeclaringType.IsGhost)
                {
                    CodeEmitterLocal local = ilgen.DeclareLocal(defaultMethod.DeclaringType.TypeAsSignatureType);
                    ilgen.Emit(OpCodes.Ldloca, local);
                    ilgen.EmitLdarg(0);
                    ilgen.Emit(OpCodes.Stfld, defaultMethod.DeclaringType.GhostRefField);
                    ilgen.Emit(OpCodes.Ldloca, local);
                }
                else
                {
                    ilgen.EmitLdarg(0);
                }
                for (int j = 0, count = defaultMethod.GetParameters().Length; j < count; j++)
                {
                    ilgen.EmitLdarg(j + 1);
                }
                ilgen.Emit(OpCodes.Call, RuntimeDefaultInterfaceJavaMethod.GetImpl(defaultMethod));
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
            }

#if IMPORTER
            void AddAccessStubs()
            {
                /*
				 * There are two types of access stubs:
				 * 
				 * Type 1   These are required when a public class extends a non-public class.
				 *			In that case we need access stubs for all public and protected members
				 *			of the non-public base classes.
				 *
				 * Type 2	When a public class exposes a member that contains a non-public type in
				 *			its signature, we need an access stub for that member (where we convert
				 *			the non-public type in the signature to the first public base type).
				 *			Additionally, when a public or protected final field is compiled
				 *			without -strictfinalfieldsemantics, a the field will be wrapper with a
				 *			read-only property.
				 *
				 * Note that type 1 access stubs may also need the type 2 signature type widening
				 * if the signature contains non-public types.
				 * 
				 * Type 1 access stubs are always required, because the JVM allow access to these
				 * members via the derived class while the CLR doesn't. Historically, we've exposed
				 * these access stubs in such a way that they are also consumable from other .NET
				 * languages (when feasible), so we'll continue to do that for back compat.
				 * 
				 * Type 2 access stubs are only required by the CLR when running on CLR v4 and the
				 * caller assembly is security transparent code (level 2). We also want the access
				 * stubs to allow other .NET languages (e.g. C#) to consume broken APIs that
				 * (accidentally) expose these members.
				 */
                AddType2FieldAccessStubs();
                AddType1FieldAccessStubs(wrapper);
                if (!wrapper.IsInterface)
                {
                    int id = 0;
                    AddType2MethodAccessStubs(ref id);
                    AddType1MethodAccessStubs(ref id);
                }
            }

            void AddType1FieldAccessStubs(RuntimeJavaType tw)
            {
                do
                {
                    if (!tw.IsPublic)
                    {
                        foreach (RuntimeJavaField fw in tw.GetFields())
                        {
                            if ((fw.IsPublic || (fw.IsProtected && !wrapper.IsFinal)) && wrapper.GetFieldWrapper(fw.Name, fw.Signature) == fw)
                            {
                                GenerateAccessStub(fw, true);
                            }
                        }
                    }

                    foreach (RuntimeJavaType iface in tw.Interfaces)
                        AddType1FieldAccessStubs(iface);

                    tw = tw.BaseTypeWrapper;
                } while (tw != null && !tw.IsPublic);
            }

            void AddType2FieldAccessStubs()
            {
                foreach (var fw in wrapper.GetFields())
                    if (wrapper.NeedsType2AccessStub(fw))
                        GenerateAccessStub(fw, false);
            }

            void GenerateAccessStub(RuntimeJavaField fw, bool type1)
            {
                if (fw is RuntimeConstantJavaField cjf)
                {
                    // constants cannot have a type 2 access stub, because constant types are always public
                    Debug.Assert(type1);

                    var attribs = fw.IsPublic ? FieldAttributes.Public : FieldAttributes.FamORAssem;
                    attribs |= FieldAttributes.Static | FieldAttributes.Literal;

                    // we attach the AccessStub custom modifier because the C# compiler prefers fields without custom modifiers
                    // so if this class defines a field with the same name, that will be preferred over this one by the C# compiler
                    var fb = typeBuilder.DefineField(fw.Name, fw.FieldTypeWrapper.TypeAsSignatureType, null, new Type[] { context.Resolver.ResolveRuntimeType(typeof(IKVM.Attributes.AccessStub).FullName).AsReflection() }, attribs);
                    context.AttributeHelper.HideFromReflection(fb);
                    fb.SetConstant(cjf.GetConstantValue());
                }
                else
                {
                    var propType = fw.FieldTypeWrapper.TypeAsPublicSignatureType;
                    var modopt = wrapper.GetModOpt(fw.FieldTypeWrapper, true);
                    var pb = typeBuilder.DefineProperty(fw.Name, PropertyAttributes.None, propType, null, modopt, Type.EmptyTypes, null, null);
                    if (type1)
                        context.AttributeHelper.HideFromReflection(pb);
                    else
                        context.AttributeHelper.SetModifiers(pb, fw.Modifiers, fw.IsInternal);
                    var attribs = fw.IsPublic ? MethodAttributes.Public : MethodAttributes.FamORAssem;
                    attribs |= MethodAttributes.HideBySig | MethodAttributes.SpecialName;
                    if (fw.IsStatic)
                        attribs |= MethodAttributes.Static;

                    // we append the IKVM.Attributes.AccessStub type to the modopt array for use in the property accessor method signature
                    // to make sure they never conflict with any user defined methhods
                    var modopt2 = ArrayUtil.Concat(modopt, context.Resolver.ResolveRuntimeType(typeof(IKVM.Attributes.AccessStub).FullName).AsReflection());
                    var getter = typeBuilder.DefineMethod("get_" + fw.Name, attribs, CallingConventions.Standard, propType, null, modopt2, Type.EmptyTypes, null, null);
                    context.AttributeHelper.HideFromJava(getter);
                    pb.SetGetMethod(getter);
                    var ilgen = context.CodeEmitterFactory.Create(getter);
                    if (!fw.IsStatic)
                        ilgen.Emit(OpCodes.Ldarg_0);
                    fw.EmitGet(ilgen);
                    ilgen.Emit(OpCodes.Ret);
                    ilgen.DoEmit();

                    if (!fw.IsFinal || (!fw.IsStatic && !type1))
                    {
                        if (fw.IsFinal)
                        {
                            // we need to generate a (private) setter for final fields for reflection and serialization
                            attribs &= ~MethodAttributes.MemberAccessMask;
                            attribs |= MethodAttributes.Private;
                        }

                        var setter = typeBuilder.DefineMethod("set_" + fw.Name, attribs, CallingConventions.Standard, null, null, null, new Type[] { propType }, null, new Type[][] { modopt2 });
                        context.AttributeHelper.HideFromJava(setter);
                        pb.SetSetMethod(setter);
                        ilgen = context.CodeEmitterFactory.Create(setter);
                        ilgen.Emit(OpCodes.Ldarg_0);
                        if (!fw.IsStatic)
                            ilgen.Emit(OpCodes.Ldarg_1);

                        // we don't do a DynamicCast if fw.FieldTypeWrapper is unloadable, because for normal unloadable fields we don't enfore the type either
                        if (propType != fw.FieldTypeWrapper.TypeAsSignatureType)
                            ilgen.Emit(OpCodes.Castclass, fw.FieldTypeWrapper.TypeAsSignatureType);
                        fw.EmitSet(ilgen);
                        ilgen.Emit(OpCodes.Ret);
                        ilgen.DoEmit();
                    }
                }
            }

            void AddType1MethodAccessStubs(ref int id)
            {
                for (var tw = wrapper.BaseTypeWrapper; tw != null && !tw.IsPublic; tw = tw.BaseTypeWrapper)
                {
                    foreach (var mw in tw.GetMethods())
                    {
                        if ((mw.IsPublic || (mw.IsProtected && !wrapper.IsFinal))
                            && (!mw.IsAbstract || wrapper.IsAbstract)
                            && mw.Name != StringConstants.INIT
                            && wrapper.GetMethod(mw.Name, mw.Signature, true) == mw
                            && ParametersAreAccessible(mw))
                        {
                            GenerateAccessStub(id, mw, true, true);
                            if (!mw.IsStatic && !mw.IsFinal && !mw.IsAbstract && !wrapper.IsFinal)
                            {
                                GenerateAccessStub(id, mw, false, true);
                            }
                            id++;
                        }
                    }
                }
            }

            void AddType2MethodAccessStubs(ref int id)
            {
                foreach (var mw in wrapper.GetMethods())
                {
                    if (mw.HasNonPublicTypeInSignature && (mw.IsPublic || (mw.IsProtected && !wrapper.IsFinal)) && ParametersAreAccessible(mw))
                    {
                        GenerateAccessStub(id, mw, true, false);
                        if (!mw.IsStatic && !mw.IsFinal && !mw.IsAbstract && mw.Name != StringConstants.INIT && !wrapper.IsFinal)
                            GenerateAccessStub(id, mw, false, false);

                        id++;
                    }
                }
            }

            void GenerateAccessStub(int id, RuntimeJavaMethod mw, bool virt, bool type1)
            {
                Debug.Assert(!mw.HasCallerID);

                var stubattribs = mw.IsPublic && virt ? MethodAttributes.Public : MethodAttributes.FamORAssem;
                stubattribs |= MethodAttributes.HideBySig;
                if (mw.IsStatic)
                    stubattribs |= MethodAttributes.Static;

                var parameters = mw.GetParameters();
                var realParameterTypes = new Type[parameters.Length];
                var parameterTypes = new Type[parameters.Length];
                var modopt = new Type[parameters.Length][];
                for (int i = 0; i < parameters.Length; i++)
                {
                    realParameterTypes[i] = parameters[i].TypeAsSignatureType;
                    parameterTypes[i] = parameters[i].TypeAsPublicSignatureType;
                    modopt[i] = wrapper.GetModOpt(parameters[i], true);
                }

                var returnType = mw.ReturnType.TypeAsPublicSignatureType;
                var modoptReturnType = ArrayUtil.Concat(wrapper.GetModOpt(mw.ReturnType, true), context.Resolver.ResolveRuntimeType(typeof(IKVM.Attributes.AccessStub).FullName).AsReflection());

                string name;
                if (mw.Name == StringConstants.INIT)
                {
                    name = ConstructorInfo.ConstructorName;
                    stubattribs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                }
                else
                {
                    name = virt ? (mw.Modifiers & Modifiers.Bridge) == 0 ? mw.Name : NamePrefix.Bridge + mw.Name : NamePrefix.NonVirtual + id;
                }

                var mb = typeBuilder.DefineMethod(name, stubattribs, CallingConventions.Standard, returnType, null, modoptReturnType, parameterTypes, null, modopt);
                if (virt && type1)
                {
                    context.AttributeHelper.HideFromReflection(mb);
                    context.AttributeHelper.SetNameSig(mb, NamePrefix.AccessStub + id + "|" + mw.Name, mw.Signature);
                }
                else
                {
                    context.AttributeHelper.HideFromJava(mb);
                    if (!type1)
                    {
                        context.AttributeHelper.SetNameSig(mb, mw.Name, mw.Signature);
                    }
                }

                var ilgen = context.CodeEmitterFactory.Create(mb);
                int argpos = 0;
                if (!mw.IsStatic)
                    ilgen.EmitLdarg(argpos++);

                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    ilgen.EmitLdarg(argpos++);

                    // we don't need to do a DynamicCast if for unloadables, because the method itself will already do that
                    if (parameterTypes[i] != realParameterTypes[i])
                        ilgen.Emit(OpCodes.Castclass, realParameterTypes[i]);
                }

                if (mw.IsStatic || !virt || mw.Name == StringConstants.INIT)
                    mw.EmitCall(ilgen);
                else
                    mw.EmitCallvirt(ilgen);

                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
            }

            bool ParametersAreAccessible(RuntimeJavaMethod mw)
            {
                foreach (var tw in mw.GetParameters())
                    if (!tw.IsAccessibleFrom(wrapper))
                        return false;

                return true;
            }

#endif // IMPORTER

            private void ImplementInterfaceMethodStubs(Dictionary<RuntimeJavaType, RuntimeJavaType> doneSet, RuntimeJavaType interfaceTypeWrapper, bool baseClassInterface)
            {
                Debug.Assert(interfaceTypeWrapper.IsInterface);

                // make sure we don't do the same method twice
                if (doneSet.ContainsKey(interfaceTypeWrapper))
                {
                    return;
                }
                doneSet.Add(interfaceTypeWrapper, interfaceTypeWrapper);
                foreach (RuntimeJavaMethod method in interfaceTypeWrapper.GetMethods())
                {
                    if (!method.IsStatic && method.IsPublic && !method.IsDynamicOnly)
                    {
                        ImplementInterfaceMethodStubImpl(method, baseClassInterface);
                    }
                }
                RuntimeJavaType[] interfaces = interfaceTypeWrapper.Interfaces;
                for (int i = 0; i < interfaces.Length; i++)
                {
                    ImplementInterfaceMethodStubs(doneSet, interfaces[i], baseClassInterface);
                }
            }

            void ImplementInterfaceMethodStubImpl(RuntimeJavaMethod ifmethod, bool baseClassInterface)
            {
                // we're mangling the name to prevent subclasses from accidentally overriding this method and to
                // prevent clashes with overloaded method stubs that are erased to the same signature (e.g. unloadable types and ghost arrays)
                // HACK the signature and name are the wrong way around to work around a C++/CLI bug (apparantely it looks looks at the last n
                // characters of the method name, or something bizarre like that)
                // https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=234167
                var mangledName = ifmethod.DeclaringType.Name + "/" + ifmethod.Signature + ifmethod.Name;
                RuntimeJavaMethod mce = null;

                var lookup = (RuntimeJavaType)wrapper;
                while (lookup != null)
                {
                    mce = lookup.GetMethod(ifmethod.Name, ifmethod.Signature, true);
                    if (mce == null || !mce.IsStatic)
                        break;

                    lookup = mce.DeclaringType.BaseTypeWrapper;
                }

                if (mce != null)
                {
                    Debug.Assert(!mce.HasCallerID);
                    if (mce.DeclaringType != wrapper)
                    {
                        // check the loader constraints
                        var error = false;
                        if (mce.ReturnType != ifmethod.ReturnType && !mce.ReturnType.IsUnloadable && !ifmethod.ReturnType.IsUnloadable)
                            error = true;

                        var mceparams = mce.GetParameters();
                        var ifparams = ifmethod.GetParameters();
                        for (int i = 0; i < mceparams.Length; i++)
                        {
                            if (mceparams[i] != ifparams[i] && !mceparams[i].IsUnloadable && !ifparams[i].IsUnloadable)
                            {
                                error = true;
                                break;
                            }
                        }

                        if (error)
                        {
                            var mb = DefineInterfaceStubMethod(mangledName, ifmethod);
                            context.AttributeHelper.HideFromJava(mb);
                            var ilgen = context.CodeEmitterFactory.Create(mb);
                            ilgen.EmitThrow("java.lang.LinkageError", wrapper.Name + "." + ifmethod.Name + ifmethod.Signature);
                            ilgen.DoEmit();
                            typeBuilder.DefineMethodOverride(mb, (MethodInfo)ifmethod.GetMethod());
                            return;
                        }
                    }

                    if (!mce.IsPublic && !mce.IsInternal)
                    {
                        // NOTE according to the ECMA spec it isn't legal for a privatescope method to be virtual, but this works and
                        // it makes sense, so I hope the spec is wrong
                        // UPDATE unfortunately, according to Serge Lidin the spec is correct, and it is not allowed to have virtual privatescope
                        // methods. Sigh! So I have to use private methods and mangle the name

                        var mb = DefineInterfaceStubMethod(NamePrefix.Incomplete + mangledName, ifmethod);
                        context.AttributeHelper.HideFromJava(mb);
                        var ilgen = context.CodeEmitterFactory.Create(mb);
                        ilgen.EmitThrow("java.lang.IllegalAccessError", wrapper.Name + "." + ifmethod.Name + ifmethod.Signature);
                        ilgen.DoEmit();
                        typeBuilder.DefineMethodOverride(mb, (MethodInfo)ifmethod.GetMethod());
                        wrapper.SetHasIncompleteInterfaceImplementation();
                    }
                    else if (mce.GetMethod() == null || mce.IsInternal || !ReflectUtil.IsSameAssembly(mce.DeclaringType.TypeAsTBD, typeBuilder) || CheckRequireOverrideStub(mce, ifmethod))
                    {
                        // NOTE methods inherited from base classes in a different assembly do *not* automatically implement
                        // interface methods, so we have to generate a stub here that doesn't do anything but call the base
                        // implementation
                        wrapper.GenerateOverrideStub(typeBuilder, ifmethod, null, mce);
                    }
                    else if (mce.RealName != ifmethod.RealName)
                    {
                        // The CLR name of our implementation differs from the CLR name of the interface method, so the CLR
                        // won't match them up implicitly. This is very common: java.lang.Comparable is mapped onto
                        // System.IComparable, so every class that implements Comparable has a "compareTo" implementing a
                        // "CompareTo" declaration. When the body lives on this very type and the signatures already line up,
                        // a MethodImpl points the interface slot straight at it, which avoids a forwarding stub on what is
                        // often a hot path (Comparable.compareTo, CharSequence.charAt, ...).
                        if (CanBeMethodImplBody(mce))
                            typeBuilder.DefineMethodOverride((MethodInfo)mce.GetMethod(), (MethodInfo)ifmethod.GetMethod());
                        else if (InheritsInterfaceMapping(mce, ifmethod, baseClassInterface))
                            ; // the base class already mapped this interface method onto the very method we would forward to
                        else
                            wrapper.GenerateOverrideStub(typeBuilder, ifmethod, null, mce);
                    }
                    else if (baseClassInterface && mce.DeclaringType == wrapper)
                    {
                        typeBuilder.DefineMethodOverride((MethodInfo)mce.GetMethod(), (MethodInfo)ifmethod.GetMethod());
                    }
                }
                else
                {
                    if (!wrapper.IsAbstract || (!baseClassInterface && wrapper.GetMethod(ifmethod.Name, ifmethod.Signature, false) != null))
                    {
                        // the type doesn't implement the interface method and isn't abstract either. The JVM allows this, but the CLR doesn't,
                        // so we have to create a stub method that throws an AbstractMethodError
                        var mb = DefineInterfaceStubMethod(NamePrefix.Incomplete + mangledName, ifmethod);
                        context.AttributeHelper.HideFromJava(mb);
                        var ilgen = context.CodeEmitterFactory.Create(mb);
                        ilgen.EmitThrow("java.lang.AbstractMethodError", wrapper.Name + "." + ifmethod.Name + ifmethod.Signature);
                        ilgen.DoEmit();
                        typeBuilder.DefineMethodOverride(mb, (MethodInfo)ifmethod.GetMethod());
                        wrapper.SetHasIncompleteInterfaceImplementation();
                        wrapper.EmitLevel4Warning(HardError.AbstractMethodError, wrapper.Name + "." + ifmethod.Name + ifmethod.Signature);
                    }
                }
            }

            MethodBuilder DefineInterfaceStubMethod(string name, RuntimeJavaMethod mw)
            {
                return mw.GetDefineMethodHelper().DefineMethod(wrapper, typeBuilder, name, MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final);
            }

            /// <summary>
            /// Returns whether the interface mapping for <paramref name="ifmethod"/> is already established by the base
            /// class that declares <paramref name="mce"/>, so that we inherit it and need to emit nothing at all.
            /// </summary>
            bool InheritsInterfaceMapping(RuntimeJavaMethod mce, RuntimeJavaMethod ifmethod, bool baseClassInterface)
            {
                // if we declare the implementation ourselves we have to map it ourselves
                if (mce.DeclaringType == wrapper)
                    return false;

                // we only get here for a base class interface when the base implementation was incomplete (it is guarded by
                // HasIncompleteInterfaceImplementation), so its mapping is a throwing stub we must not lean on
                if (baseClassInterface)
                    return false;

                // the declaring type has to implement the interface itself, otherwise it never ran this method for
                // ifmethod and so never mapped the slot (e.g. a base class that happens to have a matching close(),
                // with the interface only being added by us)
                if (mce.DeclaringType.IsSubTypeOf(ifmethod.DeclaringType) == false)
                    return false;

                // and it has to be a type we generated the same way, so that we know the mapping is there. Callers have
                // already established that it lives in the assembly we are building, so this normally holds.
                if (mce.DeclaringType is not RuntimeByteCodeJavaType)
                    return false;

                return true;
            }

            /// <summary>
            /// Returns whether <paramref name="mw"/> can serve as the body of a MethodImpl on the type we are building,
            /// instead of having to route the interface slot through a forwarding stub.
            /// </summary>
            bool CanBeMethodImplBody(RuntimeJavaMethod mw)
            {
                // a MethodImpl body has to be declared by the type that carries the MethodImpl
                if (mw.DeclaringType != wrapper)
                    return false;

                // Only for types nothing can extend. A MethodImpl whose body is a public virtual method makes the
                // interface slot *alias* that method's vtable slot. If a subclass then re-implements the same interface
                // method (which it does whenever it re-declares the interface and does not declare the method itself, so
                // it falls into the stub path), the CLR re-points that shared slot at the subclass's stub -- and the stub
                // callvirt's the very method whose slot it just took over, so it recurses forever.
                // java.nio.channels.FileChannel over AbstractInterruptibleChannel.close/IDisposable::Dispose is exactly
                // this shape. A private newslot stub does not alias, which is why the stub is safe in general.
                if (wrapper.IsFinal == false)
                    return false;

                // miranda methods and other synthesized members may not have a body we control
                if (mw.IsMirandaMethod)
                    return false;

                if (mw.GetMethod() is not MethodInfo mb)
                    return false;

                // the body has to be a concrete virtual slot on this type; abstract bodies keep the stub, because the stub
                // is what carries the AbstractMethodError for the non-abstract-class case
                if (mb.IsStatic || mb.IsAbstract || mb.IsVirtual == false)
                    return false;

                return true;
            }

#if FIRST_PASS == false && IMPORTER == false

            internal static class JniProxyBuilder
            {

                readonly static ModuleBuilder mod;
                static int count;

                /// <summary>
                /// Initializes the static instance.
                /// </summary>
                static JniProxyBuilder()
                {
                    mod = DynamicClassLoader.CreateJniProxyModuleBuilder();
                    var cab = new CustomAttributeBuilder(JVM.Context.Resolver.ResolveRuntimeType(typeof(JavaModuleAttribute).FullName).GetConstructor([]).AsReflection(), new object[0]);
                    mod.SetCustomAttribute(cab);
                }

                internal static void Generate(RuntimeByteCodeJavaType.FinishContext context, CodeEmitter ilGenerator, RuntimeByteCodeJavaType wrapper, RuntimeJavaMethod mw, TypeBuilder typeBuilder, ClassFile classFile, ClassFile.Method m, RuntimeJavaType[] args)
                {
                    TypeBuilder tb = mod.DefineType("__<jni>" + System.Threading.Interlocked.Increment(ref count), TypeAttributes.Public | TypeAttributes.Class);
                    int instance = m.IsStatic ? 0 : 1;
                    Type[] argTypes = new Type[args.Length + instance + 1];
                    if (instance != 0)
                    {
                        argTypes[0] = typeof(object);
                    }
                    for (int i = 0; i < args.Length; i++)
                    {
                        // NOTE we take a shortcut here by assuming that all "special" types (i.e. ghost or value types)
                        // are public and so we can get away with replacing all other types with object.
                        argTypes[i + instance] = !args[i].IsUnloadable && (args[i].IsPrimitive || args[i].IsGhost || args[i].IsNonPrimitiveValueType) ? args[i].TypeAsSignatureType : typeof(object);
                    }
                    argTypes[argTypes.Length - 1] = context.context.JavaBase.TypeOfIkvmInternalCallerID.TypeAsSignatureType;
                    Type retType = !mw.ReturnType.IsUnloadable && (mw.ReturnType.IsPrimitive || mw.ReturnType.IsGhost || mw.ReturnType.IsNonPrimitiveValueType) ? mw.ReturnType.TypeAsSignatureType : typeof(object);
                    MethodBuilder mb = tb.DefineMethod("method", MethodAttributes.Public | MethodAttributes.Static, retType, argTypes);
                    context.context.AttributeHelper.HideFromJava(mb);
                    CodeEmitter ilgen = context.context.CodeEmitterFactory.Create(mb);
                    new JniBuilder(context.context).Generate(context, ilgen, wrapper, mw, tb, classFile, m, args, true);
                    ilgen.DoEmit();
                    tb.CreateType();
                    for (int i = 0; i < argTypes.Length - 1; i++)
                    {
                        ilGenerator.EmitLdarg(i);
                    }
                    context.EmitCallerID(ilGenerator, m.IsLambdaFormCompiled);
                    ilGenerator.Emit(OpCodes.Call, mb);
                    if (!mw.ReturnType.IsUnloadable && !mw.ReturnType.IsPrimitive && !mw.ReturnType.IsGhost && !mw.ReturnType.IsNonPrimitiveValueType)
                    {
                        ilGenerator.Emit(OpCodes.Castclass, mw.ReturnType.TypeAsSignatureType);
                    }
                    ilGenerator.Emit(OpCodes.Ret);
                }
            }
#endif

            class JniBuilder
            {

                readonly RuntimeContext context;

                /// <summary>
                /// Initializes a new instance.
                /// </summary>
                /// <param name="context"></param>
                public JniBuilder(RuntimeContext context)
                {
                    this.context = context ?? throw new ArgumentNullException(nameof(context));
                }

                Type LocalRefStructType => context.Resolver.ResolveRuntimeType("IKVM.Runtime.JNI.JNIFrame").AsReflection();

                MethodInfo JniFuncPtrMethod => LocalRefStructType.GetMethod("GetFuncPtr");

                MethodInfo EnterLocalRefStructMethod => LocalRefStructType.GetMethod("Enter");

                MethodInfo LeaveLocalRefStructMethod => LocalRefStructType.GetMethod("Leave");

                MethodInfo MakeLocalRefMethod => LocalRefStructType.GetMethod("MakeLocalRef");

                MethodInfo UnwrapLocalRefMethod => LocalRefStructType.GetMethod("UnwrapLocalRef");

                FieldInfo LightCallsField => LocalRefStructType.GetField("LightCalls");

                MethodInfo GetEnvLightMethod => LocalRefStructType.GetMethod("GetEnvLight");

                /// <summary>wasm fork (IKVM_JNI_LIGHT): see JNIFrame.LightCalls for what makes a native eligible.</summary>
                static bool IsLightEligible(ClassFile classFile, ClassFile.Method m, RuntimeJavaMethod mw, RuntimeJavaType[] args)
                {
                    if (!m.IsStatic || m.IsSynchronized)
                        return false;
                    var cls = classFile.Name.Replace('.', '/');
                    if (!cls.StartsWith("org/lwjgl/opengl/", StringComparison.Ordinal) && cls != "org/lwjgl/system/JNI")
                        return false;
                    if (mw.ReturnType.IsUnloadable || !mw.ReturnType.IsPrimitive)
                        return false;
                    foreach (var a in args)
                        if (a.IsUnloadable || !a.IsPrimitive)
                            return false;
                    return true;
                }

                MethodInfo WriteLineMethod => context.Resolver.ResolveCoreType(typeof(Console).FullName).AsReflection().GetMethod("WriteLine", [context.Types.Object]);

                MethodInfo MonitorEnterMethod => context.Resolver.ResolveCoreType(typeof(System.Threading.Monitor).FullName).AsReflection().GetMethod("Enter", [context.Types.Object]);

                MethodInfo MonitorExitMethod => context.Resolver.ResolveCoreType(typeof(System.Threading.Monitor).FullName).AsReflection().GetMethod("Exit", [context.Types.Object]);

                internal void Generate(RuntimeByteCodeJavaType.FinishContext context, CodeEmitter ilGenerator, RuntimeByteCodeJavaType wrapper, RuntimeJavaMethod mw, TypeBuilder typeBuilder, ClassFile classFile, ClassFile.Method m, RuntimeJavaType[] args, bool thruProxy)
                {
                    CodeEmitterLocal syncObject = null;
                    if (m.IsSynchronized && m.IsStatic)
                    {
                        wrapper.EmitClassLiteral(ilGenerator);
                        ilGenerator.Emit(OpCodes.Dup);
                        syncObject = ilGenerator.DeclareLocal(context.context.Types.Object);
                        ilGenerator.Emit(OpCodes.Stloc, syncObject);
                        ilGenerator.Emit(OpCodes.Call, MonitorEnterMethod);
                        ilGenerator.BeginExceptionBlock();
                    }

                    var sig = m.Signature.Replace('.', '/');
                    // TODO use/unify JNI.METHOD_PTR_FIELD_PREFIX
                    var methodPtr = typeBuilder.DefineField("__<jniptr>" + m.Name + sig, context.context.Types.IntPtr, FieldAttributes.Static | FieldAttributes.PrivateScope);
                    var localRefStruct = ilGenerator.DeclareLocal(LocalRefStructType);
                    ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                    ilGenerator.Emit(OpCodes.Initobj, LocalRefStructType);
                    ilGenerator.Emit(OpCodes.Ldsfld, methodPtr);
                    var oklabel = ilGenerator.DefineLabel();
                    ilGenerator.EmitBrtrue(oklabel);

                    if (thruProxy)
                        ilGenerator.EmitLdarg(args.Length + (mw.IsStatic ? 0 : 1));
                    else
                        context.EmitCallerID(ilGenerator, m.IsLambdaFormCompiled);
                    ilGenerator.Emit(OpCodes.Ldstr, classFile.Name.Replace('.', '/'));
                    ilGenerator.Emit(OpCodes.Ldstr, m.Name);
                    ilGenerator.Emit(OpCodes.Ldstr, sig);
                    ilGenerator.Emit(OpCodes.Call, JniFuncPtrMethod);
                    ilGenerator.Emit(OpCodes.Stsfld, methodPtr);
                    ilGenerator.MarkLabel(oklabel);

                    // wasm fork (IKVM_JNI_LIGHT): if (JNIFrame.LightCalls) return calli(GetEnvLight(), null, args...);
                    if (IsLightEligible(classFile, m, mw, args))
                    {
                        var full = ilGenerator.DefineLabel();
                        ilGenerator.Emit(OpCodes.Ldsfld, LightCallsField);
                        ilGenerator.EmitBrfalse(full);
                        ilGenerator.Emit(OpCodes.Call, GetEnvLightMethod);
                        ilGenerator.Emit(OpCodes.Ldc_I4_0);
                        ilGenerator.Emit(OpCodes.Conv_I);
                        var lightArgs = new Type[args.Length + 2];
                        lightArgs[0] = context.context.Types.IntPtr;
                        lightArgs[1] = context.context.Types.IntPtr;
                        for (int i = 0; i < args.Length; i++)
                        {
                            lightArgs[i + 2] = args[i].TypeAsSignatureType;
                            ilGenerator.EmitLdarg(i);
                        }
                        ilGenerator.Emit(OpCodes.Ldsfld, methodPtr);
                        var lightRet = mw.ReturnType == context.Context.PrimitiveJavaTypeFactory.BOOLEAN ? context.Context.Types.Byte : mw.ReturnType.TypeAsSignatureType;
                        ilGenerator.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, lightRet, lightArgs);
                        ilGenerator.Emit(OpCodes.Ret);
                        ilGenerator.MarkLabel(full);
                    }
                    ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                    if (thruProxy)
                        ilGenerator.EmitLdarg(args.Length + (mw.IsStatic ? 0 : 1));
                    else
                        context.EmitCallerID(ilGenerator, m.IsLambdaFormCompiled);
                    ilGenerator.Emit(OpCodes.Call, EnterLocalRefStructMethod);

                    var jnienv = ilGenerator.DeclareLocal(context.context.Types.IntPtr);
                    ilGenerator.Emit(OpCodes.Stloc, jnienv);

                    ilGenerator.BeginExceptionBlock();
                    var retTypeWrapper = mw.ReturnType;
                    if (retTypeWrapper.IsUnloadable || !retTypeWrapper.IsPrimitive)
                    {
                        // this one is for use after we return from "calli"
                        ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                    }

                    ilGenerator.Emit(OpCodes.Ldloc, jnienv);

                    var modargs = new Type[args.Length + 2];
                    modargs[0] = context.context.Types.IntPtr;
                    modargs[1] = context.context.Types.IntPtr;
                    for (int i = 0; i < args.Length; i++)
                        modargs[i + 2] = args[i].TypeAsSignatureType;
                    int add = 0;
                    if (!m.IsStatic)
                    {
                        ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                        ilGenerator.Emit(OpCodes.Ldarg_0);
                        ilGenerator.Emit(OpCodes.Call, MakeLocalRefMethod);
                        add = 1;
                    }
                    else
                    {
                        ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                        wrapper.EmitClassLiteral(ilGenerator);
                        ilGenerator.Emit(OpCodes.Call, MakeLocalRefMethod);
                    }

                    for (int j = 0; j < args.Length; j++)
                    {
                        if (args[j].IsUnloadable || !args[j].IsPrimitive)
                        {
                            ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                            if (!args[j].IsUnloadable && args[j].IsNonPrimitiveValueType)
                            {
                                ilGenerator.EmitLdarg(j + add);
                                args[j].EmitBox(ilGenerator);
                            }
                            else if (!args[j].IsUnloadable && args[j].IsGhost)
                            {
                                ilGenerator.EmitLdarga(j + add);
                                ilGenerator.Emit(OpCodes.Ldfld, args[j].GhostRefField);
                            }
                            else
                            {
                                ilGenerator.EmitLdarg(j + add);
                            }
                            ilGenerator.Emit(OpCodes.Call, MakeLocalRefMethod);
                            modargs[j + 2] = context.context.Types.IntPtr;
                        }
                        else
                        {
                            ilGenerator.EmitLdarg(j + add);
                        }
                    }

                    ilGenerator.Emit(OpCodes.Ldsfld, methodPtr);

                    Type realRetType;
                    if (retTypeWrapper == context.Context.PrimitiveJavaTypeFactory.BOOLEAN)
                        realRetType = context.Context.Types.Byte;
                    else if (retTypeWrapper.IsPrimitive)
                        realRetType = retTypeWrapper.TypeAsSignatureType;
                    else
                        realRetType = context.Context.Types.IntPtr;
                    ilGenerator.EmitCalli(OpCodes.Calli, System.Runtime.InteropServices.CallingConvention.Cdecl, realRetType, modargs);

                    CodeEmitterLocal retValue = null;
                    if (retTypeWrapper != context.Context.PrimitiveJavaTypeFactory.VOID)
                    {
                        if (retTypeWrapper.IsUnloadable)
                        {
                            ilGenerator.Emit(OpCodes.Call, UnwrapLocalRefMethod);
                        }
                        else if (!retTypeWrapper.IsPrimitive)
                        {
                            ilGenerator.Emit(OpCodes.Call, UnwrapLocalRefMethod);
                            if (retTypeWrapper.IsNonPrimitiveValueType)
                            {
                                retTypeWrapper.EmitUnbox(ilGenerator);
                            }
                            else if (retTypeWrapper.IsGhost)
                            {
                                var ghost = ilGenerator.DeclareLocal(retTypeWrapper.TypeAsSignatureType);
                                var obj = ilGenerator.DeclareLocal(context.Context.Types.Object);
                                ilGenerator.Emit(OpCodes.Stloc, obj);
                                ilGenerator.Emit(OpCodes.Ldloca, ghost);
                                ilGenerator.Emit(OpCodes.Ldloc, obj);
                                ilGenerator.Emit(OpCodes.Stfld, retTypeWrapper.GhostRefField);
                                ilGenerator.Emit(OpCodes.Ldloc, ghost);
                            }
                            else
                            {
                                ilGenerator.Emit(OpCodes.Castclass, retTypeWrapper.TypeAsTBD);
                            }
                        }

                        retValue = ilGenerator.DeclareLocal(retTypeWrapper.TypeAsSignatureType);
                        ilGenerator.Emit(OpCodes.Stloc, retValue);
                    }

                    var retLabel = ilGenerator.DefineLabel();
                    ilGenerator.EmitLeave(retLabel);
                    ilGenerator.BeginCatchBlock(context.context.Types.Object);
                    ilGenerator.Emit(OpCodes.Ldstr, "*** exception in native code ***");
                    ilGenerator.Emit(OpCodes.Call, WriteLineMethod);
                    ilGenerator.Emit(OpCodes.Call, WriteLineMethod);
                    ilGenerator.Emit(OpCodes.Rethrow);
                    ilGenerator.BeginFinallyBlock();
                    ilGenerator.Emit(OpCodes.Ldloca, localRefStruct);
                    ilGenerator.Emit(OpCodes.Call, LeaveLocalRefStructMethod);
                    ilGenerator.Emit(OpCodes.Endfinally);
                    ilGenerator.EndExceptionBlock();
                    if (m.IsSynchronized && m.IsStatic)
                    {
                        ilGenerator.BeginFinallyBlock();
                        ilGenerator.Emit(OpCodes.Ldloc, syncObject);
                        ilGenerator.Emit(OpCodes.Call, MonitorExitMethod);
                        ilGenerator.Emit(OpCodes.Endfinally);
                        ilGenerator.EndExceptionBlock();
                    }
                    ilGenerator.MarkLabel(retLabel);
                    if (retTypeWrapper != context.Context.PrimitiveJavaTypeFactory.VOID)
                        ilGenerator.Emit(OpCodes.Ldloc, retValue);

                    ilGenerator.Emit(OpCodes.Ret);
                }
            }

#if IMPORTER

            void EmitCallerIDStub(RuntimeJavaMethod mw, string[] parameterNames)
            {
                // we don't need to support custom modifiers, because there aren't any callerid methods that have parameter types that require a custom modifier
                var parameters = mw.GetParameters();
                var parameterTypes = new Type[parameters.Length];
                for (int i = 0; i < parameterTypes.Length; i++)
                    parameterTypes[i] = parameters[i].TypeAsSignatureType;

                var attribs = MethodAttributes.HideBySig;
                int argcount = parameterTypes.Length;
                if (mw.IsStatic)
                {
                    attribs |= MethodAttributes.Static;
                }
                else
                {
                    argcount++;
                }
                if (mw.IsPublic)
                {
                    attribs |= MethodAttributes.Public;
                }
                else if (mw.IsProtected)
                {
                    attribs |= MethodAttributes.FamORAssem;
                }
                else if (mw.IsPrivate)
                {
                    attribs |= MethodAttributes.Private;
                }
                else
                {
                    attribs |= MethodAttributes.Assembly;
                }

                var mb = typeBuilder.DefineMethod(mw.Name, attribs, mw.ReturnType.TypeAsSignatureType, parameterTypes);
                context.AttributeHelper.HideFromJava(mb);
                mb.SetImplementationFlags(MethodImplAttributes.NoInlining);

                var ilgen = context.CodeEmitterFactory.Create(mb);
                for (int i = 0; i < argcount; i++)
                {
                    if (parameterNames != null && (mw.IsStatic || i > 0))
                    {
                        var pb = mb.DefineParameter(mw.IsStatic ? i + 1 : i, ParameterAttributes.None, parameterNames[mw.IsStatic ? i : i - 1]);
                        if (i == argcount - 1 && (mw.Modifiers & Modifiers.VarArgs) != 0)
                            context.AttributeHelper.SetParamArrayAttribute(pb);
                    }

                    ilgen.EmitLdarg(i);
                }
                ilgen.Emit(OpCodes.Ldc_I4_1);
                ilgen.Emit(OpCodes.Ldc_I4_0);
                ilgen.Emit(OpCodes.Newobj, context.Resolver.ResolveCoreType(typeof(StackFrame).FullName).AsReflection().GetConstructor(new Type[] { context.Types.Int32, context.Types.Boolean }));
                var callerID = context.JavaBase.TypeOfIkvmInternalCallerID.GetMethod("create", "(Lcli.System.Diagnostics.StackFrame;)Likvm.internal.CallerID;", false);
                callerID.Link();
                callerID.EmitCall(ilgen);
                if (mw.IsStatic)
                    mw.EmitCall(ilgen);
                else
                    mw.EmitCallvirt(ilgen);
                ilgen.Emit(OpCodes.Ret);
                ilgen.DoEmit();
            }

#endif // IMPORTER

            void ImplementInterfaces(RuntimeJavaType[] interfaces, List<RuntimeJavaType> interfaceList)
            {
                foreach (var iface in interfaces)
                {
                    if (!interfaceList.Contains(iface))
                    {
                        interfaceList.Add(iface);
                        if (!iface.IsAccessibleFrom(wrapper))
                        {
                            continue;
                        }
                        // NOTE we're using TypeAsBaseType for the interfaces!
                        typeBuilder.AddInterfaceImplementation(iface.TypeAsBaseType);
#if IMPORTER
                        if (!wrapper.IsInterface)
                        {
                            // look for "magic" interfaces that imply a .NET interface
                            if (iface.ClassLoader == context.JavaBase.TypeOfJavaLangObject.ClassLoader)
                            {
                                if (iface.Name == "java.lang.Iterable" && !wrapper.ImplementsInterface(context.ClassLoaderFactory.GetJavaTypeFromType(context.Resolver.ResolveCoreType(typeof(System.Collections.IEnumerable).FullName).AsReflection())))
                                {
                                    var enumeratorType = context.ClassLoaderFactory.GetBootstrapClassLoader().TryLoadClassByName("ikvm.lang.IterableEnumerator");
                                    if (enumeratorType != null)
                                    {
                                        typeBuilder.AddInterfaceImplementation(context.Resolver.ResolveCoreType(typeof(System.Collections.IEnumerable).FullName).AsReflection());
                                        // FXBUG we're using the same method name as the C# compiler here because both the .NET and Mono implementations of Xml serialization depend on this method name
                                        var mb = typeBuilder.DefineMethod("System.Collections.IEnumerable.GetEnumerator", MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.SpecialName, context.Resolver.ResolveCoreType(typeof(System.Collections.IEnumerator).FullName).AsReflection(), Type.EmptyTypes);
                                        context.AttributeHelper.HideFromJava(mb);
                                        typeBuilder.DefineMethodOverride(mb, context.Resolver.ResolveCoreType(typeof(System.Collections.IEnumerable).FullName).GetMethod("GetEnumerator").AsReflection());
                                        var ilgen = context.CodeEmitterFactory.Create(mb);
                                        ilgen.Emit(OpCodes.Ldarg_0);
                                        var mw = enumeratorType.GetMethod("<init>", "(Ljava.lang.Iterable;)V", false);
                                        mw.Link();
                                        mw.EmitNewobj(ilgen);
                                        ilgen.Emit(OpCodes.Ret);
                                        ilgen.DoEmit();
                                    }
                                }
                            }
                            // if we implement a ghost interface, add an implicit conversion to the ghost reference value type
                            if (iface.IsGhost && wrapper.IsPublic)
                            {
                                var mb = typeBuilder.DefineMethod("op_Implicit", MethodAttributes.HideBySig | MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName, iface.TypeAsSignatureType, new Type[] { wrapper.TypeAsSignatureType });
                                context.AttributeHelper.HideFromJava(mb);
                                var ilgen = context.CodeEmitterFactory.Create(mb);
                                var local = ilgen.DeclareLocal(iface.TypeAsSignatureType);
                                ilgen.Emit(OpCodes.Ldloca, local);
                                ilgen.Emit(OpCodes.Ldarg_0);
                                ilgen.Emit(OpCodes.Stfld, iface.GhostRefField);
                                ilgen.Emit(OpCodes.Ldloca, local);
                                ilgen.Emit(OpCodes.Ldobj, iface.TypeAsSignatureType);
                                ilgen.Emit(OpCodes.Ret);
                                ilgen.DoEmit();
                            }
                        }
#endif // IMPORTER
                        // NOTE we're recursively "implementing" all interfaces that we inherit from the interfaces we implement.
                        // The C# compiler also does this and the Compact Framework requires it.
                        ImplementInterfaces(iface.Interfaces, interfaceList);
                    }
                }
            }

            void AddUnsupportedAbstractMethods()
            {
                foreach (var mb in wrapper.BaseTypeWrapper.TypeAsBaseType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (RuntimeManagedJavaType.IsUnsupportedAbstractMethod(mb))
                        GenerateUnsupportedAbstractMethodStub(mb);

                var h = new Dictionary<MethodBase, MethodBase>();
                var tw = (RuntimeJavaType)wrapper;
                while (tw != null)
                {
                    foreach (var iface in tw.Interfaces)
                    {
                        foreach (var mb in iface.TypeAsBaseType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (!h.ContainsKey(mb))
                            {
                                h.Add(mb, mb);
                                if (RuntimeManagedJavaType.IsUnsupportedAbstractMethod(mb))
                                    GenerateUnsupportedAbstractMethodStub(mb);
                            }
                        }
                    }

                    tw = tw.BaseTypeWrapper;
                }
            }

            void GenerateUnsupportedAbstractMethodStub(MethodBase mb)
            {
                var parameters = mb.GetParameters();
                var parameterTypes = new Type[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    parameterTypes[i] = parameters[i].ParameterType;

                var attr = MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Private;
                var m = typeBuilder.DefineMethod("__<unsupported>" + mb.DeclaringType.FullName + "/" + mb.Name, attr, ((MethodInfo)mb).ReturnType, parameterTypes);
                if (mb.IsGenericMethodDefinition)
                    CopyGenericArguments(mb, m);

                var ilgen = context.CodeEmitterFactory.Create(m);
                ilgen.EmitThrow("java.lang.AbstractMethodError", "Method " + mb.DeclaringType.FullName + "." + mb.Name + " is unsupported by IKVM.");
                ilgen.DoEmit();
                typeBuilder.DefineMethodOverride(m, (MethodInfo)mb);
            }

            void CopyGenericArguments(MethodBase mi, MethodBuilder mb)
            {
                var genericParameters = mi.GetGenericArguments();
                var genParamNames = new string[genericParameters.Length];
                for (int i = 0; i < genParamNames.Length; i++)
                    genParamNames[i] = genericParameters[i].Name;

                var genParamBuilders = mb.DefineGenericParameters(genParamNames);
                for (int i = 0; i < genParamBuilders.Length; i++)
                {
                    // NOTE apparently we don't need to set the interface constraints
                    // (and if we do, it fails for some reason)
                    if (genericParameters[i].BaseType != context.Types.Object)
                        genParamBuilders[i].SetBaseTypeConstraint(genericParameters[i].BaseType);

                    genParamBuilders[i].SetGenericParameterAttributes(genericParameters[i].GenericParameterAttributes);
                }
            }

            void CompileConstructorBody(FinishContext context, CodeEmitter ilGenerator, int methodIndex)
            {
                var methods = wrapper.GetMethods();
                var m = classFile.Methods[methodIndex];

#if IMPORTER
                // do we have an implementation in map.xml?
                if (wrapper.EmitMapXmlMethodPrologueAndOrBody(ilGenerator, classFile, m))
                {
                    ilGenerator.DoEmit();
                    return;
                }
#endif
                var nonLeaf = false;
                Compiler.Compile(context, host, wrapper, methods[methodIndex], classFile, m, ilGenerator, ref nonLeaf);
                ilGenerator.DoEmit();
#if IMPORTER
                ilGenerator.EmitLineNumberTable((MethodBuilder)methods[methodIndex].GetMethod());
#else // IMPORTER
                var linenumbers = ilGenerator.GetLineNumberTable();
                if (linenumbers != null)
                {
                    if (wrapper.lineNumberTables == null)
                        wrapper.lineNumberTables = new byte[methods.Length][];

                    wrapper.lineNumberTables[methodIndex] = linenumbers;
                }
#endif // IMPORTER
            }

            static bool IsCompatibleArgList(RuntimeJavaType[] caller, RuntimeJavaType[] callee)
            {
                if (caller.Length == callee.Length)
                {
                    for (int i = 0; i < caller.Length; i++)
                    {
                        if (caller[i].IsUnloadable || callee[i].IsUnloadable)
                            return false;
                        if (!caller[i].IsAssignableTo(callee[i]))
                            return false;
                    }

                    return true;
                }

                return false;
            }

            void EmitCallerIDInitialization(CodeEmitter ilGenerator, FieldInfo callerIDField)
            {
                var tw = context.JavaBase.TypeOfIkvmInternalCallerID;
                if (tw.InternalsVisibleTo(wrapper))
                {
                    var create = tw.GetMethod("create", "(Lcli.System.RuntimeTypeHandle;)Likvm.internal.CallerID;", false);
                    // A CallerID identifies the CALLING class. Under a lazy recompile `typeBuilder` is the
                    // shared SCRATCH type, so taking its token mints the wrong identity -- silently, since
                    // nothing here throws. Use the real, already-baked type instead.
                    //
                    ilGenerator.Emit(OpCodes.Ldtoken,
                        IsLazyRecompile ? (Type)wrapper.TypeAsTBD : (Type)this.typeBuilder);
                    create.Link();
                    create.EmitCall(ilGenerator);
                    if (IsLazyRecompile)
                        System.Threading.Interlocked.Increment(ref LazyBodyStats.Gen2CallerIDReal);
                }
                else if (IsLazyRecompile && TryEmitBakedCallerID(ilGenerator))
                {
                    // Reused generation 1's nested CallerID; nothing defined, nothing declined.
                    System.Threading.Interlocked.Increment(ref LazyBodyStats.Gen2CallerIDReused);
                }
                else
                {
                    RegisterNestedTypeBuilder(EmitCreateCallerID(context, typeBuilder, ilGenerator, nestedNamePrefix));
                }

                ilGenerator.Emit(OpCodes.Stsfld, callerIDField);
            }

            /// <summary>
            /// Under a lazy recompile, obtain generation 1's ALREADY-BAKED CallerID instead of minting a new
            /// one, by calling the assembly-visible factory generation 1 emitted alongside it. Returns false
            /// if there is no factory, leaving the caller on its old path (decline).
            /// </summary>
            /// <remarks>
            /// This is what `Gen2CallerID` was counting. `EmitCreateCallerID` calls `DefineNestedType` on the
            /// context's type builder, which under a recompile is the shared scratch type -- so the CallerID
            /// it produces identifies the scratch, not the method's real declaring class. Rather than ship a
            /// wrong identity, `FlushRelinkBatch` declined the whole method and kept generation 1, measured at
            /// **1,039 declines per run** (2026-09-19) against ~4,560 queued candidates.
            ///
            /// Generation 1 compiled the same body against the same class, so if a CallerID is needed now one
            /// was needed then and is baked on the real type under the UNPREFIXED name (`nestedNamePrefix` is
            /// empty for ordinary compilation and only set for the scratch). Referencing it gives exactly the
            /// identity the decline existed to protect.
            ///
            /// It must be reached through the FACTORY. The first version of this Newobj'd the nested type's
            /// constructor directly and regressed the boot with `IllegalAccessError`: the nested type is
            /// NestedPrivate, so the scratch type cannot name it. The ctor's Assembly visibility is a red
            /// herring -- the TYPE is the binding constraint.
            ///
            /// Falls back rather than throwing: a body whose shape changed between generations might not have
            /// one, and declining that method is the pre-existing, safe behaviour.
            /// </remarks>
            bool TryEmitBakedCallerID(CodeEmitter ilGenerator)
            {
                try
                {
                    var real = wrapper.TypeAsTBD;
                    if (real == null)
                        return false;

                    /* CALL the factory; do NOT Newobj the nested type. It is NestedPrivate, so the scratch
                     * type this is emitted on cannot legally name it -- that is the bug this replaced.
                     *
                     * DeclaredOnly IS LOAD-BEARING. Static methods ARE inherited for lookup, so without it a
                     * class with no CallerID of its own silently resolves its SUPERCLASS's factory and gets
                     * the superclass's identity. That does not throw; it surfaces later as
                     * `IllegalAccessError: Try to access class X from class Y` naming a Y that never made the
                     * access -- measured, 6/6 boots. The DeclaringType check below is belt and braces: if the
                     * two ever disagree, DECLINE rather than ship a wrong caller. */
                    var factory = real.GetMethod(CallerIDFactoryName,
                                                 BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
                                                     | BindingFlags.DeclaredOnly,
                                                 null, Type.EmptyTypes, null);
                    if (factory == null)
                        return false;   /* no CallerID of its own, or baked before the factory existed (AOT) */
                    if (factory.DeclaringType != real)
                    {
                        System.Threading.Interlocked.Increment(ref LazyBodyStats.Gen2CallerIDForeign);
                        return false;
                    }

                    ilGenerator.Emit(OpCodes.Call, factory);
                    /* NAME THE POPULATION. These are the only classes whose behaviour the knob changes, so
                     * this list is what any remaining generation-2 fault has to be correlated against --
                     * the crash names a CALLER class and the question is whether it is in here. */
                    if (JVM.TraceLazyBodies)
                        Console.WriteLine($"[IKVM lazybody] cidReuse {real.FullName}");
                    return true;
                }
                catch
                {
                    // Any reflection failure here means "not reusable"; the caller's old path still applies.
                    return false;
                }
            }

            /// <param name="nestedPrefix">Scopes the nested type's name; see FinishContext.NestedName. Empty
            /// for ordinary compilation. Passed explicitly because this is static -- and CallerID is the
            /// nested name that MOST needs scoping, since it carries no per-site suffix at all and so
            /// collides between any two contexts sharing a batched scratch type.</param>
            /// <summary>
            /// Assembly-visible factory for a type's nested <c>__&lt;CallerID&gt;</c>. A method, not a nested
            /// type, precisely so that code emitted on ANOTHER type can reach it -- the nested type itself is
            /// NestedPrivate and cannot be named from outside its enclosing type.
            /// </summary>
            internal const string CallerIDFactoryName = "__<>newCallerID";

            internal static TypeBuilder EmitCreateCallerID(RuntimeContext context, TypeBuilder typeBuilder, CodeEmitter ilGenerator, string nestedPrefix = "")
            {
                var tw = context.JavaBase.TypeOfIkvmInternalCallerID;
                var typeCallerID = typeBuilder.DefineNestedType(nestedPrefix + NestedTypeName.CallerID, TypeAttributes.Sealed | TypeAttributes.NestedPrivate, tw.TypeAsBaseType);
                var cb = ReflectUtil.DefineConstructor(typeCallerID, MethodAttributes.Assembly, null);
                var ctorIlgen = context.CodeEmitterFactory.Create(cb);
                ctorIlgen.Emit(OpCodes.Ldarg_0);
                var mw = tw.GetMethod("<init>", "()V", false);
                mw.Link();
                mw.EmitCall(ctorIlgen);
                ctorIlgen.Emit(OpCodes.Ret);
                ctorIlgen.DoEmit();
                ilGenerator.Emit(OpCodes.Newobj, cb);

                /* An ASSEMBLY-VISIBLE factory for the nested type, so a later LAZY RECOMPILE can obtain
                 * this exact CallerID. It cannot do so directly: the nested type is NestedPrivate, i.e.
                 * nameable only by THIS type, while generation 2 is emitted on a shared scratch type.
                 * `Newobj` on the ctor from there is an illegal type access, which IKVM surfaces far away
                 * as `IllegalAccessError` -- it regressed the boot before this factory existed.
                 * Emitted unconditionally rather than under the recompile knob: whether a class will be
                 * recompiled is not known when it is first compiled. One static method per class that has
                 * a CallerID. */
                /* PREFIXED like the nested type itself. Without this, two methods sharing one scratch type
                 * would each try to define `__<>newCallerID` on it and the second would collide -- the same
                 * reason `nestedPrefix` exists for the type. On the real type the prefix is empty, which is
                 * the name the recompile consumer looks up. */
                var fb = typeBuilder.DefineMethod(nestedPrefix + CallerIDFactoryName,
                    MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                    tw.TypeAsBaseType, Type.EmptyTypes);
                var fIlgen = context.CodeEmitterFactory.Create(fb);
                fIlgen.Emit(OpCodes.Newobj, cb);
                fIlgen.Emit(OpCodes.Ret);
                fIlgen.DoEmit();

                return typeCallerID;
            }

            void EmitConstantValueInitialization(RuntimeJavaField[] fields, CodeEmitter ilGenerator)
            {
                ClassFile.Field[] flds = classFile.Fields;
                for (int i = 0; i < flds.Length; i++)
                {
                    ClassFile.Field f = flds[i];
                    if (f.IsStatic && !f.IsFinal)
                    {
                        object constant = f.ConstantValue;
                        if (constant != null)
                        {
                            if (constant is int)
                            {
                                ilGenerator.EmitLdc_I4((int)constant);
                            }
                            else if (constant is bool)
                            {
                                ilGenerator.EmitLdc_I4((bool)constant ? 1 : 0);
                            }
                            else if (constant is byte)
                            {
                                ilGenerator.EmitLdc_I4((byte)constant);
                            }
                            else if (constant is char)
                            {
                                ilGenerator.EmitLdc_I4((char)constant);
                            }
                            else if (constant is short)
                            {
                                ilGenerator.EmitLdc_I4((short)constant);
                            }
                            else if (constant is long)
                            {
                                ilGenerator.EmitLdc_I8((long)constant);
                            }
                            else if (constant is double)
                            {
                                ilGenerator.EmitLdc_R8((double)constant);
                            }
                            else if (constant is float)
                            {
                                ilGenerator.EmitLdc_R4((float)constant);
                            }
                            else if (constant is string)
                            {
                                ilGenerator.Emit(OpCodes.Ldstr, (string)constant);
                            }
                            else
                            {
                                throw new InvalidOperationException();
                            }
                            fields[i].EmitSet(ilGenerator);
                        }
                    }
                }
            }

            internal MethodBuilder DefineThreadLocalType()
            {
                var threadLocal = context.ClassLoaderFactory.LoadClassCritical("ikvm.internal.IntrinsicThreadLocal");
                int id = nestedTypeBuilders == null ? 0 : nestedTypeBuilders.Count;
                var tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.ThreadLocal + id), TypeAttributes.NestedPrivate | TypeAttributes.Sealed, threadLocal.TypeAsBaseType);
                var fb = tb.DefineField("field", context.Types.Object, FieldAttributes.Private | FieldAttributes.Static);
                fb.SetCustomAttribute(new CustomAttributeBuilder(context.Resolver.ResolveCoreType(typeof(ThreadStaticAttribute).FullName).GetConstructor([]).AsReflection(), new object[0]));

                var mbGet = tb.DefineMethod("get", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final, context.Types.Object, Type.EmptyTypes);
                var ilgen = mbGet.GetILGenerator();
                ilgen.Emit(OpCodes.Ldsfld, fb);
                ilgen.Emit(OpCodes.Ret);

                var mbSet = tb.DefineMethod("set", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final, null, [context.Types.Object]);
                ilgen = mbSet.GetILGenerator();
                ilgen.Emit(OpCodes.Ldarg_1);
                ilgen.Emit(OpCodes.Stsfld, fb);
                ilgen.Emit(OpCodes.Ret);

                var cb = ReflectUtil.DefineConstructor(tb, MethodAttributes.Assembly, Type.EmptyTypes);
                var ctorilgen = context.CodeEmitterFactory.Create(cb);
                ctorilgen.Emit(OpCodes.Ldarg_0);
                var basector = threadLocal.GetMethod("<init>", "()V", false);
                basector.Link();
                basector.EmitCall(ctorilgen);
                ctorilgen.Emit(OpCodes.Ret);
                ctorilgen.DoEmit();

                RegisterNestedTypeBuilder(tb);
                return cb;
            }

            internal MethodBuilder GetAtomicReferenceFieldUpdater(RuntimeJavaField field)
            {
                arfuMap ??= new Dictionary<RuntimeJavaField, MethodBuilder>();

                if (!arfuMap.TryGetValue(field, out var cb))
                {
                    RuntimeJavaType arfuTypeWrapper = context.ClassLoaderFactory.LoadClassCritical("ikvm.internal.IntrinsicAtomicReferenceFieldUpdater");
                    TypeBuilder tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.AtomicReferenceFieldUpdater + arfuMap.Count), TypeAttributes.NestedPrivate | TypeAttributes.Sealed, arfuTypeWrapper.TypeAsBaseType);
                    AtomicReferenceFieldUpdaterEmitter.EmitImpl(context, tb, field.GetField());
                    cb = ReflectUtil.DefineConstructor(tb, MethodAttributes.Assembly, Type.EmptyTypes);
                    arfuMap.Add(field, cb);
                    CodeEmitter ctorilgen = context.CodeEmitterFactory.Create(cb);
                    ctorilgen.Emit(OpCodes.Ldarg_0);
                    RuntimeJavaMethod basector = arfuTypeWrapper.GetMethod("<init>", "()V", false);
                    basector.Link();
                    basector.EmitCall(ctorilgen);
                    ctorilgen.Emit(OpCodes.Ret);
                    ctorilgen.DoEmit();
                    RegisterNestedTypeBuilder(tb);
                }
                return cb;
            }

            internal TypeBuilder DefineIndyCallSiteType()
            {
                int id = nestedTypeBuilders == null ? 0 : nestedTypeBuilders.Count;
                var tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.IndyCallSite + id), TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
                RegisterNestedTypeBuilder(tb);
                return tb;
            }

            internal TypeBuilder DefineMethodHandleConstantType(MethodHandleConstantHandle handle)
            {
                var tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.MethodHandleConstant + handle.Slot), TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit); ;
                RegisterNestedTypeBuilder(tb);
                return tb;
            }

            internal TypeBuilder DefineMethodTypeConstantType(MethodTypeConstantHandle handle)
            {
                var tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.MethodTypeConstant + handle.Slot), TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit);
                RegisterNestedTypeBuilder(tb);
                return tb;
            }

            // this is used to define intrinsified anonymous classes (in the Unsafe.defineAnonymousClass() sense)
            internal TypeBuilder DefineAnonymousClass()
            {
                int id = nestedTypeBuilders == null ? 0 : nestedTypeBuilders.Count;
                var tb = typeBuilder.DefineNestedType(NestedName(NestedTypeName.IntrinsifiedAnonymousClass + id), TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.SpecialName | TypeAttributes.BeforeFieldInit);
                RegisterNestedTypeBuilder(tb);
                return tb;
            }

            // PrivateScope members may legally share names, but name-based token resolution
            // (e.g. Mono's dynamic-image fallback for baked TypeBuilders) can then bind call
            // sites to the wrong member, so every helper gets a unique name
            int helperNameCounter;

            /// <summary>
            /// Offsets the names this context generates, so that several contexts can safely share one
            /// TypeBuilder.
            /// </summary>
            /// <remarks>
            /// Every Define* below names its member "&lt;prefix&gt;" + helperNameCounter++, and that counter is
            /// per-context. Batched in-place relinking puts many methods -- and therefore many contexts -- on
            /// ONE scratch type, so without distinct ranges the second context redefines
            /// __&lt;&gt;dynamicInstanceCheckCache0 and Reflection.Emit throws
            /// Argument_AddingDuplicateWithKey. Measured: 538 of 1,411 batched methods failed that way.
            /// </remarks>
            internal void SeedHelperNames(int seed)
            {
                helperNameCounter = seed;
                nestedNamePrefix = "r" + seed + "$";
            }

            /// <summary>
            /// Prefix applied to nested type names, non-empty only for a batched in-place recompile.
            /// </summary>
            /// <remarks>
            /// Nested types are the OTHER name collision on a shared scratch type, and a worse one than the
            /// members: NestedTypeName.CallerID has no suffix at all, and ThreadLocal/IndyCallSite/
            /// MethodHandleConstant/AtomicReferenceFieldUpdater are suffixed with PER-CONTEXT ids that
            /// restart at 0. Two contexts on one type therefore collide immediately, and Reflection.Emit
            /// reports it as Argument_AddingDuplicateWithKey against System.TypeNames -- which is what took
            /// 539 of 1,700 batched methods. Safe to rename because generation-2 IL refers to these by
            /// token, not by name; the only name-based consumer is the CallerID check, which matches on
            /// Contains() and so still sees a prefixed one.
            /// </remarks>
            private string nestedNamePrefix = "";

            /// <summary>Nested type name, scoped so batched contexts on one TypeBuilder cannot collide.</summary>
            private string NestedName(string name)
            {
                return nestedNamePrefix.Length == 0 ? name : nestedNamePrefix + name;
            }

            MethodBuilder DefineHelperMethod(string name, Type returnType, Type[] parameterTypes)
            {
                return typeBuilder.DefineMethod(name + helperNameCounter++, MethodAttributes.PrivateScope | MethodAttributes.Static, returnType, parameterTypes);
            }

            internal MethodBuilder DefineMethodHandleDispatchStub(Type returnType, Type[] parameterTypes) =>
                DefineHelperMethod("__<>MHC", returnType, parameterTypes);

            internal FieldBuilder DefineMethodHandleInvokeCacheField(Type fieldType) =>
                typeBuilder.DefineField("__<>invokeCache" + helperNameCounter++, fieldType, FieldAttributes.Static | FieldAttributes.PrivateScope);

            /// <summary>
            /// Defines the per-call-site cache holding the resolved <see cref="Type"/> a dynamic instanceof or cast
            /// tests against, so the check only has to be resolved on first execution.
            /// </summary>
            /// <returns></returns>
            internal FieldBuilder DefineDynamicInstanceCheckCacheField()
            {
                // `object`, not `Type`: the site caches the resolved RuntimeJavaType.InstanceCheck, which is internal to
                // the runtime and so cannot be named from emitted code. Dispatch goes through
                // ByteCodeHelper.InstanceCheckIsInstanceOf, which avoids the Type.IsInstanceOfType reflection icall the
                // Type-typed cache forced.
                return typeBuilder.DefineField("__<>dynamicInstanceCheckCache" + helperNameCounter++, context.Types.Object, FieldAttributes.Static | FieldAttributes.PrivateScope);
            }

            /// <summary>
            /// Defines the per-call-site flag recording that the one-time checks a 'new' of a type that was unloadable
            /// at compile time has to make have already been made.
            /// </summary>
            /// <returns></returns>
            internal FieldBuilder DefineDynamicNewCheckCacheField()
            {
                return typeBuilder.DefineField("__<>dynamicNewCheckCache" + helperNameCounter++, context.Types.Boolean, FieldAttributes.Static | FieldAttributes.PrivateScope);
            }

            internal FieldBuilder DefineDynamicMethodHandleCacheField()
            {
                return typeBuilder.DefineField("__<>dynamicMethodHandleCache" + helperNameCounter++, context.JavaBase.TypeOfJavaLangInvokeMethodHandle.TypeAsSignatureType, FieldAttributes.Static | FieldAttributes.PrivateScope);
            }

            internal FieldBuilder DefineDynamicMethodTypeCacheField()
            {
                return typeBuilder.DefineField("__<>dynamicMethodTypeCache" + helperNameCounter++, context.JavaBase.TypeOfJavaLangInvokeMethodType.TypeAsSignatureType, FieldAttributes.Static | FieldAttributes.PrivateScope);
            }

            internal MethodBuilder DefineDelegateInvokeErrorStub(Type returnType, Type[] parameterTypes)
            {
                return DefineHelperMethod("__<>", returnType, parameterTypes);
            }

            internal MethodInfo GetInvokeSpecialStub(RuntimeJavaMethod method)
            {
                invokespecialstubcache ??= new Dictionary<MethodKey, MethodInfo>();

                var key = new MethodKey(method.DeclaringType.Name, method.Name, method.Signature);

                if (!invokespecialstubcache.TryGetValue(key, out var mi))
                {
                    var dmh = method.GetDefineMethodHelper();
                    var stub = dmh.DefineMethod(wrapper, typeBuilder, "__<>" + helperNameCounter++, MethodAttributes.PrivateScope);
                    var ilgen = context.CodeEmitterFactory.Create(stub);
                    ilgen.Emit(OpCodes.Ldarg_0);
                    for (int i = 1; i <= dmh.ParameterCount; i++)
                        ilgen.EmitLdarg(i);
                    method.EmitCall(ilgen);
                    ilgen.Emit(OpCodes.Ret);
                    ilgen.DoEmit();
                    invokespecialstubcache[key] = stub;
                    mi = stub;
                }

                return mi;
            }

#if !IMPORTER

            internal void EmitLiveObjectLoad(CodeEmitter ilgen, object value)
            {
                liveObjects ??= new List<object>();
                var fi = TypeBuilder.GetField(typeof(IKVM.Runtime.LiveObjectHolder<>).MakeGenericType(typeBuilder), typeof(IKVM.Runtime.LiveObjectHolder<>).GetField("values", BindingFlags.Static | BindingFlags.Public));
                ilgen.Emit(OpCodes.Ldsfld, fi);
                ilgen.EmitLdc_I4(liveObjects.Count);
                ilgen.Emit(OpCodes.Ldelem_Ref);
                liveObjects.Add(value);
            }

#endif
        }
    }

}
