using System.Collections.Generic;
using System.IO;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using java.net;
using java.nio.file;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.nio.file
{

    /// <summary>
    /// jrt:/, the file system of the run-time image: the exploded image of the modules the class library holds, which the
    /// virtual file system has.
    /// </summary>
    [TestClass]
    public class JrtFileSystemTests
    {

        static FileSystem Jrt => FileSystems.getFileSystem(URI.create("jrt:/"));

        static string ClassesPath => VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);

        static byte[] ReadVfs(string path)
        {
            using var s = JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        static List<string> List(global::java.nio.file.Path path)
        {
            var list = new List<string>();
            using var s = Files.newDirectoryStream(path);
            var i = s.iterator();
            while (i.hasNext())
                list.Add(((global::java.nio.file.Path)i.next()).getFileName().toString());

            return list;
        }

        [TestMethod]
        public void CanReadClassFile()
        {
            // the stub of a class is the one the class path has
            foreach (var name in new[] { "java/util/ArrayList.class", "java/lang/String.class", "java/lang/Object.class" })
                Files.readAllBytes(Jrt.getPath("/modules/java.base/" + name)).Should().Equal(ReadVfs(System.IO.Path.Combine(ClassesPath, name)), name);

            Files.readAllBytes(Jrt.getPath("/modules/java.desktop/java/awt/Component.class")).Should().Equal(ReadVfs(System.IO.Path.Combine(ClassesPath, "java/awt/Component.class")));
        }

        [TestMethod]
        public void CanReadModuleInfo()
        {
            var b = Files.readAllBytes(Jrt.getPath("/modules/java.base/module-info.class"));
            global::java.lang.module.ModuleDescriptor.read(global::java.nio.ByteBuffer.wrap(b)).name().Should().Be("java.base");
        }

        [TestMethod]
        public void CanReadResource()
        {
            Files.size(Jrt.getPath("/modules/java.base/sun/net/www/content-types.properties")).Should().BeGreaterThan(0);
            Files.isRegularFile(Jrt.getPath("/modules/java.base/java/lang/uniName.dat")).Should().BeTrue();
        }

        [TestMethod]
        public void CanListModules()
        {
            List(Jrt.getPath("/modules")).Should().Contain(new[] { "java.base", "java.desktop", "java.xml", "jdk.zipfs" });
            List(Jrt.getPath("/modules/java.base")).Should().Contain(new[] { "module-info.class", "java", "jdk", "sun" }).And.NotContain("ikvm");
            List(Jrt.getPath("/modules/java.base/java/lang")).Should().Contain(new[] { "Object.class", "String.class", "Integer.class", "invoke" }).And.OnlyHaveUniqueItems();
        }

        [TestMethod]
        public void CanFindPackage()
        {
            // /packages links each package to the modules that have it: of an exploded image, those that have its
            // directory, as java.instrument has java/lang on the way to java.lang.instrument
            List(Jrt.getPath("/packages/java.lang")).Should().Contain("java.base").And.Contain("java.instrument");
            List(Jrt.getPath("/packages/java.util.concurrent.atomic")).Should().Equal("java.base");
            List(Jrt.getPath("/packages/java.awt.event")).Should().Equal("java.desktop");
            Files.isRegularFile(Jrt.getPath("/packages/java.lang/java.base/java/lang/Object.class")).Should().BeTrue();
        }

        [TestMethod]
        public void MissingIsNotFound()
        {
            Files.exists(Jrt.getPath("/modules/java.base/java/lang/NoSuchClass.class")).Should().BeFalse();
            Files.exists(Jrt.getPath("/modules/java.base/java/awt")).Should().BeFalse();
            Files.exists(Jrt.getPath("/modules/no.such.module")).Should().BeFalse();
            FluentActions.Invoking(() => Files.readAllBytes(Jrt.getPath("/modules/java.base/java/lang/NoSuchClass.class"))).Should().Throw<NoSuchFileException>();
        }

        [TestMethod]
        public void IsReadOnly()
        {
            Jrt.isReadOnly().Should().BeTrue();
            FluentActions.Invoking(() => Files.delete(Jrt.getPath("/modules/java.base/java/lang/Object.class"))).Should().Throw<global::java.nio.file.ReadOnlyFileSystemException>();
        }

    }

}
