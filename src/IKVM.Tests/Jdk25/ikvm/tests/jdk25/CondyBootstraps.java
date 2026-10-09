package ikvm.tests.jdk25;

import java.lang.invoke.MethodHandles;

// Bootstrap method for the dynamic constants of the generated ikvm.tests.jdk25.Condy class (see Jdk25Gen/GenCondy.java).
public class CondyBootstraps {

    static int calls;

    public static Object constant(MethodHandles.Lookup lookup, String name, Class<?> type) {
        calls++;
        if (type == String.class)
            return name.toUpperCase();
        if (type == int.class)
            return 42;
        if (type == long.class)
            return 1L << 40;
        return null;
    }

    public static int calls() {
        return calls;
    }

}
