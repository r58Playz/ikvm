using System;
using System.Runtime.InteropServices;

using IKVM.Attributes;

namespace IKVM.Runtime
{

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

    /// <summary>
    /// A field of java.lang.String that libjava's natives read through JNI (JDK 9+'s 'coder' and 'value'), which
    /// IKVM's strings, .NET strings, do not have: the coder is UTF16, and the value is the string's UTF-16 bytes.
    /// libjava reads the value only of a LATIN1 string.
    /// </summary>
    sealed class RuntimeStringJniJavaField : RuntimeJavaField
    {

        const byte UTF16 = 1;

        /// <summary>
        /// Gets the field of String with the name and signature, if it is one of these.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="name"></param>
        /// <param name="sig"></param>
        /// <returns></returns>
        internal static RuntimeJavaField Get(RuntimeContext context, string name, string sig)
        {
            return (name, sig) switch
            {
                ("coder", "B") => coder ??= new RuntimeStringJniJavaField(context, name, sig, context.PrimitiveJavaTypeFactory.BYTE),
                ("value", "[B") => value ??= new RuntimeStringJniJavaField(context, name, sig, context.PrimitiveJavaTypeFactory.BYTE.MakeArrayType(1)),
                _ => null,
            };
        }

        static RuntimeJavaField coder;
        static RuntimeJavaField value;

        RuntimeStringJniJavaField(RuntimeContext context, string name, string sig, RuntimeJavaType fieldType) :
            base(context.JavaBase.TypeOfJavaLangString, fieldType, name, sig, Modifiers.Private | Modifiers.Final, null, MemberFlags.HideFromReflection)
        {

        }

#if EMITTERS

        protected override void EmitGetImpl(CodeEmitter il) => throw new InvalidOperationException();

        protected override void EmitSetImpl(CodeEmitter il) => throw new InvalidOperationException();

#endif

        internal override object GetValue(object obj)
        {
            return Name == "coder" ? UTF16 : MemoryMarshal.AsBytes(((string)obj).AsSpan()).ToArray();
        }

        internal override void SetValue(object obj, object value)
        {
            throw new global::java.lang.IllegalAccessError("java.lang.String." + Name);
        }

        internal override TField UnsafeGetValue<TField>(object obj)
        {
            return (TField)GetValue(obj);
        }

        internal override TField UnsafeVolatileGet<TField>(object obj)
        {
            return (TField)GetValue(obj);
        }

    }

#endif

}
