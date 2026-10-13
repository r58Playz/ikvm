using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.security
{

    [TestClass]
    public class SecurityTests
    {

        [TestMethod]
        public void CanGetProviders()
        {
            // the providers of the modules the class library has (no java.security.jgss, java.smartcardio,
            // jdk.security.jgss, jdk.crypto.cryptoki, or the platform modules of Windows and macOS)
            var l = global::java.security.Security.getProviders();
            l.Should().Contain(i => i.toString() == "SUN version 25");
            l.Should().Contain(i => i.toString() == "SunRsaSign version 25");
            l.Should().Contain(i => i.toString() == "SunEC version 25");
            l.Should().Contain(i => i.toString() == "SunJSSE version 25");
            l.Should().Contain(i => i.toString() == "SunJCE version 25");
            l.Should().Contain(i => i.toString() == "SunSASL version 25");
            l.Should().Contain(i => i.toString() == "XMLDSig version 25");
            l.Should().Contain(i => i.toString() == "JdkLDAP version 25");
        }

    }

}
