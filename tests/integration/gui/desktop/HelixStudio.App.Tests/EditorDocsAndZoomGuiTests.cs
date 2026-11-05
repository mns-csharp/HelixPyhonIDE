using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The four editor/explorer behaviours reported against <c>async_ops.py</c>, driven through a
    /// real composed <see cref="MainForm"/> with the real python-test-sample-oop solution loaded
    /// and the real file opened in a real editor tab:
    ///
    ///   1. the virtual '{' / '}' of a wrapped <c>def</c> align to the <c>def</c>, not to the
    ///      continuation line that closes its parameter list;
    ///   2. Ctrl+MouseWheel zooms the font and the size is persisted;
    ///   3. F1 / Ctrl+K,Ctrl+I resolves a module name and a member of an imported module;
    ///   4. a Solution Explorer symbol row offers its docstring as a tooltip.
    ///
    /// Nothing here makes a window visible: the form is realized headlessly and the popups are
    /// checked through the state that feeds them rather than by putting a real tooltip on the
    /// developer's desktop.
    /// </summary>
    [TestClass]
    public class EditorDocsAndZoomGuiTests
    {
        private static MainForm _form;
        private static SolutionModel _solution;
        private static EditorTabPage _tab;
        private static string _asyncOpsPath;
        private static string _source;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            _form = TestMainForm.CreateComposed();
            FixtureSolution.RealizeHeadless(_form);

            string fixtureRoot = FixtureSolution.Path_("python", "python-test-sample-oop");
            _solution = FixtureSolution.OpenSolutionForTree(_form, fixtureRoot);

            _asyncOpsPath = Path.Combine(fixtureRoot, "lib_utils", "async_ops.py");
            Assert.IsTrue(File.Exists(_asyncOpsPath), "fixture file missing: " + _asyncOpsPath);
            _source = File.ReadAllText(_asyncOpsPath);

            _tab = FixtureSolution.OpenFile(_form, _asyncOpsPath);
            // The overlay recompute is debounced by 400ms; run it now rather than racing the timer.
            BlockRenderer().GetType()
                .GetMethod("FlushPendingRecompute", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(BlockRenderer(), null);
            TestMainForm.PumpUntilIdle(iterations: 5, delayMs: 10);
        }

        [ClassCleanup]
        public static void ClassTeardown()
        {
            FixtureSolution.TearDown(_form);
        }

        private static object BlockRenderer()
        {
            FieldInfo f = typeof(PythonEditorControl).GetField("_blockScope",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "PythonEditorControl._blockScope expected");
            object renderer = f.GetValue(_tab.Editor);
            Assert.IsNotNull(renderer, "the editor must have a block-scope renderer");
            return renderer;
        }

        private static List<BlockRange> LiveBlocks()
        {
            object renderer = BlockRenderer();
            FieldInfo f = renderer.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "BlockScopeRenderer._blocks expected");
            var blocks = f.GetValue(renderer) as List<BlockRange>;
            Assert.IsNotNull(blocks, "the renderer must have computed blocks");
            Assert.IsTrue(blocks.Count > 0, "the loaded document produced no blocks");
            return blocks;
        }

        // 1-based line of a source line that starts (after trimming) with `prefix`.
        private static int LineStartingWith(string prefix)
        {
            string[] lines = _source.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal)) return i + 1;
            Assert.Fail("no line starting with \"" + prefix + "\" in " + _asyncOpsPath);
            return -1;
        }

        // ---------------------------------------------------------------- 1. virtual braces

        /// <summary>
        /// The reported bug, measured where it shows: the X coordinate the '{' and '}' glyphs are
        /// painted at (BlockScopeRenderer computes it as PointXFromPosition(headerLineStart +
        /// ParentIndentColumn)) must be the X of column 0 — the column `async def retry_async(`
        /// starts at — not the X of the four-space indent of its wrapped `    ) -> R:` line.
        /// </summary>
        [TestMethod]
        public void WrappedDefHeader_BracesArePaintedAtTheDefsOwnColumn()
        {
            int defLine = LineStartingWith("async def retry_async(");
            int closingLine = LineStartingWith(") -> R:");
            Assert.IsTrue(closingLine > defLine, "the fixture's retry_async signature must wrap");

            BlockRange block = default(BlockRange);
            bool found = false;
            foreach (BlockRange b in LiveBlocks())
            {
                if (b.Kind == BlockKind.Def && b.HeaderLine == closingLine) { block = b; found = true; break; }
            }
            Assert.IsTrue(found, "no Def block whose header ends on the wrapped line " + closingLine);

            Assert.AreEqual(defLine, block.HeaderStartLine,
                "the block's header must start on the 'async def' line");

            // Reproduce the renderer's own glyph-X computation against the live control.
            int headerLineStart = _tab.Editor.Lines[block.HeaderLine - 1].Position;
            int braceX = _tab.Editor.PointXFromPosition(headerLineStart + block.ParentIndentColumn);
            int defLineColumnZeroX = _tab.Editor.PointXFromPosition(_tab.Editor.Lines[defLine - 1].Position);

            Assert.AreEqual(defLineColumnZeroX, braceX,
                "the virtual brace is painted at the wrong column for a wrapped parameter list");
        }

        /// <summary>
        /// Regression guard for the same computation on an UNWRAPPED nested def, which was already
        /// correct: `async def guarded(...)` is indented four spaces and its braces belong there.
        /// </summary>
        [TestMethod]
        public void UnwrappedNestedDef_KeepsItsIndentedBraceColumn()
        {
            int line = LineStartingWith("async def guarded(");
            BlockRange block = default(BlockRange);
            bool found = false;
            foreach (BlockRange b in LiveBlocks())
                if (b.Kind == BlockKind.Def && b.HeaderLine == line) { block = b; found = true; break; }
            Assert.IsTrue(found, "no Def block for the nested guarded() at line " + line);

            Assert.AreEqual(line, block.HeaderStartLine, "a one-line header starts where it ends");
            Assert.AreEqual(4, block.ParentIndentColumn);
        }

        // ---------------------------------------------------------------- 2. Ctrl+MouseWheel zoom

        // The editor-only half of the zoom (wheel handling, the clamp, the change event, and that
        // the zoomed size reaches the STYLE the document is painted with) lives in
        // tests\unit\gui\editor\HelixPythonNamespace.Editor.Tests\EditorFontZoomTests.cs — it needs no
        // MainForm, so it belongs in the fast suite. What stays here is the half that genuinely
        // needs the composed IDE: the host persisting the size and mirroring it into open tabs.

        /// <summary>
        /// The persistence half, through the real IDE: the host records the zoomed size in user
        /// settings and mirrors it into every open document, so the next session (and every other
        /// tab) comes up at the size the user zoomed to.
        /// </summary>
        [TestMethod]
        public void ZoomedSize_IsStoredInUserSettings_AndAppliedToOpenDocuments()
        {
            float original = _form._settingsManager.Settings.EditorFontSize;
            try
            {
                _form.OnEditorFontSizeZoomed(17f);

                Assert.AreEqual(17f, _form._settingsManager.Settings.EditorFontSize,
                    "the zoomed size is what gets persisted");
                Assert.AreEqual(17f, _tab.Editor.EditorFontSize,
                    "the open document must be re-styled at the new size");
                Assert.AreEqual(17, _tab.Editor.Styles[HelixTextEditorNET.Style.Default].Size,
                    "and the re-style must land on the style the document is painted with");
            }
            finally
            {
                _form.OnEditorFontSizeZoomed(original);
            }
        }

        // ---------------------------------------------------------------- 3. F1 / Ctrl+K,Ctrl+I

        // The live editor's own completion engine, so these assertions run against the controller
        // the opened document actually uses rather than a fresh one built by the test.
        private static object LiveQuickInfo(int caret)
        {
            FieldInfo cf = typeof(PythonEditorControl).GetField("_completion",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object controller = cf.GetValue(_tab.Editor);
            Assert.IsNotNull(controller, "the editor must have a completion controller");

            FieldInfo ef = controller.GetType().GetField("_pyEngine",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object engine = ef.GetValue(controller);
            Assert.IsNotNull(engine, "the controller must have a Python completion engine");

            MethodInfo m = engine.GetType().GetMethod("GetQuickInfo");
            return m.Invoke(engine, new object[] { _tab.Editor.Text, caret, _asyncOpsPath, _form._currentSolution });
        }

        private static string ItemDetail(object quickInfoResult)
        {
            Assert.IsNotNull(quickInfoResult, "Quick Info resolved nothing");
            object item = quickInfoResult.GetType().GetField("Item").GetValue(quickInfoResult);
            Assert.IsNotNull(item, "Quick Info produced no item");
            return (string)item.GetType().GetProperty("Detail").GetValue(item, null);
        }

        private static string ItemDocumentation(object quickInfoResult)
        {
            object item = quickInfoResult.GetType().GetField("Item").GetValue(quickInfoResult);
            return (string)item.GetType().GetProperty("Documentation").GetValue(item, null);
        }

        [TestMethod]
        public void F1_OnAnImportedModuleName_ResolvesIt()
        {
            int caret = _tab.Editor.Text.IndexOf("import asyncio", StringComparison.Ordinal) + "import ".Length + 3;
            Assert.AreEqual("module asyncio", ItemDetail(LiveQuickInfo(caret)));
        }

        [TestMethod]
        public void F1_OnAMemberOfAnImportedModule_ResolvesIt()
        {
            int caret = _tab.Editor.Text.IndexOf("asyncio.Semaphore(", StringComparison.Ordinal)
                        + "asyncio.".Length + 3;
            string detail = ItemDetail(LiveQuickInfo(caret));
            StringAssert.Contains(detail, "asyncio.Semaphore",
                "the member must at minimum be named fully; the docstring arrives with the catalog scan");
        }

        [TestMethod]
        public void HoverOverAFunction_ResolvesItsSignatureAndDocstring()
        {
            int caret = _tab.Editor.Text.IndexOf("async def retry_async(", StringComparison.Ordinal)
                        + "async def ".Length + 3;
            object quick = LiveQuickInfo(caret);
            StringAssert.StartsWith(ItemDetail(quick), "async def retry_async(");
            StringAssert.Contains(ItemDocumentation(quick), "Retry an async operation");
        }

        /// <summary>
        /// The reported hover gap, on the line it was reported on:
        ///   handle = asyncio.get_running_loop().call_later(seconds, task.cancel)
        /// `get_running_loop` produced a doctip, `call_later` produced nothing whatsoever because
        /// the receiver scan stops at the `)`. Whether the tip carries the real docstring depends
        /// on whether the interpreter scan of asyncio has landed in this process yet — what must
        /// hold unconditionally is that the member resolves to asyncio at all. (The catalogued
        /// content itself is pinned deterministically in PythonQuickInfoTests.)
        /// </summary>
        [TestMethod]
        public void Hover_OnAMethodCalledOnACallResult_ResolvesInsteadOfSilentlyGivingUp()
        {
            int caret = _tab.Editor.Text.IndexOf("get_running_loop().call_later(", StringComparison.Ordinal)
                        + "get_running_loop().".Length + 3;
            object quick = LiveQuickInfo(caret);
            Assert.IsNotNull(quick, "hovering call_later must resolve to something");

            object item = quick.GetType().GetField("Item").GetValue(quick);
            Assert.IsNotNull(item, "Quick Info produced no item");
            Assert.AreEqual("call_later", item.GetType().GetProperty("Label").GetValue(item, null));

            string unresolved = (string)quick.GetType().GetField("UnresolvedModuleFqn").GetValue(quick);
            string detail = (string)item.GetType().GetProperty("Detail").GetValue(item, null);
            Assert.IsTrue(
                unresolved == "asyncio" || (detail != null && detail.Contains("call_later")),
                "either the asyncio scan is requested or the catalogued method is described; got "
                + "unresolved=" + (unresolved ?? "<null>") + " detail=" + (detail ?? "<null>"));
        }

        /// <summary>ShowQuickInfoAtCaret / ShowHoverAt must be safe to call on a real document —
        /// they run the whole resolve path, they just have no window to put anything in here.</summary>
        [TestMethod]
        public void QuickInfoAndHoverEntryPoints_RunCleanlyOnARealDocument()
        {
            int caret = _tab.Editor.Text.IndexOf("async def retry_async(", StringComparison.Ordinal)
                        + "async def ".Length + 3;
            _tab.Editor.CurrentPosition = caret;
            try
            {
                _tab.Editor.ShowQuickInfoAtCaret();
                _tab.Editor.ShowHoverAt(caret);
                TestMainForm.PumpUntilIdle(iterations: 3, delayMs: 10);
            }
            finally
            {
                // Put the tip away the way the editor does when the pointer leaves (calltip AND the
                // quick-info doc panel). CallTipCancel alone left the doc panel -- a real top-level
                // window -- on screen for the rest of the run, and a later shell's minimize/restore
                // (WindowLifecycleTests.RestoringFromTheTaskbar_...) came back 16x39 px too big.
                typeof(PythonEditorControl).GetMethod("DismissHoverTip", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_tab.Editor, null);
            }
        }

        // ---------------------------------------------------------------- 4. tree tooltips

        [TestMethod]
        public void SolutionExplorer_OffersTheClassDocstringAsATooltip()
        {
            TreeNode node = FindNodeForType("MainWindow");
            Assert.IsNotNull(node, "MainWindow must be in the loaded Solution Explorer");

            string tip = ProjectTreePanel.SymbolToolTipFor(node);
            Assert.IsNotNull(tip, "hovering MainWindow must produce a tooltip");
            StringAssert.Contains(tip,
                "Top-level window: a notebook with a Shapes tab and an Animals tab.");
        }

        [TestMethod]
        public void SolutionExplorer_OffersAMethodDocstringAsATooltip()
        {
            TreeNode type = FindNodeForType("MainWindow");
            TreeNode member = null;
            foreach (TreeNode child in type.Nodes)
            {
                var m = child.Tag as MemberModel;
                if (m != null && m.Name == "_populate") { member = child; break; }
            }
            Assert.IsNotNull(member, "MainWindow._populate must be a child row");

            string tip = ProjectTreePanel.SymbolToolTipFor(member);
            StringAssert.Contains(tip, "Fill both panels with a small, representative sample.");
        }

        private static TreeNode FindNodeForType(string typeName)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tree = f.GetValue(_form._projectTreePanel) as TreeView;
            Assert.IsNotNull(tree, "the Solution Explorer must have a TreeView");

            var stack = new Stack<TreeNode>();
            foreach (TreeNode r in tree.Nodes) stack.Push(r);
            while (stack.Count > 0)
            {
                TreeNode n = stack.Pop();
                var t = n.Tag as TypeModel;
                if (t != null && t.Name == typeName) return n;
                foreach (TreeNode c in n.Nodes) stack.Push(c);
            }
            return null;
        }
    }
}
