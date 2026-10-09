namespace IKVM.Runtime
{

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

    /// <summary>
    /// The arguments of the reflective calls of java.lang.reflect.Method and Constructor.
    /// </summary>
    static class ReflectionArguments
    {

        static object ConvertPrimitive(RuntimeJavaType tw, object value)
        {
            if (tw == tw.Context.PrimitiveJavaTypeFactory.BOOLEAN)
            {
                if (value is global::java.lang.Boolean boolean)
                    return boolean.booleanValue();
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.BYTE)
            {
                if (value is global::java.lang.Byte @byte)
                    return @byte.byteValue();
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.CHAR)
            {
                if (value is global::java.lang.Character character)
                    return character.charValue();
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.SHORT)
            {
                if (value is global::java.lang.Short || value is global::java.lang.Byte)
                {
                    return ((global::java.lang.Number)value).shortValue();
                }
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.INT)
            {
                if (value is global::java.lang.Integer || value is global::java.lang.Short || value is global::java.lang.Byte)
                {
                    return ((global::java.lang.Number)value).intValue();
                }
                else if (value is global::java.lang.Character)
                {
                    return (int)((global::java.lang.Character)value).charValue();
                }
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.LONG)
            {
                if (value is global::java.lang.Long || value is global::java.lang.Integer || value is global::java.lang.Short || value is global::java.lang.Byte)
                {
                    return ((global::java.lang.Number)value).longValue();
                }
                else if (value is global::java.lang.Character)
                {
                    return (long)((global::java.lang.Character)value).charValue();
                }
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.FLOAT)
            {
                if (value is global::java.lang.Float || value is global::java.lang.Long || value is global::java.lang.Integer || value is global::java.lang.Short || value is global::java.lang.Byte)
                {
                    return ((global::java.lang.Number)value).floatValue();
                }
                else if (value is global::java.lang.Character)
                {
                    return (float)((global::java.lang.Character)value).charValue();
                }
            }
            else if (tw == tw.Context.PrimitiveJavaTypeFactory.DOUBLE)
            {
                if (value is global::java.lang.Double || value is global::java.lang.Float || value is global::java.lang.Long || value is global::java.lang.Integer || value is global::java.lang.Short || value is global::java.lang.Byte)
                {
                    return ((global::java.lang.Number)value).doubleValue();
                }
                else if (value is global::java.lang.Character)
                {
                    return (double)((global::java.lang.Character)value).charValue();
                }
            }
            throw new global::java.lang.IllegalArgumentException();
        }

        /// <summary>
        /// Converts the arguments of a reflective call to the parameter types: unboxing and widening primitives, and
        /// wrapping ghosts, failing with IllegalArgumentException as Java's reflection does.
        /// </summary>
        internal static object[] Convert(RuntimeClassLoader loader, RuntimeJavaType[] argumentTypes, object[] args)
        {
            var nargs = new object[args == null ? 0 : args.Length];
            if (nargs.Length != argumentTypes.Length)
                throw new global::java.lang.IllegalArgumentException("wrong number of arguments");

            for (int i = 0; i < nargs.Length; i++)
            {
                if (argumentTypes[i].IsPrimitive)
                {
                    nargs[i] = ConvertPrimitive(argumentTypes[i], args[i]);
                }
                else
                {
                    if (args[i] != null && !argumentTypes[i].EnsureLoadable(loader).IsInstance(args[i]))
                        throw new global::java.lang.IllegalArgumentException();

                    nargs[i] = argumentTypes[i].GhostWrap(args[i]);
                }
            }

            return nargs;
        }

    }

#endif

}
