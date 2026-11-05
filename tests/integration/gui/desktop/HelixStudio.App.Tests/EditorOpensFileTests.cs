using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 266 (user bug: double-clicking main.py doesn't open it). The last link in that chain is
    /// whether the Scintilla-backed editor really creates its native handle and loads the file.
    ///
    /// Now driven the way the bug was reported: open a REAL fixture solution, then open one of its
    /// own modules through the IDE, and assert the module's real content is in the buffer. The old
    /// version wrote a two-line "def main(): print('hello world')" into %TEMP% and hung it off a
    /// bare TabControl, which proved a Scintilla control can load a string — not that opening a file
    /// in a loaded solution works.
    ///
    /// The old test also required the window to be ON-SCREEN, believing Scintilla could not create
    /// its handle otherwise. That is not true: CreateControl realizes the handle without a visible
    /// window, which is what keeps test runs off the developer's desktop.
    /// </summary>
    [TestClass]
    public class EditorOpensFileTests
    {
        [TestMethod]
        public void OpeningAFixtureModule_LoadsItsRealContentIntoTheEditor()
        {
            string root = FixtureSolution.DirectoryFor(ProjectLanguage.Python, cuda: false);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, root);

                string module = FixtureSolution.AnySourceFile(root, ".py");
                EditorTabPage tab = FixtureSolution.OpenFile(form, module);

                string onDisk = System.IO.File.ReadAllText(module);
                string inEditor = tab.Editor.Text ?? string.Empty;

                Assert.IsTrue(inEditor.Length > 0,
                    "the editor loaded nothing for " + module + " — file-open is broken");
                Assert.AreEqual(onDisk.Replace("\r\n", "\n"), inEditor.Replace("\r\n", "\n"),
                    "the buffer must hold exactly what is on disk for " + module);
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
