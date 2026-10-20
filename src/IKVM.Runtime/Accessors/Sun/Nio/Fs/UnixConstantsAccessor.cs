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
        FieldAccessor<int> enotdir;
        FieldAccessor<int> eisdir;
        FieldAccessor<int> eexist;
        FieldAccessor<int> erofs;
        FieldAccessor<int> enxio;
        FieldAccessor<int> einval;
        FieldAccessor<int> exdev;
        FieldAccessor<int> x_ok;
        FieldAccessor<int> o_wronly;
        FieldAccessor<int> o_rdwr;
        FieldAccessor<int> o_creat;
        FieldAccessor<int> o_excl;
        FieldAccessor<int> o_trunc;
        FieldAccessor<int> at_fdcwd;
        FieldAccessor<int> at_removedir;

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

        public int ENOTDIR => GetField(ref enotdir, "ENOTDIR").GetValue();

        public int EISDIR => GetField(ref eisdir, "EISDIR").GetValue();

        public int EEXIST => GetField(ref eexist, "EEXIST").GetValue();

        public int EROFS => GetField(ref erofs, "EROFS").GetValue();

        public int ENXIO => GetField(ref enxio, "ENXIO").GetValue();

        public int EINVAL => GetField(ref einval, "EINVAL").GetValue();

        public int EXDEV => GetField(ref exdev, "EXDEV").GetValue();

        public int X_OK => GetField(ref x_ok, "X_OK").GetValue();

        public int O_WRONLY => GetField(ref o_wronly, "O_WRONLY").GetValue();

        public int O_RDWR => GetField(ref o_rdwr, "O_RDWR").GetValue();

        public int O_CREAT => GetField(ref o_creat, "O_CREAT").GetValue();

        public int O_EXCL => GetField(ref o_excl, "O_EXCL").GetValue();

        public int O_TRUNC => GetField(ref o_trunc, "O_TRUNC").GetValue();

        public int AT_FDCWD => GetField(ref at_fdcwd, "AT_FDCWD").GetValue();

        public int AT_REMOVEDIR => GetField(ref at_removedir, "AT_REMOVEDIR").GetValue();

    }

#endif

}
