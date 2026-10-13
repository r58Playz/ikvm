#include <jni.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct JVMInvokeInterface {
    jint (*JNI_GetDefaultJavaVMInitArgs)(void *);
    jint (*JNI_GetCreatedJavaVMs)(JavaVM **vmBuf, jsize bufLen, jsize *nVMs);
    jint (*JNI_CreateJavaVM)(JavaVM **p_vm, void **p_env, void *vm_args);
    void (*JVM_ThrowException)(const char*, const char*);
    void* (*JVM_GetThreadInterruptEvent)();
    jint (*JVM_ActiveProcessorCount)();
    jint (*JVM_IHashCode)(JNIEnv *pEnv, jobject handle);
    void (*JVM_ArrayCopy)(JNIEnv *pEnv, jclass ignored, jobject src, jint src_pos, jobject dst, jint dst_pst, jint length);
    jobject (*JVM_InitProperties)(JNIEnv *pEnv, jobject props);
    void* (*JVM_RawMonitorCreate)();
    void (*JVM_RawMonitorDestroy)(void *mon);
    jint (*JVM_RawMonitorEnter)(void *mon);
    void (*JVM_RawMonitorExit)(void *mon);
    void (*JVM_CopySwapMemory)(JNIEnv *env, jobject srcObj, jlong srcOffset, jobject dstObj, jlong dstOffset, jlong size, jlong elemSize);
} JVMInvokeInterface;

extern JVMInvokeInterface *jvmii;

/* JDK 25's JVM_LoadLibrary says whether to throw when the library does not load.
 *
 * Except in the wasm build, which keeps the one parameter of earlier JDKs and never throws (which is what the runtime
 * asks of it): everything is linked into one module there, LWJGL's static loader declares it with one parameter
 * (lwjgl3.patch), and a wasm call must match its callee's signature exactly. The build (build-ikvm-native.sh)
 * defines JVM_LoadLibrary as a function-like macro that drops the second argument from jvm.h's declaration and from the
 * JDK's callers; the definition names the function in parentheses so that the macro leaves it alone. */
#if defined(__EMSCRIPTEN__)
#define IKVM_LOADLIBRARY_PARAMS const char* name
#define IKVM_LOADLIBRARY_THROWS JNI_FALSE
#else
#define IKVM_LOADLIBRARY_PARAMS const char* name, jboolean throwException
#define IKVM_LOADLIBRARY_THROWS throwException
#endif

JNIEXPORT void JNICALL JVM_Init(JVMInvokeInterface *p_jvmii);

JNIEXPORT void JNICALL JVM_ThrowException(const char *name, const char *msg);

#ifdef __cplusplus
}
#endif
