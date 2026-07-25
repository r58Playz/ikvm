#ifdef WIN32
#define NETEXPORT __declspec(dllexport)
#define NETCALL __stdcall
#else
#define NETEXPORT __attribute__((visibility("default")))
#define NETCALL
#endif

#ifdef __EMSCRIPTEN__
#include <emscripten/heap.h>
#endif

/*
 * Reports the maximum size, in bytes, that the WASM linear-memory heap can grow
 * to (the module's MAXIMUM_MEMORY). This is the honest ceiling behind the managed
 * heap and maps to java.lang.Runtime.maxMemory().
 *
 * Returns 0 on platforms where this concept does not apply (native desktop
 * builds, which also compile this file via the libikvm clangproj glob); the
 * managed caller falls back to another source in that case.
 */
NETEXPORT long long NETCALL IKVM_get_heap_max(void)
{
#ifdef __EMSCRIPTEN__
    return (long long)emscripten_get_heap_max();
#else
    return 0;
#endif
}

/*
 * Reports the current size, in bytes, of the WASM linear memory
 * (WebAssembly.Memory.buffer.byteLength). Returns 0 where not applicable.
 */
NETEXPORT long long NETCALL IKVM_get_heap_size(void)
{
#ifdef __EMSCRIPTEN__
    return (long long)emscripten_get_heap_size();
#else
    return 0;
#endif
}
