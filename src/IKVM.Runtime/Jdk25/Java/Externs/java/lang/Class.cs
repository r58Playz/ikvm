using System;
using System.Collections.Generic;

using IKVM.Runtime;
using IKVM.Runtime.Accessors.Java.Lang;

namespace IKVM.Java.Externs.java.lang
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.Class', and those the IKVM version of it has in place of
    /// the fields HotSpot sets (see local\java.base\java\lang\Class.java).
    /// </summary>
    static class Class
    {

#if FIRST_PASS == false

        static ClassAccessor classAccessor;

        static ClassAccessor ClassAccessor => JVM.Internal.BaseAccessors.Get(ref classAccessor);

#endif

        public static void registerNatives()
        {

        }

        public static global::java.lang.Class forName0(string name, bool initialize, global::java.lang.ClassLoader loader, global::java.lang.Class caller)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (name == null)
                throw new global::java.lang.NullPointerException();

            RuntimeJavaType javaType = null;
            if (name.IndexOf(',') > 0)
            {
                // an assembly qualified .NET type name
                var type = Type.GetType(name);
                if (type != null)
                    javaType = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type);

                if (javaType == null)
                {
                    global::java.lang.Throwable.suppressFillInStackTrace = true;
                    throw new global::java.lang.ClassNotFoundException(name);
                }
            }
            else
            {
                try
                {
                    javaType = JVM.Context.ClassLoaderFactory.GetClassLoaderWrapper(loader).LoadClassByName(name);
                }
                catch (ClassNotFoundException e)
                {
                    global::java.lang.Throwable.suppressFillInStackTrace = true;
                    throw new global::java.lang.ClassNotFoundException(e.Message);
                }
                catch (ClassLoadingException e)
                {
                    throw e.InnerException;
                }
                catch (RetargetableJavaException e)
                {
                    throw e.ToJava();
                }
            }

            if (initialize && javaType.IsArray == false)
            {
                try
                {
                    javaType.Finish();
                }
                catch (RetargetableJavaException e)
                {
                    throw e.ToJava();
                }

                javaType.RunClassInit();
            }

            return javaType.ClassObject;
#endif
        }

        public static bool isInstance(global::java.lang.Class self, object obj)
        {
            return RuntimeJavaType.FromClass(self).IsInstance(obj);
        }

        public static bool isAssignableFrom(global::java.lang.Class self, global::java.lang.Class otherClass)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            if (otherClass == null)
                throw new global::java.lang.NullPointerException();

            return RuntimeJavaType.FromClass(otherClass).IsAssignableTo(RuntimeJavaType.FromClass(self));
#endif
        }

        public static bool isArray(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).IsArray;
        }

        public static bool isPrimitive(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).IsPrimitive;
        }

        public static bool isHidden(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).IsHidden;
        }

        /// <summary>
        /// Implements the native method 'initClassName', which caches the name in the 'name' field.
        /// </summary>
        public static string initClassName(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var name = GetName(self);
            ClassAccessor.SetName(self, name);
            return name;
#endif
        }

#if FIRST_PASS == false

        static string GetName(global::java.lang.Class self)
        {
            var type = RuntimeJavaType.FromClass(self);
            if (type.IsPrimitive)
            {
                var p = JVM.Context.PrimitiveJavaTypeFactory;
                if (type == p.BYTE)
                    return "byte";
                if (type == p.CHAR)
                    return "char";
                if (type == p.DOUBLE)
                    return "double";
                if (type == p.FLOAT)
                    return "float";
                if (type == p.INT)
                    return "int";
                if (type == p.LONG)
                    return "long";
                if (type == p.SHORT)
                    return "short";
                if (type == p.BOOLEAN)
                    return "boolean";
                if (type == p.VOID)
                    return "void";
            }

            // as HotSpot, a hidden class's name has a suffix that makes it unique
            if (type.IsHidden)
                return type.Name + "/0x" + global::java.lang.System.identityHashCode(self).ToString("x8");

            return type.Name;
        }

#endif

        public static global::java.lang.ClassLoader getClassLoader0(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).ClassLoader.GetJavaClassLoader();
        }

        public static global::java.lang.Module getModule0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return Module.GetModule(RuntimeJavaType.FromClass(self));
#endif
        }

        public static global::java.lang.Class getSuperclass(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).BaseTypeWrapper?.ClassObject;
        }

        public static global::java.lang.Class[] getInterfaces0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var ifaces = RuntimeJavaType.FromClass(self).Interfaces;
            var interfaces = new global::java.lang.Class[ifaces.Length];
            for (int i = 0; i < ifaces.Length; i++)
                interfaces[i] = ifaces[i].ClassObject;

            return interfaces;
#endif
        }

        public static global::java.lang.Class getComponentType(global::java.lang.Class self)
        {
            var type = RuntimeJavaType.FromClass(self);
            return type.IsArray ? type.ElementTypeWrapper.ClassObject : null;
        }

        public static int getModifiers(global::java.lang.Class self)
        {
            // the 0x7FFF mask comes from JVM_ACC_WRITTEN_FLAGS in hotspot\src\share\vm\utilities\accessFlags.hpp
            // masking out ACC_SUPER comes from instanceKlass::compute_modifier_flags() in hotspot\src\share\vm\oops\instanceKlass.cpp
            const int mask = 0x7FFF & (int)~IKVM.Attributes.Modifiers.Super;
            return (int)RuntimeJavaType.FromClass(self).ReflectiveModifiers & mask;
        }

        public static int getClassAccessFlagsRaw0(global::java.lang.Class self)
        {
            // the access flags of the class file, not those of the InnerClasses attribute
            return (int)RuntimeJavaType.FromClass(self).Modifiers & 0x7FFF;
        }

        public static int getClassFileVersion0(global::java.lang.Class self)
        {
            // major in the low 16 bits, minor in the high: the runtime does not keep a class's own version
            return 69;
        }

        public static object[] getEnclosingMethod0(global::java.lang.Class self)
        {
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                var enc = type.GetEnclosingMethod();
                if (enc == null)
                    return null;

                return [type.ClassLoader.LoadClassByName(enc[0]).ClassObject, enc[1], enc[2]?.Replace('.', '/')];
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
        }

        public static global::java.lang.Class getDeclaringClass0(global::java.lang.Class self)
        {
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                var declaringType = type.DeclaringTypeWrapper;
                if (declaringType == null)
                    return null;

                declaringType = declaringType.EnsureLoadable(type.ClassLoader);
                if (declaringType.IsAccessibleFrom(type) == false)
                    throw new IllegalAccessError(string.Format("tried to access class {0} from class {1}", declaringType.Name, type.Name));

                declaringType.Finish();

                foreach (var declaringTypeInnerType in declaringType.InnerClasses)
                    if (declaringTypeInnerType.Name == type.Name && declaringTypeInnerType.EnsureLoadable(declaringType.ClassLoader) == type)
                        return declaringType.ClassObject;

                throw new IncompatibleClassChangeError(string.Format("{0} and {1} disagree on InnerClasses attribute", declaringType.Name, type.Name));
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
        }

        /// <summary>
        /// Implements the native method 'getSimpleBinaryName0': the class's name in the InnerClasses attribute, which is
        /// null for an anonymous class. As javac names nested classes, it is what follows the enclosing class's name and
        /// '$', less the digits of a local class.
        /// </summary>
        public static string getSimpleBinaryName0(global::java.lang.Class self)
        {
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                string enclosingName;
                if (type.DeclaringTypeWrapper != null)
                    enclosingName = type.DeclaringTypeWrapper.Name;
                else if (type.GetEnclosingMethod() is string[] enc)
                    enclosingName = enc[0];
                else
                    return null;

                var name = type.Name;
                if (name.Length <= enclosingName.Length + 1 || name.StartsWith(enclosingName, StringComparison.Ordinal) == false)
                    return null;

                var simple = name.Substring(enclosingName.Length + 1);
                if (type.DeclaringTypeWrapper == null)
                    simple = simple.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

                return simple.Length > 0 ? simple : null;
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
        }

        public static global::java.security.ProtectionDomain getProtectionDomain0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var type = RuntimeJavaType.FromClass(self);
            if (type.IsArray)
                return null;

            var pd = type.ClassObject.pd;
            if (pd == null)
            {
                // the protection domain of a class in an assembly is made lazily
                if (type.ClassLoader is RuntimeAssemblyClassLoader acl)
                    pd = acl.GetProtectionDomain();
                else if (type is RuntimeAnonymousJavaType)
                    pd = JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(type.TypeAsTBD.DeclaringType).ClassObject.pd;
            }

            return pd;
#endif
        }

        /// <summary>
        /// Implements the native method for 'getPrimitiveClass', which only reflection calls: it is an intrinsic in the
        /// core class library.
        /// </summary>
        public static global::java.lang.Class getPrimitiveClass(string name)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return name switch
            {
                "byte" => JVM.Context.PrimitiveJavaTypeFactory.BYTE.ClassObject,
                "char" => JVM.Context.PrimitiveJavaTypeFactory.CHAR.ClassObject,
                "double" => JVM.Context.PrimitiveJavaTypeFactory.DOUBLE.ClassObject,
                "float" => JVM.Context.PrimitiveJavaTypeFactory.FLOAT.ClassObject,
                "int" => JVM.Context.PrimitiveJavaTypeFactory.INT.ClassObject,
                "long" => JVM.Context.PrimitiveJavaTypeFactory.LONG.ClassObject,
                "short" => JVM.Context.PrimitiveJavaTypeFactory.SHORT.ClassObject,
                "boolean" => JVM.Context.PrimitiveJavaTypeFactory.BOOLEAN.ClassObject,
                "void" => JVM.Context.PrimitiveJavaTypeFactory.VOID.ClassObject,
                _ => throw new ArgumentException(name),
            };
#endif
        }

        public static string getGenericSignature0(global::java.lang.Class self)
        {
            var type = RuntimeJavaType.FromClass(self);
            type.Finish();
            return type.GetGenericSignature();
        }

        public static byte[] getRawAnnotations(global::java.lang.Class self)
        {
            // the runtime has the annotations themselves (see getDeclaredAnnotationsImpl)
            return null;
        }

        public static byte[] getRawTypeAnnotations(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).GetRawTypeAnnotations();
        }

        public static object getConstantPool(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return IKVM.Java.Externs.jdk.@internal.reflect.ConstantPool.Create(RuntimeJavaType.FromClass(self).GetConstantPool());
#endif
        }

        public static object getDeclaredAnnotationsImpl(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var type = RuntimeJavaType.FromClass(self);

            try
            {
                type.Finish();
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }

            return AnnotationsToMap(type.ClassLoader, type.GetDeclaredAnnotations());
#endif
        }

        internal static object AnnotationsToMap(RuntimeClassLoader loader, object[] objAnn)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var map = new global::java.util.LinkedHashMap();
            if (objAnn != null)
            {
                foreach (var obj in objAnn)
                {
                    if (obj is global::java.lang.annotation.Annotation a)
                    {
                        map.put(a.annotationType(), FreezeOrWrapAttribute(a));
                    }
                    else if (obj is IKVM.Attributes.DynamicAnnotationAttribute dynamicAnnotation)
                    {
                        a = (global::java.lang.annotation.Annotation)JVM.NewAnnotation(loader.GetJavaClassLoader(), dynamicAnnotation.Definition);
                        if (a != null)
                            map.put(a.annotationType(), a);
                    }
                }
            }

            return map;
#endif
        }

#if FIRST_PASS == false

        internal static global::java.lang.annotation.Annotation FreezeOrWrapAttribute(global::java.lang.annotation.Annotation ann)
        {
            if (ann is global::ikvm.@internal.AnnotationAttributeBase attr)
            {
                // freeze to make sure the defaults are set
                attr.freeze();
                ann = global::sun.reflect.annotation.AnnotationParser.annotationForMap(attr.annotationType(), attr.getValues());
            }

            return ann;
        }

#endif

        public static global::java.lang.reflect.Field[] getDeclaredFields0(global::java.lang.Class self, bool publicOnly)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                var fields = type.GetFields();
                var list = new List<global::java.lang.reflect.Field>(fields.Length);
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].IsHideFromReflection == false && (publicOnly == false || fields[i].IsPublic))
                        list.Add((global::java.lang.reflect.Field)fields[i].ToField(false, i));

                return list.ToArray();
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

        public static global::java.lang.reflect.Method[] getDeclaredMethods0(global::java.lang.Class self, bool publicOnly)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                if (type.HasVerifyError)
                    throw new VerifyError();

                if (type.HasClassFormatError)
                    throw new ClassFormatError(type.Name);

                var methods = type.GetMethods();
                var list = new List<global::java.lang.reflect.Method>(methods.Length);
                foreach (var method in methods)
                {
                    // we don't want to expose "hideFromReflection" methods (one reason is that it would mess up the serialVersionUID computation)
                    if (!method.IsHideFromReflection && !method.IsConstructor && !method.IsClassInitializer && (!publicOnly || method.IsPublic))
                        list.Add((global::java.lang.reflect.Method)method.ToMethodOrConstructor(false));
                }

                return list.ToArray();
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

        public static global::java.lang.reflect.Constructor[] getDeclaredConstructors0(global::java.lang.Class self, bool publicOnly)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                if (type.HasVerifyError)
                    throw new VerifyError();

                if (type.HasClassFormatError)
                    throw new ClassFormatError(type.Name);

                var methods = type.GetMethods();
                var list = new List<global::java.lang.reflect.Constructor>(methods.Length);
                foreach (var method in methods)
                {
                    // we don't want to expose "hideFromReflection" methods (one reason is that it would mess up the serialVersionUID computation)
                    if (method.IsHideFromReflection == false && method.IsConstructor && (publicOnly == false || method.IsPublic))
                        list.Add((global::java.lang.reflect.Constructor)method.ToMethodOrConstructor(false));
                }

                return list.ToArray();
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

        public static global::java.lang.Class[] getDeclaredClasses0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var type = RuntimeJavaType.FromClass(self);
                type.Finish();

                var innerTypes = type.InnerClasses;
                var list = new global::java.lang.Class[innerTypes.Length];
                for (int i = 0; i < list.Length; i++)
                {
                    var innerType = innerTypes[i].EnsureLoadable(type.ClassLoader);
                    if (innerType.IsAccessibleFrom(type) == false)
                        throw new IllegalAccessError(string.Format("tried to access class {0} from class {1}", innerType.Name, type.Name));

                    innerType.Finish();
                    list[i] = innerType.ClassObject;
                }

                return list;
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }
#endif
        }

        public static bool isRecord0(global::java.lang.Class self)
        {
            return RuntimeJavaType.FromClass(self).IsRecord;
        }

#if FIRST_PASS == false

        static readonly global::System.Reflection.FieldInfo recordComponentClazz = GetRecordComponentField("clazz");
        static readonly global::System.Reflection.FieldInfo recordComponentName = GetRecordComponentField("name");
        static readonly global::System.Reflection.FieldInfo recordComponentType = GetRecordComponentField("type");
        static readonly global::System.Reflection.FieldInfo recordComponentAccessor = GetRecordComponentField("accessor");
        static readonly global::System.Reflection.FieldInfo recordComponentSignature = GetRecordComponentField("signature");

        static global::System.Reflection.FieldInfo GetRecordComponentField(string name)
        {
            return typeof(global::java.lang.reflect.RecordComponent).GetField(name, global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic);
        }

#endif

        /// <summary>
        /// Implements the native method 'getRecordComponents0': the components of a record class, from its Record
        /// attribute, as HotSpot's Reflection::new_record_component makes them. They do not have the component's
        /// annotations (ikvmc does not keep them yet).
        /// </summary>
        public static global::java.lang.reflect.RecordComponent[] getRecordComponents0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var tw = RuntimeJavaType.FromClass(self);
            if (tw.IsRecord == false)
                return null;

            var components = tw.RecordComponents ?? Array.Empty<RecordComponentInfo>();
            var result = new global::java.lang.reflect.RecordComponent[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                var rc = (global::java.lang.reflect.RecordComponent)global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::java.lang.reflect.RecordComponent));
                recordComponentClazz.SetValue(rc, self);
                recordComponentName.SetValue(rc, component.Name);
                recordComponentType.SetValue(rc, tw.ClassLoader.FieldTypeWrapperFromSig(component.Descriptor, LoadMode.LoadOrThrow).EnsureLoadable(tw.ClassLoader).ClassObject);
                recordComponentAccessor.SetValue(rc, tw.GetMethod(component.Name, "()" + component.Descriptor, false)?.ToMethodOrConstructor(true));
                recordComponentSignature.SetValue(rc, component.Signature);
                result[i] = rc;
            }

            return result;
#endif
        }

        public static global::java.lang.Class getNestHost0(global::java.lang.Class self)
        {
            try
            {
                var type = RuntimeJavaType.FromClass(self);

                // a hidden nestmate is in the nest of the class it was defined as a nestmate of
                if (type.HiddenNestmateOf is RuntimeJavaType nestmateOf)
                    return getNestHost0(nestmateOf.ClassObject);

                if (type.NestHostName is string hostName && type.IsNestmateOf(type.ClassLoader.LoadClassByName(hostName)))
                    return type.ClassLoader.LoadClassByName(hostName).ClassObject;

                // a class not in a valid nest is the host of its own
                return self;
            }
            catch (RetargetableJavaException)
            {
                return self;
            }
        }

        public static global::java.lang.Class[] getNestMembers0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var type = RuntimeJavaType.FromClass(self);
            var list = new List<global::java.lang.Class>() { self };
            foreach (var name in type.NestMemberNames ?? Array.Empty<string>())
            {
                try
                {
                    list.Add(type.ClassLoader.LoadClassByName(name).ClassObject);
                }
                catch (RetargetableJavaException x)
                {
                    throw x.ToJava();
                }
            }

            return list.ToArray();
#endif
        }

        public static global::java.lang.Class[] getPermittedSubclasses0(global::java.lang.Class self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var type = RuntimeJavaType.FromClass(self);
            if (type.IsSealed == false)
                return null;

            var list = new List<global::java.lang.Class>();
            foreach (var name in type.PermittedSubclassNames ?? Array.Empty<string>())
            {
                try
                {
                    list.Add(type.ClassLoader.LoadClassByName(name).ClassObject);
                }
                catch (RetargetableJavaException)
                {
                    // as HotSpot, a permitted subclass that cannot be loaded is left out
                }
            }

            return list.ToArray();
#endif
        }

        public static bool desiredAssertionStatus0(global::java.lang.Class clazz)
        {
            return IKVM.Runtime.Assertions.IsEnabled(RuntimeJavaType.FromClass(clazz));
        }

    }

}
