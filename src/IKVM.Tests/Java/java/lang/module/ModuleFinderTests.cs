using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.lang.module
{

    [TestClass]
    public class ModuleFinderTests
    {

        [TestMethod]
        public void SystemFinderFindsModulesOfClassLibrary()
        {
            // there is no lib/modules: the system modules are those of the exploded image, the class library's
            var finder = global::java.lang.module.ModuleFinder.ofSystem();
            finder.find("java.desktop").isPresent().Should().BeTrue();
            finder.find("no.such.module").isPresent().Should().BeFalse();

            var reference = (global::java.lang.module.ModuleReference)finder.find("java.base").get();
            reference.descriptor().name().Should().Be("java.base");
            reference.descriptor().packages().contains("java.lang").Should().BeTrue();

            var reader = reference.open();
            try
            {
                reader.find("java/lang/Object.class").isPresent().Should().BeTrue();
                reader.find("java/lang/NoSuchClass.class").isPresent().Should().BeFalse();
            }
            finally
            {
                reader.close();
            }
        }

    }

}
