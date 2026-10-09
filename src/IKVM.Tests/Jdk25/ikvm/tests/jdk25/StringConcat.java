package ikvm.tests.jdk25;

// Compiled by javac 25, every '+' on strings below is an invokedynamic bootstrapped by
// StringConcatFactory.makeConcatWithConstants. Uses only Java 8 APIs.
public class StringConcat {

    public static String run() {
        int i = 42;
        long l = -7L;
        char c = 'x';
        boolean b = true;
        byte by = 3;
        short sh = -4;
        float f = 1.5f;
        double d = 0.1 + 0.2;
        Object nul = null;
        String s = "str";
        String ns = null;
        int[] arr = null;
        Object o = new Object() {
            @Override
            public String toString() {
                return "obj";
            }
        };

        // more than four pieces: every primitive type, null object and string, arrays, toString()
        String all = "i=" + i + " l=" + l + " c=" + c + " b=" + b + " by=" + by + " sh=" + sh + " f=" + f + " d=" + d + " nul=" + nul + " s=" + s + " ns=" + ns + " o=" + o + " arr=" + arr;

        // literal text containing the recipe tags is passed as a constant
        String tags = "<\u0001" + i + "\u0002>";

        // arguments are converted to strings left to right, after all of them were evaluated
        StringBuilder order = new StringBuilder();
        Object first = new Object() {
            @Override
            public String toString() {
                order.append('1');
                return "A";
            }
        };
        Object second = new Object() {
            @Override
            public String toString() {
                order.append('2');
                return "B";
            }
        };
        String ordered = "" + first + second;

        // a single argument and nothing else
        String one = "" + i;

        return all + "|" + tags.length() + tags.charAt(1) + tags.charAt(4) + "|" + ordered + order + "|" + one;
    }

}
