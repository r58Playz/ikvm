using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using java.io;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.io
{

    [TestClass]
    public class FileTests
    {

        [TestMethod]
        public void CanConvertRelativePathToRealPath()
        {
            (new global::java.io.File(".")).toPath().toRealPath();
        }

        [TestMethod]
        public void CanCreateFile()
        {
            var f = new global::java.io.File(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
            f.createNewFile().Should().BeTrue();
        }

        [TestMethod]
        public void CanWriteFile()
        {
            var w = new global::java.io.FileWriter("test.txt");
            w.write("TEST");
            w.close();
        }

        [TestMethod]
        public void CanReadFile()
        {
            var w = new global::java.io.FileWriter("test.txt");
            w.write("TEST");
            w.close();

            var f = new global::java.io.File("test.txt");
            var r = new global::java.util.Scanner(f);
            r.hasNextLine().Should().BeTrue();
            r.nextLine().Should().Be("TEST");
            r.hasNextLine().Should().BeFalse();
            r.close();
        }

        [TestMethod]
        public void CanSetReadOnly()
        {
            var f = new global::java.io.File(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
            f.createNewFile().Should().BeTrue();
            f.canWrite().Should().BeTrue();
            f.setReadOnly().Should().BeTrue();
            f.canWrite().Should().BeFalse();
        }

        [TestMethod]
        public void ShouldRemoveDotFromCanonicalizedPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                new global::java.io.File(@"C:\Windows\.\System32").getCanonicalPath().Should().Be(@"C:\Windows\System32");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                new global::java.io.File(@"/usr/./lib").getCanonicalPath().Should().Be(@"/usr/lib");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                new global::java.io.File(@"/usr/./lib").getCanonicalPath().Should().Be(@"/usr/lib");
        }

        [TestMethod]
        public void ShouldRemoveDotDotDotFromCanonicalizedPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                new global::java.io.File(@"C:\Windows\..\Windows\System32").getCanonicalPath().Should().Be(@"C:\Windows\System32");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                new global::java.io.File(@"/usr/../usr/lib").getCanonicalPath().Should().Be(@"/usr/lib");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                new global::java.io.File(@"/usr/../usr/lib").getCanonicalPath().Should().Be(@"/usr/lib");
        }

        [TestMethod]
        public void CanGetPathSeparator()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                global::java.io.File.pathSeparator.Should().Be(";");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                global::java.io.File.pathSeparator.Should().Be(":");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                global::java.io.File.pathSeparator.Should().Be(":");
        }

        [TestMethod]
        public void VfsAssemblyClassesDirectoryShouldBeDirectory()
        {
            var d = VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(object).Assembly, JVM.Properties.HomePath);
            new global::java.io.File(d).isDirectory().Should().BeTrue();
        }

        [TestMethod]
        public void VfsAssemblyClassesDirectoryCanBeListed()
        {
            var d = VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(object).Assembly, JVM.Properties.HomePath);
            var l = new global::java.io.File(d).list();
            foreach (var s in l)
                System.Console.WriteLine(s);
        }

        [TestMethod]
        public void VfsAssemblyClassCanBeRead()
        {
            var f = System.IO.Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "java", "lang", "Class.class");
            var s = new global::java.io.FileInputStream(f);
            var b = new byte[new global::java.io.File(f).length()];
            s.read(b);
            b.Length.Should().BeGreaterOrEqualTo(32);
        }

        [TestMethod]
        [ExpectedException(typeof(global::java.io.FileNotFoundException))]
        public void VfsAssemblyClassCanNotBeWritten()
        {
            var f = System.IO.Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "java", "lang", "Class.class");
            new global::java.io.FileOutputStream(f).write(0);
        }

        static string VfsObjectClassPath => System.IO.Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "java", "lang", "Object.class");

        static byte[] ReadVfs(string path)
        {
            using var s = JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        [TestMethod]
        public void VfsMountRootIsDirectory()
        {
            var root = new global::java.io.File(System.IO.Path.Combine(JVM.Properties.HomePath, "assembly"));
            root.exists().Should().BeTrue();
            root.isDirectory().Should().BeTrue();
            root.mkdir().Should().BeFalse();
            root.list().Should().BeEmpty();
        }

        [TestMethod]
        public void VfsPathWithDotSegmentsCanBeRead()
        {
            var d = VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);
            var f = new global::java.io.File(System.IO.Path.Combine(d, "java", "util", "..", "lang", ".", "Object.class"));
            f.exists().Should().BeTrue();
            f.length().Should().Be(ReadVfs(VfsObjectClassPath).Length);
            f.getCanonicalPath().Should().Be(VfsObjectClassPath);
            using var s = new global::java.io.FileInputStream(f);
            s.readAllBytes().Should().Equal(ReadVfs(VfsObjectClassPath));
        }

        [TestMethod]
        public void VfsPathEnteredByParentSegmentCanBeRead()
        {
            var home = JVM.Properties.HomePath.TrimEnd('/');
            var f = new global::java.io.File(home + "/lib/.." + VfsObjectClassPath.Substring(home.Length));
            f.isFile().Should().BeTrue();
        }

        [TestMethod]
        public void VfsPathLeftByParentSegmentIsHost()
        {
            var d = VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);
            var f = new global::java.io.File(System.IO.Path.Combine(d, "..", "..", ".."));
            f.isDirectory().Should().BeTrue();
            f.list().Should().Contain("bin");
        }

#if NETCOREAPP
        [TestMethod]
        public void VfsRelativePathCanBeRead()
        {
            var f = new global::java.io.File(System.IO.Path.GetRelativePath(System.Environment.CurrentDirectory, VfsObjectClassPath));
            f.isAbsolute().Should().BeFalse();
            f.isFile().Should().BeTrue();
            using var s = new global::java.io.FileInputStream(f);
            s.readAllBytes().Should().Equal(ReadVfs(VfsObjectClassPath));
        }
#endif

        [TestMethod]
        public void VfsFileIsNotExecutable()
        {
            new global::java.io.File(VfsObjectClassPath).canExecute().Should().BeFalse();
            new global::java.io.File(VfsObjectClassPath).canWrite().Should().BeFalse();
            new global::java.io.File(VfsObjectClassPath).canRead().Should().BeTrue();
        }

        [TestMethod]
        public void VfsCreateNewFileFails()
        {
            new global::java.io.File(VfsObjectClassPath).createNewFile().Should().BeFalse();
            var d = VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);
            FluentActions.Invoking(() => new global::java.io.File(d, "new.txt").createNewFile()).Should().ThrowExactly<global::java.io.IOException>().Which.getMessage().Should().Be("Read-only file system");
        }

        [TestMethod]
        public void VfsFileInputStreamChannelCanBeRead()
        {
            var expected = ReadVfs(VfsObjectClassPath);
            using var s = new global::java.io.FileInputStream(VfsObjectClassPath);
            s.getFD().valid().Should().BeTrue();
            var ch = s.getChannel();
            ch.size().Should().Be(expected.Length);
            s.read().Should().Be(expected[0]);
            ch.position().Should().Be(1);
            var b = global::java.nio.ByteBuffer.allocate(3);
            ch.read(b).Should().Be(3);
            b.array().Should().Equal(expected.Skip(1).Take(3));
            s.read().Should().Be(expected[4]);
            s.close();
            ch.isOpen().Should().BeFalse();
            s.getFD().valid().Should().BeFalse();
        }

        [TestMethod]
        public void VfsFileInputStreamCanTransferToFileOutputStream()
        {
            var expected = ReadVfs(VfsObjectClassPath);
            var t = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            using (var s = new global::java.io.FileInputStream(VfsObjectClassPath))
            using (var o = new global::java.io.FileOutputStream(t))
                s.transferTo(o).Should().Be(expected.Length);

            System.IO.File.ReadAllBytes(t).Should().Equal(expected);
        }

        [TestMethod]
        public void VfsFileInputStreamSkipsAsLseek()
        {
            using var s = new global::java.io.FileInputStream(VfsObjectClassPath);
            s.read();
            s.skip(-1).Should().Be(-1);
            FluentActions.Invoking(() => s.skip(-10)).Should().ThrowExactly<global::java.io.IOException>();
        }

        [TestMethod]
        public void VfsRandomAccessFileChannelCanBeRead()
        {
            var expected = ReadVfs(VfsObjectClassPath);
            using var f = new global::java.io.RandomAccessFile(VfsObjectClassPath, "r");
            f.seek(2);
            f.getChannel().position().Should().Be(2);
            f.getChannel().size().Should().Be(expected.Length);
            FluentActions.Invoking(() => new global::java.io.RandomAccessFile(VfsObjectClassPath, "rw")).Should().Throw<global::java.io.FileNotFoundException>().Which.getMessage().Should().EndWith("(Read-only file system)");
        }

    }

}
