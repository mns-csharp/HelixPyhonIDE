using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Notebook;
using HelixPythonNamespace.Notebooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The three copies a cell owes its user: the source without the output, the output without
    /// the source, and the pair.
    ///
    /// <para>
    /// Asserted on the text that actually reaches the clipboard, because every way of getting this
    /// wrong — a truncated output, a stray ANSI escape, LF-only line endings, a fabricated prompt
    /// marker — produces a copy that still looks perfectly right on screen and is only discovered
    /// after it has been pasted somewhere else.
    /// </para>
    /// </summary>
    [TestClass]
    public class NotebookCellCopyTests
    {
        private const char Escape = (char)27;

        private static NotebookCell CellWith(string source, params NotebookOutput[] outputs)
        {
            var cell = new NotebookCell(NotebookCellKind.Code) { Source = source };
            foreach (NotebookOutput output in outputs) cell.Outputs.Add(output);
            return cell;
        }

        private static NotebookOutput Stream(string text)
        {
            return new NotebookOutput
            {
                Kind = NotebookOutputKind.Stream,
                StreamName = "stdout",
                Text = text,
            };
        }

        // ------------------------------------------------------------------ what gets copied

        [TestMethod]
        public void CopyingTheSourceLeavesTheOutputBehind()
        {
            NotebookCell cell = CellWith("import torch\nprint(torch.__version__)\n", Stream("2.1.0\n"));

            string copied = NotebookCellClipboard.SourceText(cell);

            Assert.AreEqual("import torch" + Environment.NewLine + "print(torch.__version__)", copied);
            Assert.IsFalse(copied.Contains("2.1.0"),
                "the source copy must carry no trace of what running the cell produced");
        }

        [TestMethod]
        public void CopyingTheOutputLeavesTheSourceBehind()
        {
            NotebookCell cell = CellWith("import torch\nprint(torch.__version__)\n", Stream("2.1.0\n"));

            string copied = NotebookCellClipboard.OutputText(cell);

            Assert.AreEqual("2.1.0", copied);
            Assert.IsFalse(copied.Contains("import torch"),
                "the output copy must carry none of the code that produced it");
        }

        [TestMethod]
        public void CopyingBothPutsTheSourceFirstSeparatedByABlankLine()
        {
            NotebookCell cell = CellWith("print(1)\n", Stream("1\n"));

            Assert.AreEqual("print(1)" + Environment.NewLine + Environment.NewLine + "1",
                            NotebookCellClipboard.SourceAndOutputText(cell));
        }

        [TestMethod]
        public void ATracebackIsCopiedWithoutItsAnsiEscapes()
        {
            var error = new NotebookOutput
            {
                Kind = NotebookOutputKind.Error,
                ErrorName = "ModuleNotFoundError",
                ErrorValue = "No module named 'cms'",
            };
            error.Traceback.Add(Escape + "[0;31mModuleNotFoundError" + Escape + "[0m   Traceback (most recent call last)");
            error.Traceback.Add(Escape + "[0;32m----> 3 from cms.tensor import device");

            string copied = NotebookCellClipboard.OutputText(CellWith("from cms.tensor import device\n", error));

            Assert.IsTrue(copied.IndexOf(Escape) < 0,
                "`[0;31m` pasted into a bug report is noise the user then has to clean out by hand");
            StringAssert.Contains(copied, "ModuleNotFoundError   Traceback (most recent call last)");
            StringAssert.Contains(copied, "----> 3 from cms.tensor import device");
        }

        /// <summary>
        /// The painter caps an output at <c>MaxOutputLines</c> and says so on screen. The clipboard
        /// has nowhere to put that note, so carrying the cap here would hand the user a truncated
        /// copy of their own output with nothing attached to say it had been cut.
        /// </summary>
        [TestMethod]
        public void ALongOutputIsCopiedWhole()
        {
            int lines = new NotebookStyle().MaxOutputLines * 3;
            var text = new StringBuilder();
            for (int i = 0; i < lines; i++) text.Append("line ").Append(i).Append('\n');

            string copied = NotebookCellClipboard.OutputText(
                CellWith("for i in range(n): print(i)\n", Stream(text.ToString())));

            Assert.AreEqual(lines, NotebookCellClipboard.LineCount(copied));
            StringAssert.Contains(copied, "line " + (lines - 1));
            Assert.IsFalse(copied.Contains("not shown"));
        }

        [TestMethod]
        public void EveryCopyUsesTheClipboardsLineEndings()
        {
            NotebookCell cell = CellWith("a = 1\nb = 2\n", Stream("done\nreally\n"));

            foreach (NotebookCopyPart part in new[]
                     { NotebookCopyPart.Source, NotebookCopyPart.Output, NotebookCopyPart.SourceAndOutput })
            {
                string copied = NotebookCellClipboard.TextFor(cell, part);
                Assert.AreEqual(0, Regex.Matches(copied, "(?<!\r)\n").Count,
                    "a bare LF pastes into Notepad as one long line: " + part);
            }
        }

        [TestMethod]
        public void MultipleOutputsAreJoinedInOrderWithNothingInvented()
        {
            NotebookCell cell = CellWith("print('a'); print('b')\n", Stream("a\n"), Stream("b\n"));

            Assert.AreEqual("a" + Environment.NewLine + "b", NotebookCellClipboard.OutputText(cell));
        }

        [TestMethod]
        public void AnImageOnlyOutputCopiesNothingAndIsNotConfusedWithNeverHavingRun()
        {
            var figure = new NotebookOutput { Kind = NotebookOutputKind.DisplayData };
            figure.Data["image/png"] = "iVBORw0KGgo=";

            NotebookCell plotted = CellWith("df.plot()\n", figure);
            NotebookCell neverRan = CellWith("df.plot()\n");

            Assert.AreEqual(string.Empty, NotebookCellClipboard.OutputText(plotted));
            Assert.IsTrue(NotebookCellClipboard.HasOnlyNonTextOutputs(plotted),
                "a figure has no text, and saying so is a different message from 'run it first'");
            Assert.IsFalse(NotebookCellClipboard.HasOnlyNonTextOutputs(neverRan));
        }

        [TestMethod]
        public void CopyingBothFromACellThatNeverRanIsJustTheSource()
        {
            Assert.AreEqual("print(1)", NotebookCellClipboard.SourceAndOutputText(CellWith("print(1)\n")),
                "a cell with no output must not copy with a blank line hanging off the bottom");
        }

        // ------------------------------------------------------------------ through the surface

        private static NotebookTabPage TabWith(params NotebookCell[] cells)
        {
            var document = new NotebookDocument();
            foreach (NotebookCell cell in cells) document.Cells.Add(cell);

            var tab = new NotebookTabPage { Size = new Size(1000, 700) };
            tab.View.Size = new Size(1000, 700);
            tab.View.CreateControl();
            tab.View.Document = document;
            return tab;
        }

        /// <summary>
        /// Raise the control's own mouse handler by reflection rather than through a test-only
        /// entry point — the same reason <c>NotebookCellSizingTests</c> does: the point is to drive
        /// the very <c>OnMouseDown</c> a real click reaches, and a production hook added for a test
        /// is a second path that can drift away from the first.
        /// </summary>
        private static void MouseDown(NotebookViewControl view, MouseButtons button, Point at)
        {
            typeof(NotebookViewControl)
                .GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance
                                          | System.Reflection.BindingFlags.NonPublic)
                .Invoke(view, new object[] { new MouseEventArgs(button, 1, at.X, at.Y, 0) });
        }

        private static void KeyDown(NotebookViewControl view, Keys keys)
        {
            typeof(NotebookViewControl)
                .GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance
                                        | System.Reflection.BindingFlags.NonPublic)
                .Invoke(view, new object[] { new KeyEventArgs(keys) });
        }

        [TestMethod]
        public void TheThreeCommandsReachTheRealClipboard()
        {
            using (NotebookTabPage tab = TabWith(CellWith("import torch\n", Stream("2.1.0\n"))))
            {
                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };

                tab.CopyCellSource();
                AssertClipboard("import torch", reported);

                tab.CopyCellOutput();
                AssertClipboard("2.1.0", reported);

                tab.CopyCellSourceAndOutput();
                AssertClipboard("import torch" + Environment.NewLine + Environment.NewLine + "2.1.0", reported);
            }
        }

        /// <summary>
        /// Assert on the real clipboard without turning another program into a test failure.
        ///
        /// <para>
        /// The Windows clipboard is one machine-wide object that any running process can hold open —
        /// clipboard history, a browser, a chat client — and while it is held, <c>SetText</c> throws
        /// and <c>GetText</c> can come back stale. Measured: this test passed alone and in its own
        /// suite and failed once in a combined run, for exactly that reason. Two responses, and the
        /// distinction matters: a copy the product REPORTED as blocked is not evidence about this
        /// code, so it is inconclusive; anything else is a real mismatch and fails.
        /// </para>
        /// </summary>
        /// <summary>The clipboard's text, or null when it is momentarily held by someone else.</summary>
        private static string ReadClipboard()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { return Clipboard.GetText(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                System.Threading.Thread.Sleep(25);
            }
            return null;
        }

        private static void AssertClipboard(string expected, string reportedStatus)
        {
            if (reportedStatus != null && reportedStatus.StartsWith("Could not copy", StringComparison.Ordinal))
            {
                Assert.Inconclusive("another process was holding the clipboard: " + reportedStatus);
                return;
            }

            // The clipboard settles asynchronously; a read on the very next instruction can still
            // see the previous owner's data.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                string actual = null;
                try { actual = Clipboard.GetText(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
                if (string.Equals(actual, expected, StringComparison.Ordinal)) return;
                System.Threading.Thread.Sleep(25);
            }

            Assert.AreEqual(expected, Clipboard.GetText());
        }

        [TestMethod]
        public void AnEmptyCopyReportsWhyInsteadOfWipingTheClipboard()
        {
            try { Clipboard.SetText("something the user copied earlier"); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                Assert.Inconclusive("another process was holding the clipboard: " + ex.Message);
                return;
            }

            using (NotebookTabPage tab = TabWith(CellWith("df.plot()\n")))
            {
                string reported = null;
                tab.StatusChanged += delegate (object sender, string message) { reported = message; };

                // Confirm the sentinel is still ours immediately before the action. Anything else
                // means another process took the clipboard between the two calls, and this test
                // then has nothing to measure — which is not the same as the product failing.
                if (!string.Equals(ReadClipboard(), "something the user copied earlier", StringComparison.Ordinal))
                {
                    Assert.Inconclusive("another process replaced the clipboard before the test could act");
                    return;
                }

                tab.CopyCellOutput();

                Assert.AreEqual("That cell has not produced any output yet.", reported);
                Assert.AreEqual("something the user copied earlier", ReadClipboard(),
                    "a copy with nothing to copy must not throw away what the user already had");
            }
        }

        [TestMethod]
        public void CtrlCCopiesTheSourceAndCtrlShiftCCopiesBoth()
        {
            using (NotebookTabPage tab = TabWith(CellWith("print(1)\n", Stream("1\n"))))
            {
                var asked = new List<NotebookCopyPart>();
                tab.View.CopyRequested += delegate (object sender, NotebookCopyPart part) { asked.Add(part); };

                KeyDown(tab.View, Keys.C | Keys.Control);
                KeyDown(tab.View, Keys.C | Keys.Control | Keys.Shift);

                CollectionAssert.AreEqual(
                    new[] { NotebookCopyPart.Source, NotebookCopyPart.SourceAndOutput }, asked);
            }
        }

        /// <summary>
        /// Ctrl+C while a cell is open for editing belongs to the embedded editor: the user is
        /// looking at a selection inside a text box, and copying the whole cell instead would be
        /// the surface overruling what they highlighted.
        /// </summary>
        [TestMethod]
        public void CtrlCIsLeftToTheEditorWhileACellIsBeingEdited()
        {
            using (NotebookTabPage tab = TabWith(CellWith("print(1)\n", Stream("1\n"))))
            {
                var asked = new List<NotebookCopyPart>();
                tab.View.CopyRequested += delegate (object sender, NotebookCopyPart part) { asked.Add(part); };

                tab.View.BeginEdit(0);
                KeyDown(tab.View, Keys.C | Keys.Control);

                Assert.AreEqual(0, asked.Count);
            }
        }

        /// <summary>
        /// A right click is how the copy menu is reached, so it has to move the selection onto the
        /// cell it landed on — and must do nothing else on the way. It used to run the very
        /// handler a left click runs, which opened the cell for editing: an edit nobody asked for,
        /// on the way to a menu.
        /// </summary>
        [TestMethod]
        public void ARightClickSelectsTheCellWithoutOpeningItForEditing()
        {
            using (NotebookTabPage tab = TabWith(CellWith("first = 1\n"), CellWith("second = 2\n")))
            {
                MouseDown(tab.View, MouseButtons.Right, InsideSourceOf(tab.View, 1));

                Assert.AreEqual(1, tab.View.SelectedIndex, "the menu that follows is about the cell clicked");
                Assert.IsFalse(tab.View.IsEditing, "a right click must not open the editor");
            }
        }

        [TestMethod]
        public void ALeftClickInTheSameSpotStillOpensTheEditor()
        {
            using (NotebookTabPage tab = TabWith(CellWith("first = 1\n"), CellWith("second = 2\n")))
            {
                MouseDown(tab.View, MouseButtons.Left, InsideSourceOf(tab.View, 1));

                Assert.AreEqual(1, tab.View.SelectedIndex);
                Assert.IsTrue(tab.View.IsEditing,
                    "the right-click guard must not have cost the left click its behaviour");
            }
        }

        /// <summary>A client point inside a cell's code area, clear of the resize grip.</summary>
        private static Point InsideSourceOf(NotebookViewControl view, int index)
        {
            Rectangle source = view.Layouts[index].SourceBounds;
            Point scroll = view.AutoScrollPosition;
            return new Point(source.Left + 20 + scroll.X, source.Top + 4 + scroll.Y);
        }
    }
}
