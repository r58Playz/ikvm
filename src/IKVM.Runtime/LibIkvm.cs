using System;
using System.IO;
using System.Runtime.InteropServices;

namespace IKVM.Runtime
{

#if FIRST_PASS == false && IMPORTER == FALSE && EXPORTER == false

    /// <summary>
    /// Holds a reference to the 'ikvm' native library. This class cannot depend on NativeLibrary as that class can
    /// potentially use libikvm on down-level platforms.
    /// </summary>
    internal class LibIkvm
    {

        /// <summary>
        /// Holds the externs to prevent initialization.
        /// </summary>
        static class Externs
        {

            /// <summary>
            /// Invokes the native 'IKVM_dl_open' function.
            /// </summary>
            /// <param name="path"></param>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern nint IKVM_dl_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

            /// <summary>
            /// Invokes the native 'IKVM_dl_sym' function.
            /// </summary>
            /// <param name="handle"></param>
            /// <param name="symbol"></param>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern nint IKVM_dl_sym(nint handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);

            /// <summary>
            /// Invokes the native 'IKVM_dl_close' function.
            /// </summary>
            /// <param name="handle"></param>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern void IKVM_dl_close(nint handle);

            /// <summary>
            /// Invokes the native 'IKVM_sig_get_size_sigaction' function.
            /// </summary>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static unsafe extern int IKVM_sig_get_size_sigaction();

            /// <summary>
            /// Invokes the native 'IKVM_sig_get_chld_action' function.
            /// </summary>
            /// <param name="sig"></param>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static unsafe extern int IKVM_sig_get_chld_action(void* sig);

            /// <summary>
            /// Invokes the native 'IKVM_sig_set_chld_action' function.
            /// </summary>
            /// <param name="sig"></param>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static unsafe extern int IKVM_sig_set_chld_action(void* sig);

            /// <summary>
            /// Invokes the native 'IKVM_get_heap_max' function.
            /// </summary>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern long IKVM_get_heap_max();

            /// <summary>
            /// Invokes the native 'IKVM_get_heap_size' function.
            /// </summary>
            /// <returns></returns>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern long IKVM_get_heap_size();

            /// <summary>
            /// Invokes the native 'IKVM_lazy_replace_body' function: installs <paramref name="source"/>'s IL
            /// as <paramref name="target"/>'s body and forces the interpreter to re-transform it.
            /// </summary>
            /// <remarks>
            /// Both arguments are MonoMethod*, taken from MethodInfo.MethodHandle.Value. Returns 1 replaced,
            /// 0 declined (already tiered up or already wasm-JITted), -1 an incompatible shape, -2 a runtime
            /// without the swap (CoreCLR) -- and -2 is not an error, the caller keeps generation 1.
            /// </remarks>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern int IKVM_lazy_replace_body(nint target, nint source);

            /// <summary>
            /// Invokes the native 'IKVM_lazy_replace_supported' function.
            /// </summary>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern int IKVM_lazy_replace_supported();

            /// <summary>
            /// Invokes the native 'IKVM_lazy_mark_pending' function.
            /// </summary>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern void IKVM_lazy_mark_pending(nint method, int pending);

            /// <summary>
            /// Invokes the native 'IKVM_lazy_relink_stat' function: 0=replaced 1=late 2=untouched 3=rejected.
            /// </summary>
            [DllImport("ikvm", SetLastError = false)]
            internal static extern int IKVM_lazy_relink_stat(int which);

        }

        /// <summary>
        /// Installs <paramref name="source"/>'s IL body on <paramref name="target"/>, in place, so callers
        /// reach the recompiled body with no indirection at all.
        /// </summary>
        /// <remarks>
        /// This is the whole point of the in-place path. Reaching a second-generation body through a cached
        /// delegate costs a static load, a branch, a Delegate::Invoke and a non-inlinable frame on EVERY
        /// call, forever -- measured at 3x what removing the dynamic stubs saved (+14.2% per frame in-game,
        /// R226). Swapping the body pays none of it.
        ///
        /// Never throws: every refusal is a return code, because a failed swap must degrade to the body
        /// that was already running rather than fail a method that worked.
        /// </remarks>
        internal static int ReplaceMethodBody(nint target, nint source)
        {
            try
            {
                return Externs.IKVM_lazy_replace_body(target, source);
            }
            catch
            {
                return -2;
            }
        }

        /// <summary>
        /// Whether the runtime under us can swap a method body at all, so the caller can decline once
        /// instead of once per method.
        /// </summary>
        /// <summary>
        /// Marks <paramref name="method"/> as awaiting an in-place body replacement, so the wasm JIT does
        /// not compile it from the first-generation body meanwhile. Pass false to clear.
        /// </summary>
        /// <remarks>
        /// EVERY mark must be cleared, on every outcome including refusal: a method left marked is a method
        /// the wasm JIT will never compile, which trades a bounded loss (it keeps generation 1) for an
        /// unbounded one (it never reaches the JIT tier at all).
        /// </remarks>
        internal static void MarkRelinkPending(nint method, bool pending)
        {
            try
            {
                Externs.IKVM_lazy_mark_pending(method, pending ? 1 : 0);
            }
            catch
            {
                // A runtime without the entry point simply does not have the race to avoid.
            }
        }

        internal static bool CanReplaceMethodBody
        {
            get
            {
                try
                {
                    return Externs.IKVM_lazy_replace_supported() == 1;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Reads one of the runtime-side swap counters: 0=replaced 1=late 2=untouched 3=rejected.
        /// </summary>
        internal static int RelinkStat(int which)
        {
            try
            {
                return Externs.IKVM_lazy_relink_stat(which);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Invokes the native 'LoadLibrary' function.
        /// </summary>
        /// <param name="lpLibFileName"></param>
        /// <returns></returns>
        [DllImport("kernel32", SetLastError = false)]
        static extern nint LoadLibrary(string lpLibFileName);

        /// <summary>
        /// Invokes the native 'FreeLibrary' function.
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        [DllImport("kernel32", SetLastError = false)]
        static extern nint FreeLibrary(nint handle);

        /// <summary>
        /// Implements the native OS load functionality for 'libikvm'.
        /// </summary>
        /// <param name="nameOrPath"></param>
        /// <returns></returns>
        static nint LoadImpl(string nameOrPath)
        {
#if NETFRAMEWORK
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return LoadLibrary(nameOrPath);
            }
            else
            {
                // Mono (Framework on non-Windows), just P/Invoke, we can remap with dllmap
                return Externs.IKVM_dl_open(nameOrPath);
            }
#else
            return System.Runtime.InteropServices.NativeLibrary.TryLoad(nameOrPath, typeof(LibIkvm).Assembly, null, out var h) ? h : 0;
#endif
        }

        /// <summary>
        /// Implements the native OS free functionality.
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        static void FreeImpl(nint handle)
        {
#if NETFRAMEWORK
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                FreeLibrary(handle);
            }
            else
            {
                // Mono (Framework on non-Windows), just P/Invoke, we can remap with dllmap
                // this should be a secondary reference, as the DllImports should own the primary
                Externs.IKVM_dl_close(handle);
            }
#else
            System.Runtime.InteropServices.NativeLibrary.Free(handle);
#endif
        }

        /// <summary>
        /// Gets the instance of libikvm.
        /// </summary>
        public static readonly LibIkvm Instance = new();

        /// <summary>
        /// Holds an internal handle to the 'ikvm' library.
        /// </summary>
        public nint Handle { get; private set; }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        LibIkvm()
        {
            if ((Handle = Load()) == 0)
                throw new InternalException("Could not load ikvm native library.");
        }

        /// <summary>
        /// Loads the 'ikvm' library.
        /// </summary>
        static nint Load()
        {
            // give native loader a chance to load the library
            if (LoadImpl("ikvm") is nint h1 and not 0)
                return h1;

            // start looking at assembly path, or path given by environmental variable
            var asml = typeof(NativeLibrary).Assembly.Location is string s && !string.IsNullOrEmpty(s) ? Path.GetDirectoryName(s) : null;
            var root = Environment.GetEnvironmentVariable("IKVM_LIBRARY_PATH") ?? asml ?? AppContext.BaseDirectory;

            // assembly possible loaded in memory: we have no available search path
            if (string.IsNullOrEmpty(root))
                return 0;

            // check in root directory
            if (Path.Combine(root, NativeLibrary.MapLibraryName("ikvm")) is string lib1)
                if (File.Exists(lib1) && LoadImpl(lib1) is nint h2 and not 0)
                    return h2;

            // scan supported RIDs for current platform
            foreach (var rid in RuntimeUtil.SupportedRuntimeIdentifiers)
                if (Path.Combine(root, "runtimes", rid, "native", NativeLibrary.MapLibraryName("ikvm")) is string lib2)
                    if (File.Exists(lib2) && LoadImpl(lib2) is nint h3 and not 0)
                        return h3;

            return 0;
        }

        /// <summary>
        /// Invokes the 'dl_open' function.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public nint dl_open(string path) => Externs.IKVM_dl_open(path);

        /// <summary>
        /// Invokes the 'dl_sym' function.
        /// </summary>
        /// <param name="handle"></param>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public nint dl_sym(nint handle, string symbol) => Externs.IKVM_dl_sym(handle, symbol);

        /// <summary>
        /// Invokes the 'dl_close' function.
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        public void dl_close(nint handle) => Externs.IKVM_dl_close(handle);

        /// <summary>
        /// Invokes the 'sig_get_size_sigaction' function.
        /// </summary>
        /// <returns></returns>
        public unsafe int sig_get_size_sigaction() => Externs.IKVM_sig_get_size_sigaction();

        /// <summary>
        /// Invokes the 'sig_get_chld_action' function.
        /// </summary>
        /// <param name="sig"></param>
        /// <returns></returns>
        public unsafe int sig_get_chld_action(void* sig) => Externs.IKVM_sig_get_chld_action(sig);

        /// <summary>
        /// Invokes the 'sig_set_chld_action' function.
        /// </summary>
        /// <param name="sig"></param>
        /// <returns></returns>
        public unsafe int sig_set_chld_action(void* sig) => Externs.IKVM_sig_set_chld_action(sig);

        /// <summary>
        /// Invokes the 'get_heap_max' function. On WASM this is the linear-memory ceiling
        /// (MAXIMUM_MEMORY), in bytes; returns 0 on platforms where it does not apply.
        /// </summary>
        /// <returns></returns>
        public long get_heap_max() => Externs.IKVM_get_heap_max();

        /// <summary>
        /// Invokes the 'get_heap_size' function. On WASM this is the current linear-memory
        /// size, in bytes; returns 0 on platforms where it does not apply.
        /// </summary>
        /// <returns></returns>
        public long get_heap_size() => Externs.IKVM_get_heap_size();

        /// <summary>
        /// Releases the instance.
        /// </summary>
        ~LibIkvm()
        {
            if (Handle != 0)
            {
                var h = Handle;
                Handle = 0;
                FreeImpl(h);
            }
        }

    }

#endif

}
