using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using FluentAssertions;

using IKVM.ByteCode;
using IKVM.ByteCode.Buffers;
using IKVM.ByteCode.Decoding;
using IKVM.ByteCode.Encoding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Runtime
{

    /// <summary>
    /// The class files a class loader reads from the class path for compiled classes: as OpenJDK 25's would be but for
    /// their method bodies.
    /// </summary>
    [TestClass]
    public class StubGeneratorTests
    {

        static System.Reflection.Assembly fixtures;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public async Task TestInitialize()
        {
            fixtures ??= await Jdk25FixtureImportTests.CompileJar("jdk25.jar", TestContext);
        }

        /// <summary>
        /// A class loader that defines class files, to define stubs.
        /// </summary>
        sealed class StubDefiningClassLoader : global::java.lang.ClassLoader
        {

            public StubDefiningClassLoader() :
                base((global::java.lang.ClassLoader)null)
            {

            }

            public global::java.lang.Class Define(byte[] classFile)
            {
                return defineClass(null, classFile, 0, classFile.Length);
            }

        }

        static byte[] ReadAll(global::java.io.InputStream stream)
        {
            stream.Should().NotBeNull();
            try
            {
                return stream.readAllBytes();
            }
            finally
            {
                stream.close();
            }
        }

        /// <summary>
        /// Reads the class file of a class of the class library from its class's resources.
        /// </summary>
        static byte[] ReadJdkClassFile(Type type, string resource)
        {
            return ReadAll(((global::java.lang.Class)type).getResourceAsStream(resource));
        }

        /// <summary>
        /// The class loader of the compiled fixtures.
        /// </summary>
        static global::java.lang.ClassLoader FixtureLoader => ((global::java.lang.Class)fixtures.GetType("ikvm.tests.jdk25.StubFixture", true)).getClassLoader();

        /// <summary>
        /// An attribute as it is apart from the constant pool it is in: the class file of an empty class with only the
        /// attribute, its constants copied into a new pool in the order the attribute refers to them.
        /// </summary>
        static string DescribeAttribute(ClassFile clazz, in IKVM.ByteCode.Decoding.Attribute attribute)
        {
            var name = clazz.Constants.Get(attribute.Name).Value;
            var builder = new ClassFileBuilder(ClassFormatVersion.Java25, AccessFlag.Public, "X", null);
            attribute.CopyTo(clazz.Constants, builder.Constants, ref builder.Attributes.Encoder);
            var blob = new BlobBuilder();
            builder.Serialize(blob);
            return name + " " + BitConverter.ToString(blob.ToArray());
        }

        /// <summary>
        /// Describes a class file but for its constant pool's layout, its method bodies, the BootstrapMethods they use,
        /// and the IKVM.NET.Assembly attribute of a stub. Whether a method has a body is described.
        /// </summary>
        static List<string> DescribeClassFile(byte[] bytes)
        {
            using var clazz = ClassFile.Read(bytes);
            var constants = clazz.Constants;
            var lines = new List<string>
            {
                $"version {clazz.Version.Major}.{clazz.Version.Minor}",
                $"flags {clazz.AccessFlags}",
                $"class {constants.Get(clazz.This).Name}",
                $"super {(clazz.Super.IsNotNil ? constants.Get(clazz.Super).Name : "")}",
            };

            foreach (var iface in clazz.Interfaces)
                lines.Add($"interface {constants.Get(iface.Class).Name}");

            void DescribeAttributes(string owner, AttributeTable attributes)
            {
                foreach (var attribute in attributes)
                {
                    switch (constants.Get(attribute.Name).Value)
                    {
                        case AttributeName.Code:
                            lines.Add($"{owner} has code");
                            break;
                        case AttributeName.BootstrapMethods:
                        case "IKVM.NET.Assembly":
                            break;
                        default:
                            lines.Add($"{owner} {DescribeAttribute(clazz, attribute)}");
                            break;
                    }
                }
            }

            foreach (var field in clazz.Fields)
            {
                var owner = $"field {constants.Get(field.Name).Value} {constants.Get(field.Descriptor).Value}";
                lines.Add($"{owner} flags {field.AccessFlags}");
                DescribeAttributes(owner, field.Attributes);
            }

            foreach (var method in clazz.Methods)
            {
                var owner = $"method {constants.Get(method.Name).Value}{constants.Get(method.Descriptor).Value}";
                lines.Add($"{owner} flags {method.AccessFlags}");
                DescribeAttributes(owner, method.Attributes);
            }

            DescribeAttributes("class", clazz.Attributes);
            return lines;
        }

        static IKVM.ByteCode.Decoding.Attribute? FindAttribute(ClassFile clazz, string name)
        {
            foreach (var attribute in clazz.Attributes)
                if (clazz.Constants.Get(attribute.Name).Value == name)
                    return attribute;

            return null;
        }

        [TestMethod]
        public void StubOfCompiledClassIsItsClassFile()
        {
            // every class javac 25 compiled for the fixtures: its stub is its class file but for the method bodies
            var root = Path.Combine(AppContext.BaseDirectory, "jdk25");
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.class", SearchOption.AllDirectories))
            {
                var resource = file.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                var stub = ReadAll(FixtureLoader.getResourceAsStream(resource));
                DescribeClassFile(stub).Should().Equal(DescribeClassFile(File.ReadAllBytes(file)), resource);
                count++;
            }

            count.Should().BeGreaterThan(10);
        }

        [TestMethod]
        public void StubOfCompiledClassNamesItsAssembly()
        {
            using var clazz = ClassFile.Read(ReadAll(FixtureLoader.getResourceAsStream("ikvm/tests/jdk25/StubFixture.class")));
            var attribute = FindAttribute(clazz, "IKVM.NET.Assembly");
            attribute.Should().NotBeNull();
            var reader = new ClassFormatReader(attribute.Value.Data);
            reader.TryReadU2(out var index).Should().BeTrue();
            clazz.Constants.Get(new Utf8ConstantHandle(index)).Value.Should().Be(fixtures.FullName);
        }

        [TestMethod]
        public void StubOfInterfaceWithMethodBodiesCanBeDefined()
        {
            // an interface's default, static and private methods have bodies (an interface method may not be native),
            // and defining the stub gives the compiled class
            var withBodies = global::java.lang.Class.forName("ikvm.tests.jdk25.StubFixture$WithBodies", false, FixtureLoader);
            new StubDefiningClassLoader().Define(ReadAll(FixtureLoader.getResourceAsStream("ikvm/tests/jdk25/StubFixture$WithBodies.class"))).Should().BeSameAs(withBodies);

            var charSequence = (global::java.lang.Class)typeof(global::java.lang.CharSequence);
            new StubDefiningClassLoader().Define(ReadJdkClassFile(typeof(global::java.lang.CharSequence), "CharSequence.class")).Should().BeSameAs(charSequence);
        }

        [TestMethod]
        public void StubOfJdkClassHasItsNest()
        {
            using var clazz = ClassFile.Read(ReadJdkClassFile(typeof(global::java.util.HashMap), "HashMap$Node.class"));
            clazz.Version.Should().Be(ClassFormatVersion.Java25);
            var nestHost = FindAttribute(clazz, AttributeName.NestHost);
            nestHost.Should().NotBeNull();
            clazz.Constants.Get(((IKVM.ByteCode.Decoding.NestHostAttribute)nestHost.Value).NestHost).Name.Should().Be("java/util/HashMap");
        }

        [TestMethod]
        public void StubOfJdkSealedInterfaceHasItsPermittedSubclasses()
        {
            using var clazz = ClassFile.Read(ReadJdkClassFile(typeof(global::java.lang.constant.ConstantDesc), "ConstantDesc.class"));
            var permitted = FindAttribute(clazz, AttributeName.PermittedSubclasses);
            permitted.Should().NotBeNull();
            var subclasses = ((IKVM.ByteCode.Decoding.PermittedSubclassesAttribute)permitted.Value).PermittedSubclasses;
            var names = Enumerable.Range(0, subclasses.Count).Select(i => clazz.Constants.Get(subclasses[i]).Name.Replace('/', '.')).ToList();
            var expected = ((global::java.lang.Class)typeof(global::java.lang.constant.ConstantDesc)).getPermittedSubclasses().Select(i => i.getName()).ToList();
            names.Should().BeEquivalentTo(expected);
        }

        [TestMethod]
        public void StubOfJdkRecordHasItsComponents()
        {
            using var clazz = ClassFile.Read(ReadJdkClassFile(typeof(global::java.lang.reflect.Proxy), "Proxy$ProxyBuilder$ProxyClassContext.class"));
            var record = FindAttribute(clazz, AttributeName.Record);
            record.Should().NotBeNull();
            var components = ((IKVM.ByteCode.Decoding.RecordAttribute)record.Value).Components;
            Enumerable.Range(0, components.Count).Select(i => clazz.Constants.Get(components[i].Name).Value).Should().Equal("module", "packageName", "accessFlags");
        }

        [TestMethod]
        public void GeneratedStubOfRemappedClassIsVersion69()
        {
            // java.lang.String is not compiled from a class file but mapped onto System.String: its stub is generated
            var bytes = ReadJdkClassFile(typeof(global::java.lang.Object), "/java/lang/String.class");
            using (var clazz = ClassFile.Read(bytes))
            {
                clazz.Version.Should().Be(ClassFormatVersion.Java25);
                FindAttribute(clazz, "IKVM.NET.Assembly").Should().NotBeNull();

                // only the native methods are native, and only those and abstract methods lack a body
                foreach (var method in clazz.Methods)
                {
                    var hasCode = method.Attributes.Any(i => clazz.Constants.Get(i.Name).Value == AttributeName.Code);
                    hasCode.Should().Be((method.AccessFlags & (AccessFlag.Native | AccessFlag.Abstract)) == 0, clazz.Constants.Get(method.Name).Value);
                }
            }

            new StubDefiningClassLoader().Define(bytes).Should().BeSameAs((global::java.lang.Class)typeof(string));
        }

        [TestMethod]
        public void ModuleHasItsModuleInfo()
        {
            var module = ((global::java.lang.Class)typeof(global::java.lang.Object)).getModule();
            var descriptor = global::java.lang.module.ModuleDescriptor.read(module.getResourceAsStream("module-info.class"));
            descriptor.name().Should().Be("java.base");
            descriptor.equals(module.getDescriptor()).Should().BeTrue();

            // through a class of the module too
            var other = global::java.lang.module.ModuleDescriptor.read(((global::java.lang.Class)typeof(global::java.util.logging.Logger)).getResourceAsStream("/module-info.class"));
            other.name().Should().Be("java.logging");
        }

        [TestMethod]
        public void ModuleAnnotationsAreReadFromItsModuleInfo()
        {
            // Module defines its module-info.class, made an interface, to read the module's annotations: java.base has none
            var module = ((global::java.lang.Class)typeof(global::java.lang.Object)).getModule();
            module.getAnnotations().Should().BeEmpty();
            module.getAnnotation((global::java.lang.Class)typeof(global::java.lang.Deprecated)).Should().BeNull();
        }

        [TestMethod]
        public void SystemResourcesHaveEachModulesModuleInfo()
        {
            var urls = new List<string>();
            var e = global::java.lang.ClassLoader.getSystemResources("module-info.class");
            while (e.hasMoreElements())
                urls.Add(((global::java.net.URL)e.nextElement()).toString());

            var modules = global::java.lang.ModuleLayer.boot().modules().iterator();
            while (modules.hasNext())
            {
                var name = ((global::java.lang.Module)modules.next()).getName();
                urls.Should().Contain(i => i.EndsWith("/modules/" + name + "/module-info.class"), name);
            }
        }

    }

}
