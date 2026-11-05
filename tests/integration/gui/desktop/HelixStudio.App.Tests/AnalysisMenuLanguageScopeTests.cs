using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// User report: Analysis, Lint, Dead Code, Complexity, Metrics and Profiler were
    /// missing from the menu bar for a C++/CUDA solution though Python had them all. They were
    /// blanket-gated as "Python engines" — but most of those engines DO cover the other languages
    /// (Run Analysis reads the language-neutral TypeModel graph, Metrics has per-language profiles,
    /// the Profiler has JFR/Julia/dotnet-trace/Rust/nvcc backends, and Rust/C#/Java/Julia/CUDA each
    /// have a native linter).
    ///
    /// The two that genuinely are Python-only — Find Dead Code and Asymptotic Complexity, both
    /// bound to the Python AST — must STAY hidden elsewhere. A menu command that can only ever
    /// report nothing is worse than an absent one.
    /// </summary>
    [TestClass]
    public class AnalysisMenuLanguageScopeTests
    {
        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void OpenMenus(MainForm form)
        {
            form.UpdateMenuWorkflowVisibility();
            typeof(MainForm).GetMethod("OnAnalysisMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, new object[] { form, EventArgs.Empty });
        }

        /// <summary>
        /// LOAD that language's real fixture solution into the form. This used to hand-build a
        /// <c>SolutionModel</c> with a fabricated anchor path and an uninitialized
        /// <c>CMakeProjectDescriptor</c>, which meant the menu gate was being asked about a solution
        /// no loader had ever produced — the gating could agree with a synthetic model and still be
        /// wrong about a real one.
        /// </summary>
        private static void UseSolution(MainForm form, ProjectLanguage lang, bool cuda)
        {
            FixtureSolution.OpenSolution(form, lang, cuda);
        }

        private static bool Available(MainForm form, string field)
        {
            var item = Field<ToolStripItem>(form, field);
            return item != null && item.Available;
        }

        private static ToolStripMenuItem ComplexityMenuItem(MainForm form)
        {
            var analysis = Field<ToolStripMenuItem>(form, "analysisMenuItem");
            foreach (ToolStripItem it in analysis.DropDownItems)
                if ((it.Text ?? string.Empty).Replace("&", string.Empty)
                        .StartsWith("Asymptotic", StringComparison.OrdinalIgnoreCase))
                    return it as ToolStripMenuItem;
            return null;
        }

        private static bool ComplexityAvailable(MainForm form)
        {
            var item = ComplexityMenuItem(form);
            return item != null && item.Available;
        }

        [TestMethod]
        public void CudaSolution_GetsAnalysisMetricsProfilerAndLint()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                UseSolution(form, ProjectLanguage.Cpp, cuda: true);
                OpenMenus(form);

                Assert.IsTrue(Available(form, "analysisMenuItem"), "Analysis menu");
                Assert.IsTrue(Available(form, "metricsMenuItem"), "Metrics menu");
                Assert.IsTrue(Available(form, "profilerMenuItem"), "Profiler menu (nvcc occupancy)");
                Assert.IsTrue(Available(form, "runAnalysisMenuItem"), "Run Analysis is language-neutral");
                Assert.IsTrue(Available(form, "lintMenuItem"), "CUDA has its own CU60xx lint");
            }
        }

        /// <summary>
        /// C++ was the one language missing both a linter and a profiler. Both now exist —
        /// <c>CppLinter</c> (CPL0xx) and <c>CppProfiler</c> (the shared native sampler over a
        /// CMake-built binary) — so a C++ solution reaches the full set except the two engines that
        /// are Python-bound.
        /// </summary>
        [TestMethod]
        public void CppSolution_ReachesEveryCommandThatHasACppBackend()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                UseSolution(form, ProjectLanguage.Cpp, cuda: false);
                OpenMenus(form);

                Assert.IsTrue(Available(form, "analysisMenuItem"), "Analysis menu");
                Assert.IsTrue(Available(form, "metricsMenuItem"), "Metrics has a C++ profile");
                Assert.IsTrue(Available(form, "runAnalysisMenuItem"), "Run Analysis is language-neutral");
                Assert.IsTrue(Available(form, "lintMenuItem"), "C++ now has CppLinter");
                Assert.IsTrue(Available(form, "profilerMenuItem"), "C++ now has CppProfiler");
            }
        }

        [TestMethod]
        public void DeadCodeAndComplexity_StayPythonOnly()
        {
            // C++ is the only non-Python solution kind HelixPython can open (it is the target of
            // the Convert-to-C/C++/CUDA commands). Rust/C#/Java/Julia solutions are opened by their
            // own products, so their fixtures are not forked here.
            foreach (var lang in new[] { ProjectLanguage.Cpp })
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    UseSolution(form, lang, cuda: false);
                    OpenMenus(form);

                    Assert.IsFalse(Available(form, "deadCodeMenuItem"),
                        lang + ": Find Dead Code is bound to the Python AST — it must not be offered");
                    Assert.IsFalse(ComplexityAvailable(form),
                        lang + ": Asymptotic Complexity is bound to the Python AST — it must not be offered");
                }
            }
        }

        [TestMethod]
        public void PythonSolution_KeepsEverythingItAlwaysHad()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                UseSolution(form, ProjectLanguage.Python, cuda: false);
                OpenMenus(form);

                foreach (string f in new[] { "analysisMenuItem", "metricsMenuItem", "profilerMenuItem",
                                             "runAnalysisMenuItem", "lintMenuItem", "deadCodeMenuItem" })
                    Assert.IsTrue(Available(form, f), f + " must still be there for Python");
                Assert.IsTrue(ComplexityAvailable(form), "Asymptotic Complexity stays for Python");
                var complexity = ComplexityMenuItem(form);
                Assert.AreEqual("Asymptotic Complexity", complexity.Text.Replace("&", string.Empty));
                Assert.AreEqual(0, complexity.DropDownItems.Count,
                    "the complexity command must run as one menu item");
            }
        }

        [TestMethod]
        public void TheLintCommandIsNoLongerLabelledPythonOnly()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                UseSolution(form, ProjectLanguage.Cpp, cuda: false);
                OpenMenus(form);

                string text = (Field<ToolStripMenuItem>(form, "lintMenuItem").Text ?? string.Empty)
                    .Replace("&", string.Empty);
                Assert.AreEqual("Lint Sources", text,
                    "HelixPython lints Python AND the native sources it emits — 'Lint Python "
                    + "Sources' would be a lie in a C++ solution");
            }
        }

        /// <summary>The capability predicates the menus gate on, pinned directly.</summary>
        [TestMethod]
        public void CapabilityPredicates_MatchTheEnginesThatActuallyExist()
        {
            foreach (string ext in new[] { ".py", ".cu", ".cuh",
                                           ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx" })
                Assert.IsTrue(EngineCoordinator.CanLintExtension(ext), ext + " has a linter");
            foreach (string ext in new[] { ".txt", ".md", ".json" })
                Assert.IsFalse(EngineCoordinator.CanLintExtension(ext), ext + " has no linter");
            foreach (string ext in new[] { ".rs", ".cs", ".java", ".jl" })
                Assert.IsFalse(EngineCoordinator.CanLintExtension(ext),
                    ext + ": its linter ships in that language's own Helix product");

            foreach (string ext in new[] { ".py", ".java", ".jl", ".cs", ".rs", ".cu", ".cuh",
                                           ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx" })
                Assert.IsTrue(EngineCoordinator.CanProfileExtension(ext), ext + " has a profiler");
            foreach (string ext in new[] { ".txt", ".md", ".json" })
                Assert.IsFalse(EngineCoordinator.CanProfileExtension(ext), ext + " has no profiler");
        }
    }
}
