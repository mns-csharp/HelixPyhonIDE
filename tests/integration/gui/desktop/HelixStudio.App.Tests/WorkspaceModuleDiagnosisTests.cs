using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A ModuleNotFoundError for a module the SOLUTION defines is not a pip problem.
    ///
    /// Offering to install one is worse than saying nothing: PyPI has packages under short,
    /// ordinary names — `inference` is a real one — so accepting the offer downloads a stranger's
    /// library that shadows the user's own module and leaves the import just as broken. The run
    /// still fails, and now the environment is wrong too.
    /// </summary>
    [TestClass]
    public class WorkspaceModuleDiagnosisTests
    {
        // Reaches the coordinator's resolver and flattens its result for assertions.
        private static void Match(MainForm form, string moduleName,
                                  out string filePath, out string importPath, out bool isExact)
        {
            object coordinator = typeof(MainForm)
                .GetField("_buildRunCoordinator", BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public)
                .GetValue(form);
            object match = coordinator.GetType()
                .GetMethod("MatchModuleInWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(coordinator, new object[] { moduleName });

            if (match == null) { filePath = null; importPath = null; isExact = false; return; }
            Type t = match.GetType();
            filePath = (string)t.GetField("FilePath").GetValue(match);
            importPath = (string)t.GetField("ImportPath").GetValue(match);
            isExact = (bool)t.GetField("IsExact").GetValue(match);
        }

        private static string Suggestion(MainForm form, string moduleName)
        {
            object coordinator = typeof(MainForm)
                .GetField("_buildRunCoordinator", BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public)
                .GetValue(form);
            object match = coordinator.GetType()
                .GetMethod("MatchModuleInWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(coordinator, new object[] { moduleName });
            return match == null ? null : (string)match.GetType().GetField("Suggestion").GetValue(match);
        }

        private static string FixtureRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-module-match-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(root, "src", "sample_common");
            Directory.CreateDirectory(Path.Combine(project, "sample_common", "inference"));
            Directory.CreateDirectory(Path.Combine(project, "sample_common", "onnx"));
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"module-match-fixture\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n\n"
              + "[tool.uv.workspace]\nmembers = [\"src/sample_common\"]\n");
            File.WriteAllText(Path.Combine(project, "pyproject.toml"),
                "[project]\nname = \"sample_common\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n\n"
              + "[tool.helixstudio]\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(project, "sample_common", "__init__.py"), "");
            File.WriteAllText(Path.Combine(project, "sample_common", "inference", "__init__.py"), "");
            File.WriteAllText(Path.Combine(project, "sample_common", "onnx", "__init__.py"), "");
            File.WriteAllText(Path.Combine(project, "sample_common", "onnx", "export_onnx.py"), "class Exporter:\n    pass\n");
            return root;
        }

        /// <summary>
        /// `inference.export_onnx` fails while the solution happens to contain an `inference.py`.
        /// That is a coincidence of names: a MODULE cannot carry a submodule, so no import path
        /// would ever make it work. Announcing "it's in your solution" here replaces a wrong guess
        /// (pip) with a confident wrong answer, which is worse.
        /// </summary>
        [TestMethod]
        public void ADottedImportThatCannotResolveIsNotClaimedAsFound()
        {
            string fixtureRoot = FixtureRoot();
            Assert.IsNotNull(fixtureRoot, "the sample_workspace fixture is missing");

            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(Path.Combine(fixtureRoot, "pyproject.toml"));
                TestMainForm.PumpUntilIdle();
                Assert.IsNotNull(form._currentSolution, "the fixture solution did not open");

                string file, import; bool exact;
                Match(form, "inference.export_onnx", out file, out import, out exact);

                Assert.IsNotNull(file, "the near-miss should still be reported, just not as a match");
                Assert.IsFalse(exact,
                    "'inference.export_onnx' was claimed to resolve, but inference.py is a module");
                Assert.AreEqual("sample_common.inference", import,
                    "the importable name for the near-miss is wrong");

                // export_onnx.py really does exist, under a path the author misremembered. Naming
                // where it lives turns "that import is wrong" into a line the user can paste.
                Assert.AreEqual("sample_common.onnx.export_onnx", Suggestion(form, "inference.export_onnx"),
                    "the suggestion did not point at the module that actually defines it");

                form.Close();
            }
        }

        /// <summary>The plain name does resolve, and resolves to the name Python would accept.</summary>
        [TestMethod]
        public void AModuleTheSolutionDefinesResolvesToItsImportablePath()
        {
            string fixtureRoot = FixtureRoot();
            Assert.IsNotNull(fixtureRoot);

            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(Path.Combine(fixtureRoot, "pyproject.toml"));
                TestMainForm.PumpUntilIdle();

                string file, import; bool exact;
                Match(form, "inference", out file, out import, out exact);

                Assert.IsTrue(exact, "'inference' is a module in this solution and should resolve");
                // A nested package shadows a same-named module file; the package __init__.py
                // remains the importable module in the workspace.
                StringAssert.EndsWith(file.Replace('\\', '/'), "sample_common/inference/__init__.py");
                Assert.AreEqual("sample_common.inference", import);

                // And a real dotted path through a package resolves all the way down.
                Match(form, "sample_common.inference", out file, out import, out exact);
                Assert.IsTrue(exact, "a dotted name through a real package should resolve");

                form.Close();
            }
        }

        [TestMethod]
        public void AGenuinelyThirdPartyModuleStillReachesPip()
        {
            string fixtureRoot = FixtureRoot();
            Assert.IsNotNull(fixtureRoot);

            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(Path.Combine(fixtureRoot, "pyproject.toml"));
                TestMainForm.PumpUntilIdle();

                // Nothing here is called this, so the pip offer must still be made — the guard has
                // to stay narrow or it breaks the case it sits in front of.
                string file, import; bool exact;
                Match(form, "numpy_zzz_not_here", out file, out import, out exact);
                Assert.IsNull(file, "a module the solution does not define was claimed as local");

                form.Close();
            }
        }

        /// <summary>
        /// The real traceback, verbatim. Python reports only the first segment it could not find —
        /// "No module named 'inference'" — so a diagnosis built from the error message judges
        /// `inference.export_onnx` on the strength of the word `inference`, and a coincidental
        /// `inference.py` in the workspace gets announced as the answer. The statement is a line
        /// further up in the same traceback, and it is the evidence.
        /// </summary>
        [TestMethod]
        public void TheFailingImportIsTakenFromTheTracebackNotTheErrorMessage()
        {
            string traceback = string.Join("\r\n", new[]
            {
                "Traceback (most recent call last):",
                "  File \"D:\\p\\export_onnx\\main.py\", line 57, in <module>",
                "    raise SystemExit(main())",
                "  File \"D:\\p\\export_onnx\\main.py\", line 30, in execute",
                "    from inference.export_onnx import export_checkpoint",
                "ModuleNotFoundError: No module named 'inference'",
            });

            Assert.AreEqual("inference", MainForm.ExtractMissingModule(traceback),
                "precondition: the error message names only the top-level segment");

            Assert.AreEqual("inference.export_onnx",
                MainForm.ExtractFailingImport(traceback, "inference"),
                "the whole dotted import was not recovered from the traceback");
        }

        /// <summary>An unrelated import in the same traceback must not be mistaken for the failure.</summary>
        [TestMethod]
        public void AnUnrelatedImportInTheTracebackIsNotPickedUp()
        {
            string traceback = string.Join("\r\n", new[]
            {
                "    import os",
                "    from sample_common.pipeline import run",
                "    from inference.export_onnx import export_checkpoint",
                "ModuleNotFoundError: No module named 'inference'",
            });

            Assert.AreEqual("inference.export_onnx",
                MainForm.ExtractFailingImport(traceback, "inference"));
            Assert.IsNull(MainForm.ExtractFailingImport(traceback, "numpy"),
                "a module that never appears in the traceback was matched anyway");
        }
    }
}
