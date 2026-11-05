using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 395 (PSM polish): the New Project wizard must scaffold packages that already satisfy the
    /// per-package rules (docs/python-solution-model.md). Every template — App, GuiApp, FlaskWebApp,
    /// ClassLibrary — must validate clean. Flask is WSGI (no main.py / def main()), so it is exempt
    /// from the main-module rules via ProjectKinds.RequiresMainModule.
    /// </summary>
    [TestClass]
    public class ScaffoldedPackageComplianceTests
    {
        private static SolutionModel Scaffold(ProjectKind kind, out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "helix_scaf_" + System.Guid.NewGuid().ToString("N"));
            return PythonProjectScaffolder.CreateNewProject(root, "demo", kind);
        }

        private static void AssertClean(ProjectKind kind)
        {
            SolutionModel sln = Scaffold(kind, out _);
            var findings = PackageComplianceValidator.Validate(sln);
            Assert.AreEqual(0, findings.Count,
                kind + " scaffold should be PSM-compliant, but had: " +
                string.Join("; ", findings.Select(f => f.ToString())));
        }

        [TestMethod] public void AppScaffold_IsCompliant() { AssertClean(ProjectKind.App); }
        [TestMethod] public void GuiAppScaffold_IsCompliant() { AssertClean(ProjectKind.GuiApp); }
        [TestMethod] public void FlaskWebAppScaffold_IsCompliant() { AssertClean(ProjectKind.FlaskWebApp); }
        [TestMethod] public void ClassLibraryScaffold_IsCompliant() { AssertClean(ProjectKind.ClassLibrary); }

        [TestMethod]
        public void FlaskWebApp_IsExemptFromMainModuleRule()
        {
            // Regression guard: Flask is runnable (IsExecutable) but must NOT be flagged PSM-03 for
            // lacking a main.py — RequiresMainModule is false for it.
            Assert.IsTrue(ProjectKinds.IsExecutable(ProjectKind.FlaskWebApp));
            Assert.IsFalse(ProjectKinds.RequiresMainModule(ProjectKind.FlaskWebApp));
        }
    }
}
