using System;

namespace IKVM.Runtime.Accessors.Sun.Nio.Fs
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the fields of the 'sun.nio.fs.UnixFileAttributes' type that are populated by
    /// the (normally native) stat/lstat calls.
    /// </summary>
    internal sealed class UnixFileAttributesAccessor : Accessor<object>
    {

        FieldAccessor<object, int> st_mode;
        FieldAccessor<object, long> st_ino;
        FieldAccessor<object, long> st_dev;
        FieldAccessor<object, int> st_nlink;
        FieldAccessor<object, long> st_size;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public UnixFileAttributesAccessor(AccessorTypeResolver resolver) :
            base(resolver, "sun.nio.fs.UnixFileAttributes")
        {

        }

        public void SetStMode(object self, int value) => GetField(ref st_mode, "st_mode").SetValue(self, value);

        public void SetStIno(object self, long value) => GetField(ref st_ino, "st_ino").SetValue(self, value);

        public void SetStDev(object self, long value) => GetField(ref st_dev, "st_dev").SetValue(self, value);

        public void SetStNlink(object self, int value) => GetField(ref st_nlink, "st_nlink").SetValue(self, value);

        public void SetStSize(object self, long value) => GetField(ref st_size, "st_size").SetValue(self, value);

    }

#endif

}
