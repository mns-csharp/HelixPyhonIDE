using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using System.Windows.Forms;

namespace HelixPythonNamespace.Editor.Tests
{
    /// <summary>
    /// Enter-key auto-indent derives from the text BEFORE the caret only. Regression for
    /// "pressed Enter three times, NewClass is totally ill-indented": the whole-line version
    /// saw the trailing ':' of "class NewClass:" even with the caret at column 0, so each
    /// press pushed the class four more columns right while its body stayed put.
    /// </summary>
    [TestClass]
    public class AutoIndentTests
    {
        private sealed class KeyDrivenEditor : PythonEditorControl
        {
            public void PressEnter()
            {
                base.OnKeyPress(new KeyPressEventArgs('\r'));
            }

            public void Press(char value)
            {
                base.OnKeyPress(new KeyPressEventArgs(value));
            }
        }
        [TestMethod]
        public void EnterAtLineStart_MovesTheLineDownUnchanged()
        {
            Assert.AreEqual("", PythonEditorControl.ComputeAutoIndent("class NewClass:", 0),
                "no indent may be injected before existing text");
        }

        [TestMethod]
        public void ManuallyTypedOpeningDelimiters_DoNotInsertClosingDelimiters()
        {
            foreach (char opening in new[] { '(', '[', '{' })
            {
                using (var editor = new KeyDrivenEditor())
                {
                    editor.Text = "value";
                    editor.GotoPosition(editor.TextLength);
                    editor.Press(opening);

                    Assert.AreEqual("value" + opening, editor.Text, opening.ToString());
                    Assert.AreEqual(editor.TextLength, editor.CurrentPosition, opening.ToString());
                }
            }
        }

        [TestMethod]
        public void EnterInsideLeadingWhitespace_PreservesNetIndentation()
        {
            // Prefix whitespace ("  ") + the remainder's own remaining whitespace ("  pass")
            // reassemble to the original four columns — the split line keeps its indentation.
            Assert.AreEqual("  ", PythonEditorControl.ComputeAutoIndent("    pass", 2));
        }

        [TestMethod]
        public void EnterAtEndOfBlockOpener_IndentsOneLevelDeeper()
        {
            Assert.AreEqual("    ", PythonEditorControl.ComputeAutoIndent("class NewClass:", 15));
            Assert.AreEqual("        ", PythonEditorControl.ComputeAutoIndent("    def m(self):", 16),
                "existing leading whitespace is kept and one level added");
        }

        [TestMethod]
        public void EnterAtEndOfPlainLine_KeepsTheCurrentIndent()
        {
            Assert.AreEqual("    ", PythonEditorControl.ComputeAutoIndent("    x = 1", 9));
        }

        [TestMethod]
        public void EnterAfterReturn_DedentsFromMethodBodyToClassBody()
        {
            Assert.AreEqual("    ",
                PythonEditorControl.ComputeAutoIndent("        return self._name", 25));
        }

        [TestMethod]
        public void RealEditorEnterAfterReturn_PlacesCaretAtClassIndent()
        {
            using (var editor = new KeyDrivenEditor())
            {
                editor.Text = "class NewClass:\n    def name(self):\n        return self._name";
                editor.GotoPosition(editor.TextLength);
                editor.PressEnter();
                Assert.AreEqual(4, editor.CurrentPosition - editor.Lines[editor.LineFromPosition(editor.CurrentPosition)].Position);
            }
        }

        [TestMethod]
        public void RealEditorEnterBeforeTrailingWhitespace_StillPlacesCaretAtClassIndent()
        {
            using (var editor = new KeyDrivenEditor())
            {
                editor.Text = "class NewClass:\n    def name(self):\n        return self._name   ";
                editor.GotoPosition(editor.Text.IndexOf("   ", editor.Text.IndexOf("return"),
                    System.StringComparison.Ordinal));
                editor.PressEnter();
                Assert.AreEqual(4, editor.CurrentPosition - editor.Lines[editor.LineFromPosition(editor.CurrentPosition)].Position);
            }
        }

        [TestMethod]
        public void TerminalStatements_DedentExactlyOneScope()
        {
            foreach (string statement in new[] { "raise error", "break", "continue", "pass" })
                Assert.AreEqual("    ",
                    PythonEditorControl.ComputeAutoIndent("        " + statement, 8 + statement.Length),
                    statement);
            Assert.AreEqual("\t", PythonEditorControl.ComputeAutoIndent("\t\treturn value", 14),
                "tab-indented code removes one tab");
        }

        [TestMethod]
        public void EnterMidIdentifier_AddsNothing()
        {
            Assert.AreEqual("", PythonEditorControl.ComputeAutoIndent("class NewClass:", 3),
                "splitting a word must not invent indentation for the moved remainder");
        }

        [TestMethod]
        public void EnterJustAfterAMidLineColon_IndentsFromThePrefix()
        {
            // caret right after ':' with trailing text still on the line — the prefix rule
            // still applies (the remainder becomes the indented body line).
            Assert.AreEqual("    ", PythonEditorControl.ComputeAutoIndent("if x: y()", 5));
        }

        [TestMethod]
        public void DegenerateInputs_AreSafe()
        {
            Assert.AreEqual("", PythonEditorControl.ComputeAutoIndent(null, 0));
            Assert.AreEqual("", PythonEditorControl.ComputeAutoIndent("abc", 99), "clamped past end: prefix has no colon");
            Assert.AreEqual("", PythonEditorControl.ComputeAutoIndent("x:", -5),
                "negative caret clamps to 0 — empty prefix, no indent, no throw");
        }
    }
}
