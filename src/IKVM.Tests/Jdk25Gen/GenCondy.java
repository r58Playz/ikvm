import java.lang.classfile.ClassFile;
import java.lang.classfile.CodeBuilder;
import java.lang.constant.ClassDesc;
import java.lang.constant.ConstantDesc;
import java.lang.constant.ConstantDescs;
import java.lang.constant.DirectMethodHandleDesc;
import java.lang.constant.DynamicConstantDesc;
import java.lang.constant.MethodHandleDesc;
import java.lang.constant.MethodTypeDesc;
import java.nio.file.Files;
import java.nio.file.Path;

import static java.lang.constant.ConstantDescs.*;

// Generates ikvm.tests.jdk25.Condy, whose run() loads dynamic constants (CONSTANT_Dynamic, JDK 11), which javac does
// not emit for ordinary code: a custom bootstrap method returning a String, an int and a long (ldc2_w), and the
// java.lang.invoke.ConstantBootstraps nullConstant, primitiveClass, enumConstant and getStaticFinal. The String
// constant is loaded twice, and the custom bootstrap's call count is appended last: a constant is resolved once.
// Uses only Java 8 APIs at run time apart from ConstantBootstraps, which IKVM handles without the class.
public class GenCondy {

    static final ClassDesc CD_StringBuilder = ClassDesc.of("java.lang.StringBuilder");

    public static void main(String[] args) throws Exception {
        var self = ClassDesc.of("ikvm.tests.jdk25.Condy");
        var boots = ClassDesc.of("ikvm.tests.jdk25.CondyBootstraps");
        var bsm = MethodHandleDesc.ofMethod(DirectMethodHandleDesc.Kind.STATIC, boots, "constant", MethodTypeDesc.of(CD_Object, CD_MethodHandles_Lookup, CD_String, CD_Class));

        ConstantDesc[] constants = {
            DynamicConstantDesc.ofNamed(bsm, "hello", CD_String),
            DynamicConstantDesc.ofNamed(bsm, "answer", CD_int),
            DynamicConstantDesc.ofNamed(bsm, "big", CD_long),
            DynamicConstantDesc.ofNamed(BSM_NULL_CONSTANT, DEFAULT_NAME, CD_String),
            DynamicConstantDesc.ofNamed(BSM_PRIMITIVE_CLASS, "J", CD_Class),
            DynamicConstantDesc.ofNamed(BSM_ENUM_CONSTANT, "SECONDS", ClassDesc.of("java.util.concurrent.TimeUnit")),
            DynamicConstantDesc.ofNamed(BSM_GET_STATIC_FINAL, "MAX_VALUE", CD_int, ClassDesc.of("java.lang.Integer")),
            DynamicConstantDesc.ofNamed(bsm, "hello", CD_String),
        };

        var bytes = ClassFile.of().build(self, cb -> {
            cb.withFlags(ClassFile.ACC_PUBLIC | ClassFile.ACC_SUPER);
            cb.withMethodBody(INIT_NAME, MTD_void, ClassFile.ACC_PUBLIC, b -> b.aload(0).invokespecial(CD_Object, INIT_NAME, MTD_void).return_());
            cb.withMethodBody("run", MethodTypeDesc.of(CD_String), ClassFile.ACC_PUBLIC | ClassFile.ACC_STATIC, b -> {
                b.new_(CD_StringBuilder).dup().invokespecial(CD_StringBuilder, INIT_NAME, MTD_void);
                for (var c : constants) {
                    var type = ((DynamicConstantDesc<?>) c).constantType();
                    b.ldc(c);
                    append(b, type.isPrimitive() ? type : CD_Object);
                    b.bipush(',');
                    append(b, CD_char);
                }
                b.invokestatic(boots, "calls", MethodTypeDesc.of(CD_int));
                append(b, CD_int);
                b.invokevirtual(CD_StringBuilder, "toString", MethodTypeDesc.of(CD_String)).areturn();
            });
        });

        var file = Path.of(args[0], "ikvm", "tests", "jdk25", "Condy.class");
        Files.createDirectories(file.getParent());
        Files.write(file, bytes);
    }

    static void append(CodeBuilder b, ClassDesc type) {
        b.invokevirtual(CD_StringBuilder, "append", MethodTypeDesc.of(CD_StringBuilder, type));
    }

}
