using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Editor.Markdown;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The Markdown preview: the layout engine, the view control, and the editor wiring that makes
    /// a .md file open rendered instead of syntax-coloured as Python.
    ///
    /// Lives in the FAST editor suite, not App.Tests: none of it needs a composed <c>MainForm</c> or
    /// a message pump. Layout is asserted on the visual list the engine produces rather than on
    /// pixels — a screenshot can only suggest that text wrapped, whereas the visuals say where every
    /// run was actually placed.
    /// </summary>
    [TestClass]
    public class MarkdownRenderingTests
    {
        // ---- file classification ------------------------------------------------------------------

        /// <summary>
        /// The bug that started this: <c>.md</c> had no branch in the highlighting dispatch, so it
        /// fell through to <c>Lexer.Python</c> and a README's prose was coloured as source — every
        /// `is`, `not`, `and`, `class` and `import` in it rendered as a keyword.
        /// </summary>
        [TestMethod]
        public void MarkdownExtensionsAreRecognised()
        {
            Assert.IsTrue(PythonEditorControl.IsMarkdownFilePath(@"C:\x\README.md"));
            Assert.IsTrue(PythonEditorControl.IsMarkdownFilePath(@"C:\x\notes.MARKDOWN"));
            Assert.IsTrue(PythonEditorControl.IsMarkdownFilePath("a.mkd"));
            Assert.IsFalse(PythonEditorControl.IsMarkdownFilePath(@"C:\x\main.py"));
            Assert.IsFalse(PythonEditorControl.IsMarkdownFilePath(@"C:\x\pyproject.toml"));
            Assert.IsFalse(PythonEditorControl.IsMarkdownFilePath(null));
        }

        // ---- layout ---------------------------------------------------------------------------------

        private static System.Collections.Generic.IList<MarkdownVisual> LayoutOf(string markdown, int width = 600)
        {
            using (var view = new MarkdownViewControl())
            {
                view.Width = width;
                view.Height = 400;
                view.SetMarkdown(markdown);
                return view.Visuals.ToList();
            }
        }

        [TestMethod]
        public void AHeadingIsLaidOutLargerThanBodyText()
        {
            var visuals = LayoutOf("# Big\n\nplain");
            MarkdownVisual heading = visuals.First(v => v.Text == "Big");
            MarkdownVisual body = visuals.First(v => v.Text != null && v.Text.StartsWith("plain"));
            Assert.IsTrue(heading.Font.Size > body.Font.Size,
                "heading " + heading.Font.Size + " vs body " + body.Font.Size);
        }

        [TestMethod]
        public void StrongAndEmphasisReachTheFont()
        {
            var visuals = LayoutOf("**bold** and *slanted*");
            Assert.IsTrue(visuals.First(v => v.Text != null && v.Text.StartsWith("bold")).Font.Bold);
            Assert.IsTrue(visuals.First(v => v.Text != null && v.Text.StartsWith("slanted")).Font.Italic);
        }

        [TestMethod]
        public void InlineCodeGetsTheMonospaceFontAndItsOwnBackground()
        {
            MarkdownVisual code = LayoutOf("use `run_phase()` here")
                .First(v => v.Text != null && v.Text.Contains("run_phase"));
            Assert.AreEqual("Consolas", code.Font.FontFamily.Name);
            Assert.AreNotEqual(System.Drawing.Color.Empty, code.BackColor);
        }

        /// <summary>
        /// A paragraph longer than the pane wraps onto multiple lines, and every run stays inside
        /// the content width. Asserting the second half — that nothing overflows — is what catches a
        /// wrap that "works" by letting one long word run off the right edge.
        /// </summary>
        [TestMethod]
        public void LongParagraphsWrapWithinTheAvailableWidth()
        {
            string paragraph = string.Join(" ", Enumerable.Repeat("word", 200).ToArray());
            var visuals = LayoutOf(paragraph, 400);
            var runs = visuals.Where(v => v.Kind == MarkdownVisualKind.Text).ToList();

            int distinctRows = runs.Select(v => v.Bounds.Top).Distinct().Count();
            Assert.IsTrue(distinctRows > 5, "expected the paragraph to wrap; rows=" + distinctRows);
            Assert.IsTrue(runs.All(v => v.Bounds.Right <= 400), "a run overflowed the pane width");
        }

        /// <summary>
        /// Reflow on resize. Markdown paragraphs are not pre-wrapped, so a narrower pane must
        /// produce more rows — if it does not, the layout is being reused when it should not be.
        /// </summary>
        [TestMethod]
        public void NarrowingThePaneProducesMoreLines()
        {
            string paragraph = string.Join(" ", Enumerable.Repeat("word", 120).ToArray());
            int wide = LayoutOf(paragraph, 700).Where(v => v.Kind == MarkdownVisualKind.Text)
                                               .Select(v => v.Bounds.Top).Distinct().Count();
            int narrow = LayoutOf(paragraph, 300).Where(v => v.Kind == MarkdownVisualKind.Text)
                                                 .Select(v => v.Bounds.Top).Distinct().Count();
            Assert.IsTrue(narrow > wide, "wide=" + wide + " narrow=" + narrow);
        }

        [TestMethod]
        public void ACodeBlockIsDrawnAsAShadedBoxWithOneRunPerLine()
        {
            var visuals = LayoutOf("```python\na = 1\nb = 2\n```");
            Assert.IsTrue(visuals.Any(v => v.Kind == MarkdownVisualKind.Fill), "no shaded background");
            Assert.IsTrue(visuals.Any(v => v.Text == "a = 1"));
            Assert.IsTrue(visuals.Any(v => v.Text == "b = 2"));
            Assert.IsTrue(visuals.Where(v => v.Text == "a = 1" || v.Text == "b = 2")
                                 .All(v => v.Font.FontFamily.Name == "Consolas"));
        }

        /// <summary>
        /// Code is laid out unwrapped, so a long line is reachable by scrolling rather than being
        /// reflowed — reflowing code changes what it says.
        /// </summary>
        [TestMethod]
        public void ALongCodeLineIsNotWrapped()
        {
            string line = new string('x', 400);
            var visuals = LayoutOf("```\n" + line + "\n```", 300);
            Assert.IsTrue(visuals.Any(v => v.Text == line), "the code line was broken up");
        }

        [TestMethod]
        public void ATableIsLaidOutAsAGrid()
        {
            var visuals = LayoutOf("| was | is now |\n|---|---|\n| nine caches | class attributes |");
            Assert.IsTrue(visuals.Any(v => v.Text != null && v.Text.StartsWith("was")));
            Assert.IsTrue(visuals.Any(v => v.Text != null && v.Text.StartsWith("nine")));
            Assert.IsTrue(visuals.Count(v => v.Kind == MarkdownVisualKind.Rule) >= 3,
                "expected gridlines around and between the rows");
        }

        /// <summary>Header cells are bold and shaded, which is what distinguishes a table from an
        /// indented block of text at a glance.</summary>
        [TestMethod]
        public void TableHeadersAreBoldAndShaded()
        {
            var visuals = LayoutOf("| head |\n|---|\n| body |");
            Assert.IsTrue(visuals.First(v => v.Text != null && v.Text.StartsWith("head")).Font.Bold);
            Assert.IsTrue(visuals.Any(v => v.Kind == MarkdownVisualKind.Fill), "no header shading");
        }

        /// <summary>
        /// The header fill must be painted BEFORE the header text, or it covers it. The visuals
        /// list is painted in order, so this is an ordering assertion, not a colour one.
        /// </summary>
        [TestMethod]
        public void TheHeaderShadingIsPaintedBehindItsText()
        {
            var visuals = LayoutOf("| head |\n|---|\n| body |");
            int fill = visuals.ToList().FindIndex(v => v.Kind == MarkdownVisualKind.Fill);
            int text = visuals.ToList().FindIndex(v => v.Text != null && v.Text.StartsWith("head"));
            Assert.IsTrue(fill >= 0 && text >= 0);
            Assert.IsTrue(fill < text, "the fill would paint over the header text");
        }

        [TestMethod]
        public void ALinkCarriesItsDestinationForHitTesting()
        {
            MarkdownVisual link = LayoutOf("see [the docs](https://example.invalid/d) now")
                .First(v => v.LinkTarget != null);
            Assert.AreEqual("https://example.invalid/d", link.LinkTarget);
            StringAssert.Contains(link.Text, "the");
        }

        /// <summary>
        /// An image is a labelled placeholder, never a fetch. Loading it would mean reading an
        /// arbitrary path — or making a network request for an <c>https://</c> source — because a
        /// document was opened in an editor.
        /// </summary>
        [TestMethod]
        public void AnImageIsAPlaceholderAndIsNeverFetched()
        {
            var visuals = LayoutOf("![a diagram](https://example.invalid/x.png)");
            MarkdownVisual box = visuals.First(v => v.Kind == MarkdownVisualKind.ImageBox);
            Assert.AreEqual("a diagram", box.Text);
        }

        [TestMethod]
        public void ListsAreIndentedAndMarked()
        {
            var visuals = LayoutOf("- alpha\n- beta");
            Assert.AreEqual(2, visuals.Count(v => v.Text == "•"));
            MarkdownVisual bullet = visuals.First(v => v.Text == "•");
            MarkdownVisual text = visuals.First(v => v.Text != null && v.Text.StartsWith("alpha"));
            Assert.IsTrue(text.Bounds.Left > bullet.Bounds.Left, "item text must be indented past its marker");
        }

        [TestMethod]
        public void OrderedListsNumberTheirItems()
        {
            var visuals = LayoutOf("3. a\n4. b");
            Assert.IsTrue(visuals.Any(v => v.Text == "3."));
            Assert.IsTrue(visuals.Any(v => v.Text == "4."));
        }

        [TestMethod]
        public void AThematicBreakIsARule()
        {
            Assert.IsTrue(LayoutOf("a\n\n---\n\nb").Any(v => v.Kind == MarkdownVisualKind.Rule));
        }

        [TestMethod]
        public void AnEmptyDocumentLaysOutWithoutThrowing()
        {
            Assert.AreEqual(0, LayoutOf(string.Empty).Count);
        }

        // ---- the control -----------------------------------------------------------------------------

        [TestMethod]
        public void TheViewExposesTheDocumentTitle()
        {
            using (var view = new MarkdownViewControl())
            {
                view.SetMarkdown("# The Engine\n\nbody");
                Assert.AreEqual("The Engine", view.DocumentTitle);
            }
        }

        /// <summary>
        /// A document taller than the pane must report a content height greater than the viewport,
        /// or there is nothing for the scrollbar to scroll.
        /// </summary>
        [TestMethod]
        public void ALongDocumentProducesAScrollableExtent()
        {
            using (var view = new MarkdownViewControl())
            {
                view.Width = 400;
                view.Height = 200;
                view.SetMarkdown(string.Join("\n\n", Enumerable.Repeat("paragraph", 80).ToArray()));
                Assert.IsTrue(view.ContentSize.Height > 200, "height=" + view.ContentSize.Height);
            }
        }

        /// <summary>
        /// The control never follows a link itself: it raises the event and leaves the decision to
        /// the host. Opening a URL or a file from a preview pane is not a call this control gets to
        /// make.
        /// </summary>
        [TestMethod]
        public void TheViewRaisesLinkClicksRatherThanFollowingThem()
        {
            using (var view = new MarkdownViewControl())
            {
                string seen = null;
                view.LinkClicked += (s, e) => seen = e.Target;
                view.Width = 400; view.Height = 200;
                view.SetMarkdown("[x](https://example.invalid/y)");

                MarkdownVisual link = view.Visuals.First(v => v.LinkTarget != null);
                view.TestClickAt(link.Bounds.Left + 2, link.Bounds.Top + 2);

                Assert.AreEqual("https://example.invalid/y", seen);
            }
        }

        // ---- editor wiring ------------------------------------------------------------------------------

        private static string WriteTemp(string name, string content)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-md-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// The answer to the original report. Opening a .md file lands on the RENDERED view, not on
        /// a syntax-coloured text file.
        /// </summary>
        [TestMethod]
        public void OpeningAMarkdownFile_LandsOnThePreview()
        {
            string path = WriteTemp("README.md", "# Title\n\nbody text\n");
            using (var tab = new EditorTabPage(path))
            {
                Assert.IsTrue(tab.IsMarkdown);
                Assert.IsTrue(tab.IsPreviewVisible, "a .md file must open rendered");
                Assert.AreEqual("Title", tab.MarkdownView.DocumentTitle);
            }
        }

        [TestMethod]
        public void OpeningAPythonFile_IsUnaffected()
        {
            string path = WriteTemp("main.py", "class A:\n    pass\n");
            using (var tab = new EditorTabPage(path))
            {
                Assert.IsFalse(tab.IsMarkdown);
                Assert.IsFalse(tab.IsPreviewVisible);
                Assert.IsNull(tab.MarkdownView, "a non-Markdown tab must not build a preview at all");
            }
        }

        [TestMethod]
        public void TheSourceToggleSwapsTheVisibleHalf()
        {
            string path = WriteTemp("README.md", "# Title\n");
            using (var tab = new EditorTabPage(path))
            {
                tab.ShowMarkdownPreview(false);
                Assert.IsFalse(tab.IsPreviewVisible);
                Assert.IsTrue(tab.Editor.Visible, "Source mode must show the editor");

                tab.ShowMarkdownPreview(true);
                Assert.IsTrue(tab.IsPreviewVisible);
                Assert.IsFalse(tab.Editor.Visible);
            }
        }

        /// <summary>
        /// The toggle is inert on a non-Markdown tab — a stray call must not hide the editor of a
        /// .py file behind a preview that was never built.
        /// </summary>
        [TestMethod]
        public void TheToggleIsInertOnANonMarkdownTab()
        {
            string path = WriteTemp("main.py", "x = 1\n");
            using (var tab = new EditorTabPage(path))
            {
                tab.ShowMarkdownPreview(true);
                Assert.IsFalse(tab.IsPreviewVisible);
                Assert.IsTrue(tab.Editor.Visible);
            }
        }

        /// <summary>
        /// Switching to Source re-renders from the CURRENT editor text, so an edit made there shows
        /// up on the way back. The debounce timer is not driven here — the mode switch itself has to
        /// re-render, or a user who edits and immediately clicks Preview sees stale content.
        /// </summary>
        [TestMethod]
        public void SwitchingBackToPreview_RendersTheEditedText()
        {
            string path = WriteTemp("README.md", "# Before\n");
            using (var tab = new EditorTabPage(path))
            {
                tab.ShowMarkdownPreview(false);
                tab.Editor.Text = "# After\n";
                tab.ShowMarkdownPreview(true);
                Assert.AreEqual("After", tab.MarkdownView.DocumentTitle);
            }
        }
    }
}
