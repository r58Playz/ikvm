#include <jni.h>

/**
* Dummy method to prevent dlsym from loading JNI_OnLoad from libiava.
*/
JNIEXPORT jint __libsunec_JNI_OnLoad(JavaVM* vm, void* reserved) {
    return JNI_VERSION_1_8;
}

/**
* Dummy method to prevent dlsym from loading JNI_OnUnload from libiava.
*/
JNIEXPORT void __libsunec_JNI_OnUnload(JavaVM* vm, void* reserved) {
    
}
