import java.lang.classfile.ClassFile;
import java.lang.classfile.attribute.PermittedSubclassesAttribute;
import java.lang.constant.ClassDesc;
import java.nio.file.Files;
import java.nio.file.Path;

import static java.lang.constant.ConstantDescs.*;

// Generates a sealed class and interface (JDK 17) that permit only SealedAllowed, and SealedRogue and
// SealedRogueImpl, which extend or implement them without being permitted: loading those must fail with
// IncompatibleClassChangeError (JVMS 5.3.5). javac would refuse to compile them.
public class GenSealed {

    public static void main(String[] args) throws Exception {
        var base = ClassDesc.of("ikvm.tests.jdk25.SealedBase");
        var iface = ClassDesc.of("ikvm.tests.jdk25.SealedIface");
        var allowed = ClassDesc.of("ikvm.tests.jdk25.SealedAllowed");
        var rogue = ClassDesc.of("ikvm.tests.jdk25.SealedRogue");
        var rogueImpl = ClassDesc.of("ikvm.tests.jdk25.SealedRogueImpl");

        write(args[0], base, ClassFile.of().build(base, cb -> {
            cb.withFlags(ClassFile.ACC_PUBLIC | ClassFile.ACC_ABSTRACT | ClassFile.ACC_SUPER);
            cb.with(PermittedSubclassesAttribute.ofSymbols(allowed));
            cb.withMethodBody(INIT_NAME, MTD_void, ClassFile.ACC_PROTECTED, b -> b.aload(0).invokespecial(CD_Object, INIT_NAME, MTD_void).return_());
        }));
        write(args[0], iface, ClassFile.of().build(iface, cb -> {
            cb.withFlags(ClassFile.ACC_PUBLIC | ClassFile.ACC_INTERFACE | ClassFile.ACC_ABSTRACT);
            cb.with(PermittedSubclassesAttribute.ofSymbols(allowed));
        }));
        write(args[0], allowed, subclass(allowed, base, iface));
        write(args[0], rogue, subclass(rogue, base, null));
        write(args[0], rogueImpl, subclass(rogueImpl, CD_Object, iface));
    }

    static byte[] subclass(ClassDesc self, ClassDesc superclass, ClassDesc iface) {
        return ClassFile.of().build(self, cb -> {
            cb.withFlags(ClassFile.ACC_PUBLIC | ClassFile.ACC_FINAL | ClassFile.ACC_SUPER);
            cb.withSuperclass(superclass);
            if (iface != null)
                cb.withInterfaceSymbols(iface);
            cb.withMethodBody(INIT_NAME, MTD_void, ClassFile.ACC_PUBLIC, b -> b.aload(0).invokespecial(superclass, INIT_NAME, MTD_void).return_());
        });
    }

    static void write(String root, ClassDesc desc, byte[] bytes) throws Exception {
        var file = Path.of(root, desc.packageName().replace('.', '/'), desc.displayName() + ".class");
        Files.createDirectories(file.getParent());
        Files.write(file, bytes);
    }

}
