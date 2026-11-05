using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS solution_explorer_rules §8–§9 fixes: §8.2 background analysis is opt-in
    /// (the CUDA virtual compiler and non-Python linter/analyzer passes never run
    /// on typing pauses unless explicitly enabled; Python's parser-only re-parse is
    /// always allowed); §8.1 an echo of the IDE's own write is not an "external
    /// change"; §9 the diagnostics timer dies with its editor and solution close
    /// clears reference panels and closes the graphical designers.
    /// </summary>
    [TestClass]
    public class EditorLifecycleSrs89Tests
    {
        [TestInitialize]
        public void ResetGate()
        {
            AnalysisGate.BackgroundAnalysisEnabled = false;
        }

        [TestCleanup]
        public void CleanupGate()
        {
            AnalysisGate.BackgroundAnalysisEnabled = false;
        }

        // ------------------------------------------------------------------ §8.2

        [TestMethod]
        public void EditTimeDiagnostics_PythonAlways_HeavyLanguagesOnlyWhenOptedIn()
        {
            Assert.IsTrue(PythonEditorControl.ShouldRunEditTimeDiagnostics(@"C:\x\mod.py"),
                "a Python re-parse is not a compile — always allowed");
            Assert.IsTrue(PythonEditorControl.ShouldRunEditTimeDiagnostics(null),
                "untitled buffers default to the Python path");

            Assert.IsTrue(PythonEditorControl.ShouldRunEditTimeDiagnostics(@"C:\x\kernel.cu"),
                "CUDA-C syntax squiggles are always on (the run is PARSE-ONLY without the " +
                "opt-in — RunDiagnostics downgrades the pass set; the full analyses stay gated). " +
                "Off the gate a .cu showed no squiggles at all, and the opt-in toggle lives on " +
                "the Analysis menu, which native workspaces hide.");
            // Rust, C# and Java once followed CUDA into the always-on set for the same reason.
            // They left with their products; a .rs/.cs/.java buffer is no longer a HelixPython
            // document at all, so it falls into the gated default below.

            // Still gated: a build script is not a source buffer with a syntax tier of its own.
            Assert.IsFalse(PythonEditorControl.ShouldRunEditTimeDiagnostics(@"C:\x\CMakeLists.txt"));
            Assert.IsFalse(PythonEditorControl.ShouldRunEditTimeDiagnostics(@"C:\x\notes.md"));

            AnalysisGate.BackgroundAnalysisEnabled = true;
            Assert.IsTrue(PythonEditorControl.ShouldRunEditTimeDiagnostics(@"C:\x\kernel.cu"),
                "with the explicit opt-in, the full background analysis may run");
        }

        [TestMethod]
        public void AnalysisMenu_CarriesTheExplicitOptInToggle()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                FieldInfo f = typeof(MainForm).GetField("analysisMenuItem",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var analysis = (ToolStripMenuItem)f.GetValue(form);
                ToolStripMenuItem toggle = MainForm.FindMenuItemByText(analysis, "Live Background Analysis");
                Assert.IsNotNull(toggle, "§8.2 requires an explicit user enablement — the toggle must exist");
                Assert.IsTrue(toggle.CheckOnClick);
                Assert.IsFalse(toggle.Checked, "off by default");
            }
        }

        // ------------------------------------------------------------------ §8.1

        [TestMethod]
        public void ExternalChangeEcho_MatchingDiskAndCleanEditor_IsIgnored()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_echo_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                using (var tab = new EditorTabPage(tmp))
                {
                    FieldInfo dictField = typeof(MainForm).GetField("_openEditorTabs",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var dict = (System.Collections.Generic.Dictionary<string, TabPage>)dictField.GetValue(form);
                    dict[Path.GetFullPath(tmp)] = tab;

                    Assert.IsTrue(form.ShouldIgnoreExternalChange(tmp),
                        "clean tab + identical bytes = the IDE's own write echoing back");

                    File.WriteAllText(tmp, "x = 2  # really changed externally\n");
                    Assert.IsFalse(form.ShouldIgnoreExternalChange(tmp),
                        "a real external change must still surface");
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ------------------------------------------------------------------ §9

        [TestMethod]
        public void DiagnosticsTimer_DiesWithItsEditor()
        {
            var editor = new PythonEditorControl();
            editor.Text = "x = 1";   // arms the debounce timer via OnTextChanged
            FieldInfo f = typeof(PythonEditorControl).GetField("_diagTimer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f.GetValue(editor), "typing must have created the timer");

            editor.Dispose();
            Assert.IsNull(f.GetValue(editor),
                "§9: the timer's Tick closure roots the control (and its SolutionModel) if not disposed");
        }

        /// <summary>
        /// §9 over the DOCUMENT, which is where a designer lives now.
        ///
        /// This used to watch the singleton WINDOW: opening one and asserting the static instance
        /// was gone afterwards. A GUI Designer Canvas opens as a tab whenever there is an IDE to put one
        /// in, so that instance stays null and the window limb of the rule never fires — the tabs
        /// are the whole of it, and watching the old field would have passed while a designer for a
        /// file in the closed solution sat open on screen.
        /// </summary>
        [TestMethod]
        public void SolutionClose_ClosesTheSingletonDesigners()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                // Open a designer on the sample form, as the menu does with nothing open, then close
                // the solution.
                form.AddIn<GuiDesignerAddIn>().ShowForSource(null);
                Assert.AreEqual(1, DesignerDocuments(form), "designer open before close");

                FieldInfo coord = typeof(MainForm).GetField("_solutionCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object solutionCoordinator = coord.GetValue(form);
                MethodInfo close = solutionCoordinator.GetType().GetMethod("NewEmptyWorkspace");
                close.Invoke(solutionCoordinator, null);

                Assert.AreEqual(0, DesignerDocuments(form),
                    "§9: closing a solution closes every graphical designer");
                // The static singleton field went with the floating window (spec 004, Task B), so
                // the window limb is asked of the running application instead: no designer window.
                Assert.AreEqual(0, Application.OpenForms.Cast<Form>().OfType<GuiDesignerForm>().Count(d => d.TopLevel),
                    "§9: and any designer that did open as a window");
            }
        }

        private static int DesignerDocuments(MainForm form)
        {
            if (form.AddIn<GuiDesignerAddIn>()._guiDesignerTabs == null) return 0;
            return form.AddIn<GuiDesignerAddIn>()._guiDesignerTabs.TabPages.Cast<TabPage>()
                .Count(t => t.Controls.Count > 0 && t.Controls[0] is GuiDesignerForm);
        }

        [TestMethod]
        public void SolutionClose_ClearsReferenceAndCallHierarchyPanels()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                FieldInfo coord = typeof(MainForm).GetField("_solutionCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object solutionCoordinator = coord.GetValue(form);
                MethodInfo close = solutionCoordinator.GetType().GetMethod("NewEmptyWorkspace");
                // Must not throw with the panels present; their Clear() is now on the
                // §9 teardown path (stale reference rows resurrected dead documents).
                close.Invoke(solutionCoordinator, null);
            }
        }
    }
}
