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
    /// static String run() method. The fixtures use only Java 8 APIs, so these run against either JDK flavor.
    /// </summary>
    public abstract class Jdk25FixtureTests
    {

        protected abstract java.lang.Class GetFixture(string className);

        string Run(string className)
        {
            return (string)GetFixture(className).getMethod("run").invoke(null);
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

    }

    /// <summary>
    /// The fixtures compiled ahead of time by ikvmc.
    /// </summary>
    [TestClass]
    public class Jdk25FixtureImportTests : Jdk25FixtureTests
    {

        static Assembly assembly;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public async Task TestInitialize()
        {
            if (assembly != null)
                return;

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
            var output = Path.Combine(Path.GetTempPath(), n, $"jdk25_{n}.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var events = new List<IkvmToolDiagnosticEvent>();
            var launcher = new IkvmImporterLauncher(Path.Combine(AppContext.BaseDirectory, "ikvmc", ikvmTool, rid), new IkvmToolDelegateDiagnosticListener(evt => { events.Add(evt); TestContext.WriteLine(evt.Message, evt.Args); }));
            var options = new IkvmImporterOptions()
            {
                Runtime = Path.Combine(ikvmLibs, "IKVM.Runtime.dll"),
                ResponseFile = $"{n}_ikvmc.rsp",
                Input = { Path.Combine(AppContext.BaseDirectory, "jdk25.jar") },
                Assembly = $"jdk25_{n}",
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
            assembly = Assembly.LoadFrom(output);
        }

        protected override java.lang.Class GetFixture(string className)
        {
            return assembly.GetType(className, true);
        }

    }

}
