using HelixPythonNamespace.TestSupport;
namespace HelixPythonNamespace.Editor.Tests
{
    using System.Drawing;
    using System.Windows.Forms;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Covers the "select a loop, right-click, choose Vectorize/Un-vectorize" editor feature: a
    /// real on-screen PythonEditorControl (Scintilla needs a live handle), exercising
    /// ApplyVectorizationCore directly (internal, via InternalsVisibleTo) rather than the
    /// MessageBox-showing public wrapper, since a modal MessageBox would hang the test run
    /// waiting to be dismissed — see PythonEditorControl.ApplyVectorization vs.
    /// ApplyVectorizationCore for that split.
    /// </summary>
    [TestClass]
    public class VectorizeContextMenuTests
    {
        private static PythonEditorControl NewEditorOn(Form form, string text)
        {
            var editor = new PythonEditorControl { Dock = DockStyle.Fill };
            form.Controls.Add(editor);
            TestWindow.ShowOffScreen(form);
            editor.Text = text;
            return editor;
        }

        private static Form NewHostForm()
        {
            var form = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(600, 400),
                ShowInTaskbar = false,
            };
            return form;
        }

        [TestMethod]
        public void Vectorize_ElementwiseLoop_ReplacesWithNumpyAndInsertsImport()
        {
            using (var form = NewHostForm())
            {
                string loop = "for i in range(len(a)):\n    c[i] = a[i] + b[i]\n";
                var editor = NewEditorOn(form, loop);

                editor.SelectionStart = 0;
                editor.SelectionEnd = loop.Length;

                var result = editor.ApplyVectorizationCore(forward: true);

                Assert.IsNotNull(result);
                Assert.IsTrue(result.Applied, result.Message);
                StringAssert.Contains(editor.Text, "np.asarray(a)");
                StringAssert.Contains(editor.Text, "import numpy as np");
            }
        }

        [TestMethod]
        public void Vectorize_DoesNotDuplicateAnAlreadyPresentImport()
        {
            using (var form = NewHostForm())
            {
                string src = "import numpy as np\n\nfor i in range(len(a)):\n    c[i] = a[i] + b[i]\n";
                var editor = NewEditorOn(form, src);

                int loopStart = src.IndexOf("for i", System.StringComparison.Ordinal);
                editor.SelectionStart = loopStart;
                editor.SelectionEnd = src.Length;

                var result = editor.ApplyVectorizationCore(forward: true);

                Assert.IsTrue(result.Applied, result.Message);
                int firstImport = editor.Text.IndexOf("import numpy as np", System.StringComparison.Ordinal);
                int secondImport = editor.Text.IndexOf("import numpy as np", firstImport + 1, System.StringComparison.Ordinal);
                Assert.AreEqual(-1, secondImport, "the import must not be duplicated when already present");
            }
        }

        [TestMethod]
        public void Unvectorize_SumCall_ReplacesWithExplicitLoop()
        {
            using (var form = NewHostForm())
            {
                string src = "total = sum(xs)\n";
                var editor = NewEditorOn(form, src);

                editor.SelectionStart = 0;
                editor.SelectionEnd = src.Length;

                var result = editor.ApplyVectorizationCore(forward: false);

                Assert.IsNotNull(result);
                Assert.IsTrue(result.Applied, result.Message);
                StringAssert.Contains(editor.Text, "for x in xs");
                StringAssert.Contains(editor.Text, "total += x");
            }
        }

        [TestMethod]
        public void Vectorize_OnAlreadyVectorizedCode_FindsNoForwardCandidate()
        {
            using (var form = NewHostForm())
            {
                // This IS a recognizable pattern, just not a forward (loop->numpy) one — asking to
                // Vectorize a selection that's only reverse-matchable must report "no match", not
                // silently pick a reverse candidate.
                string src = "total = sum(xs)\n";
                var editor = NewEditorOn(form, src);

                editor.SelectionStart = 0;
                editor.SelectionEnd = src.Length;

                var result = editor.ApplyVectorizationCore(forward: true);

                Assert.IsNotNull(result);
                Assert.IsFalse(result.Applied);
                StringAssert.Contains(result.Message, "No recognizable pattern");
            }
        }

        [TestMethod]
        public void ApplyVectorizationCore_WithNoSelection_ReturnsNull()
        {
            using (var form = NewHostForm())
            {
                var editor = NewEditorOn(form, "x = 1\n");
                editor.SelectionStart = 0;
                editor.SelectionEnd = 0;

                Assert.IsNull(editor.ApplyVectorizationCore(forward: true));
            }
        }

        [TestMethod]
        public void IsPythonFile_RecognizesPyAndPyi_NotOtherExtensions()
        {
            Assert.IsTrue(PythonEditorControl.IsPythonFile(@"C:\x\a.py"));
            Assert.IsTrue(PythonEditorControl.IsPythonFile(@"C:\x\a.pyi"));
            Assert.IsFalse(PythonEditorControl.IsPythonFile(@"C:\x\a.cu"));
            Assert.IsFalse(PythonEditorControl.IsPythonFile(null));
        }

        [TestMethod]
        public void FindImportInsertionPoint_LandsAfterTheExistingImportBlock()
        {
            string src = "\"\"\"doc\"\"\"\nimport os\nimport sys\n\nfrom app import thing\n\nclass X:\n    pass\n";
            int insertAt = PythonEditorControl.FindImportInsertionPoint(src);
            string before = src.Substring(0, insertAt);
            Assert.IsTrue(before.Contains("from app import thing"));
            Assert.IsFalse(before.Contains("class X"));
        }

        [TestMethod]
        public void FindImportInsertionPoint_WithNoImports_LandsAtTheTop()
        {
            string src = "x = 1\ny = 2\n";
            Assert.AreEqual(0, PythonEditorControl.FindImportInsertionPoint(src));
        }
    }
}
