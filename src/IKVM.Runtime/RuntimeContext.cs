using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

using IKVM.CoreLib.Diagnostics;
using IKVM.CoreLib.Symbols;



#if IMPORTER
using IKVM.Tools.Importer;
#endif

#if EXPORTER
using IKVM.Tools.Exporter;
#endif

#if IMPORTER == false || EXPORTER == false
using IKVM.Runtime.StubGen;
#endif

namespace IKVM.Runtime
{

    /// <summary>
    /// Maintains services relevant to an instane of the IKVM runtime.
    /// </summary>
    class RuntimeContext
    {

        readonly RuntimeContextOptions options;
        readonly IDiagnosticHandler diagnostics;
        readonly ISymbolResolver resolver;
        readonly bool bootstrap;
        readonly ConcurrentDictionary<Type, object> singletons = new();

        Types types;
        CoreClasses javaBase;
        AttributeHelper attributeHelper;
        RuntimeClassLoaderFactory classLoaderFactory;
        RuntimeAssemblyClassLoaderFactory assemblyClassLoaderFactory;
        RuntimeManagedJavaTypeFactory managedJavaTypeFactory;
        RuntimeManagedByteCodeJavaTypeFactory managedByteCodeJavaTypeFactory;
        RuntimePrimitiveJavaTypeFactory primitiveJavaTypeFactory;
        RuntimeVerifierJavaTypeFactory verifierJavaTypeFactory;

#if IMPORTER == false && EXPORTER == false
        ExceptionHelper exceptionHelper;
#endif

#if IMPORTER == false || EXPORTER == false
        StubGenerator stubGenerator;
#endif

#if EXPORTER == false
        CodeEmitterFactory codeEmitterFactory;
        DynamicClassLoaderFactory dynamicClassLoaderFactory;
        ByteCodeHelperMethods byteCodeHelperMethods;
        Serialization serialization;
        InterlockedMethods interlockedMethods;
        CompilerFactory compilerFactory;
        MethodAnalyzerFactory methodAnalyzerFactory;
        MethodHandleUtil methodHandleUtil;
        Boxer boxer;
#endif

#if IMPORTER
        ProxyGenerator proxyGenerator;
#endif

#if IMPORTER || EXPORTER
        StaticCompiler staticCompiler;
        FakeTypes fakeTypes;
#endif

#if IMPORTER || EXPORTER

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        /// <param name="diagnostics"></param>
        /// <param name="bootstrap"></param>
        /// <param name="staticCompiler"></param>
        public RuntimeContext(RuntimeContextOptions options, IDiagnosticHandler diagnostics, ISymbolResolver resolver, bool bootstrap, StaticCompiler staticCompiler)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            this.bootstrap = bootstrap;
            this.staticCompiler = staticCompiler;
        }

#else

		/// <summary>
		/// Initializes a new instance.
		/// </summary>
		/// <param name="options"></param>
		/// <param name="diagnostics"></param>
		/// <param name="resolver"></param>
		/// <param name="bootstrap"></param>
		public RuntimeContext(RuntimeContextOptions options, IDiagnosticHandler diagnostics, ISymbolResolver resolver, bool bootstrap)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            this.bootstrap = bootstrap;
        }

#endif

        /// <summary>
        /// Gets the <see cref="IDiagnosticHandler"/> where events should be sent.
        /// </summary>
        public IDiagnosticHandler Diagnostics => diagnostics;

        /// <summary>
        /// Gets or creates a new instance in a thread safe manner.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="create"></param>
        /// <returns></returns>
        /// <remarks>
        /// <paramref name="create"/> takes the context as an argument rather than closing over it, so callers can pass
        /// a non-capturing (static) lambda. Roslyn caches those in a static field, which makes the initialized path
        /// allocation free. A closure would otherwise be allocated on every read of every one of these properties,
        /// including the overwhelmingly common case where the singleton already exists and <paramref name="create"/>
        /// is never invoked. These properties sit under hot paths such as <see cref="RuntimeJavaType.IsAssignableTo"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        T GetOrCreateSingleton<T>(ref T value, Func<RuntimeContext, T> create)
            where T : class
        {
            return Volatile.Read(ref value) ?? CreateSingleton(ref value, create);
        }

        /// <summary>
        /// Cold path of <see cref="GetOrCreateSingleton{T}(ref T, Func{RuntimeContext, T})"/>, kept out of line so the
        /// initialized path stays small enough to inline.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="create"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        T CreateSingleton<T>(ref T value, Func<RuntimeContext, T> create)
            where T : class
        {
            lock (this)
            {
                var v = Volatile.Read(ref value);
                if (v is null)
                    Volatile.Write(ref value, v = create(this));

                return v;
            }
        }

        /// <summary>
        /// Gets or creates an object of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="create"></param>
        /// <returns></returns>
        public T GetOrCreateSingleton<T>(Func<T> create) => (T)singletons.GetOrAdd(typeof(T), _ => create());

        /// <summary>
        /// Gets the <see cref="RuntimeContextOptions"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeContextOptions Options => options;

		/// <summary>
		/// Gets the <see cref="ISymbolResolver"/> associated with this instance of the runtime.
		/// </summary>
		public ISymbolResolver Resolver => resolver;

        /// <summary>
        /// Gets whether or not the runtime is running in bootstrap mode; that is, we are compiling the Java base assembly itself.
        /// </summary>
        public bool Bootstrap => bootstrap;

        /// <summary>
        /// Gets the <see cref="Types"/> associated with this instance of the runtime.
        /// </summary>
        public Types Types => GetOrCreateSingleton(ref types, static self => new Types(self));

        /// <summary>
        /// Gets the <see cref="CoreClasses"/> associated with this instance of the runtime.
        /// </summary>
        public CoreClasses JavaBase => GetOrCreateSingleton(ref javaBase, static self => new CoreClasses(self));

        /// <summary>
        /// Gets the <see cref="AttributeHelper"/> associated with this instance of the runtime.
        /// </summary>
        public AttributeHelper AttributeHelper => GetOrCreateSingleton(ref attributeHelper, static self => new AttributeHelper(self));

        /// <summary>
        /// Gets the <see cref="RuntimePrimitiveJavaTypeFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimePrimitiveJavaTypeFactory PrimitiveJavaTypeFactory => GetOrCreateSingleton(ref primitiveJavaTypeFactory, static self => new RuntimePrimitiveJavaTypeFactory(self));

        /// <summary>
        /// Gets the <see cref="RuntimeVerifierJavaTypeFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeVerifierJavaTypeFactory VerifierJavaTypeFactory => GetOrCreateSingleton(ref verifierJavaTypeFactory, static self => new RuntimeVerifierJavaTypeFactory(self));

#if IMPORTER == false || EXPORTER == false

        /// <summary>
        /// Gets the <see cref="StubGenerator"/> associated with this instance of the runtime.
        /// </summary>
        public StubGenerator StubGenerator => GetOrCreateSingleton(ref stubGenerator, static self => new StubGenerator(self));

#endif

#if EXPORTER == false

        /// <summary>
        /// Gets the <see cref="CodeEmitterFactory"/> associated with this instance of the runtime.
        /// </summary>
        public CodeEmitterFactory CodeEmitterFactory => GetOrCreateSingleton(ref codeEmitterFactory, static self => new CodeEmitterFactory(self));

        /// <summary>
        /// Gets the <see cref="DynamicClassLoaderFactory"/> associated with this instance of the runtime.
        /// </summary>
        public DynamicClassLoaderFactory DynamicClassLoaderFactory => GetOrCreateSingleton(ref dynamicClassLoaderFactory, static self => new DynamicClassLoaderFactory(self));

        /// <summary>
        /// Gets the <see cref="ByteCodeHelperMethods"/> associated with this instance of the runtime.
        /// </summary>
        public ByteCodeHelperMethods ByteCodeHelperMethods => GetOrCreateSingleton(ref byteCodeHelperMethods, static self => new ByteCodeHelperMethods(self));

        /// <summary>
        /// Gets the <see cref="Serialization"/> associated with this instance of the runtime.
        /// </summary>
        public Serialization Serialization => GetOrCreateSingleton(ref serialization, static self => new Serialization(self));

        /// <summary>
        /// Gets the <see cref="CompilerFactory"/> associated with this instance of the runtime.
        /// </summary>
        /// 
        public CompilerFactory CompilerFactory => GetOrCreateSingleton(ref compilerFactory, static self => new CompilerFactory(self, self.bootstrap));

        /// <summary>
        /// Gets the <see cref="InterlockedMethods"/> associated with this instance of the runtime.
        /// </summary>
        public InterlockedMethods InterlockedMethods => GetOrCreateSingleton(ref interlockedMethods, static self => new InterlockedMethods(self));

        /// <summary>
        /// Gets the <see cref="MethodAnalyzerFactory"/> associated with this instance of the runtime.
        /// </summary>
        public MethodAnalyzerFactory MethodAnalyzerFactory => GetOrCreateSingleton(ref methodAnalyzerFactory, static self => new MethodAnalyzerFactory(self));

        /// <summary>
        /// Gets the <see cref="MethodHandleUtil"/> associated with this instance of the runtime.
        /// </summary>
        public MethodHandleUtil MethodHandleUtil => GetOrCreateSingleton(ref methodHandleUtil, static self => new MethodHandleUtil(self));

        /// <summary>
        /// Gets the <see cref="Boxer"/> associated with this instance of the runtime.
        /// </summary>
        public Boxer Boxer => GetOrCreateSingleton(ref boxer, static self => new Boxer(self));

#endif

#if IMPORTER == false && EXPORTER == false

        /// <summary>
        /// Gets the <see cref="ExceptionHelper"/> associated with this instance of the runtime.
        /// </summary>
        public ExceptionHelper ExceptionHelper => GetOrCreateSingleton(ref exceptionHelper, static self => new ExceptionHelper(self));

#endif

        /// <summary>
        /// Gets the <see cref="RuntimeClassLoaderFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeClassLoaderFactory ClassLoaderFactory => GetOrCreateSingleton(ref classLoaderFactory, static self => new RuntimeClassLoaderFactory(self));

        /// <summary>
        /// Gets the <see cref="RuntimeAssemblyClassLoaderFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeAssemblyClassLoaderFactory AssemblyClassLoaderFactory => GetOrCreateSingleton(ref assemblyClassLoaderFactory, static self => new RuntimeAssemblyClassLoaderFactory(self));

        /// <summary>
        /// Gets the <see cref="RuntimeManagedJavaTypeFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeManagedJavaTypeFactory ManagedJavaTypeFactory => GetOrCreateSingleton(ref managedJavaTypeFactory, static self => new RuntimeManagedJavaTypeFactory(self));

        /// <summary>
        /// Gets the <see cref="RuntimeManagedJavaTypeFactory"/> associated with this instance of the runtime.
        /// </summary>
        public RuntimeManagedByteCodeJavaTypeFactory ManagedByteCodeJavaTypeFactory => GetOrCreateSingleton(ref managedByteCodeJavaTypeFactory, static self => new RuntimeManagedByteCodeJavaTypeFactory(self));

#if IMPORTER || EXPORTER

        /// <summary>
        /// Gets the <see cref="StaticCompiler"/> associated with this instance of the runtime.
        /// </summary>
        public StaticCompiler StaticCompiler => staticCompiler;

        /// <summary>
        /// Gets the <see cref="FakeTypes"/> associated with this instance of the runtime.
        /// </summary>
        public FakeTypes FakeTypes => GetOrCreateSingleton(ref fakeTypes, static self => new FakeTypes(self));

#endif

#if IMPORTER

        /// <summary>
        /// Gets the <see cref="ProxyGenerator"/> associated with this instance of the runtime.
        /// </summary>
        public ProxyGenerator ProxyGenerator => GetOrCreateSingleton(ref proxyGenerator, static self => new ProxyGenerator(self));

#endif

    }

}
