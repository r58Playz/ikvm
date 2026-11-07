/*
  Copyright (C) 2002-2014 Jeroen Frijters

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
using IKVM.Attributes;

#if IMPORTER || EXPORTER
using IKVM.Reflection.Emit;
#else
using System.Reflection.Emit;
#endif

namespace IKVM.Runtime
{

    abstract class RuntimeMirandaJavaMethod : RuntimeSmartJavaMethod
    {

        sealed class AbstractMirandaMethodWrapper : RuntimeMirandaJavaMethod
        {

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="declaringType"></param>
            /// <param name="ifmethod"></param>
            internal AbstractMirandaMethodWrapper(RuntimeJavaType declaringType, RuntimeJavaMethod ifmethod) :
                base(declaringType, ifmethod, Modifiers.Public | Modifiers.Abstract)
            {

            }

        }

        sealed class DefaultMirandaMethodWrapper : RuntimeMirandaJavaMethod
        {

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="declaringType"></param>
            /// <param name="ifmethod"></param>
            internal DefaultMirandaMethodWrapper(RuntimeJavaType declaringType, RuntimeJavaMethod ifmethod) :
                base(declaringType, ifmethod, Modifiers.Public)
            {

            }

        }

        sealed class ErrorMirandaMethodWrapper : RuntimeMirandaJavaMethod
        {

            string error;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="declaringType"></param>
            /// <param name="ifmethod"></param>
            /// <param name="error"></param>
            internal ErrorMirandaMethodWrapper(RuntimeJavaType declaringType, RuntimeJavaMethod ifmethod, string error) :
                base(declaringType, ifmethod, Modifiers.Public)
            {
                this.error = error;
            }

            protected override RuntimeMirandaJavaMethod AddConflictError(RuntimeJavaMethod mw)
            {
                error += " " + mw.DeclaringType.Name + "." + mw.Name;
                return this;
            }

            internal override RuntimeMirandaJavaMethod Inherit(RuntimeJavaType declaringType)
            {
                // a conflict names the conflicting methods; any other error names the class
                return new ErrorMirandaMethodWrapper(declaringType, BaseMethod, IsConflictError ? error : declaringType.Name + "." + Name + Signature);
            }

            internal override string Error
            {
                get { return error; }
            }

        }

        readonly RuntimeJavaMethod ifmethod;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="declaringType"></param>
        /// <param name="ifmethod"></param>
        /// <param name="modifiers"></param>
        RuntimeMirandaJavaMethod(RuntimeJavaType declaringType, RuntimeJavaMethod ifmethod, Modifiers modifiers) :
            base(declaringType, ifmethod.Name, ifmethod.Signature, null, null, null, modifiers, MemberFlags.HideFromReflection | MemberFlags.MirandaMethod)
        {
            this.ifmethod = ifmethod;
        }

        internal static RuntimeMirandaJavaMethod Create(RuntimeJavaType declaringType, RuntimeJavaMethod ifmethod)
        {
            var dmmw = ifmethod as DefaultMirandaMethodWrapper;
            if (dmmw != null)
                return new DefaultMirandaMethodWrapper(declaringType, dmmw.BaseMethod);

            return ifmethod.IsAbstract ? new AbstractMirandaMethodWrapper(declaringType, ifmethod) : new DefaultMirandaMethodWrapper(declaringType, ifmethod);
        }

        /// <summary>
        /// wasm fork: whether <paramref name="mw"/> is the miranda method ikvmc emitted on a PRECOMPILED class (a forwarder to an
        /// inherited interface method, or an abstract/error stub for one). Loaded back from the assembly it is an ordinary method
        /// hidden from reflection, not a <see cref="RuntimeMirandaJavaMethod"/>, so a class loaded at runtime that extends it took
        /// it for a real superclass declaration -- and a superclass declaration beats every interface default. A more specific
        /// default from an interface the subclass adds was then never considered: `class P extends AbstractMap implements
        /// ConcurrentMap` ran Map's computeIfAbsent (get, apply, put: a losing racer overwrites the winner) instead of
        /// ConcurrentMap's (putIfAbsent), and 26.2's ResourceKey interning through Guava's MapMakerInternalMap handed out two
        /// keys for one name ("Unbound values in registry"). Access stubs are hidden from reflection too, and are excluded.
        /// </summary>
        internal static bool IsPrecompiledForwarder(RuntimeJavaMethod mw)
        {
            return mw != null && !mw.IsMirandaMethod && mw.IsHideFromReflection && !mw.IsAccessStub && mw.IsVirtual &&
                mw.DeclaringType is RuntimeManagedByteCodeJavaType && !mw.DeclaringType.IsInterface;
        }

        /// <summary>
        /// wasm fork: what the precompiled class <paramref name="baseType"/> resolved <paramref name="name"/><paramref name="sig"/>
        /// to from its interfaces, as a miranda method of <paramref name="declaringType"/> -- the fold AddMirandaMethods performs
        /// (superinterfaces first, each candidate merged with <see cref="Update"/>). Null when no interface of the base declares it.
        /// </summary>
        internal static RuntimeMirandaJavaMethod ResolveFromInterfaces(RuntimeJavaType declaringType, RuntimeJavaType baseType, string name, string sig)
        {
            RuntimeMirandaJavaMethod result = null;
            var seen = new System.Collections.Generic.HashSet<RuntimeJavaType>();

            void Fold(RuntimeJavaType iface)
            {
                if (!seen.Add(iface))
                    return;

                foreach (var sup in iface.Interfaces)
                    Fold(sup);

                foreach (var m in iface.GetMethods())
                    if (m.IsVirtual && m.Name == name && m.Signature == sig)
                        result = result == null ? Create(declaringType, m) : result.Update(m);
            }

            for (var t = baseType; t != null; t = t.BaseTypeWrapper)
                foreach (var iface in t.Interfaces)
                    Fold(iface);

            return result;
        }

        /// <summary>
        /// wasm fork: this miranda method as inherited by <paramref name="declaringType"/>, a subclass that implements further
        /// interfaces declaring it. The subclass merges those into the result with <see cref="Update"/>.
        /// </summary>
        internal virtual RuntimeMirandaJavaMethod Inherit(RuntimeJavaType declaringType)
        {
            return Create(declaringType, ifmethod);
        }

        internal RuntimeMirandaJavaMethod Update(RuntimeJavaMethod mw)
        {
            if (ifmethod == mw)
            {
                // an interface can be implemented multiple times
                return this;
            }
            else if (ifmethod.DeclaringType.ImplementsInterface(mw.DeclaringType))
            {
                // we can override a base interface without problems
                return this;
            }
            else if (mw.DeclaringType.ImplementsInterface(ifmethod.DeclaringType))
            {
                return Create(DeclaringType, mw);
            }
            else if (!ifmethod.IsAbstract && !mw.IsAbstract)
            {
                return AddConflictError(mw);
            }
            else if (!ifmethod.IsAbstract && mw.IsAbstract)
            {
                return new ErrorMirandaMethodWrapper(DeclaringType, mw, DeclaringType.Name + "." + Name + Signature);
            }
            else
            {
                return this;
            }
        }

        protected virtual RuntimeMirandaJavaMethod AddConflictError(RuntimeJavaMethod mw)
        {
            return new ErrorMirandaMethodWrapper(DeclaringType, ifmethod, "Conflicting default methods:")
                .AddConflictError(ifmethod)
                .AddConflictError(mw);
        }

        internal bool IsConflictError
        {
            get { return Error != null && Error.StartsWith("Conflicting default methods:"); }
        }

        internal RuntimeJavaMethod BaseMethod
        {
            get { return ifmethod; }
        }

        internal virtual string Error
        {
            get { return null; }
        }

#if EMITTERS

        protected override void CallImpl(CodeEmitter ilgen)
        {
            ilgen.Emit(OpCodes.Call, GetMethod());
        }

        protected override void CallvirtImpl(CodeEmitter ilgen)
        {
            ilgen.Emit(OpCodes.Callvirt, GetMethod());
        }

#endif // EMITTERS

    }

}
