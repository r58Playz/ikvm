package ikvm.tests.jdk25;

// Compiled by javac 25: nest members reach each other's private members directly (no access$NNN
// bridges), private methods are invoked with invokevirtual, and private interface methods with
// invokeinterface. Uses only Java 8 APIs.
public class Nestmates {

    private int outerField = 1;

    private static int outerStatic = 2;

    private int outerMethod() {
        return 3;
    }

    private Nestmates() {
    }

    static class Inner {

        private int innerField = 10;

        private Inner() {
        }

        private int innerMethod() {
            return 20;
        }

        // inner -> outer: private field, private method, private static, private constructor
        int readOuter() {
            Nestmates n = new Nestmates();
            n.outerField += 4;
            return n.outerField + n.outerMethod() + outerStatic;
        }

    }

    static class Sibling {

        // sibling -> sibling: private constructor, field and method
        int readInner() {
            Inner i = new Inner();
            i.innerField++;
            return i.innerField + i.innerMethod();
        }

    }

    interface WithPrivate {

        private int secret() {
            return 100;
        }

        private static int staticSecret() {
            return 200;
        }

        default int open() {
            // invokeinterface on a private interface method (javac 11+)
            return secret() + staticSecret();
        }

    }

    static class Impl implements WithPrivate {
    }

    public static String run() {
        Nestmates n = new Nestmates();
        Inner inner = new Inner();
        // outer -> inner: private field and method
        int outerToInner = inner.innerField + inner.innerMethod();
        // private method of this class through invokevirtual
        int self = n.outerMethod();
        // StringBuilder rather than '+', which javac 25 compiles to an indy StringConcatFactory call
        return new StringBuilder()
            .append(outerToInner).append(',')
            .append(inner.readOuter()).append(',')
            .append(new Sibling().readInner()).append(',')
            .append(self).append(',')
            .append(new Impl().open())
            .toString();
    }

}
