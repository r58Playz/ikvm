using System;

namespace IKVM.Runtime.Accessors.Sun.Nio.Fs
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the static fields of the 'sun.nio.fs.UnixConstants' type.
    /// </summary>
    /// <remarks>
    /// These values are platform specific (the emscripten runtime overrides them, e.g. ENOENT is not the usual
    /// glibc value), so they are read from the type rather than hard-coded.
    /// </remarks>
    internal sealed class UnixConstantsAccessor : Accessor<object>
    {

        FieldAccessor<int> enoent;
        FieldAccessor<int> eacces;
        FieldAccessor<int> w_ok;
        FieldAccessor<int> s_ifdir;
        FieldAccessor<int> s_ifreg;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public UnixConstantsAccessor(AccessorTypeResolver resolver) :
            base(resolver, "sun.nio.fs.UnixConstants")
        {

        }

        public int ENOENT => GetField(ref enoent, "ENOENT").GetValue();

        public int EACCES => GetField(ref eacces, "EACCES").GetValue();

        public int W_OK => GetField(ref w_ok, "W_OK").GetValue();

        public int S_IFDIR => GetField(ref s_ifdir, "S_IFDIR").GetValue();

        public int S_IFREG => GetField(ref s_ifreg, "S_IFREG").GetValue();

    }

#endif

}
