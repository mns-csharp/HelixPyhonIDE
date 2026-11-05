using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 292: the editor's right-click menu was symbol-only — it cancelled entirely (no menu) when
    /// you clicked whitespace, a string, or a comment. It now always offers Cut / Copy / Paste / Select
    /// All (VS / VS Code parity), with symbol actions appended when applicable. Verified on a real
    /// (on-screen) Scintilla control; self-skips if the native editor can't initialise here.
    /// </summary>
    [TestClass]
    public class EditorContextMenuTests
    {
        [TestMethod]
        public void RightClickOnWhitespace_StillOffersStandardEditCommands()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_ctx_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\n");
            try
            {
                using (var form = new Form())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(0, 0);
                    form.Size = new Size(500, 360);
                    form.ShowInTaskbar = false;

                    var tab = new EditorTabPage(tmp);
                    var tc = new TabControl { Dock = DockStyle.Fill };
                    tc.TabPages.Add(tab);
                    form.Controls.Add(tc);

                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();
                    tab.Editor.CreateControl();
                    Application.DoEvents();

                    var editor = tab.Editor;
                    if ((editor.Text ?? "").Length == 0)
                    {
                        Assert.Inconclusive("Scintilla editor did not initialise in this environment.");
                        return;
                    }

                    // Put the caret on the space after 'x' — a non-symbol position that used to cancel.
                    editor.GotoPosition(1);

                    var ctx = editor.ContextMenuStrip;
                    Assert.IsNotNull(ctx, "editor should have a context menu");

                    var opening = typeof(PythonEditorControl).GetMethod(
                        "OnContextMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(opening, "OnContextMenuOpening expected");
                    var args = new CancelEventArgs();
                    opening.Invoke(editor, new object[] { ctx, args });

                    Assert.IsFalse(args.Cancel, "the context menu must not be cancelled on whitespace");
                    var items = ctx.Items.OfType<ToolStripMenuItem>().ToList();
                    var labels = items.Select(i => i.Text).ToList();
                    CollectionAssert.Contains(labels, "Cut");
                    CollectionAssert.Contains(labels, "Copy");
                    CollectionAssert.Contains(labels, "Paste");
                    CollectionAssert.Contains(labels, "Select All");

                    // Iter 299: editor commands are surfaced with their shortcuts for discoverability.
                    AssertShortcut(items, "Toggle Comment", "Ctrl+/");
                    AssertShortcut(items, "Move Line Up", "Alt+Up");
                    AssertShortcut(items, "Move Line Down", "Alt+Down");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [TestMethod]
        public void SelectedMethodAtCallBoundary_ResolvesInsideTheToken()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_ctx_" + Guid.NewGuid().ToString("N") + ".py");
            const string source = "return RunFullPipelineApplication().run()\n";
            File.WriteAllText(tmp, source);
            try
            {
                using (var form = new Form())
                {
                    var tab = new EditorTabPage(tmp);
                    var tc = new TabControl { Dock = DockStyle.Fill };
                    tc.TabPages.Add(tab);
                    form.Controls.Add(tc);
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var editor = tab.Editor;
                    int start = source.IndexOf("run", StringComparison.Ordinal);
                    editor.SelectionStart = start;
                    editor.SelectionEnd = start + 3; // caret is now on the opening parenthesis

                    EditorSymbolRequestArgs request = null;
                    editor.ResolveSymbolRequested += (sender, args) => request = args;
                    var opening = typeof(PythonEditorControl).GetMethod(
                        "OnContextMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
                    opening.Invoke(editor, new object[] { editor.ContextMenuStrip, new CancelEventArgs() });

                    Assert.IsNotNull(request, "the selected method must request symbol actions");
                    Assert.AreEqual("run", request.Token);
                    Assert.AreEqual(start + 2, request.Column,
                        "the 1-based AST column must land inside `run`, not on its following parenthesis");
                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static void AssertShortcut(System.Collections.Generic.List<ToolStripMenuItem> items,
            string label, string shortcut)
        {
            var item = items.Find(i => i.Text == label);
            Assert.IsNotNull(item, "expected a '" + label + "' item in the editor context menu");
            Assert.AreEqual(shortcut, item.ShortcutKeyDisplayString,
                "'" + label + "' should advertise its shortcut " + shortcut);
        }
    }
}
