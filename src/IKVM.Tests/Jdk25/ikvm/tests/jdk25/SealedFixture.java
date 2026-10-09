package ikvm.tests.jdk25;

// Loads the generated sealed hierarchy (Jdk25Gen/GenSealed.java): a permitted subclass loads, a class extending
// or implementing a sealed type that does not permit it fails with IncompatibleClassChangeError.
public class SealedFixture {

    static String load(String name) {
        try {
            Class.forName("ikvm.tests.jdk25." + name, false, SealedFixture.class.getClassLoader());
            return "ok";
        } catch (IncompatibleClassChangeError e) {
            return "icce";
        } catch (Throwable t) {
            return t.getClass().getName();
        }
    }

    public static String run() {
        return load("SealedAllowed") + "," + load("SealedRogue") + "," + load("SealedRogueImpl");
    }

}
