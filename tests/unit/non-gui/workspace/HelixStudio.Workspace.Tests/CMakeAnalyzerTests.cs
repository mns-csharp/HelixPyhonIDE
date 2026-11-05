using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CMakeAnalyzerTests
    {
        private static bool Has(CMakeAnalysisResult r, string code) { return r.Findings.Any(f => f.Code == code); }

        [TestMethod]
        public void BuildsTargetGraph_WithLinkDependencies()
        {
            string src =
                "add_library(core STATIC core.cpp)\n" +
                "add_executable(app main.cpp)\n" +
                "target_link_libraries(app PRIVATE core pthread)\n";
            var r = CMakeAnalyzer.Analyze(src);
            Assert.AreEqual(2, r.Targets.Count);
            var app = r.FindTarget("app");
            CollectionAssert.Contains(app.LinkDependencies, "core");
            CollectionAssert.Contains(app.LinkDependencies, "pthread");   // external lib recorded, not flagged
            Assert.IsFalse(r.Findings.Any(), "clean project analyzes clean: " + string.Join("; ", r.Findings.Select(f => f.ToString())));
        }

        [TestMethod]
        public void Flags_TargetCommandOnUndefinedTarget()
        {
            string src = "add_executable(app main.cpp)\ntarget_link_libraries(ghost core)\n";
            var r = CMakeAnalyzer.Analyze(src);
            var d = r.Findings.FirstOrDefault(f => f.Code == "CMA001");
            Assert.IsNotNull(d);
            StringAssert.Contains(d.Message, "ghost");
        }

        [TestMethod]
        public void Flags_DuplicateTarget()
        {
            string src = "add_library(dup a.cpp)\nadd_library(dup b.cpp)\n";
            var r = CMakeAnalyzer.Analyze(src);
            Assert.IsTrue(Has(r, "CMA003"));
        }

        [TestMethod]
        public void Flags_VariableUsedBeforeSet()
        {
            string src =
                "add_executable(app ${SRCS})\n" +   // used on line 1
                "set(SRCS main.cpp)\n";             // set on line 2
            var r = CMakeAnalyzer.Analyze(src);
            var d = r.Findings.FirstOrDefault(f => f.Code == "CMA002");
            Assert.IsNotNull(d, "def-before-use flagged");
            StringAssert.Contains(d.Message, "SRCS");
        }

        [TestMethod]
        public void DoesNotFlag_VariableSetBeforeUse()
        {
            string src = "set(SRCS main.cpp)\nadd_executable(app ${SRCS})\n";
            var r = CMakeAnalyzer.Analyze(src);
            Assert.IsFalse(Has(r, "CMA002"), "set-then-use is fine");
        }

        [TestMethod]
        public void DoesNotFlag_ExternalVariable()
        {
            // OpenCV_LIBS is never set() in-file (comes from find_package) — must NOT be flagged.
            string src = "add_executable(app main.cpp)\ntarget_link_libraries(app ${OpenCV_LIBS})\n";
            var r = CMakeAnalyzer.Analyze(src);
            Assert.IsFalse(Has(r, "CMA002"));
        }

        [TestMethod]
        public void Detects_DependencyCycle()
        {
            string src =
                "add_library(a a.cpp)\n" +
                "add_library(b b.cpp)\n" +
                "target_link_libraries(a b)\n" +
                "target_link_libraries(b a)\n";
            var r = CMakeAnalyzer.Analyze(src);
            var d = r.Findings.FirstOrDefault(f => f.Code == "CMA004");
            Assert.IsNotNull(d, "a<->b cycle detected");
            StringAssert.Contains(d.Message, "cycle");
        }

        [TestMethod]
        public void NoCycle_ForAcyclicGraph()
        {
            string src =
                "add_library(a a.cpp)\nadd_library(b b.cpp)\nadd_executable(app main.cpp)\n" +
                "target_link_libraries(app a b)\ntarget_link_libraries(a b)\n";
            var r = CMakeAnalyzer.Analyze(src);
            Assert.IsFalse(Has(r, "CMA004"), "diamond/DAG is not a cycle");
        }
    }
}
