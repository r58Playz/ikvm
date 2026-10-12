#include <jni.h>

/* jdk.management's libmanagement_ext, whose natives the runtime implements (Jdk25\Java\Externs\com\sun\management):
   loading it only has to succeed. The JDK's own gets HotSpot's management interface (JVM_GetManagement) here, which
   IKVM's libjvm does not have. */
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    return JNI_VERSION_1_8;
}
