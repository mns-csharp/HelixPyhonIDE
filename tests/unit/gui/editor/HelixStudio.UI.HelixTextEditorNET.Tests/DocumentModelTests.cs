using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixTextEditorNET.Core;

namespace HelixTextEditorNET.Tests
{
    [TestClass]
    public class DocumentModelTests
    {
        [TestMethod]
        public void LineIndex_TracksInsertAndDelete()
        {
            var doc = new DocumentModel();
            doc.SetText("aaa\nbbb\nccc");
            Assert.AreEqual(3, doc.LinesTotal);
            Assert.AreEqual(0, doc.LineStart(0));
            Assert.AreEqual(4, doc.LineStart(1));
            Assert.AreEqual(8, doc.LineStart(2));
            Assert.AreEqual(1, doc.LineFromPosition(5));

            doc.InsertString(4, "xxx\n");
            Assert.AreEqual(4, doc.LinesTotal);
            Assert.AreEqual("xxx", doc.GetText(4, 3));
            Assert.AreEqual(8, doc.LineStart(2));

            doc.DeleteChars(4, 4);
            Assert.AreEqual(3, doc.LinesTotal);
            Assert.AreEqual("aaa\nbbb\nccc", doc.GetText(0, doc.Length));
        }

        [TestMethod]
        public void LineEnd_ExcludesEolCharacters()
        {
            var doc = new DocumentModel();
            doc.SetText("ab\r\ncd\nef");
            Assert.AreEqual(2, doc.LineEnd(0));   // before \r\n
            Assert.AreEqual(6, doc.LineEnd(1));   // before \n
            Assert.AreEqual(9, doc.LineEnd(2));   // end of doc
        }

        [TestMethod]
        public void Undo_GroupsActionsBetweenBeginEnd()
        {
            var doc = new DocumentModel();
            doc.BeginUndoAction();
            doc.InsertString(0, "hello ");
            doc.InsertString(6, "world");
            doc.EndUndoAction();
            Assert.AreEqual("hello world", doc.GetText(0, doc.Length));

            doc.Undo();
            Assert.AreEqual(0, doc.Length);  // both inserts undone as one group
            Assert.IsTrue(doc.CanRedo);

            doc.Redo();
            Assert.AreEqual("hello world", doc.GetText(0, doc.Length));
        }

        [TestMethod]
        public void FindText_PlainCaseInsensitiveAndWholeWord()
        {
            var doc = new DocumentModel();
            doc.SetText("Cat catalog cat");
            int len;
            Assert.AreEqual(0, doc.FindText(0, doc.Length, "cat", false, false, false, out len));
            Assert.AreEqual(3, len);
            // Whole word skips "Cat…" inside "catalog".
            Assert.AreEqual(12, doc.FindText(1, doc.Length, "cat", true, true, false, out len));
        }

        [TestMethod]
        public void FindText_RegexAndInvalidPattern()
        {
            var doc = new DocumentModel();
            doc.SetText("foo123bar");
            int len;
            int pos = doc.FindText(0, doc.Length, "[0-9]+", true, false, true, out len);
            Assert.AreEqual(3, pos);
            Assert.AreEqual(3, len);
            // An invalid pattern reports "not found" instead of throwing.
            Assert.AreEqual(DocumentModel.InvalidPosition,
                            doc.FindText(0, doc.Length, "[", true, false, true, out len));
        }

        [TestMethod]
        public void BraceMatch_SkipsBracesInOtherStyles()
        {
            var doc = new DocumentModel();
            doc.SetText("f(a) # (x)");
            // Style the comment region differently so its parens are ignored.
            doc.SetStyleFor(5, 5, 1);
            Assert.AreEqual(3, doc.BraceMatch(1));
            Assert.AreEqual(1, doc.BraceMatch(3));
        }

        [TestMethod]
        public void Markers_AddDeleteAndSearch()
        {
            var doc = new DocumentModel();
            doc.SetText("a\nb\nc\nd");
            doc.AddMark(1, 3);
            doc.AddMark(3, 3);
            Assert.AreEqual(1 << 3, doc.MarkersAtLine(1));
            Assert.AreEqual(1, doc.LineFromMarkerNext(0, 1 << 3));
            Assert.AreEqual(3, doc.LineFromMarkerNext(2, 1 << 3));
            Assert.AreEqual(3, doc.LineFromMarkerPrevious(3, 1 << 3));
            doc.DeleteMark(1, 3);
            Assert.AreEqual(0, doc.MarkersAtLine(1));
        }

        [TestMethod]
        public void Indicators_FillClearAndQuery()
        {
            var doc = new DocumentModel();
            doc.SetText("hello world");
            doc.IndicatorFillRange(8, 2, 5, 1);
            Assert.AreEqual(1, doc.IndicatorValueAt(8, 3));
            Assert.AreEqual(2, doc.IndicatorStart(8, 3));
            Assert.AreEqual(7, doc.IndicatorEnd(8, 3));
            Assert.AreEqual(0, doc.IndicatorValueAt(8, 7));
            doc.IndicatorClearRange(8, 0, doc.Length);
            Assert.AreEqual(0, doc.IndicatorValueAt(8, 3));
        }

        [TestMethod]
        public void Indicators_TrackEdits()
        {
            var doc = new DocumentModel();
            doc.SetText("hello world");
            doc.IndicatorFillRange(0, 6, 5, 1);  // "world"
            doc.InsertString(0, "XX");
            Assert.AreEqual(8, doc.IndicatorStart(0, 9));
            doc.DeleteChars(0, 2);
            Assert.AreEqual(6, doc.IndicatorStart(0, 7));
        }

        [TestMethod]
        public void WordBoundaries_MatchScintillaSemantics()
        {
            var doc = new DocumentModel();
            doc.SetText("foo_bar baz");
            Assert.AreEqual(0, doc.WordStartPosition(4, true));
            Assert.AreEqual(7, doc.WordEndPosition(4, true));
            Assert.AreEqual(8, doc.WordStartPosition(9, true));
        }
    }

    [TestClass]
    public class EditorEngineTests
    {
        [TestMethod]
        public void MoveSelectedLines_UpThenDown_Restores()
        {
            var eng = new EditorEngine();
            eng.Doc.SetText("aaa\nbbb\nccc\n");
            eng.SetEmptySelection(4);  // start of "bbb"

            eng.MoveSelectedLines(-1);
            Assert.IsTrue(eng.Doc.GetText(0, eng.Doc.Length).StartsWith("bbb"));

            eng.MoveSelectedLines(1);
            Assert.AreEqual("aaa\nbbb\nccc\n", eng.Doc.GetText(0, eng.Doc.Length),
                            "a selection touching the next line's start must not drag that line along");
        }

        [TestMethod]
        public void ReplaceSelection_MultiRange_AppliesLowToHighWithDelta()
        {
            var eng = new EditorEngine();
            eng.Doc.SetText("a b c");
            eng.Sel.SetSelection(new SelectionRange(1, 0));   // "a"
            eng.Sel.AddSelection(new SelectionRange(5, 4));   // "c"
            eng.ReplaceSelectionWith("XY");
            Assert.AreEqual("XY b XY", eng.Doc.GetText(0, eng.Doc.Length));
        }

        [TestMethod]
        public void SearchInTarget_CollapsesTargetToMatch()
        {
            var eng = new EditorEngine();
            eng.Doc.SetText("one two three two");
            eng.TargetStart = 0;
            eng.TargetEnd = eng.Doc.Length;
            eng.SearchFlags = 0;
            Assert.AreEqual(4, eng.SearchInTarget("two"));
            Assert.AreEqual(4, eng.TargetStart);
            Assert.AreEqual(7, eng.TargetEnd);
            Assert.AreEqual(1, eng.ReplaceTarget("2"));  // returns the replacement's length
            Assert.AreEqual("one 2 three two", eng.Doc.GetText(0, eng.Doc.Length));
        }

        [TestMethod]
        public void FindInRange_BackwardReturnsLastMatch()
        {
            var eng = new EditorEngine();
            eng.Doc.SetText("x ab x ab x");
            int len;
            int p = eng.FindInRange("ab", 0, eng.Doc.Length, 0, out len);  // to < from: backward
            Assert.AreEqual(7, p);
            Assert.AreEqual(2, len);
        }

        [TestMethod]
        public void FoldAll_ContractHidesChildren_ExpandRestores()
        {
            var eng = new EditorEngine();
            eng.Doc.SetText("def f():\n    a = 1\n    b = 2\nprint(1)");
            // Indent fold: line 0 header (level base), lines 1-2 children.
            HelixTextEditorNET.Lexing.Folding.FoldByIndent(eng.Doc);
            eng.FoldAll(HelixTextEditorNET.FoldAction.Contract);
            Assert.IsFalse(eng.Cont.GetVisible(1));
            Assert.IsFalse(eng.Cont.GetVisible(2));
            Assert.IsTrue(eng.Cont.GetVisible(3));
            Assert.AreEqual(2, eng.Cont.LinesDisplayed);
            Assert.AreEqual(3, eng.Cont.DocFromDisplay(1));

            eng.FoldAll(HelixTextEditorNET.FoldAction.Expand);
            Assert.IsTrue(eng.Cont.GetVisible(1));
            Assert.AreEqual(4, eng.Cont.LinesDisplayed);
        }
    }
}
