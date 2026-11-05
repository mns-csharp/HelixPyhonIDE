using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression for "I opened a CUDA solution and now I don't see the Test Runner menu":
    /// the top-level Tests menu used to be python-scoped and vanished for native C++/CUDA
    /// workspaces. Now that Run All Tests routes to the real ctest pipeline for native
    /// solutions (EngineCoordinator.RunNativeCMakeTests), Tests is solution-scoped — visible
    /// for every loaded solution — while the still-Python-only menus stay native-hidden.
    /// </summary>
    [TestClass]
    public class TestsMenuVisibilityTests
    {
        [TestMethod]
        public void TestsAndAnalysisAreVisibleForANativeCMakeWorkspace_DiagramIsNot()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_menu_native_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(rootCmake,
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\nadd_executable(GpuApp main.cu)\n");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                // No solution: both are gone.
                form.UpdateMenuWorkflowVisibility();
                Assert.IsFalse(form.testsMenuItem.Available, "no solution → no Tests menu");

                // Native CMake workspace: Tests (ctest pipeline) and Analysis are both there.
                form._currentSolution = new SolutionModel(rootCmake, "GpuApp");
                form._currentCMakeFolder = root;
                form._currentCMakeProject = new HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor(
                    root, "GpuApp", ProjectKind.CudaExecutable, null);
                form.UpdateMenuWorkflowVisibility();
                Assert.IsTrue(form.testsMenuItem.Available,
                    "a native C++/CUDA workspace must keep the Tests menu — Run All routes to ctest");
                // Analysis followed Tests out of the Python-only set: Run Analysis reads the
                // language-neutral TypeModel graph and CUDA has its own lint, so the menu is
                // reachable here too. Which COMMANDS inside it apply is gated per language —
                // see AnalysisMenuLanguageScopeTests.
                Assert.IsTrue(form.analysisMenuItem.Available,
                    "a native C++/CUDA workspace now reaches Analysis (Run Analysis + CUDA lint)");
                var diagram = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("diagramMenuItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(form);
                Assert.IsFalse(diagram.Available,
                    "Diagram is the one that stays Python-scoped — it writes Python source");

                form._currentSolution = null;
                form._currentCMakeFolder = null;
                form._currentCMakeProject = null;
                form.Close();
            }
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
