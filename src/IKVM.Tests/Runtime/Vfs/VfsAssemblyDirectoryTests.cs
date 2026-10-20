using System;
using System.IO;
using System.IO.Compression;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Runtime.Vfs
{

    [TestClass]
    public class VfsAssemblyDirectoryTests
    {

        [TestMethod]
        public void CanGetAssemblyDirectory()
        {
            var dir = new VfsAssemblyDirectory(JVM.Vfs.Context);
            var asm = typeof(VfsAssemblyDirectoryTests).Assembly.GetName();
            var ent = dir.GetEntry($"{asm.Name}__{asm.Version}__{BitConverter.ToString(asm.GetPublicKeyToken()).ToLower().Replace("-", "")}") as VfsDirectory;
            ent.Should().NotBeNull();
            ent.List().Should().HaveCount(3);
            ent.GetEntry("classes").Should().BeAssignableTo<VfsDirectory>();
            ent.GetEntry("resources").Should().BeAssignableTo<VfsDirectory>();
            ent.GetEntry("modules").Should().BeAssignableTo<VfsDirectory>();
        }

        [TestMethod]
        public void CanFindModuleInfo()
        {
            // the module-info.class of a module the class library holds, as the JDK image has it
            var dir = new VfsAssemblyModuleDirectory(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly);
            dir.List().Should().Contain("java.base");
            var ent = (dir.GetEntry("java.base") as VfsDirectory)?.GetEntry("module-info.class") as VfsFile;
            ent.Should().NotBeNull();
            new BinaryReader(ent.Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
            dir.GetEntry("no.such.module").Should().BeNull();
        }

        /// <summary>
        /// The modules the class library holds.
        /// </summary>
        static VfsDirectory ClassLibraryModules => new VfsAssemblyModuleDirectory(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly);

        /// <summary>
        /// The stubs of the class library's classes on the class path.
        /// </summary>
        static VfsDirectory ClassLibraryClasses => new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, "");

        static VfsEntry Find(VfsDirectory dir, string path)
        {
            VfsEntry entry = dir;
            foreach (var name in path.Split('/'))
                if ((entry = (entry as VfsDirectory)?.GetEntry(name)) == null)
                    return null;

            return entry;
        }

        static byte[] Read(VfsEntry entry)
        {
            entry.Should().BeAssignableTo<VfsFile>();
            using var s = ((VfsFile)entry).Open(FileMode.Open, FileAccess.Read);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        [TestMethod]
        public void ModuleHasDirectoriesOfItsPackages()
        {
            // a module's packages are those its module-info.class has, and a directory is on the way to one
            Find(ClassLibraryModules, "java.base").Should().BeAssignableTo<VfsDirectory>().Which.List().Should().Contain(new[] { "module-info.class", "java", "javax", "jdk", "sun" }).And.NotContain("ikvm");
            Find(ClassLibraryModules, "java.base/java").Should().BeAssignableTo<VfsDirectory>().Which.List().Should().BeEquivalentTo(new[] { "io", "lang", "math", "net", "nio", "security", "text", "time", "util" });
            Find(ClassLibraryModules, "java.base/java/awt").Should().BeNull();
            Find(ClassLibraryModules, "java.desktop/java/awt").Should().BeAssignableTo<VfsDirectory>();
            Find(ClassLibraryModules, "java.desktop/java/lang").Should().BeNull();

            // IKVM's own packages are in no module
            Find(ClassLibraryModules, "java.base/ikvm").Should().BeNull();
        }

        [TestMethod]
        public void ModuleHasStubsOfItsClasses()
        {
            // the stub a module has of a class is the one the class path has
            var list = Find(ClassLibraryModules, "java.base/java/util").Should().BeAssignableTo<VfsDirectory>().Which.List();
            list.Should().Contain(new[] { "ArrayList.class", "HashMap$Node.class", "concurrent" }).And.OnlyHaveUniqueItems();

            var file = Find(ClassLibraryModules, "java.base/java/util/ArrayList.class");
            var bytes = Read(file);
            ((VfsFile)file).Size.Should().Be(bytes.Length);
            bytes.Should().Equal(Read(Find(ClassLibraryClasses, "java/util/ArrayList.class")));

            Find(ClassLibraryModules, "java.desktop/java/awt/Component.class").Should().BeOfType<VfsAssemblyJarFile>();
            Find(ClassLibraryModules, "java.base/java/util/NoSuchClass.class").Should().BeNull();
        }

        [TestMethod]
        public void ModuleHasStubsOfRemappedClasses()
        {
            // the remapped classes are not compiled from class files, and have no stored stub: theirs is generated, as on
            // the class path
            var list = Find(ClassLibraryModules, "java.base/java/lang").Should().BeAssignableTo<VfsDirectory>().Which.List();
            list.Should().Contain(new[] { "Object.class", "String.class", "Throwable.class", "Comparable.class", "AutoCloseable.class", "String$CaseInsensitiveComparator.class", "Integer.class" }).And.OnlyHaveUniqueItems();

            foreach (var name in new[] { "Object.class", "String.class", "Throwable.class", "Comparable.class", "AutoCloseable.class" })
            {
                var file = Find(ClassLibraryModules, "java.base/java/lang/" + name);
                file.Should().BeOfType<VfsAssemblyClassFile>(name);
                var bytes = Read(file);
                ((VfsFile)file).Size.Should().Be(bytes.Length);
                bytes.Should().Equal(Read(Find(ClassLibraryClasses, "java/lang/" + name)), name);
            }
        }

        [TestMethod]
        public void ModuleHasResourcesOfItsPackages()
        {
            // a resource of a package is in the package's directory, as the class library holds it
            using var jar = new ZipArchive(((VfsFile)new VfsAssemblyResourceDirectory(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly).GetEntry("resources.jar")).Open(FileMode.Open, FileAccess.Read));
            foreach (var name in new[] { "java/lang/uniName.dat", "sun/net/www/content-types.properties" })
            {
                using var expected = new MemoryStream();
                using (var s = jar.GetEntry(name).Open())
                    s.CopyTo(expected);

                var file = Find(ClassLibraryModules, "java.base/" + name);
                ((VfsFile)file).Size.Should().Be(expected.Length);
                Read(file).Should().Equal(expected.ToArray(), name);
            }

            Find(ClassLibraryModules, "java.base/sun/net/www").Should().BeAssignableTo<VfsDirectory>().Which.List().Should().Contain("content-types.properties");
        }

        [TestMethod]
        public void HomeHasModulesOfClassLibrary()
        {
            // the run-time image is exploded, with the modules the class library holds: there is no jimage
            File.Exists(Path.Combine(JVM.Properties.HomePath, "lib", "modules")).Should().BeFalse();
            var modules = Path.Combine(JVM.Properties.HomePath, "modules");
            JVM.Vfs.GetEntry(modules).Should().BeOfType<VfsAssemblyModuleDirectory>().Which.List().Should().Contain(new[] { "java.base", "java.desktop", "java.xml", "jdk.zipfs" });
            JVM.Vfs.GetEntry(Path.Combine(modules, "java.base", "java", "lang", "Object.class")).Should().BeAssignableTo<VfsFile>();
        }

        [TestMethod]
        public void CanGetAssemblyResource()
        {
            var dir = new VfsAssemblyDirectory(JVM.Vfs.Context);
            var asm = typeof(VfsAssemblyDirectoryTests).Assembly.GetName();
            var ent = dir.GetEntry($"{asm.Name}__{asm.Version}__{BitConverter.ToString(asm.GetPublicKeyToken()).ToLower().Replace("-", "")}") as VfsDirectory;
            ent.Should().NotBeNull();
            ent.List().Should().HaveCount(3);
            var res = ent.GetEntry("resources") as VfsDirectory;
            res.Should().NotBeNull();
            var rsx = res.GetEntry("IKVM.Tests.Runtime.Vfs.VfsTestResource.txt") as VfsFile;
            rsx.Should().NotBeNull();
            new StreamReader(rsx.Open(FileMode.Open, FileAccess.Read)).ReadLine().Should().Be("TEST");
        }

        [TestMethod]
        public void CanFindClassFileInPackage()
        {
            var dir = new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, "java.lang");
            var ent = dir.GetEntry("Object.class");
            ent.Should().NotBeNull();
            ent.Should().BeOfType<VfsAssemblyClassFile>();
            new BinaryReader(((VfsFile)ent).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanFindClassFileForNestedClass()
        {
            var dir = new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::java.lang.Character).Assembly, "java.lang");
            var ent = dir.GetEntry("Character$Subset.class");
            ent.Should().NotBeNull();
            ent.Should().BeOfType<VfsAssemblyClassFile>();
            new BinaryReader(((VfsFile)ent).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanFindClassFileForAnonymousClass()
        {
            var dir = new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::java.lang.ClassLoader).Assembly, "java.lang");
            var ent = dir.GetEntry("ClassLoader$1.class");
            ent.Should().NotBeNull();
            ent.Should().BeOfType<VfsAssemblyClassFile>();
            new BinaryReader(((VfsFile)ent).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanFindClassFileForInnerClassOfInterface()
        {
            var dir = new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::java.lang.CharSequence).Assembly, "java.lang");
            var ent = dir.GetEntry("CharSequence$1CharIterator.class");
            ent.Should().NotBeNull();
            ent.Should().BeOfType<VfsAssemblyClassFile>();
            new BinaryReader(((VfsFile)ent).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanFindClassFileForNestedClassOfInterface()
        {
            var dir = new VfsAssemblyClassDirectory(JVM.Vfs.Context, typeof(global::javax.tools.JavaFileObject).Assembly, "javax.tools");
            var ent = dir.GetEntry("JavaFileObject$Kind.class");
            ent.Should().NotBeNull();
            ent.Should().BeOfType<VfsAssemblyClassFile>();
            new BinaryReader(((VfsFile)ent).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

    }

}
