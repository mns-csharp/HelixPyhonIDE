using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The adapter that lets every Python engine in the IDE read a notebook without knowing it is
    /// one. Two properties carry the whole feature:
    /// <list type="number">
    /// <item>names bound in an earlier cell are visible to a later one — otherwise completion in a
    /// notebook knows nothing, which is the state every "notebook support" ships in first;</item>
    /// <item>offsets and line numbers stay exact, so a diagnostic at joined line 412 comes back as
    /// "cell 9, line 4" instead of pointing at nothing.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class NotebookCodeViewTests
    {
        private static NotebookDocument Notebook(params object[] cells)
        {
            var notebook = new NotebookDocument();
            for (int i = 0; i < cells.Length; i += 2)
                notebook.InsertCell(notebook.Cells.Count, (NotebookCellKind)cells[i], (string)cells[i + 1]);
            return notebook;
        }

        [TestMethod]
        public void CodeCellsAreJoined_AndMarkdownCellsAreNot()
        {
            NotebookDocument notebook = Notebook(
                NotebookCellKind.Code, "import numpy as np\n",
                NotebookCellKind.Markdown, "# Not python at all: `class`, `import`, `def`\n",
                NotebookCellKind.Code, "x = np.zeros(3)\n");

            NotebookCodeView view = NotebookCodeView.Build(notebook);

            StringAssert.Contains(view.Source, "import numpy as np");
            StringAssert.Contains(view.Source, "x = np.zeros(3)");
            Assert.IsFalse(view.Source.Contains("Not python at all"),
                "prose in a markdown cell must never reach a Python parser");
            Assert.AreEqual(2, view.Spans.Count);
        }

        [TestMethod]
        public void ACellsCaretOffsetMapsIntoTheJoinedSource()
        {
            NotebookDocument notebook = Notebook(
                NotebookCellKind.Code, "alpha = 1\n",
                NotebookCellKind.Code, "beta = alpha\n");

            NotebookCodeView view = NotebookCodeView.Build(notebook);
            int global = view.ToGlobalOffset(1, "beta = al".Length);

            Assert.IsTrue(global > 0);
            Assert.AreEqual("beta = al", view.Source.Substring(view.Spans[1].StartOffset, "beta = al".Length));
            Assert.AreEqual(global, view.Spans[1].StartOffset + "beta = al".Length,
                "completion asks the engine about THIS offset; off by one cell and it answers about "
                + "the wrong scope");
        }

        [TestMethod]
        public void AJoinedLineMapsBackToItsCellAndLine()
        {
            NotebookDocument notebook = Notebook(
                NotebookCellKind.Code, "a = 1\nb = 2\n",
                NotebookCellKind.Markdown, "## heading\n",
                NotebookCellKind.Code, "c = 3\nd = 4\n");

            NotebookCodeView view = NotebookCodeView.Build(notebook);

            int cell, line;
            Assert.IsTrue(view.ToCellLine(1, out cell, out line));
            Assert.AreEqual(0, cell);
            Assert.AreEqual(1, line, "second line of the first cell");

            Assert.IsTrue(view.ToCellLine(3, out cell, out line));
            Assert.AreEqual(2, cell, "the markdown cell contributes nothing, so cell 2 follows cell 0");
            Assert.AreEqual(1, line);

            Assert.IsFalse(view.ToCellLine(999, out cell, out line), "a line past the end maps nowhere");
        }

        [TestMethod]
        public void PrecedingCodeIsWhatCompletionInACellShouldSee()
        {
            NotebookDocument notebook = Notebook(
                NotebookCellKind.Code, "import pandas as pd\n",
                NotebookCellKind.Code, "frame = pd.DataFrame()\n",
                NotebookCellKind.Code, "frame.");

            string context = NotebookCodeView.PrecedingCode(notebook, 2);

            StringAssert.Contains(context, "import pandas as pd");
            StringAssert.Contains(context, "frame = pd.DataFrame()");
            Assert.IsFalse(context.Contains("frame."), "the cell being typed in is not its own context");
            Assert.AreEqual(string.Empty, NotebookCodeView.PrecedingCode(notebook, 0));
        }

        // ------------------------------------------------------------------ magics

        [TestMethod]
        public void MagicsAreRecognised()
        {
            Assert.IsTrue(NotebookMagics.IsMagicLine("%matplotlib inline"));
            Assert.IsTrue(NotebookMagics.IsMagicLine("  %load_ext tensorboard"));
            Assert.IsTrue(NotebookMagics.IsMagicLine("!pip install pandas"));
            Assert.IsTrue(NotebookMagics.IsMagicLine("%%time"));
            Assert.IsTrue(NotebookMagics.IsMagicLine("df.head?"));

            Assert.IsFalse(NotebookMagics.IsMagicLine("x = a % b"), "modulo is not a magic");
            Assert.IsFalse(NotebookMagics.IsMagicLine("print(\"100%\")"));
            Assert.IsFalse(NotebookMagics.IsMagicLine("assert x != y"));
            Assert.IsFalse(NotebookMagics.IsMagicLine(string.Empty));
        }

        [TestMethod]
        public void AMagicBecomesACommentOfExactlyTheSameLength()
        {
            // Blanking rather than deleting is what keeps every later offset and line number exact.
            const string source = "%matplotlib inline\nx = 1\n";
            string neutral = NotebookMagics.NeutraliseForParsing(source);

            Assert.AreEqual(source.Length, neutral.Length, "lengths must match character for character");
            StringAssert.StartsWith(neutral, "#");
            Assert.IsFalse(neutral.Contains("matplotlib"));
            StringAssert.Contains(neutral, "x = 1", "the real code is untouched");
        }

        [TestMethod]
        public void AnIndentedMagicKeepsItsIndentation()
        {
            // A magic inside a loop body must stay inside it, or the parser sees a dedent that is
            // not there and everything after it is a syntax error.
            string neutral = NotebookMagics.NeutraliseForParsing("for i in range(3):\n    !echo hi\n    x = i\n");

            string[] lines = neutral.Split('\n');
            StringAssert.StartsWith(lines[1], "    #");
            Assert.AreEqual("    x = i", lines[2]);
        }

        [TestMethod]
        public void ACellMagicNeutralisesTheWholeCell()
        {
            // `%%bash` means the rest of the cell is not Python; parsing it as Python is nonsense.
            string neutral = NotebookMagics.NeutraliseForParsing("%%bash\nls -la\nfor f in *; do echo $f; done\n");

            foreach (string line in neutral.Split('\n'))
                Assert.IsTrue(line.Length == 0 || line.TrimStart().StartsWith("#"), "left as code: " + line);
        }

    }
}
