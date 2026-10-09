package ikvm.tests.jdk25;

// Compiled by javac 25, the pattern switches below are invokedynamics bootstrapped by
// java.lang.runtime.SwitchBootstraps.typeSwitch and enumSwitch. Uses only Java 8 APIs.
public class Switches {

    sealed interface Shape permits Circle, Square, Rect {
    }

    static final class Circle implements Shape {
        final int r;

        Circle(int r) {
            this.r = r;
        }
    }

    static final class Square implements Shape {
        final int s;

        Square(int s) {
            this.s = s;
        }
    }

    static final class Rect implements Shape {
        final int w, h;

        Rect(int w, int h) {
            this.w = w;
            this.h = h;
        }
    }

    enum Color {
        RED, GREEN, BLUE
    }

    // class labels, guards (which restart the switch past the failed case), null and array patterns
    static String describe(Object o) {
        return switch (o) {
            case null -> "null";
            case String s when s.isEmpty() -> "empty";
            case String s -> "string" + s.length();
            case Integer i when i > 10 -> "big";
            case Integer i -> "int";
            case int[] a -> "ints" + a.length;
            case Shape sh -> "shape" + area(sh);
            default -> "other";
        };
    }

    // exhaustive over a sealed hierarchy, no default
    static int area(Shape s) {
        return switch (s) {
            case Circle c -> 3 * c.r * c.r;
            case Square q -> q.s * q.s;
            case Rect r -> r.w * r.h;
        };
    }

    // String constant labels mixed with a pattern
    static String strings(String s) {
        return switch (s) {
            case "a" -> "A";
            case "b" -> "B";
            case String t when t.length() > 3 -> "long";
            case String t -> "short";
        };
    }

    // Integer constant labels against a boxed Integer and a Character selector
    static String integers(Integer x) {
        return switch (x) {
            case 1 -> "one";
            case 2 -> "two";
            case Integer i when i > 100 -> "huge";
            case Integer i -> "n";
        };
    }

    static String chars(Character c) {
        return switch (c) {
            case 'a' -> "a";
            case Character d when Character.isDigit(d) -> "digit";
            case Character d -> "char";
        };
    }

    // enum constant labels with a pattern
    static String colors(Color c) {
        return switch (c) {
            case RED -> "red";
            case Color x when x == Color.GREEN -> "green";
            case Color x -> "other";
        };
    }

    public static String run() {
        StringBuilder b = new StringBuilder();
        Object[] values = { null, "", "abc", 5, 50, new int[3], new Circle(2), new Square(3), new Rect(2, 5), 1.5 };
        for (Object v : values)
            b.append(describe(v)).append(',');
        b.append('|');
        for (String s : new String[] { "a", "b", "abcd", "c" })
            b.append(strings(s)).append(',');
        b.append('|');
        for (int i : new int[] { 1, 2, 3, 500 })
            b.append(integers(i)).append(',');
        b.append('|');
        for (char c : new char[] { 'a', '7', 'z' })
            b.append(chars(c)).append(',');
        b.append('|');
        for (Color c : Color.values())
            b.append(colors(c)).append(',');
        return b.toString();
    }

}
