using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonSolutionAuditorTests
    {
        // ---- IsMainModule (rule #3b: underscore equivalence) ----

        [DataTestMethod]
        [DataRow("main.py")]
        [DataRow("_main.py")]
        [DataRow("main_.py")]
        [DataRow("_main_.py")]
        [DataRow("__main__.py")]
        [DataRow("___main___.py")]
        [DataRow("MAIN.py")]
        [DataRow("__MAIN__.py")]
        [DataRow("main.pyi")]
        [DataRow("/some/dir/__main__.py")]
        public void IsMainModule_TreatsUnderscoreVariantsAsEquivalent(string name)
        {
            Assert.IsTrue(PythonSolutionAuditor.IsMainModule(name), name);
        }

        [DataTestMethod]
        [DataRow("domain.py")]
        [DataRow("main2.py")]
        [DataRow("mymain.py")]
        [DataRow("mainframe.py")]
        [DataRow("main.txt")]
        [DataRow("remain.py")]
        [DataRow("")]
        [DataRow(null)]
        public void IsMainModule_RejectsNonEquivalents(string name)
        {
            Assert.IsFalse(PythonSolutionAuditor.IsMainModule(name), name ?? "(null)");
        }

        // ---- ContainsModuleLevelMainFunction ----

        [TestMethod]
        public void MainFunction_DetectedAtModuleLevel()
        {
            Assert.IsTrue(PythonSolutionAuditor.ContainsModuleLevelMainFunction("def main():\n    pass\n"));
            Assert.IsTrue(PythonSolutionAuditor.ContainsModuleLevelMainFunction("def main(argv):\n    pass\n"));
            Assert.IsTrue(PythonSolutionAuditor.ContainsModuleLevelMainFunction("async def main():\n    pass\n"));
        }

        [TestMethod]
        public void MainFunction_IgnoresIndentedMethodsAndFalsePositives()
        {
            Assert.IsFalse(PythonSolutionAuditor.ContainsModuleLevelMainFunction(
                "class App:\n    def main(self):\n        pass\n"));
            Assert.IsFalse(PythonSolutionAuditor.ContainsModuleLevelMainFunction("def maindish():\n    pass\n"));
            Assert.IsFalse(PythonSolutionAuditor.ContainsModuleLevelMainFunction("define(x)\n"));
        }

        [TestMethod]
        public void MainFunction_IgnoresCommentsAndDocstrings()
        {
            Assert.IsFalse(PythonSolutionAuditor.ContainsModuleLevelMainFunction("# def main():\n"));
            Assert.IsFalse(PythonSolutionAuditor.ContainsModuleLevelMainFunction(
                "\"\"\"\ndef main():\n    pass\n\"\"\"\n"));
            // A docstring that closes, then a real def main after it -> detected.
            Assert.IsTrue(PythonSolutionAuditor.ContainsModuleLevelMainFunction(
                "\"\"\"module doc\"\"\"\ndef main():\n    pass\n"));
        }

        // ---- End-to-end Audit against a temp tree ----

        [TestMethod]
        public void Audit_CompliantSolution_IsCompliantAndCanGenerateToml()
        {
            string root = NewTempDir();
            try
            {
                Write(root, "main.py", "def main():\n    print('hi')\n");
                Write(root, "util.py", "def helper():\n    pass\n");

                SolutionAuditReport report = PythonSolutionAuditor.Audit(root);

                Assert.IsTrue(report.IsCompliant);
                Assert.IsTrue(report.CanAutoGenerateToml);
                Assert.AreEqual(1, report.MainModules.Count);
                Assert.AreEqual(1, report.MainFunctionFiles.Count);
            }
            finally { Delete(root); }
        }

        [TestMethod]
        public void Audit_DuplicateMainModules_ReportsError()
        {
            string root = NewTempDir();
            try
            {
                Write(root, "main.py", "print(1)\n");
                Directory.CreateDirectory(Path.Combine(root, "pkg"));
                Write(Path.Combine(root, "pkg"), "__main__.py", "print(2)\n");

                SolutionAuditReport report = PythonSolutionAuditor.Audit(root);

                Assert.IsFalse(report.IsCompliant);
                Assert.IsFalse(report.CanAutoGenerateToml);
                Assert.AreEqual(2, report.MainModules.Count);
                bool hasErr = false;
                foreach (SolutionAuditFinding f in report.Findings)
                    if (f.Rule == SolutionAuditRule.SingleMainModule && f.Severity == SolutionAuditSeverity.Error)
                        hasErr = true;
                Assert.IsTrue(hasErr);
            }
            finally { Delete(root); }
        }

        [TestMethod]
        public void Audit_DuplicateMainFunctions_ReportsError()
        {
            string root = NewTempDir();
            try
            {
                Write(root, "a.py", "def main():\n    pass\n");
                Write(root, "b.py", "def main():\n    pass\n");

                SolutionAuditReport report = PythonSolutionAuditor.Audit(root);

                Assert.IsFalse(report.IsCompliant);
                Assert.AreEqual(2, report.MainFunctionFiles.Count);
            }
            finally { Delete(root); }
        }

        [TestMethod]
        public void Audit_SkipsIgnoredDirectories()
        {
            string root = NewTempDir();
            try
            {
                Write(root, "main.py", "def main():\n    pass\n");
                string venv = Path.Combine(root, ".venv");
                Directory.CreateDirectory(venv);
                Write(venv, "__main__.py", "def main():\n    pass\n"); // must be ignored

                SolutionAuditReport report = PythonSolutionAuditor.Audit(root);

                Assert.IsTrue(report.IsCompliant, "files under .venv must be ignored");
                Assert.AreEqual(1, report.MainModules.Count);
            }
            finally { Delete(root); }
        }

        // ---- helpers ----

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pyaudit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Write(string dir, string name, string content)
        {
            File.WriteAllText(Path.Combine(dir, name), content);
        }

        private static void Delete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
