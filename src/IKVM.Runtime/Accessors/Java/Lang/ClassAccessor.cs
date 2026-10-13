using System;

namespace IKVM.Runtime.Accessors.Java.Lang
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.Class' type.
    /// </summary>
    internal sealed class ClassAccessor : Accessor<object>
    {

        Type ikvmInternalCallerID;
        Type javaLangClass;
        Type javaLangReflectMethod;

        MethodAccessor<Func<Type, object>> init;
        MethodAccessor<Func<string, object, object>> forName;
        MethodAccessor<Func<object, string, object[], object>> getMethod;

        // JDK 25
        FieldAccessor<object, object> classData;
        FieldAccessor<object, string> name;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public ClassAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.Class")
        {

        }

        Type IkvmInternalCallerID => Resolve(ref ikvmInternalCallerID, "ikvm.internal.CallerID");

        Type JavaLangClass => Resolve(ref javaLangClass, "java.lang.Class");

        Type JavaLangReflectMethod => Resolve(ref javaLangReflectMethod, "java.lang.reflect.Method");

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public object Init(Type type) => GetConstructor(ref init, typeof(Type)).Invoker(type);

        /// <summary>
        /// Invokes the 'forName' method.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="callerID"></param>
        /// <returns></returns>
        public object InvokeForName(string name, object callerID) => GetMethod(ref forName, nameof(forName), JavaLangClass, typeof(string), IkvmInternalCallerID).Invoker(name, callerID);

        /// <summary>
        /// Invokes the 'getMethod' method.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="name"></param>
        /// <param name="parameterTypes"></param>
        /// <param name="callerID"></param>
        /// <returns></returns>
        // not caller sensitive since JDK 24, which removed the security manager: the caller ID is not passed
        public object InvokeGetMethod(object self, string name, object[] parameterTypes, object callerID) => GetMethod(ref getMethod, nameof(getMethod), JavaLangReflectMethod, typeof(string), JavaLangClass.MakeArrayType()).Invoker(self, name, parameterTypes);

        /// <summary>
        /// Sets the value of the 'classData' field (JDK 25), the class data of a hidden class.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="value"></param>
        public void SetClassData(object self, object value) => GetField(ref classData, nameof(classData)).SetValue(self, value);

        /// <summary>
        /// Sets the value of the 'name' field (JDK 25), which caches the class's name.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="value"></param>
        public void SetName(object self, string value) => GetField(ref name, nameof(name)).SetValue(self, value);

    }

#endif

}
