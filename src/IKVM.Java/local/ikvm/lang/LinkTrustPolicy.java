package ikvm.lang;

/**
 * Extension point for class loaders running under IKVM's lazy (OpenJDK-aligned) linking mode.
 *
 * When lazy linking is enabled (IKVM_LAZY_CLASS_LOADING), the runtime never invokes a user
 * class loader's loadClass while linking a class's constant pool or member signatures; references
 * to classes that are not yet loaded are instead resolved lazily at the first execution of the
 * referencing instruction, matching HotSpot's resolution timing (JVMS 5.4.3). Such lazily
 * resolved call sites carry a permanent indirection cost.
 *
 * A class loader may implement this interface to declare that eagerly loading certain class
 * names through it is unobservable: loading them runs no bytecode transformation, has no side
 * effects, and always yields the same class regardless of timing (for example names served
 * directly from statically compiled assemblies). For such names the runtime is allowed to
 * invoke loadClass eagerly at link time, producing a direct (full speed) binding.
 *
 * Implementations must be side-effect free and must not load or define classes.
 */
public interface LinkTrustPolicy
{

    /**
     * Returns true if the given class name (binary name, e.g. "com.google.common.base.Joiner")
     * may be loaded eagerly through this loader at link time without observable effects.
     */
    boolean isEagerLinkSafe(String name);

}
