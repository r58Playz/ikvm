package sun.nio.fs;
class UnixConstants {
    private UnixConstants() { }
    static final int O_RDONLY = 00;
    static final int O_WRONLY = 01;
    static final int O_RDWR = 02;
    static final int O_APPEND = 02000;
    static final int O_CREAT = 0100;
    static final int O_EXCL = 0200;
    static final int O_TRUNC = 01000;
    static final int O_SYNC = 04010000;





    static final int O_DSYNC = 010000;



    static final int O_NOFOLLOW = 0400000;
    static final int O_DIRECT = 00;


    static final int S_IAMB =
        (0400|0200|0100|(0400 >> 3)|(0200 >> 3)|(0100 >> 3)|((0400 >> 3) >> 3)|((0200 >> 3) >> 3)|((0100 >> 3) >> 3));
    static final int S_IRUSR = 0400;
    static final int S_IWUSR = 0200;
    static final int S_IXUSR = 0100;
    static final int S_IRGRP = (0400 >> 3);
    static final int S_IWGRP = (0200 >> 3);
    static final int S_IXGRP = (0100 >> 3);
    static final int S_IROTH = ((0400 >> 3) >> 3);
    static final int S_IWOTH = ((0200 >> 3) >> 3);
    static final int S_IXOTH = ((0100 >> 3) >> 3);

    static final int S_IFMT = 0170000;
    static final int S_IFREG = 0100000;
    static final int S_IFDIR = 0040000;
    static final int S_IFLNK = 0120000;
    static final int S_IFCHR = 0020000;
    static final int S_IFBLK = 0060000;
    static final int S_IFIFO = 0010000;
    static final int R_OK = 4;
    static final int W_OK = 2;
    static final int X_OK = 1;
    static final int F_OK = 0;
    // Emscripten's libc uses WASI errno values, not Linux kernel errno values.
    static final int ENOENT = 44;
    static final int ENXIO = 60;
    static final int EACCES = 2;
    static final int EEXIST = 20;
    static final int ENOTDIR = 54;
    static final int EINVAL = 28;
    static final int EXDEV = 75;
    static final int EISDIR = 31;
    static final int ENOTEMPTY = 55;
    static final int ENOSPC = 51;
    static final int EAGAIN = 6;
    static final int EWOULDBLOCK = 6;
    static final int ENOSYS = 52;
    static final int ELOOP = 32;
    static final int EROFS = 69;





    static final int ENODATA = 116;






    static final int XATTR_NOT_FOUND = 116;





    static final int ERANGE = 68;
    static final int EMFILE = 33;







    static final int AT_SYMLINK_NOFOLLOW = 0x100;
    static final int AT_REMOVEDIR = 0x200;
    static final int ATTR_CMN_CRTIME = 00;
    static final int ATTR_CMN_MODTIME = 00;
    static final int ATTR_CMN_ACCTIME = 00;
    static final int FSOPT_NOFOLLOW = 00;




    static final int POSIX_FADV_SEQUENTIAL = 2;
    static final int POSIX_FADV_NOREUSE = 5;
    static final int POSIX_FADV_WILLNEED = 3;

}
