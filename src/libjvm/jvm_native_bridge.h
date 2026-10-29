/*
 * jvm_native_bridge.h
 *
 * Native libraries this program can't run itself but something it links can: an Android app's arm64-v8a libraries,
 * run by a binary translator in the same memory (android-wasm's native/ndk, on gex). The same idea as ART's
 * libnativebridge: the embedder installs a table of operations with JVM_SetNativeBridge, and JVM_LoadLibrary falls
 * back to it for a library the static registry doesn't have.
 *
 * Bridged library handles and method pointers are tagged with JVM_BRIDGE_TAG, which no handle of the static registry
 * (an address in low static data) and no function-table index has: IKVM.Runtime tells a bridged method by it, and
 * calls it through JVM_BridgeInvoke instead of calli.
 */
#ifndef JVM_NATIVE_BRIDGE_H
#define JVM_NATIVE_BRIDGE_H

#include <jni.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define JVM_BRIDGE_TAG ((uintptr_t)0x80000000u)

typedef struct JVMNativeBridge {
    /* Whether the bridge runs the library at `path`. */
    int (*is_supported)(const char *path);
    /* Load it: a library id (not zero), or NULL with the reason in `err`. */
    void *(*load)(const char *path, char *err, int errlen);
    /* Library `lib`'s function `sym`, a JNI method of the shorty given (the return type, then each parameter; L for
     * any reference): a method id, or NULL. */
    void *(*trampoline)(void *lib, const char *sym, const char *shorty);
    /* Run library `lib`'s JNI_OnLoad: its result, or -1 when it has none. */
    jint (*on_load)(void *lib);
    /* Call method `method`: the receiver or class, then each argument, in 8-byte `slots` (references as local
     * references, floats in the low 32 bits, doubles' bits). The result as a jlong (a float's or double's bits). */
    jlong (*invoke)(void *method, JNIEnv *env, const jlong *slots);
} JVMNativeBridge;

JNIEXPORT void JNICALL JVM_SetNativeBridge(const JVMNativeBridge *nb);

/* For IKVM.Runtime: library `lib`'s (a JVM_LoadLibrary handle with JVM_BRIDGE_TAG) method `sym`, tagged; NULL when it
 * has none. */
JNIEXPORT void *JNICALL JVM_BridgeTrampoline(void *lib, const char *sym, const char *shorty);

/* Run a bridged library's JNI_OnLoad: its result, or -1 when it has none. */
JNIEXPORT jint JNICALL JVM_BridgeOnLoad(void *lib);

/* Call a bridged method (a JVM_BridgeTrampoline result, or a function pointer the guest registered). */
JNIEXPORT jlong JNICALL JVM_BridgeInvoke(void *method, JNIEnv *env, const jlong *slots);

#ifdef __cplusplus
}
#endif

#endif
