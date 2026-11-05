using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeParserTests
    {
        [TestMethod]
        public void Parses_CommandsAndArguments_WithPositions()
        {
            var f = CMakeParser.Parse("cmake_minimum_required(VERSION 3.20)\nproject(demo LANGUAGES CXX)\n");
            Assert.IsFalse(f.HasErrors, string.Join("; ", f.Diagnostics.Select(d => d.ToString())));
            Assert.AreEqual(2, f.Commands.Count);
            Assert.AreEqual("cmake_minimum_required", f.Commands[0].Name);
            Assert.AreEqual("VERSION", f.Commands[0].Arguments[0].Text);
            Assert.AreEqual(2, f.Commands[1].Line);          // project() is on line 2
            Assert.AreEqual("demo", f.Commands[1].Arguments[0].Text);
        }

        [TestMethod]
        public void Builds_IfBlockNesting_WithMatchingEnd()
        {
            string src =
                "if(WIN32)\n" +
                "  add_definitions(-DWIN)\n" +
                "else()\n" +
                "  add_definitions(-DNIX)\n" +
                "endif()\n";
            var f = CMakeParser.Parse(src);
            Assert.IsFalse(f.HasErrors, string.Join("; ", f.Diagnostics.Select(d => d.ToString())));
            var ifCmd = f.Commands.Single(c => c.Name == "if");
            Assert.IsTrue(ifCmd.IsBlockOpen);
            Assert.IsNotNull(ifCmd.EndCommand);
            Assert.AreEqual("endif", ifCmd.EndCommand.Name);
            Assert.IsNotNull(ifCmd.Body);
            // Body contains: add_definitions, else, add_definitions (endif closes the enclosing scope).
            Assert.IsTrue(ifCmd.Body.OfType<CMakeCommand>().Any(c => c.Name == "add_definitions"));
            Assert.IsTrue(ifCmd.Body.OfType<CMakeCommand>().Any(c => c.Name == "else"));
        }

        [TestMethod]
        public void Nested_ForeachInsideFunction_Balances()
        {
            string src =
                "function(doit)\n" +
                "  foreach(x ${LIST})\n" +
                "    message(${x})\n" +
                "  endforeach()\n" +
                "endfunction()\n";
            var f = CMakeParser.Parse(src);
            Assert.IsFalse(f.HasErrors, string.Join("; ", f.Diagnostics.Select(d => d.ToString())));
            var fn = f.Commands.Single(c => c.Name == "function");
            Assert.AreEqual("endfunction", fn.EndCommand.Name);
            var fe = f.Commands.Single(c => c.Name == "foreach");
            Assert.AreEqual("endforeach", fe.EndCommand.Name);
        }

        [TestMethod]
        public void Reports_UnbalancedBlock()
        {
            var f = CMakeParser.Parse("if(X)\n  message(hi)\n");   // no endif
            Assert.IsTrue(f.HasErrors);
            Assert.IsTrue(f.Diagnostics.Any(d => d.Code == "CMP006"), "missing-endif reported");
        }

        [TestMethod]
        public void Reports_MismatchedEnd()
        {
            var f = CMakeParser.Parse("if(X)\nendforeach()\n");
            Assert.IsTrue(f.Diagnostics.Any(d => d.Code == "CMP005"), "endforeach!=if mismatch reported");
        }

        [TestMethod]
        public void Extracts_VariableAndEnvAndGenexReferences()
        {
            var f = CMakeParser.Parse("target_compile_options(t PRIVATE ${FLAGS} $ENV{CFLAGS} $<CONFIG:Debug>)\n");
            var cmd = f.Commands.Single();
            var refs = cmd.Arguments.SelectMany(a => a.References).ToList();
            Assert.IsTrue(refs.Any(r => r.Name == "FLAGS" && !r.IsGeneratorExpression && r.Scope == ""));
            Assert.IsTrue(refs.Any(r => r.Name == "CFLAGS" && r.Scope == "ENV"));
            Assert.IsTrue(refs.Any(r => r.IsGeneratorExpression));
        }

        [TestMethod]
        public void Handles_QuotedAndBracketArguments_AndComments()
        {
            string src =
                "# a line comment\n" +
                "message(\"hello world\")\n" +
                "set(DOC [[multi\nline]])\n";
            var f = CMakeParser.Parse(src);
            Assert.IsFalse(f.HasErrors, string.Join("; ", f.Diagnostics.Select(d => d.ToString())));
            var msg = f.Commands.Single(c => c.Name == "message");
            Assert.AreEqual(CMakeArgKind.Quoted, msg.Arguments[0].Kind);
            Assert.AreEqual("hello world", msg.Arguments[0].Text);
            var set = f.Commands.Single(c => c.Name == "set");
            Assert.AreEqual(CMakeArgKind.Bracket, set.Arguments[1].Kind);
            StringAssert.Contains(set.Arguments[1].Text, "multi");
        }
    }
}
