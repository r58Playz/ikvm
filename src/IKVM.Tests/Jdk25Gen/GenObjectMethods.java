import java.lang.classfile.ClassFile;
import java.lang.constant.ClassDesc;
import java.lang.constant.DirectMethodHandleDesc;
import java.lang.constant.DynamicCallSiteDesc;
import java.lang.constant.MethodHandleDesc;
import java.lang.constant.MethodTypeDesc;
import java.nio.file.Files;
import java.nio.file.Path;

import static java.lang.constant.ConstantDescs.*;

// Generates ikvm.tests.jdk25.ObjMethods, a plain class (java.lang.Record is not in the Java 8 class library) whose
// toString, hashCode and equals are invokedynamics bootstrapped by java.lang.runtime.ObjectMethods.bootstrap over its
// fields -- exactly what javac emits for a record (int a, String b, double c).
public class GenObjectMethods {

    public static void main(String[] args) throws Exception {
        var self = ClassDesc.of("ikvm.tests.jdk25.ObjMethods");
        var bsm = MethodHandleDesc.ofMethod(DirectMethodHandleDesc.Kind.STATIC, ClassDesc.of("java.lang.runtime.ObjectMethods"), "bootstrap",
            MethodTypeDesc.of(CD_Object, CD_MethodHandles_Lookup, CD_String, ClassDesc.of("java.lang.invoke.TypeDescriptor"), CD_Class, CD_String, CD_MethodHandle.arrayType()));
        var getA = MethodHandleDesc.ofField(DirectMethodHandleDesc.Kind.GETTER, self, "a", CD_int);
        var getB = MethodHandleDesc.ofField(DirectMethodHandleDesc.Kind.GETTER, self, "b", CD_String);
        var getC = MethodHandleDesc.ofField(DirectMethodHandleDesc.Kind.GETTER, self, "c", CD_double);
        var ctorType = MethodTypeDesc.of(CD_void, CD_int, CD_String, CD_double);

        var bytes = ClassFile.of().build(self, cb -> {
            cb.withFlags(ClassFile.ACC_PUBLIC | ClassFile.ACC_FINAL | ClassFile.ACC_SUPER);
            cb.withField("a", CD_int, ClassFile.ACC_PRIVATE | ClassFile.ACC_FINAL);
            cb.withField("b", CD_String, ClassFile.ACC_PRIVATE | ClassFile.ACC_FINAL);
            cb.withField("c", CD_double, ClassFile.ACC_PRIVATE | ClassFile.ACC_FINAL);
            cb.withMethodBody(INIT_NAME, ctorType, ClassFile.ACC_PUBLIC, b -> b
                .aload(0).invokespecial(CD_Object, INIT_NAME, MTD_void)
                .aload(0).iload(1).putfield(self, "a", CD_int)
                .aload(0).aload(2).putfield(self, "b", CD_String)
                .aload(0).dload(3).putfield(self, "c", CD_double)
                .return_());
            cb.withMethodBody("toString", MethodTypeDesc.of(CD_String), ClassFile.ACC_PUBLIC | ClassFile.ACC_FINAL, b -> b
                .aload(0)
                .invokedynamic(DynamicCallSiteDesc.of(bsm, "toString", MethodTypeDesc.of(CD_String, self), self, "a;b;c", getA, getB, getC))
                .areturn());
            cb.withMethodBody("hashCode", MethodTypeDesc.of(CD_int), ClassFile.ACC_PUBLIC | ClassFile.ACC_FINAL, b -> b
                .aload(0)
                .invokedynamic(DynamicCallSiteDesc.of(bsm, "hashCode", MethodTypeDesc.of(CD_int, self), self, "a;b;c", getA, getB, getC))
                .ireturn());
            cb.withMethodBody("equals", MethodTypeDesc.of(CD_boolean, CD_Object), ClassFile.ACC_PUBLIC | ClassFile.ACC_FINAL, b -> b
                .aload(0).aload(1)
                .invokedynamic(DynamicCallSiteDesc.of(bsm, "equals", MethodTypeDesc.of(CD_boolean, self, CD_Object), self, "a;b;c", getA, getB, getC))
                .ireturn());
        });

        var file = Path.of(args[0], "ikvm", "tests", "jdk25", "ObjMethods.class");
        Files.createDirectories(file.getParent());
        Files.write(file, bytes);
    }

}
