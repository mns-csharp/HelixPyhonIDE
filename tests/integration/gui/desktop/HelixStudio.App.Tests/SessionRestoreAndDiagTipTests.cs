using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Two session-quality regressions from the same report:
    /// (1) "when I open the solution, I have to browse through the entire code tree once
    ///     again" — a NATIVE CMake workspace never restored the previously open editor
    ///     tabs or the Solution Explorer expansion (and quitting the IDE never persisted
    ///     them for ANY workspace — only the explicit File ▸ Close Solution did).
    /// (2) "mouse hover on squiggle must show the issue as a tooltip" — the error marks
    ///     carried no retrievable message.
    /// </summary>
    [TestClass]
    public class SessionRestoreAndDiagTipTests
    {
        [TestMethod]
        public void NativeCMakeOpen_RestoresPersistedEditorTabs()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_restore_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            string cmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(cmake,
                "cmake_minimum_required(VERSION 3.18)\nproject(RestoreDemo LANGUAGES CXX)\nadd_executable(RestoreDemo main.cpp)\n");
            string mainCpp = Path.Combine(root, "main.cpp");
            File.WriteAllText(mainCpp, "int main() { return 0; }\n");

            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    // Session state left behind by a previous run.
                    form._settingsManager.Settings.SetLastOpenEditorFiles(cmake, new[] { mainCpp });

                    form.LoadCMakeFolderAsNativeProject(root);
                    Application.DoEvents();

                    bool reopened = false;
                    foreach (TabPage tp in form._editorInnerTabControl.TabPages)
                    {
                        var etp = tp as HelixPythonNamespace.Editor.EditorTabPage;
                        if (etp != null && string.Equals(etp.FilePath, mainCpp, StringComparison.OrdinalIgnoreCase))
                        { reopened = true; break; }
                    }
                    Assert.IsTrue(reopened,
                        "opening a native CMake workspace must reopen the editor tabs from last session");
                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ExpandedTreePaths_RoundTripThroughSettings()
        {
            var settings = new UserSettings();
            settings.SetExpandedTreePaths(@"X:\s\CMakeLists.txt", new[] { @"Sln\app_cli", @"Sln\app_cli\include" });
            var back = settings.GetExpandedTreePaths(@"X:\s\CMakeLists.txt");
            CollectionAssert.AreEquivalent(new[] { @"Sln\app_cli", @"Sln\app_cli\include" }, back);
            Assert.AreEqual(0, settings.GetExpandedTreePaths(@"X:\other.toml").Count, "unknown solution -> empty");
        }

        [TestMethod]
        public void DiagnosticMessageAt_ReturnsTheSquiggledLinesIssue()
        {
            using (var ed = new HelixPythonNamespace.Editor.PythonEditorControl())
            {
                ed.Text = "line one\nline two\nline three\n";
                ed.MarkError(2, 3, "Expected ';'");

                int posLine2 = ed.Lines[1].Position + 2;
                Assert.AreEqual("Expected ';'", ed.DiagnosticMessageAt(posLine2),
                    "hovering the squiggled line must surface the diagnostic text");
                Assert.IsNull(ed.DiagnosticMessageAt(ed.Lines[0].Position),
                    "a clean line has no diagnostic tip");

                ed.MarkError(2, 9, "Expected ';'");   // duplicate message: not repeated
                ed.MarkError(2, 12, "second issue");
                StringAssert.Contains(ed.DiagnosticMessageAt(posLine2), "second issue");
                Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(
                    ed.DiagnosticMessageAt(posLine2), "Expected ';'").Count, "duplicates collapse");

                ed.ClearErrorMarkers();
                Assert.IsNull(ed.DiagnosticMessageAt(posLine2), "cleared marks leave no stale tips");
            }
        }
    }
}
