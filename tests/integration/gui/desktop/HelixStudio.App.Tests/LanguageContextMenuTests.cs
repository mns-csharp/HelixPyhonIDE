using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS solution_explorer_rules §6.2–6.7: the menu system shows only commands
    /// relevant to the active language. A Python solution hides the CMake build; a
    /// native CMake/CUDA workspace hides the Python engines (Analysis/Tests/Metrics/
    /// Profiler/Diagram menus, the Build menu's Python commands, and the Python
    /// import entry); the per-language Tools designers appear only for their language.
    /// </summary>
    [TestClass]
    public class LanguageContextMenuTests
    {
        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void SetField(object obj, string name, object value)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            f.SetValue(obj, value);
        }

        private static void Invoke(MainForm form, string method)
        {
            MethodInfo m = typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, method + " expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        private static void MakeNative(MainForm form)
        {
            SetField(form, "_currentSolution", new SolutionModel(@"C:\native\CMakeLists.txt", "native"));
            SetField(form, "_currentCMakeProject",
                FormatterServices.GetUninitializedObject(typeof(CMakeProjectDescriptor)));
        }

        private static void MakePython(MainForm form)
        {
            SetField(form, "_currentSolution", new SolutionModel(@"C:\proj\pyproject.toml", "Proj"));
        }

        [TestMethod]
        public void NativeWorkspace_HidesOnlyTheGenuinelyPythonMenus()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                form.UpdateMenuWorkflowVisibility();

                // Diagram is the last Python-scoped menu: Add Interface/Abstract/Concrete write
                // Python source through the Python TypeService.
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "diagramMenuItem").Available,
                    "diagramMenuItem writes Python source — §6.3 hides it in a native workspace");

                // Analysis/Metrics/Profiler are NOT Python-only: Run Analysis reads the
                // language-neutral TypeModel graph, Metrics has a CUDA/C++ profile, and the
                // Profiler has the nvcc occupancy backend. See AnalysisMenuLanguageScopeTests for
                // the per-command, per-language contract.
                foreach (string n in new[] { "analysisMenuItem", "metricsMenuItem", "profilerMenuItem" })
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                        n + " has a real backend for C++/CUDA and must be reachable");

                // testsMenuItem moved out of that set when native workspaces got a REAL test
                // pipeline: Tests ▸ Run All routes to ctest for C++/CUDA (EngineCoordinator
                // .RunNativeCMakeTests), so it is solution-scoped, not Python-scoped.
                foreach (string n in new[] { "buildTopMenuItem", "debugMenuItem", "gitMenuItem", "testsMenuItem" })
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                        n + " applies to a native workspace too");
            }
        }

        [TestMethod]
        public void PythonSolution_KeepsAllEightWorkflowMenus()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakePython(form);
                form.UpdateMenuWorkflowVisibility();
                foreach (string n in new[] { "buildTopMenuItem", "analysisMenuItem", "testsMenuItem",
                                             "metricsMenuItem", "profilerMenuItem", "diagramMenuItem",
                                             "debugMenuItem", "gitMenuItem" })
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available, n);
            }
        }

        [TestMethod]
        public void BuildMenu_ShowsPythonCommandsOnlyInPythonWorkspaces()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakePython(form);
                Invoke(form, "OnBuildMenuOpening");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "compileMenuItem").Available, "Compile (python)");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "selectInterpreterMenuItem").Available,
                    "Select Python Interpreter (python)");
                // The native Build/Rebuild/Clean group ("Build CMake Project" became the VS-style
                // "Build Solution" when Rebuild/Clean and the per-project submenus were added).
                ToolStripMenuItem cmake = MainForm.FindMenuItemByText(
                    Field<ToolStripMenuItem>(form, "buildTopMenuItem"), "Build Solution");
                if (cmake != null)
                    Assert.IsFalse(cmake.Available, "§6.2: CMake build hidden in a Python solution");
                ToolStripMenuItem openCMake = MainForm.FindMenuItemByText(
                    Field<ToolStripMenuItem>(form, "buildTopMenuItem"), "Open CMake Folder");
                if (openCMake != null)
                    Assert.IsFalse(openCMake.Available,
                        "§6.2: even the CMake entry point is an unrelated-language command inside a Python solution");

                MakeNative(form);
                Invoke(form, "OnBuildMenuOpening");
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "compileMenuItem").Available,
                    "§6.3: the Python compile is hidden in a native workspace");
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "selectInterpreterMenuItem").Available);
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "_runStartupMenuItem").Available);
                if (cmake != null)
                    Assert.IsTrue(cmake.Available, "§6.3: the CMake build shows in a native workspace");
            }
        }

        /// <summary>
        /// The Analysis menu's commands are scoped per COMMAND now, not per workspace: each is shown
        /// where it has a backend. Only Find Dead Code stays Python-only (it walks the Python AST).
        /// </summary>
        [TestMethod]
        public void AnalysisMenu_CommandsAreScopedToTheEnginesThatSupportTheLanguage()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakePython(form);
                Invoke(form, "OnAnalysisMenuOpening");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "lintMenuItem").Available);
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "deadCodeMenuItem").Available);

                MakeNative(form);   // a CMake workspace reads as CUDA
                Invoke(form, "OnAnalysisMenuOpening");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "lintMenuItem").Available,
                    "CUDA has its own CU60xx lint rules");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "runAnalysisMenuItem").Available,
                    "the 14 analysis rules read the TypeModel graph, which the CMake loader populates");
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "deadCodeMenuItem").Available,
                    "Find Dead Code is bound to the Python AST — there is no C++/CUDA implementation");
            }
        }

        [TestMethod]
        public void FileMenu_HidesThePythonImportInsideANativeWorkspace()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakeNative(form);
                Invoke(form, "OnFileMenuOpening");
                Assert.IsFalse(Field<ToolStripMenuItem>(form, "importDirectoryMenuItem").Available,
                    "§6.3: Import Python Directory is an unrelated-language command there");

                SetField(form, "_currentCMakeProject", null);
                Invoke(form, "OnFileMenuOpening");
                Assert.IsTrue(Field<ToolStripMenuItem>(form, "importDirectoryMenuItem").Available,
                    "the import entry point returns everywhere else");
            }
        }

        [TestMethod]
        public void ToolsMenu_LanguageDesigners_FollowWorkspaceAndActiveDocument()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                MakePython(form);
                Invoke(form, "OnToolsMenuOpening");
                form.OnAddInMenuOpening(form.AddInMenuForTests, EventArgs.Empty);
                ToolStripMenuItem tools = Field<ToolStripMenuItem>(form, "toolsMenuItem");

                // The GUI Designer Canvas is an add-in (spec 004, Task B): its item moved from Tools
                // to Add-in ▸ GUI Designer Canvas, with the same visibility rule.
                ToolStripMenuItem guiMenu = MainForm.FindMenuItemByText(form.AddInMenuForTests, "GUI Designer Canvas");
                Assert.IsNotNull(guiMenu, "Add-in ▸ GUI Designer Canvas expected");
                ToolStripMenuItem gui = MainForm.FindMenuItemByText(guiMenu, "GUI Designer Canvas");
                ToolStripMenuItem rust = MainForm.FindMenuItemByText(tools, "Rust Designer");
                ToolStripMenuItem nvcc = MainForm.FindMenuItemByText(tools, "Verify Active .cu");
                Assert.IsNotNull(gui, "GUI Designer Canvas expected under Add-in ▸ GUI Designer Canvas");
                Assert.IsNull(MainForm.FindMenuItemByText(tools, "GUI Designer Canvas"), "the GUI Designer Canvas left Tools");
                Assert.IsTrue(gui.Available, "Python designer visible in a Python workspace");
                if (rust != null)
                    Assert.IsFalse(rust.Available, "§6.2: Rust designer hidden with no Rust in play");
                if (nvcc != null)
                    Assert.IsFalse(nvcc.Available, "CUDA verify hidden with no .cu in play");

                MakeNative(form);
                Invoke(form, "OnToolsMenuOpening");
                form.OnAddInMenuOpening(form.AddInMenuForTests, EventArgs.Empty);
                if (nvcc != null)
                    Assert.IsTrue(nvcc.Available, "§6.3: CUDA verify shows in a native workspace");
                Assert.IsFalse(gui.Available, "§6.3: the Python designer hides in a native workspace");
            }
        }

        [TestMethod]
        public void FindMenuItemByText_StripsMnemonicsAndMatchesPrefix()
        {
            var parent = new ToolStripMenuItem("Parent");
            parent.DropDownItems.Add(new ToolStripMenuItem("&Build CMake Project"));
            ToolStripMenuItem found = MainForm.FindMenuItemByText(parent, "Build CMake");
            Assert.IsNotNull(found);
            Assert.IsNull(MainForm.FindMenuItemByText(parent, "Nonexistent"));
        }
    }
}
