using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using java.io;
using java.lang;

namespace IKVM.Java.Tests.Util
{

    /// <summary>
    /// Provides methods to compile a set of sources into a Java classes.
    /// </summary>
    /// <remarks>
    /// The class library has no javac (jdk.compiler), so the sources are compiled by the javac of a JDK 25 or later on
    /// the host: JDK_HOME's or JAVA_HOME's if either is one, else the newest under /usr/lib/jvm. The classes are loaded
    /// from memory by a class loader of their own.
    /// </remarks>
    public class InMemoryCompiler
    {

        /// <summary>
        /// The Java SE release the sources are compiled for: the class library's.
        /// </summary>
        const int Release = 25;

        static readonly Lazy<string> javac = new(FindJavac);

        /// <summary>
        /// Class loader which reads from the compiled class files.
        /// </summary>
        class InMemoryClassLoader : ClassLoader
        {

            readonly ConcurrentDictionary<string, byte[]> classes;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="classes"></param>
            public InMemoryClassLoader(ConcurrentDictionary<string, byte[]> classes)
            {
                this.classes = classes ?? throw new ArgumentNullException(nameof(classes));
            }

            /// <summary>
            /// Finds the class with the specified name.
            /// </summary>
            /// <param name="className"></param>
            /// <returns></returns>
            protected override Class findClass(string className)
            {
                if (classes.TryGetValue(className, out var b))
                    return defineClass(className, b, 0, b.Length);

                throw new ClassNotFoundException(className);
            }

        }

        readonly InMemoryCodeUnit[] units;
        readonly ConcurrentDictionary<string, byte[]> classes = new();
        readonly ClassLoader classLoader;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="source"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public InMemoryCompiler(InMemoryCodeUnit[] source)
        {
            this.units = source ?? throw new ArgumentNullException(nameof(source));
            this.classLoader = new InMemoryClassLoader(classes);
        }

        /// <summary>
        /// Compiles the added java code.
        /// </summary>
        public void Compile()
        {
            if (units.Length == 0)
                return;

            var javacPath = javac.Value ?? throw new System.Exception($"No javac of a JDK {Release} or later: set JDK_HOME or JAVA_HOME to one.");
            var dir = Path.Combine(Path.GetTempPath(), "ikvm-inmemorycompiler-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(dir, "src");
            var bin = Path.Combine(dir, "classes");

            try
            {
                Directory.CreateDirectory(bin);

                // a source file per unit, where javac expects a public class of its name
                var files = new List<string>();
                foreach (var unit in units)
                {
                    var file = Path.Combine(src, unit.Name.Replace('.', Path.DirectorySeparatorChar) + ".java");
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    System.IO.File.WriteAllText(file, unit.Code, new UTF8Encoding(false));
                    files.Add(file);
                }

                var psi = new ProcessStartInfo(javacPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var arg in new[] { "-g", "-nowarn", "-encoding", "UTF-8", "--release", Release.ToString(), "-d", bin }.Concat(files))
                    psi.ArgumentList.Add(arg);

                using var process = System.Diagnostics.Process.Start(psi);
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new System.Exception(stdout + stderr.Result);

                foreach (var file in Directory.EnumerateFiles(bin, "*.class", SearchOption.AllDirectories))
                {
                    var name = Path.GetRelativePath(bin, file);
                    name = name.Substring(0, name.Length - ".class".Length).Replace(Path.DirectorySeparatorChar, '.');
                    classes[name] = System.IO.File.ReadAllBytes(file);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (System.IO.IOException)
                {
                    // a temporary directory left behind is harmless
                }
            }
        }

        /// <summary>
        /// Gets the compiled class with the given name.
        /// </summary>
        /// <param name="className"></param>
        /// <returns></returns>
        /// <exception cref="java.lang.ClassNotFoundException"></exception>
        public Class GetClass(string className)
        {
            var cls = classLoader.loadClass(className);
            if (cls == null)
                throw new ClassNotFoundException("Class returned by ClassLoader was null!");

            return cls;
        }

        /// <summary>
        /// Writes the compiled classes to a JAR.
        /// </summary>
        /// <param name="path"></param>
        public void WriteJar(string path)
        {
            using var jar = System.IO.File.OpenWrite(path);
            WriteJar(jar);
        }

        /// <summary>
        /// Writes the compiled classes to a JAR on the given stream.
        /// </summary>
        /// <param name="stream"></param>
        public void WriteJar(Stream stream)
        {
            // write to temporary stream
            var buf = new ByteArrayOutputStream();
            WriteJar(buf);
            stream.Write(buf.toByteArray(), 0, buf.size());
        }

        /// <summary>
        /// Writes the compiled classes to a JAR on the given stream.
        /// </summary>
        /// <param name="stream"></param>
        public void WriteJar(OutputStream stream)
        {
            var man = new java.util.jar.Manifest();
            man.getMainAttributes().put(java.util.jar.Attributes.Name.MANIFEST_VERSION, "1.0");
            var jar = new java.util.jar.JarOutputStream(stream, man);
            WriteJar(jar);
            jar.close();
        }

        /// <summary>
        /// Writes the compiled classes to a JAR.
        /// </summary>
        /// <param name="jar"></param>
        public void WriteJar(java.util.jar.JarOutputStream jar)
        {
            foreach (var i in classes)
            {
                var e = new java.util.jar.JarEntry(i.Key.Replace(".", "/") + ".class");
                jar.putNextEntry(e);
                jar.write(i.Value);
                jar.closeEntry();
            }
        }

        /// <summary>
        /// Finds the javac of a JDK of <see cref="Release"/> or later.
        /// </summary>
        /// <returns></returns>
        static string FindJavac()
        {
            var homes = new List<string>();
            foreach (var env in new[] { "JDK_HOME", "JAVA_HOME" })
                if (Environment.GetEnvironmentVariable(env) is string home && string.IsNullOrWhiteSpace(home) == false)
                    homes.Add(home);

            if (Directory.Exists("/usr/lib/jvm"))
                homes.AddRange(Directory.GetDirectories("/usr/lib/jvm").OrderByDescending(GetFeatureVersion));

            var exe = OperatingSystem.IsWindows() ? "javac.exe" : "javac";
            foreach (var home in homes)
                if (GetFeatureVersion(home) >= Release && System.IO.File.Exists(Path.Combine(home, "bin", exe)))
                    return Path.Combine(home, "bin", exe);

            return null;
        }

        /// <summary>
        /// Gets the feature version of the JDK at the given home from its release file, or 0.
        /// </summary>
        /// <param name="home"></param>
        /// <returns></returns>
        static int GetFeatureVersion(string home)
        {
            try
            {
                var release = Path.Combine(home, "release");
                if (System.IO.File.Exists(release) == false)
                    return 0;

                foreach (var line in System.IO.File.ReadLines(release))
                {
                    if (line.StartsWith("JAVA_VERSION=") == false)
                        continue;

                    // JAVA_VERSION="25.0.1", or "1.8.0_472" before JDK 9
                    var v = line.Substring("JAVA_VERSION=".Length).Trim('"').Split('.', '_', '-', '+');
                    if (int.TryParse(v[0], out var major))
                        return major == 1 && v.Length > 1 && int.TryParse(v[1], out var minor) ? minor : major;
                }
            }
            catch (System.IO.IOException)
            {

            }

            return 0;
        }

    }

}
