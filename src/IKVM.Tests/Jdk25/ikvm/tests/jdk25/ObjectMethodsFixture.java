package ikvm.tests.jdk25;

// Exercises the ObjectMethods.bootstrap invokedynamics of the generated ObjMethods class (Jdk25Gen/GenObjectMethods.java).
public class ObjectMethodsFixture {

    public static String run() {
        ObjMethods x = new ObjMethods(1, "one", 1.5);
        ObjMethods y = new ObjMethods(1, "one", 1.5);
        ObjMethods negZero = new ObjMethods(2, null, -0.0);
        ObjMethods posZero = new ObjMethods(2, null, 0.0);
        ObjMethods nan1 = new ObjMethods(3, "n", Double.NaN);
        ObjMethods nan2 = new ObjMethods(3, "n", Double.NaN);
        StringBuilder b = new StringBuilder();
        b.append(x).append('|').append(negZero).append('|');
        b.append(x.equals(y)).append(',').append(x.equals(x)).append(',').append(x.equals(negZero)).append(',');
        b.append(negZero.equals(posZero)).append(',').append(nan1.equals(nan2)).append(',');
        b.append(x.equals(null)).append(',').append(x.equals("one")).append('|');
        b.append(x.hashCode() == y.hashCode()).append(',').append(x.hashCode()).append(',').append(negZero.hashCode());
        return b.toString();
    }

}
