package ikvm.tests.jdk25;

import java.io.IOException;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;
import java.util.List;
import java.util.function.Supplier;

// The class file features a class's stub carries (StubGeneratorTests compares the stubs of these classes with their
// class files): nested, local and anonymous classes and their nest, a sealed hierarchy, a record, interface methods
// with bodies, visible, invisible, parameter and type annotations, generic signatures, constants, a static
// initializer, throws clauses, varargs and native methods.
@StubFixture.Visible(value = "class", nums = { 3, 4 })
@StubFixture.Invisible
public class StubFixture<T extends Comparable<T>> {

    @Retention(RetentionPolicy.RUNTIME)
    public @interface Visible {
        String value() default "v";
        int[] nums() default { 1, 2 };
        ElementType kind() default ElementType.TYPE;
        Class<?> type() default Object.class;
    }

    @Retention(RetentionPolicy.CLASS)
    public @interface Invisible {
    }

    @Retention(RetentionPolicy.RUNTIME)
    @Target(ElementType.TYPE_USE)
    public @interface TypeUse {
    }

    public sealed interface Shape permits Circle, Square {
        double area();
    }

    public record Circle(@Visible("radius") @Invisible double radius, @TypeUse List<@TypeUse String> tags) implements Shape {
        public double area() {
            return Math.PI * radius * radius;
        }
    }

    public static final class Square implements Shape {
        private final double side;

        public Square(double side) {
            this.side = side;
        }

        public double area() {
            return side * side;
        }
    }

    public interface WithBodies {
        int value();

        default int twice() {
            return helper() * 2;
        }

        static int three() {
            return 3;
        }

        private int helper() {
            return value();
        }
    }

    public static final int INT_CONSTANT = 42;
    public static final long LONG_CONSTANT = 1L << 40;
    public static final String STRING_CONSTANT = "constant";
    public static final List<String> NOT_A_CONSTANT;

    static {
        NOT_A_CONSTANT = List.of("a", "b");
    }

    @Visible("field")
    @TypeUse
    protected T value;

    private int secret = 7;

    public class Inner {
        public int peek() {
            return secret;
        }

        // a nestmate's private field, reflectively, without setAccessible
        public int peekReflectively() throws ReflectiveOperationException {
            return StubFixture.class.getDeclaredField("secret").getInt(StubFixture.this);
        }
    }

    @Deprecated
    @Visible(value = "method", kind = ElementType.METHOD, type = String.class)
    @Invisible
    public <U extends T> List<U> generic(@Visible("param") U first, @Invisible @TypeUse U... rest) throws IOException, IllegalStateException {
        return List.of(first);
    }

    public native long nativeMethod(int a, long b, double c);

    public Supplier<String> local() {
        class Local implements Supplier<String> {
            public String get() {
                return "local";
            }
        }

        return new Local();
    }

    public Supplier<String> anonymous() {
        return new Supplier<String>() {
            public String get() {
                return "anonymous";
            }
        };
    }

    public static String run() throws ReflectiveOperationException {
        var fixture = new StubFixture<String>();
        WithBodies withBodies = () -> 5;
        return fixture.new Inner().peek() + "," + fixture.new Inner().peekReflectively() + "," + fixture.local().get() + "," + fixture.anonymous().get() + "," + withBodies.twice() + "," + WithBodies.three() + "," + (int)new Square(2).area();
    }

}
