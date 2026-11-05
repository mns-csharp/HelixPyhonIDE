using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Projection;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 355 (SymPy drill-down screenshot audit): a type box on the Designer canvas rendered
    /// "handle_starttag(tag: , attr: ) : void" — a dangling colon after every unannotated Python
    /// parameter and a C# "void" return that Python does not have. Both are pure rendering: the
    /// model carries empty TypeName / ReturnType for unannotated Python, and the formatter invented
    /// syntax to fill the gap. A commercial IDE must render Python as Python.
    /// </summary>
    [TestClass]
    public class PythonMemberSignatureFormatTests
    {
        [TestMethod]
        public void UnannotatedParameters_RenderWithoutADanglingColon()
        {
            var m = new MemberModel("handle_starttag", MemberKind.Method) { AccessModifier = AccessModifier.Public };
            m.Parameters.Add(new MethodParameterModel("tag", null));
            m.Parameters.Add(new MethodParameterModel("attr", ""));

            string line = FirstLine(m);

            StringAssert.Contains(line, "handle_starttag(tag, attr)");
            Assert.IsFalse(line.Contains("tag: "), "no dangling colon on an unannotated parameter");
            Assert.IsFalse(line.Contains("attr: "), "no dangling colon on an unannotated parameter");
        }

        [TestMethod]
        public void AnnotatedParameters_KeepTheirType()
        {
            var m = new MemberModel("connect", MemberKind.Method) { AccessModifier = AccessModifier.Public };
            m.Parameters.Add(new MethodParameterModel("host", "str"));
            m.Parameters.Add(new MethodParameterModel("port", "int"));

            StringAssert.Contains(FirstLine(m), "connect(host: str, port: int)");
        }

        [TestMethod]
        public void AMethodWithNoReturnAnnotation_ShowsNoVoid()
        {
            var m = new MemberModel("run", MemberKind.Method) { AccessModifier = AccessModifier.Public };

            string line = FirstLine(m);

            Assert.IsFalse(line.Contains("void"), "Python has no void");
            Assert.IsFalse(line.Contains(" : "), "no UML return-suffix when there is no annotation");
            StringAssert.Contains(line, "run()");
        }

        [TestMethod]
        public void AnAnnotatedReturn_IsStillShown()
        {
            var m = new MemberModel("total", MemberKind.Method) { AccessModifier = AccessModifier.Public, ReturnType = "int" };

            StringAssert.Contains(FirstLine(m), "total() : int");
        }

        [TestMethod]
        public void AConstructor_DoesNotGainAVoidReturn()
        {
            var m = new MemberModel("__init__", MemberKind.Constructor) { AccessModifier = AccessModifier.Public };
            m.Parameters.Add(new MethodParameterModel("value", null));

            string line = FirstLine(m);

            Assert.IsFalse(line.Contains("void"));
            StringAssert.Contains(line, "__init__(value)");
        }

        [TestMethod]
        public void AnUnannotatedField_HasNoObjectPlaceholder()
        {
            var m = new MemberModel("is_imported", MemberKind.Field) { AccessModifier = AccessModifier.Public };

            string line = FirstLine(m);

            Assert.IsFalse(line.Contains("object"), "an untyped Python attribute is not ': object'");
            Assert.IsFalse(line.Contains(" : "), "no type suffix when there is no annotation");
            StringAssert.Contains(line, "is_imported");
        }

        [TestMethod]
        public void AnAnnotatedField_KeepsItsType()
        {
            var m = new MemberModel("count", MemberKind.Field) { AccessModifier = AccessModifier.Public, ReturnType = "int" };
            StringAssert.Contains(FirstLine(m), "count : int");
        }

        [TestMethod]
        public void AnUnannotatedProperty_ShowsNameAndAccessorsButNoObjectType()
        {
            var m = new MemberModel("value", MemberKind.Property)
            { AccessModifier = AccessModifier.Public, HasGetter = true, HasSetter = true };

            string line = FirstLine(m);

            Assert.IsFalse(line.Contains("object"), "an untyped property is not ': object'");
            Assert.IsFalse(line.Contains(" : "), "no type suffix when there is no annotation");
            StringAssert.Contains(line, "value { get; set; }");
            Assert.IsFalse(line.EndsWith(" "), "no dangling trailing space");
        }

        [TestMethod]
        public void AnAnnotatedProperty_KeepsItsType()
        {
            var m = new MemberModel("name", MemberKind.Property)
            { AccessModifier = AccessModifier.Public, HasGetter = true, ReturnType = "str" };
            StringAssert.Contains(FirstLine(m), "name : str { get; }");
        }

        // FormatMemberLines is private static on the projection builder; reflect one member through it.
        private static string FirstLine(MemberModel member)
        {
            var solution = new SolutionModel(@"D:\r\pyproject.toml", "s");
            var project = new ProjectModel(@"D:\r\pyproject.toml", "p", solution);
            var type = new TypeModel("T", "ns", TypeKind.ConcreteClass, project);
            type.Members.Add(member);

            MethodInfo mi = typeof(SolutionProjectionBuilder).GetMethod(
                "FormatMemberLines", BindingFlags.Static | BindingFlags.NonPublic);
            var lines = (List<string>)mi.Invoke(null, new object[] { type });
            Assert.AreEqual(1, lines.Count, "one member in, one line out");
            return lines[0];
        }
    }
}
