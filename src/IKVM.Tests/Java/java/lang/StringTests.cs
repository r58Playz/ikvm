using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IKVM.Tests.Java.java.lang
{

    [TestClass]
    public class StringTests
    {

        static global::java.lang.Class StringClass => (global::java.lang.Class)typeof(string);

        static global::java.lang.Class ObjectArrayClass => (global::java.lang.Class)typeof(object[]);

        [TestMethod]
        public void FormattedIsVarArgs()
        {
            // an instance method of String is a static helper of System.String, which is sealed, whose modifiers are
            // given by ModifiersAttribute: they have varargs as the ParamArrayAttribute of its last parameter does
            var m = StringClass.getMethod("formatted", ObjectArrayClass);
            m.isVarArgs().Should().BeTrue();
            m.getModifiers().Should().Be(global::java.lang.reflect.Modifier.PUBLIC | 0x80);
            ((string)m.invoke("%s-%s", new object[] { new object[] { "a", "b" } })).Should().Be("a-b");
        }

        [TestMethod]
        public void FormatIsVarArgs()
        {
            StringClass.getMethod("format", StringClass, ObjectArrayClass).isVarArgs().Should().BeTrue();
            StringClass.getMethod("format", (global::java.lang.Class)typeof(global::java.util.Locale), StringClass, ObjectArrayClass).isVarArgs().Should().BeTrue();
        }

    }

}
