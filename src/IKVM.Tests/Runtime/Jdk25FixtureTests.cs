using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using FluentAssertions;

using IKVM.Tests.Util;
using IKVM.Tools.Runner.Diagnostics;
using IKVM.Tools.Runner.Importer;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Runtime
{

    /// <summary>
    /// Runs classes compiled by javac 25 (class file version 69) from the Jdk25 fixture sources. Each fixture has a
    /// static String run() method.
    /// </summary>
    public abstract class Jdk25FixtureTests
    {

        protected abstract java.lang.Class GetFixture(string className);

        string Run(string className)
        {
            try
            {
                return (string)GetFixture(className).getMethod("run").invoke(null);
            }
            catch (java.lang.reflect.InvocationTargetException e)
            {
                // fail with what run() threw, and its Java stack trace
                var cause = (Exception)e.getCause();
                throw new Exception("run() threw " + cause + Environment.NewLine + string.Join(Environment.NewLine, Array.ConvertAll(global::ikvm.extensions.ExtensionMethods.getStackTrace(cause), f => "    at " + f)), cause);
            }
        }

        [TestMethod]
        public void Nestmates()
        {
            Run("ikvm.tests.jdk25.Nestmates").Should().Be("30,10,31,3,300");
        }

        [TestMethod]
        public void StringConcat()
        {
            Run("ikvm.tests.jdk25.StringConcat").Should().Be("i=42 l=-7 c=x b=true by=3 sh=-4 f=1.5 d=0.30000000000000004 nul=null s=str ns=null o=obj arr=null|6\u0001\u0002|AB12|42");
        }

        [TestMethod]
        public void Switches()
        {
            Run("ikvm.tests.jdk25.Switches").Should().Be("null,empty,string3,int,big,ints3,shape12,shape9,shape10,other,|A,B,long,short,|one,two,n,huge,|a,digit,char,|red,green,other,");
        }

        [TestMethod]
        public void Condy()
        {
            Run("ikvm.tests.jdk25.Condy").Should().Be("HELLO,42,1099511627776,null,long,SECONDS,2147483647,HELLO,3");
        }

        [TestMethod]
        public void ObjectMethods()
        {
            Run("ikvm.tests.jdk25.ObjectMethodsFixture").Should().Be("ObjMethods[a=1, b=one, c=1.5]|ObjMethods[a=2, b=null, c=-0.0]|true,true,false,false,true,false,false|true,1076634139,-2147481726");
        }

        [TestMethod]
        public void Sealed()
        {
            Run("ikvm.tests.jdk25.SealedFixture").Should().Be("ok,icce,icce");
        }

        [TestMethod]
        public void StubFixture()
        {
            Run("ikvm.tests.jdk25.StubFixture").Should().Be("7,7,local,anonymous,10,3,4");
        }

        /// <summary>
        /// Loads a class of the fixtures through the class loader of StubFixture: a nested class is not a type of the
        /// compiled assembly by its Java name.
        /// </summary>
        java.lang.Class GetFixtureClass(string className)
        {
            return java.lang.Class.forName(className, true, GetFixture("ikvm.tests.jdk25.StubFixture").getClassLoader());
        }

        [TestMethod]
        public void NestHostAndMembers()
        {
            var host = GetFixtureClass("ikvm.tests.jdk25.StubFixture");
            var inner = GetFixtureClass("ikvm.tests.jdk25.StubFixture$Inner");
            inner.getNestHost().Should().BeSameAs(host);
            host.getNestHost().Should().BeSameAs(host);
            host.getNestMembers().Should().Contain(inner);
            inner.isNestmateOf(host).Should().BeTrue();
        }

        [TestMethod]
        public void RecordComponentAnnotations()
        {
            var components = GetFixtureClass("ikvm.tests.jdk25.StubFixture$Circle").getRecordComponents();
            components.Should().HaveCount(2);

            // the RUNTIME annotation is there, the CLASS one is not
            var annotations = components[0].getAnnotations();
            annotations.Should().ContainSingle();
            var visible = (java.lang.annotation.Annotation)annotations[0];
            visible.annotationType().getName().Should().Be("ikvm.tests.jdk25.StubFixture$Visible");
            ((string)visible.annotationType().getMethod("value").invoke(visible)).Should().Be("radius");

            // the type annotation of the component's type
            var typeAnnotations = components[1].getAnnotatedType().getAnnotations();
            typeAnnotations.Should().ContainSingle();
            ((java.lang.annotation.Annotation)typeAnnotations[0]).annotationType().getName().Should().Be("ikvm.tests.jdk25.StubFixture$TypeUse");
        }

    }

    /// <summary>
    /// The fixtures loaded from class files through a Java class loader, compiled by IKVM at run time.
    /// </summary>
    [TestClass]
    public class Jdk25FixtureRuntimeTests : Jdk25FixtureTests
    {

        static java.lang.ClassLoader loader;

        protected override java.lang.Class GetFixture(string className)
        {
            loader ??= new java.net.URLClassLoader(new[] { new java.io.File(Path.Combine(AppContext.BaseDirectory, "jdk25")).toURI().toURL() });
            return java.lang.Class.forName(className, true, loader);
        }

        [TestMethod]
        public void MultiReleaseJar()
        {
            // the class library's JarFile picks the entries of a multi-release JAR, so Java 8 sees only the base ones
            var jar = new java.io.File(Path.Combine(AppContext.BaseDirectory, "jdk25mr.jar"));
            var mrLoader = new java.net.URLClassLoader(new[] { jar.toURI().toURL() });
            var clazz = java.lang.Class.forName("ikvm.tests.mr.Release", true, mrLoader);
            ((string)clazz.getMethod("run").invoke(null)).Should().Be(Jdk25FixtureImportTests.ExpectedMultiReleaseResult);
        }

    }

    /// <summary>
    /// The fixtures compiled ahead of time by ikvmc.
    /// </summary>
    [TestClass]
    public class Jdk25FixtureImportTests : Jdk25FixtureTests
    {

        /// <summary>
        /// The version of ikvm.tests.mr.Release a JDK of this IKVM's version uses from jdk25mr.jar: Java 8 knows only the
        /// base entries, Java 11 and later use the META-INF/versions/11 one.
        /// </summary>
        internal static string ExpectedMultiReleaseResult => java.lang.System.getProperty("java.specification.version") == "1.8" ? "base" : "11";

        static Assembly assembly;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public async Task TestInitialize()
        {
            assembly ??= await CompileJar("jdk25.jar", TestContext);
        }

        /// <summary>
        /// Compiles a JAR from the test output directory with ikvmc and loads the result.
        /// </summary>
        internal static async Task<Assembly> CompileJar(string jar, TestContext testContext)
        {
#if NET472
            var ikvmTool = "net472";
            var ikvmLibs = Path.Combine(AppContext.BaseDirectory, "lib", "net472");
            var libPaths = DotNetSdkUtil.GetPathToReferenceAssemblies("net472", ".NETFramework", "4.7.2");
#elif NET6_0 || NET7_0
            var ikvmTool = "net10.0";
            var ikvmLibs = Path.Combine(AppContext.BaseDirectory, "lib", "net6.0");
            var libPaths = DotNetSdkUtil.GetPathToReferenceAssemblies("net6.0", ".NET", "6.0");
#else
            var ikvmTool = "net10.0";
            var ikvmLibs = Path.Combine(AppContext.BaseDirectory, "lib", "net8.0");
            var libPaths = DotNetSdkUtil.GetPathToReferenceAssemblies("net8.0", ".NET", "8.0");
#endif

            var rid = RuntimeInformation.RuntimeIdentifier switch
            {
                var r when r.StartsWith("win") => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64",
                var r when r.StartsWith("osx") => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64",
                _ => RuntimeInformation.ProcessArchitecture switch { Architecture.Arm64 => "linux-arm64", Architecture.Arm => "linux-arm", _ => "linux-x64" },
            };

            var n = Guid.NewGuid().ToString("n");
            var name = Path.GetFileNameWithoutExtension(jar) + "_" + n;
            var output = Path.Combine(Path.GetTempPath(), n, name + ".dll");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var launcher = new IkvmImporterLauncher(Path.Combine(AppContext.BaseDirectory, "ikvmc", ikvmTool, rid), new IkvmToolDelegateDiagnosticListener(evt => testContext.WriteLine(evt.Message, evt.Args)));
            var options = new IkvmImporterOptions()
            {
                Runtime = Path.Combine(ikvmLibs, "IKVM.Runtime.dll"),
                ResponseFile = $"{n}_ikvmc.rsp",
                Input = { Path.Combine(AppContext.BaseDirectory, jar) },
                Assembly = name,
                Version = "1.0.0.0",
                NoStdLib = true,
                Output = output,
                Lib = [.. libPaths, ikvmLibs],
            };

            // the framework assemblies are referenced explicitly: with -nostdlib, ikvmc fails on the first class it
            // cannot find (MatchException, which the Java 8 class library lacks) when the assemblies its lookup walks
            // through are not all resolvable
            foreach (var dir in libPaths)
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    options.References.Add(dll);

            (await launcher.ExecuteAsync(options)).Should().Be(0);
            return Assembly.LoadFrom(output);
        }

        protected override java.lang.Class GetFixture(string className)
        {
            return assembly.GetType(className, true);
        }

        [TestMethod]
        public async Task MultiReleaseJar()
        {
            // ikvmc compiles the entries of a multi-release JAR that this IKVM's Java version would use, and keeps its
            // module-info.class as it is rather than compiling it
            var mr = await CompileJar("jdk25mr.jar", TestContext);
            var clazz = (java.lang.Class)mr.GetType("ikvm.tests.mr.Release", true);
            ((string)clazz.getMethod("run").invoke(null)).Should().Be(ExpectedMultiReleaseResult);
            mr.GetType("module-info").Should().BeNull();
            mr.GetManifestResourceNames().Should().Contain("module-info-ikvm.tests.mr.class");
        }

    }

}
