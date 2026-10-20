using System.IO;
using System.Linq;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using java.nio;
using java.nio.channels;
using java.nio.file;
using java.nio.file.attribute;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.nio.file
{

    /// <summary>
    /// java.nio.file against the paths of the virtual file system, which libnio does not know of.
    /// </summary>
    [TestClass]
    public class FilesVfsTests
    {

        static string ClassesPath => VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);

        static string ResourcesPath => VfsTable.GetAssemblyResourcesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);

        static string ObjectClassPath => System.IO.Path.Combine(ClassesPath, "java", "lang", "Object.class");

        static string StringClassPath => System.IO.Path.Combine(ClassesPath, "java", "lang", "String.class");

        static string ResourcesJarPath => System.IO.Path.Combine(ResourcesPath, "resources.jar");

        static global::java.nio.file.Path Get(string path) => Paths.get(path);

        static byte[] ReadVfs(string path)
        {
            using var s = JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        static string TempDirectory()
        {
            var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            Directory.CreateDirectory(d);
            return d;
        }

        [TestMethod]
        public void CanReadAllBytes()
        {
            Files.readAllBytes(Get(ObjectClassPath)).Should().Equal(ReadVfs(ObjectClassPath));
        }

        [TestMethod]
        public void CanReadThroughInputStream()
        {
            using var s = Files.newInputStream(Get(ObjectClassPath));
            s.readAllBytes().Should().Equal(ReadVfs(ObjectClassPath));
        }

        [TestMethod]
        public void CanReadThroughFileUri()
        {
            // the URLs the class loader hands out for resources and stubs are of the virtual file system
            var p = Paths.get(new global::java.io.File(ObjectClassPath).toURI());
            Files.readAllBytes(p).Should().Equal(ReadVfs(ObjectClassPath));
        }

        [TestMethod]
        public void CanUseFileChannel()
        {
            var expected = ReadVfs(ObjectClassPath);
            using var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            ch.size().Should().Be(expected.Length);

            var b = ByteBuffer.allocate(4);
            ch.read(b).Should().Be(4);
            ch.position().Should().Be(4);
            b.array().Should().Equal(expected.Take(4));

            // positional reads leave the position as it is
            var d = ByteBuffer.allocateDirect(4);
            ch.read(d, 8).Should().Be(4);
            ch.position().Should().Be(4);
            d.get(0).Should().Be(expected[8]);

            ch.position(expected.Length);
            ch.read(ByteBuffer.allocate(1)).Should().Be(-1);
        }

        [TestMethod]
        public void CanMapFileChannel()
        {
            var expected = ReadVfs(ObjectClassPath);
            using var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            var m = ch.map(FileChannel.MapMode.READ_ONLY, 2, expected.Length - 2);
            m.isReadOnly().Should().BeTrue();
            m.capacity().Should().Be(expected.Length - 2);
            m.get(0).Should().Be(expected[2]);
            FluentActions.Invoking(() => ch.map(FileChannel.MapMode.READ_WRITE, 0, 1)).Should().Throw<NonWritableChannelException>();
        }

        [TestMethod]
        public void CanTransferFromFileChannel()
        {
            var expected = ReadVfs(ObjectClassPath);
            var t = System.IO.Path.Combine(TempDirectory(), "Object.class");
            using (var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ))
            using (var o = FileChannel.open(Get(t), StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE))
                ch.transferTo(0, long.MaxValue, o).Should().Be(expected.Length);

            System.IO.File.ReadAllBytes(t).Should().Equal(expected);
        }

        [TestMethod]
        public void FileChannelIsNotWritable()
        {
            using var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            FluentActions.Invoking(() => ch.write(ByteBuffer.allocate(1))).Should().Throw<NonWritableChannelException>();
            FluentActions.Invoking(() => ch.truncate(0)).Should().Throw<NonWritableChannelException>();
            FluentActions.Invoking(() => ch.@lock()).Should().Throw<NonWritableChannelException>();
        }

        [TestMethod]
        public void CanLockFileChannelShared()
        {
            using var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            var l = ch.@lock(0, 10, true);
            l.isShared().Should().BeTrue();
            l.isValid().Should().BeTrue();
            FluentActions.Invoking(() => ch.tryLock(5, 10, true)).Should().Throw<OverlappingFileLockException>();
            l.release();
            l.isValid().Should().BeFalse();
            ch.tryLock(5, 10, true).isValid().Should().BeTrue();
        }

        [TestMethod]
        public void CanCloseFileChannel()
        {
            var ch = FileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            ch.close();
            ch.isOpen().Should().BeFalse();
            FluentActions.Invoking(() => ch.size()).Should().Throw<ClosedChannelException>();
        }

        [TestMethod]
        public void CanReadAsynchronousFileChannel()
        {
            var expected = ReadVfs(ObjectClassPath);
            using var ch = AsynchronousFileChannel.open(Get(ObjectClassPath), StandardOpenOption.READ);
            ch.size().Should().Be(expected.Length);
            var b = ByteBuffer.allocate(expected.Length);
            ((global::java.lang.Integer)ch.read(b, 0).get()).intValue().Should().Be(expected.Length);
            b.array().Should().Equal(expected);
        }

        [TestMethod]
        public void OpeningForWriteFailsAsReadOnly()
        {
            FluentActions.Invoking(() => Files.newByteChannel(Get(ObjectClassPath), StandardOpenOption.WRITE)).Should().ThrowExactly<FileSystemException>().Which.getReason().Should().Be("Read-only file system");
            FluentActions.Invoking(() => Files.write(Get(ObjectClassPath), new byte[] { 1 })).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.newByteChannel(Get(ObjectClassPath), StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE)).Should().Throw<FileAlreadyExistsException>();
            FluentActions.Invoking(() => Files.newByteChannel(Get(System.IO.Path.Combine(ClassesPath, "missing.txt")))).Should().Throw<NoSuchFileException>();
            FluentActions.Invoking(() => Files.newByteChannel(Get(ClassesPath))).Should().ThrowExactly<FileSystemException>().Which.getReason().Should().Be("Is a directory");
        }

        [TestMethod]
        public void CanListDirectory()
        {
            using var s = Files.list(Get(System.IO.Path.Combine(ClassesPath, "java", "lang")));
            var names = new System.Collections.Generic.List<string>();
            var i = s.iterator();
            while (i.hasNext())
                names.Add(((global::java.nio.file.Path)i.next()).getFileName().toString());

            names.Should().Contain("Object.class");
            names.Should().Contain("String.class");
            names.Should().Contain("invoke");
        }

        [TestMethod]
        public void CanUseDirectoryStream()
        {
            using var s = Files.newDirectoryStream(Get(System.IO.Path.Combine(ClassesPath, "java", "lang")), "Obj*");
            var names = new System.Collections.Generic.List<string>();
            var i = s.iterator();
            while (i.hasNext())
                names.Add(((global::java.nio.file.Path)i.next()).getFileName().toString());

            names.Should().Contain("Object.class");
            names.Should().OnlyContain(n => n.StartsWith("Obj"));
            FluentActions.Invoking(() => s.iterator()).Should().Throw<global::java.lang.IllegalStateException>();
        }

        [TestMethod]
        public void DirectoryStreamOfFileFails()
        {
            FluentActions.Invoking(() => Files.newDirectoryStream(Get(ObjectClassPath))).Should().Throw<NotDirectoryException>();
            FluentActions.Invoking(() => Files.newDirectoryStream(Get(System.IO.Path.Combine(ClassesPath, "missing")))).Should().Throw<NoSuchFileException>();
        }

        [TestMethod]
        public void CanWalkDirectory()
        {
            using var s = Files.walk(Get(System.IO.Path.Combine(ClassesPath, "java", "lang", "annotation")), 1);
            s.count().Should().BeGreaterThan(1);
        }

        [TestMethod]
        public void CanCopyFile()
        {
            var t = System.IO.Path.Combine(TempDirectory(), "Object.class");
            Files.copy(Get(ObjectClassPath), Get(t));
            System.IO.File.ReadAllBytes(t).Should().Equal(ReadVfs(ObjectClassPath));
        }

        [TestMethod]
        public void CanCopyFileWithAttributesOverExisting()
        {
            var t = System.IO.Path.Combine(TempDirectory(), "Object.class");
            System.IO.File.WriteAllBytes(t, new byte[] { 1 });
            Files.copy(Get(ObjectClassPath), Get(t), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.COPY_ATTRIBUTES);
            System.IO.File.ReadAllBytes(t).Should().Equal(ReadVfs(ObjectClassPath));
        }

        [TestMethod]
        public void CanCopyDirectory()
        {
            var t = System.IO.Path.Combine(TempDirectory(), "lang");
            Files.copy(Get(System.IO.Path.Combine(ClassesPath, "java", "lang")), Get(t));
            Directory.Exists(t).Should().BeTrue();
        }

        [TestMethod]
        public void CanOpenZipFileSystem()
        {
            using var fs = FileSystems.newFileSystem(Get(ResourcesJarPath));
            Files.exists(fs.getPath("META-INF", "MANIFEST.MF")).Should().BeTrue();
        }

        [TestMethod]
        public void ChangesFailAsReadOnly()
        {
            FluentActions.Invoking(() => Files.delete(Get(ObjectClassPath))).Should().ThrowExactly<FileSystemException>().Which.getReason().Should().Be("Read-only file system");
            FluentActions.Invoking(() => Files.delete(Get(System.IO.Path.Combine(ClassesPath, "missing")))).Should().Throw<NoSuchFileException>();
            Files.deleteIfExists(Get(System.IO.Path.Combine(ClassesPath, "missing"))).Should().BeFalse();
            FluentActions.Invoking(() => Files.deleteIfExists(Get(ObjectClassPath))).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.createDirectory(Get(System.IO.Path.Combine(ClassesPath, "newdir")))).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.createDirectory(Get(ClassesPath))).Should().Throw<FileAlreadyExistsException>();
            FluentActions.Invoking(() => Files.createFile(Get(System.IO.Path.Combine(ClassesPath, "new.txt")))).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.setLastModifiedTime(Get(ObjectClassPath), FileTime.fromMillis(0))).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.setPosixFilePermissions(Get(ObjectClassPath), PosixFilePermissions.fromString("rwxrwxrwx"))).Should().ThrowExactly<FileSystemException>();
            FluentActions.Invoking(() => Files.createSymbolicLink(Get(System.IO.Path.Combine(ClassesPath, "link")), Get("/"))).Should().ThrowExactly<FileSystemException>();
        }

        [TestMethod]
        public void CanCreateExistingDirectories()
        {
            Files.createDirectories(Get(ClassesPath));
        }

        [TestMethod]
        public void MoveFailsAsReadOnly()
        {
            var t = System.IO.Path.Combine(TempDirectory(), "Object.class");
            FluentActions.Invoking(() => Files.move(Get(ObjectClassPath), Get(t))).Should().ThrowExactly<FileSystemException>().Which.getReason().Should().Be("Read-only file system");
            System.IO.File.Exists(t).Should().BeFalse();
            Files.exists(Get(ObjectClassPath)).Should().BeTrue();
            FluentActions.Invoking(() => Files.move(Get(ObjectClassPath), Get(t), StandardCopyOption.ATOMIC_MOVE)).Should().Throw<AtomicMoveNotSupportedException>();
        }

        [TestMethod]
        public void HasReadOnlyAccess()
        {
            Files.isReadable(Get(ObjectClassPath)).Should().BeTrue();
            Files.isWritable(Get(ObjectClassPath)).Should().BeFalse();
            Files.isExecutable(Get(ObjectClassPath)).Should().BeFalse();
            Files.isExecutable(Get(ClassesPath)).Should().BeTrue();
        }

        [TestMethod]
        public void HasNoLinks()
        {
            FluentActions.Invoking(() => Files.readSymbolicLink(Get(ObjectClassPath))).Should().Throw<NotLinkException>();
            Files.isSymbolicLink(Get(ObjectClassPath)).Should().BeFalse();
        }

        [TestMethod]
        public void CanWatchDirectory()
        {
            using var w = FileSystems.getDefault().newWatchService();
            var k = Get(ClassesPath).register(w, StandardWatchEventKinds.ENTRY_CREATE);
            k.isValid().Should().BeTrue();
            Get(ClassesPath).register(w, StandardWatchEventKinds.ENTRY_DELETE).Should().BeSameAs(k);
            k.cancel();
            k.isValid().Should().BeFalse();
        }

        [TestMethod]
        public void CanGetFileStore()
        {
            var s = Files.getFileStore(Get(ObjectClassPath));
            s.isReadOnly().Should().BeTrue();
            s.getTotalSpace().Should().Be(0);
            s.supportsFileAttributeView("posix").Should().BeTrue();
            s.Should().Be(Files.getFileStore(Get(ClassesPath)));
        }

        [TestMethod]
        public void FilesHaveDistinctKeys()
        {
            var a = Files.getAttribute(Get(ObjectClassPath), "fileKey");
            var b = Files.getAttribute(Get(StringClassPath), "fileKey");
            a.Should().NotBe(b);
            a.Should().Be(Files.getAttribute(Get(ObjectClassPath), "fileKey"));
            Files.isSameFile(Get(ObjectClassPath), Get(StringClassPath)).Should().BeFalse();
        }

        [TestMethod]
        public void CanResolveDotSegments()
        {
            var p = Get(System.IO.Path.Combine(ClassesPath, "java", "util", "..", "lang", ".", "Object.class"));
            Files.exists(p).Should().BeTrue();
            p.toRealPath().toString().Should().Be(ObjectClassPath);
        }

#if NETCOREAPP
        [TestMethod]
        public void CanResolveRelativePath()
        {
            var p = Get(System.IO.Path.GetRelativePath(System.Environment.CurrentDirectory, ObjectClassPath));
            Files.readAllBytes(p).Should().Equal(ReadVfs(ObjectClassPath));
        }
#endif

        [TestMethod]
        public void MountRootIsDirectory()
        {
            var root = Get(System.IO.Path.Combine(JVM.Properties.HomePath, "assembly"));
            Files.isDirectory(root).Should().BeTrue();
            FluentActions.Invoking(() => Files.createDirectory(root)).Should().Throw<FileAlreadyExistsException>();
        }

        [TestMethod]
        public void CanLeaveByParentSegment()
        {
            // the host's directory the mount point is in, through the mount
            var p = Get(System.IO.Path.Combine(ClassesPath, "..", "..", ".."));
            Files.isDirectory(p).Should().BeTrue();
            p.toRealPath().toString().Should().Be(System.IO.Path.GetFullPath(JVM.Properties.HomePath).TrimEnd('/'));
        }

    }

}
