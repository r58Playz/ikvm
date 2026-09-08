/*
  Bridge to the mono runtime's generational-body swap (mono_interp_replace_method_body, tiering.c).

  Why a shim here rather than a direct P/Invoke: managed P/Invoke resolves through the wasm build's
  generated pinvoke_tables[], keyed by native MODULE name, and 'ikvm' is a module the generator already
  scans (LibIkvm.cs has eight imports). libikvm and the mono runtime are linked into the same wasm
  module, so a plain extern declaration is resolved by the linker at link time -- no headers, no mono
  internals exposed here.

  Why WEAK: libikvm also ships as a real shared library on win/linux/osx, where IKVM runs on CoreCLR and
  no such symbol exists. A hard reference would break that link outright. An undefined weak function
  resolves to NULL on both ELF and wasm-ld, so the feature simply reports unavailable off-wasm instead
  of making libikvm unloadable.
*/
#include <stdint.h>

#if defined(__wasm__) || defined(__linux__) || defined(__APPLE__)
#define IKVM_WEAK __attribute__((weak))
#else
#define IKVM_WEAK
#endif

/* Defined in the mono runtime (src/mono/mono/mini/interp/tiering.c) when linked into the same module. */
IKVM_WEAK int mono_interp_replace_method_body(void *target, void *source);
IKVM_WEAK void mono_interp_mark_relink_pending(void *method, int pending);
IKVM_WEAK extern int32_t mono_interp_relink_replaced;
IKVM_WEAK extern int32_t mono_interp_relink_late;
IKVM_WEAK extern int32_t mono_interp_relink_untouched;
IKVM_WEAK extern int32_t mono_interp_relink_rejected;

/*
 * Replaces target's IL body with source's. Both are MonoMethod* obtained managed-side from
 * MethodInfo.MethodHandle.Value.
 *
 * Returns 1 replaced, 0 declined (already tiered or wasm-JITted), -1 bad shape, -2 unsupported runtime.
 * -2 is the CoreCLR answer and is not an error: the caller keeps generation 1.
 */
int IKVM_lazy_replace_body(void *target, void *source)
{
#if defined(__wasm__) || defined(__linux__) || defined(__APPLE__)
    if (!mono_interp_replace_method_body)
        return -2;
#endif
    return mono_interp_replace_method_body(target, source);
}

/*
 * Marks a method as awaiting an in-place body replacement, so the wasm JIT skips it meanwhile.
 * pending=0 clears. The caller must clear every mark it makes, on every outcome.
 */
void IKVM_lazy_mark_pending(void *method, int pending)
{
#if defined(__wasm__) || defined(__linux__) || defined(__APPLE__)
    if (!mono_interp_mark_relink_pending)
        return;
#endif
    mono_interp_mark_relink_pending(method, pending);
}

/* Whether the swap is available at all, so managed code can decline once instead of per method. */
int IKVM_lazy_replace_supported(void)
{
#if defined(__wasm__) || defined(__linux__) || defined(__APPLE__)
    return mono_interp_replace_method_body ? 1 : 0;
#else
    return 1;
#endif
}

/* 0=replaced 1=late 2=untouched 3=rejected; -1 if the counters are not linked in. */
int32_t IKVM_lazy_relink_stat(int which)
{
#if defined(__wasm__) || defined(__linux__) || defined(__APPLE__)
    if (!mono_interp_replace_method_body)
        return -1;
#endif
    switch (which) {
    case 0: return mono_interp_relink_replaced;
    case 1: return mono_interp_relink_late;
    case 2: return mono_interp_relink_untouched;
    case 3: return mono_interp_relink_rejected;
    default: return -1;
    }
}
