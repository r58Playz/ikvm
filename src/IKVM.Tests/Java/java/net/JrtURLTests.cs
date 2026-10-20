using System.IO;

using FluentAssertions;

using IKVM.Runtime;
using IKVM.Runtime.Vfs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.net
{

    /// <summary>
    /// jrt: URLs, which name the resources of the modules of the run-time image.
    /// </summary>
    [TestClass]
    public class JrtURLTests
    {

        static string ClassesPath => VfsTable.GetAssemblyClassesPath(JVM.Vfs.Context, typeof(global::java.lang.Object).Assembly, JVM.Properties.HomePath);

        static byte[] ReadVfs(string path)
        {
            using var s = JVM.Vfs.Open(path, FileMode.Open, FileAccess.Read);
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }

        static byte[] Read(string url)
        {
            var s = new global::java.net.URL(url).openStream();
            try
            {
                return s.readAllBytes();
            }
            finally
            {
                s.close();
            }
        }

        [TestMethod]
        public void CanReadClassFile()
        {
            // the stub of a class is the one the class path has
            Read("jrt:/java.base/java/lang/String.class").Should().Equal(ReadVfs(System.IO.Path.Combine(ClassesPath, "java", "lang", "String.class")));
            Read("jrt:/java.base/java/util/ArrayList.class").Should().Equal(ReadVfs(System.IO.Path.Combine(ClassesPath, "java", "util", "ArrayList.class")));
            Read("jrt:/java.desktop/java/awt/Component.class").Should().Equal(ReadVfs(System.IO.Path.Combine(ClassesPath, "java", "awt", "Component.class")));
        }

        [TestMethod]
        public void CanReadModuleInfo()
        {
            global::java.lang.module.ModuleDescriptor.read(global::java.nio.ByteBuffer.wrap(Read("jrt:/java.base/module-info.class"))).name().Should().Be("java.base");
        }

        [TestMethod]
        public void HasContentLength()
        {
            var c = new global::java.net.URL("jrt:/java.base/java/util/ArrayList.class").openConnection();
            c.getContentLengthLong().Should().Be(ReadVfs(System.IO.Path.Combine(ClassesPath, "java", "util", "ArrayList.class")).Length);
            new global::java.net.URL("jrt:/java.base/java/lang/NoSuchClass.class").openConnection().getContentLengthLong().Should().Be(-1);
        }

        [TestMethod]
        public void MissingIsNotFound()
        {
            foreach (var url in new[] {
                "jrt:/java.base/java/lang/NoSuchClass.class",
                "jrt:/no.such.module/java/lang/Object.class",
                "jrt:/java.desktop/java/lang/Object.class",
                "jrt:/java.base/java/lang",
                "jrt:/java.base/java/lang/" })
                FluentActions.Invoking(() => Read(url)).Should().Throw<global::java.io.IOException>(url);

            // a module, or none, is no resource
            FluentActions.Invoking(() => Read("jrt:/java.base")).Should().Throw<global::java.io.IOException>();
            FluentActions.Invoking(() => Read("jrt:/")).Should().Throw<global::java.io.IOException>();
        }

        [TestMethod]
        public void CannotLeaveModule()
        {
            // the release file is in java.home, which the modules directory is in
            System.IO.File.Exists(System.IO.Path.Combine(JVM.Properties.HomePath, "release")).Should().BeTrue();

            // URL removes the '.' and '..' segments it can from a path, but not those it decodes later, nor a leading
            // one: a name the run-time image has none of, with an empty, '.' or '..' element, is not looked for
            foreach (var url in new[] {
                "jrt:/../release",
                "jrt:/java.base/../../release",
                "jrt:/java.base/%2E%2E/%2E%2E/release",
                "jrt:/java.base/java%2F..%2F..%2F..%2Frelease",
                "jrt:/java.base/java/lang/%2E%2E/lang/Object.class",
                "jrt:/java.base/%2E/java/lang/Object.class",
                "jrt:/java.base//java/lang/Object.class",
                "jrt:/java.base/java%5Clang%5CObject.class" })
                FluentActions.Invoking(() => Read(url)).Should().Throw<global::java.io.IOException>(url);
        }

    }

}
