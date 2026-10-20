using System.IO;
using System.IO.Compression;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Runtime.Vfs
{

    [TestClass]
    public class VfsTableTests
    {

        [TestMethod]
        public void CanGetAssemblyDirectory()
        {
            JVM.Vfs.GetEntry(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath)).Should().BeAssignableTo<VfsDirectory>();
            JVM.Vfs.GetEntry(VfsTable.GetAssemblyResourcesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath)).Should().BeAssignableTo<VfsDirectory>();
        }

        [TestMethod]
        public void CanListAssemblyClassesDirectory()
        {
            var dir = (VfsDirectory)JVM.Vfs.GetEntry(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath));
            var lst = dir.List();
            lst.Should().NotContain("");
        }

        [TestMethod]
        public void CanGetClassFileForStandardClass()
        {
            var f1 = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "java", "lang", "Object.class"));
            f1.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)f1).Size.Should().BeGreaterThan(1);
            ((VfsFile)f1).Open(FileMode.Open, FileAccess.Read).Length.Should().BeGreaterThan(1);

            var f2 = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.String).Assembly, JVM.Properties.HomePath), "java", "lang", "String.class"));
            f2.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)f2).Size.Should().BeGreaterThan(1);
            ((VfsFile)f2).Open(FileMode.Open, FileAccess.Read).Length.Should().BeGreaterThan(1);
        }

        [TestMethod]
        public void CanGetResourcesJar()
        {
            var f1 = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyResourcesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "resources.jar"));
            f1.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)f1).Size.Should().BeGreaterThan(1);
            ((VfsFile)f1).Open(FileMode.Open, FileAccess.Read).Length.Should().BeGreaterThan(1);
        }

        [TestMethod]
        public void CanReadManifestFromResourcesJar()
        {
            var fil = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyResourcesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath), "resources.jar"));
            var stm = ((VfsFile)fil).Open(FileMode.Open, FileAccess.Read);
            var zip = new ZipArchive(stm);
            var man = zip.GetEntry("META-INF/MANIFEST.MF");
            man.Should().NotBeNull();
            new StreamReader(man.Open()).ReadToEnd();
        }

        [TestMethod]
        public void CanGetClassFileForNestedClass()
        {
            var file = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Character).Assembly, JVM.Properties.HomePath), "java", "lang", "Character$Subset.class"));
            file.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)file).Size.Should().BeGreaterThan(1);
            new BinaryReader(((VfsFile)file).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanGetClassFileForAnonymousClass()
        {
            var file = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.ClassLoader).Assembly, JVM.Properties.HomePath), "java", "lang", "ClassLoader$1.class"));
            file.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)file).Size.Should().BeGreaterThan(1);
            new BinaryReader(((VfsFile)file).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanGetClassFileForInnerClassOfInterface()
        {
            var file = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.CharSequence).Assembly, JVM.Properties.HomePath), "java", "lang", "CharSequence$1CharIterator.class"));
            file.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)file).Size.Should().BeGreaterThan(1);
            new BinaryReader(((VfsFile)file).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        [TestMethod]
        public void CanGetClassFileForNestedClassOfInterface()
        {
            var file = JVM.Vfs.GetEntry(Path.Combine(VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::javax.tools.JavaFileObject).Assembly, JVM.Properties.HomePath), "javax", "tools", "JavaFileObject$Kind.class"));
            file.Should().BeAssignableTo<VfsFile>();
            ((VfsFile)file).Size.Should().BeGreaterThan(1);
            new BinaryReader(((VfsFile)file).Open(FileMode.Open, FileAccess.Read)).ReadUInt32().Should().Be(0xBEBAFECA);
        }

        static string ClassesPath => VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);

        static string ObjectClassPath => Path.Combine(ClassesPath, "java", "lang", "Object.class");

        static string MountRoot => Path.Combine(JVM.Properties.HomePath, "assembly");

        [TestMethod]
        public void MountRootIsInMount()
        {
            JVM.Vfs.Resolve(MountRoot).IsVfs.Should().BeTrue();
            JVM.Vfs.Resolve(MountRoot + Path.DirectorySeparatorChar).IsVfs.Should().BeTrue();
            JVM.Vfs.GetEntry(MountRoot).Should().BeAssignableTo<VfsDirectory>();
            JVM.Vfs.Resolve(MountRoot + "x").IsVfs.Should().BeFalse();
        }

        [TestMethod]
        public void ResolveNormalizesPathsOfMount()
        {
            var p = JVM.Vfs.Resolve(ClassesPath);
            p.IsVfs.Should().BeTrue();
            p.Path.Should().Be(ClassesPath.TrimEnd(Path.DirectorySeparatorChar));

            var q = JVM.Vfs.Resolve(Path.Combine(ClassesPath, "java", "util", "..", "lang", ".", "Object.class"));
            q.IsVfs.Should().BeTrue();
            q.Path.Should().Be(ObjectClassPath);
            JVM.Vfs.GetEntry(q).Should().BeAssignableTo<VfsFile>();
        }

        [TestMethod]
        public void ResolveEntersMountByParentSegment()
        {
            var home = JVM.Properties.HomePath.TrimEnd(Path.DirectorySeparatorChar);
            var p = JVM.Vfs.Resolve(home + "/lib/.." + ObjectClassPath.Substring(home.Length));
            p.IsVfs.Should().BeTrue();
            p.Path.Should().Be(ObjectClassPath);
        }

        [TestMethod]
        public void ResolveRewritesPathLeavingMount()
        {
            var p = JVM.Vfs.Resolve(Path.Combine(ClassesPath, "..", "..", "..", "lib"));
            p.IsVfs.Should().BeFalse();
            p.Rewritten.Should().BeTrue();
            p.Path.Should().Be(Path.Combine(Path.GetFullPath(JVM.Properties.HomePath), "lib"));
        }

        [TestMethod]
        public void ResolveLeavesHostPathAsIs()
        {
            var path = Path.Combine(Path.GetTempPath(), "x", "..", "y");
            var p = JVM.Vfs.Resolve(path);
            p.IsVfs.Should().BeFalse();
            p.Rewritten.Should().BeFalse();
            p.Path.Should().BeSameAs(path);

            var q = JVM.Vfs.Resolve("relative/path");
            q.IsVfs.Should().BeFalse();
            q.Path.Should().Be("relative/path");
        }

#if NETCOREAPP
        [TestMethod]
        public void ResolveRelativePathAgainstCurrentDirectory()
        {
            var p = JVM.Vfs.Resolve(Path.GetRelativePath(System.Environment.CurrentDirectory, ObjectClassPath));
            p.IsVfs.Should().BeTrue();
            p.Path.Should().Be(ObjectClassPath);
        }
#endif

        [TestMethod]
        public void FileIdsAreStableAndDistinct()
        {
            var a = JVM.Vfs.GetFileId(JVM.Vfs.Resolve(ObjectClassPath));
            var b = JVM.Vfs.GetFileId(JVM.Vfs.Resolve(Path.Combine(ClassesPath, "java", "lang", "String.class")));
            a.Should().NotBe(b);
            JVM.Vfs.GetFileId(JVM.Vfs.Resolve(Path.Combine(ClassesPath, "java", ".", "lang", "Object.class"))).Should().Be(a);
        }

        [TestMethod]
        public void ChecksAreThoseOfReadOnlyFileSystem()
        {
            var file = JVM.Vfs.Resolve(ObjectClassPath);
            var dir = JVM.Vfs.Resolve(ClassesPath);
            var missing = JVM.Vfs.Resolve(Path.Combine(ClassesPath, "missing.txt"));
            var orphan = JVM.Vfs.Resolve(Path.Combine(ClassesPath, "missing", "missing.txt"));
            var host = JVM.Vfs.Resolve(Path.GetTempPath());

            JVM.Vfs.CheckCreate(file).Should().Be(VfsStatus.Exists);
            JVM.Vfs.CheckCreate(missing).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckCreate(orphan).Should().Be(VfsStatus.NotFound);
            JVM.Vfs.CheckCreate(JVM.Vfs.Resolve(Path.Combine(ObjectClassPath, "x"))).Should().Be(VfsStatus.NotDirectory);

            JVM.Vfs.CheckRemove(file, false).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckRemove(file, true).Should().Be(VfsStatus.NotDirectory);
            JVM.Vfs.CheckRemove(dir, false).Should().Be(VfsStatus.IsDirectory);
            JVM.Vfs.CheckRemove(dir, true).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckRemove(missing, false).Should().Be(VfsStatus.NotFound);

            JVM.Vfs.CheckOpen(file, false, false, false, false).Should().Be(VfsStatus.Ok);
            JVM.Vfs.CheckOpen(file, true, false, false, false).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckOpen(file, true, true, true, false).Should().Be(VfsStatus.Exists);
            JVM.Vfs.CheckOpen(dir, true, false, false, false).Should().Be(VfsStatus.IsDirectory);
            JVM.Vfs.CheckOpen(missing, true, true, false, false).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckOpen(missing, false, false, false, false).Should().Be(VfsStatus.NotFound);

            JVM.Vfs.CheckRename(file, host).Should().Be(VfsStatus.CrossDevice);
            JVM.Vfs.CheckRename(file, missing).Should().Be(VfsStatus.ReadOnly);
            JVM.Vfs.CheckRename(missing, host).Should().Be(VfsStatus.NotFound);
            JVM.Vfs.CheckLink(file, host).Should().Be(VfsStatus.CrossDevice);
            JVM.Vfs.CheckLink(host, file).Should().Be(VfsStatus.Exists);

            JVM.Vfs.CheckReadLink(file).Should().Be(VfsStatus.NotLink);
            JVM.Vfs.CheckOpenDirectory(file).Should().Be(VfsStatus.NotDirectory);
        }

    }

}
