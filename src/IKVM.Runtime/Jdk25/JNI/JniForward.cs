namespace IKVM.Runtime.JNI
{

#if FIRST_PASS == false && IMPORTER == false && EXPORTER == false

    /// <summary>
    /// A call of the JNI implementation of a native method, made by a native the runtime implements only in part (for
    /// the paths of IKVM's virtual file system) for the rest. <see cref="Enter"/>, then call the function
    /// <see cref="Resolve"/> gives with <see cref="Env"/> and the local references <see cref="Ref"/> makes, then
    /// <see cref="Leave"/>, which throws the exception the function threw, if any.
    /// </summary>
    struct JniForward
    {

        JNIFrame frame;

        /// <summary>
        /// Gets the JNIEnv* to pass.
        /// </summary>
        public nint Env { get; private set; }

        /// <summary>
        /// Enters a JNI frame for a call from the class of the caller ID.
        /// </summary>
        /// <param name="callerID"></param>
        /// <returns></returns>
        public static JniForward Enter(global::ikvm.@internal.CallerID callerID)
        {
            var c = new JniForward();
            c.Env = c.frame.Enter(callerID);
            return c;
        }

        /// <summary>
        /// Makes a local reference to pass.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public readonly nint Ref(object obj) => frame.MakeLocalRef(obj);

        /// <summary>
        /// Gets the object of a local reference returned.
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public readonly object Unwrap(nint p) => frame.UnwrapLocalRef(p);

        /// <summary>
        /// Leaves the JNI frame, throwing the exception the call threw.
        /// </summary>
        public readonly void Leave() => frame.Leave();

        /// <summary>
        /// Gets the address of the JNI function that implements a native method, which the native libraries of the
        /// caller's class loader have, finding it the first time.
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="callerID"></param>
        /// <param name="clazz">the class, in internal form</param>
        /// <param name="name"></param>
        /// <param name="sig"></param>
        /// <returns></returns>
        public static nint Resolve(ref nint cache, global::ikvm.@internal.CallerID callerID, string clazz, string name, string sig)
        {
            var p = cache;
            if (p == 0)
                cache = p = JNIFrame.GetFuncPtr(callerID, clazz, name, sig);

            return p;
        }

    }

#endif

}
