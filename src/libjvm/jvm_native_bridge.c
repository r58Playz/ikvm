/*
 * jvm_native_bridge.c: the native-bridge table (jvm_native_bridge.h) and what IKVM.Runtime calls of it.
 */
#ifdef __EMSCRIPTEN__

#include "jvm_native_bridge.h"

static const JVMNativeBridge *bridge;

const JVMNativeBridge *jvm_native_bridge(void)
{
    return bridge;
}

JNIEXPORT void JNICALL JVM_SetNativeBridge(const JVMNativeBridge *nb)
{
    bridge = nb;
}

JNIEXPORT void *JNICALL JVM_BridgeTrampoline(void *lib, const char *sym, const char *shorty)
{
    if (!bridge || !((uintptr_t)lib & JVM_BRIDGE_TAG))
        return NULL;
    void *m = bridge->trampoline((void *)((uintptr_t)lib & ~JVM_BRIDGE_TAG), sym, shorty);
    return m ? (void *)((uintptr_t)m | JVM_BRIDGE_TAG) : NULL;
}

JNIEXPORT jint JNICALL JVM_BridgeOnLoad(void *lib)
{
    if (!bridge || !((uintptr_t)lib & JVM_BRIDGE_TAG))
        return -1;
    return bridge->on_load((void *)((uintptr_t)lib & ~JVM_BRIDGE_TAG));
}

JNIEXPORT jlong JNICALL JVM_BridgeInvoke(void *method, JNIEnv *env, const jlong *slots)
{
    return bridge->invoke(method, env, slots);
}

#endif /* __EMSCRIPTEN__ */
