using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeLinterTests
    {
        private static bool Has(System.Collections.Generic.List<CMakeLintFinding> f, string code)
        {
            return f.Any(x => x.Code == code);
        }

        [TestMethod]
        public void Flags_UnknownCommand()
        {
            var f = CMakeLinter.Lint("cmake_minimum_required(VERSION 3.20)\nfrobnicate(x y)\n");
            Assert.IsTrue(Has(f, "CML002"), "unknown command frobnicate");
        }

        [TestMethod]
        public void UserDefinedFunction_IsNotUnknown()
        {
            string src = "function(my_helper a)\nendfunction()\nmy_helper(1)\n";
            var f = CMakeLinter.Lint(src);
            Assert.IsFalse(f.Any(x => x.Code == "CML002" && x.Message.Contains("my_helper")),
                "a function defined in-file is a known command");
        }

        [TestMethod]
        public void Flags_DeprecatedCommand()
        {
            var f = CMakeLinter.Lint("exec_program(ls)\n");
            var d = f.FirstOrDefault(x => x.Code == "CML003");
            Assert.IsNotNull(d);
            StringAssert.Contains(d.Message, "execute_process");
        }

        [TestMethod]
        public void Flags_TooFewArguments()
        {
            var f = CMakeLinter.Lint("add_executable(app)\n"); // needs a source too
            Assert.IsTrue(Has(f, "CML004"), "add_executable with only a name");
        }

        [TestMethod]
        public void Flags_UnusedSetVariable_ButNotReferencedOne()
        {
            string src =
                "set(FOO 1)\n" +               // never referenced -> unused
                "set(BAR 2)\n" +               // referenced below -> not unused
                "message(${BAR})\n";
            var f = CMakeLinter.Lint(src);
            var unused = f.Where(x => x.Code == "CML005").ToList();
            Assert.IsTrue(unused.Any(x => x.Message.Contains("'FOO'")), "FOO flagged as unused");
            Assert.IsFalse(unused.Any(x => x.Message.Contains("'BAR'")), "BAR is referenced, not flagged");
        }

        [TestMethod]
        public void CacheAndCmakeVariables_AreNotUnused()
        {
            string src =
                "set(MY_OPT ON CACHE BOOL \"help\")\n" +   // CACHE = public knob
                "set(CMAKE_CXX_STANDARD 17)\n" +            // read by CMake itself
                "option(BUILD_TESTS \"tests\" OFF)\n";
            var f = CMakeLinter.Lint(src);
            Assert.IsFalse(f.Any(x => x.Code == "CML005"), "cache/option/CMAKE_* vars are not 'unused'");
        }

        [TestMethod]
        public void Flags_FileGlobForSources()
        {
            var f = CMakeLinter.Lint("file(GLOB SRC \"*.cpp\")\n");
            Assert.IsTrue(Has(f, "CML006"), "file(GLOB) discouraged");
        }

        [TestMethod]
        public void FoldsParserErrors_UnbalancedBlock()
        {
            var f = CMakeLinter.Lint("if(X)\nmessage(hi)\n"); // missing endif
            Assert.IsTrue(f.Any(x => x.Code == "CMP006" && x.Severity == CMakeLintSeverity.Error),
                "parser unbalanced-block error surfaced by the linter");
        }

        [TestMethod]
        public void CleanFile_HasNoWarnings()
        {
            string src =
                "cmake_minimum_required(VERSION 3.20)\n" +
                "project(demo LANGUAGES CXX)\n" +
                "set(SRC main.cpp util.cpp)\n" +
                "add_executable(app ${SRC})\n";
            var f = CMakeLinter.Lint(src);
            Assert.AreEqual(0, f.Count, "a clean CMakeLists lints clean: " + string.Join("; ", f.Select(x => x.ToString())));
        }
    }
}
