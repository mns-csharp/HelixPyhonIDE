using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;
using HelixTextEditorNET;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    // Regression coverage for the HelixTextEditorNET hardening pass (ESC-dismisses-popups +
    // IntelliSense-after-.-and-,). Everything here drives the real Win32 message pump
    // (SendMessage for WM_KEYDOWN/WM_CHAR against the control's live HWND, or
    // Control.PreProcessMessage) instead of calling OnKeyDown/OnKeyPress directly, so a
    // routing-layer swallow (ancestor ProcessCmdKey/ProcessDialogKey, IsInputKey gaps) would
    // be caught, not just the isolated handler logic.
    [TestClass]
    public class EditorEscapeDismissesPopupsTests
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_CHAR = 0x0102;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern System.IntPtr SendMessage(System.IntPtr hWnd, int msg, System.IntPtr wParam, System.IntPtr lParam);

        [TestMethod]
        public void Escape_PreProcessMessage_DismissesActiveAutoC()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;
                form.Opacity = 0;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                editor.AutoCShow(0, "foo bar baz");
                Application.DoEvents();
                Assert.IsTrue(editor.AutoCActive, "precondition: AutoC popup must be showing");

                var preMsg = Message.Create(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                bool preConsumed = editor.PreProcessMessage(ref preMsg);
                if (!preConsumed)
                    SendMessage(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive,
                    "Escape should dismiss the AutoC popup. PreProcessMessage pre-consumed=" + preConsumed +
                    " (if false, WM_KEYDOWN was delivered to the real WndProc and OnKeyDown still didn't cancel it).");

                form.Close();
            }
        }

        [TestMethod]
        public void MovingCaretOutsideCompletionPrefix_DismissesStalePopup()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Text = "alpha beta";
                editor.CurrentPosition = editor.TextLength;
                editor.Focus();
                Application.DoEvents();

                editor.AutoCShow(4, "beta better");
                Application.DoEvents();
                Assert.IsTrue(editor.AutoCActive, "precondition: completion session should be active");

                editor.GotoPosition(0);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive,
                    "moving outside the session's prefix range must close a stale completion popup");
                form.Close();
            }
        }

        [TestMethod]
        public void SelectingTextDuringCompletion_DismissesStalePopup()
        {
            using (var form = new Form())
            {
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.Text = "beta";
                editor.CurrentPosition = editor.TextLength;
                editor.AutoCShow(4, "beta better");
                Assert.IsTrue(editor.AutoCActive);

                editor.SetSelection(4, 0);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive,
                    "a non-empty selection invalidates the completion replacement range");
                form.Close();
            }
        }

        [TestMethod]
        public void Escape_PreProcessMessage_DismissesActiveCallTip()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Text = "def foo(a, b):\n    pass\n";
                editor.CurrentPosition = editor.TextLength;
                editor.Focus();
                Application.DoEvents();

                editor.CallTipShow(0, "foo(a, b)");
                Application.DoEvents();
                Assert.IsTrue(editor.CallTipActive, "precondition: CallTip popup must be showing");

                var preMsg = Message.Create(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                bool preConsumed = editor.PreProcessMessage(ref preMsg);
                if (!preConsumed)
                    SendMessage(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsFalse(editor.CallTipActive,
                    "Escape should dismiss the CallTip popup. PreProcessMessage pre-consumed=" + preConsumed +
                    " (if false, WM_KEYDOWN was delivered to the real WndProc and OnKeyDown still didn't cancel it).");

                form.Close();
            }
        }

        // Reproduces the bug report faithfully: a real MainForm (KeyPreview=true, full
        // docking/menu/ProcessCmdKey ancestor chain), a real file open in a real EditorTabPage,
        // Escape delivered to the editor's actual live HWND while it's nested in that chain.
        [TestMethod]
        public void Escape_WithEditorNestedInRealMainForm_DismissesActiveAutoC()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_esc_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\ny = 2\n");
            try
            {
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    var editor = OpenFile(form, tmp);
                    if (editor == null || (editor.Text ?? "").Length == 0)
                    {
                        Assert.Inconclusive("Scintilla did not initialise in this environment.");
                        return;
                    }

                    editor.Focus();
                    Application.DoEvents();

                    editor.AutoCShow(0, "foo bar baz");
                    Application.DoEvents();
                    Assert.IsTrue(editor.AutoCActive, "precondition: AutoC popup must be showing");

                    var preMsg = Message.Create(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                    bool preConsumed = editor.PreProcessMessage(ref preMsg);
                    if (!preConsumed)
                        SendMessage(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                    Application.DoEvents();

                    Assert.IsFalse(editor.AutoCActive,
                        "Escape should dismiss the AutoC popup even when the editor is nested inside the real " +
                        "MainForm/docking ancestor chain. PreProcessMessage pre-consumed=" + preConsumed +
                        " (true means an ancestor's ProcessCmdKey/ProcessDialogKey swallowed Escape before WndProc).");

                    form.Close();
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // Reproduces the actual "major bug": AutoC list + doc panel shown together (as
        // CompletionController.Show(items, ctx) does on every real completion), then Escape.
        // OnKeyDownForNavigation (PythonEditorControl.cs) is wired via `this.KeyDown +=`, which
        // fires from the TOP of Scintilla.OnKeyDown (via base.OnKeyDown(e)) — BEFORE that same
        // method's own "_acActive && Escape -> AutoCCancel()" branch runs further down. So its
        // `!this.AutoCActive` guard reads AutoC as still active and skips HideDoc(); AutoC then
        // gets cancelled later in the same keystroke, but the doc panel never does.
        [TestMethod]
        public void Escape_WithAutoCAndDocPanelBothShowing_DismissesBoth()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Text = "print\n";
                editor.CurrentPosition = 3; // inside "print", a builtin the engine always knows about
                editor.Focus();
                Application.DoEvents();

                object completion = GetField(editor, "_completion");
                Assert.IsNotNull(completion, "_completion field expected on PythonEditorControl");

                editor.AutoCShow(0, "print foo bar");
                Application.DoEvents();
                Assert.IsTrue(editor.AutoCActive, "precondition: AutoC popup must be showing");

                InvokeMethod(completion, "ShowQuickInfoAtCaret");
                Application.DoEvents();
                object docPanel = GetField(completion, "_doc");
                Form docHost = (Form)GetField(docPanel, "_host");
                Assert.IsTrue(docHost.Visible, "precondition: doc panel must be showing alongside AutoC");

                var preMsg = Message.Create(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                bool preConsumed = editor.PreProcessMessage(ref preMsg);
                if (!preConsumed)
                    SendMessage(editor.Handle, WM_KEYDOWN, (System.IntPtr)(int)Keys.Escape, System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive, "Escape should dismiss the AutoC popup");
                Assert.IsFalse(docHost.Visible,
                    "Escape should also dismiss the doc panel shown alongside AutoC — this is the reported bug: " +
                    "OnKeyDownForNavigation's !AutoCActive guard runs before AutoC is cancelled, so HideDoc() " +
                    "never fires while both popups are up together.");

                form.Close();
            }
        }

        // Task #15 repro: types real characters through WM_CHAR (the same path the OS message
        // pump uses to invoke OnKeyPress -> RaiseCharAdded -> CompletionController.OnCharAdded)
        // instead of calling ShowCallTip()/AutoCShow() directly, to see whether ',' genuinely
        // (re-)triggers the call tip in practice.
        [TestMethod]
        public void TypingComma_InsideRealCall_ShowsCallTip()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "print(1")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }
                Assert.IsTrue(editor.CallTipActive, "precondition: typing '(' after a known builtin should show the call tip");

                editor.CallTipCancel();
                Application.DoEvents();
                Assert.IsFalse(editor.CallTipActive, "precondition: call tip must be dismissed before testing ','");

                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)',', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsTrue(editor.CallTipActive,
                    "Typing ',' inside an open call should re-show the call tip (signature help). Text is now: '" +
                    editor.Text + "'");

                form.Close();
            }
        }

        // Task #15 repro: same but for '.' member-access completion — real WM_CHAR keystrokes,
        // checking whether AutoC actually shows for a builtin/known symbol after '.'.
        //
        // Stdlib module member introspection (`os.` -> os's real attribute list) is NOT
        // implemented — PythonCompletionEngine has no typeshed-like database of stdlib module
        // members, only local-file classes, solution types, and builtin container methods
        // (str/list/dict/tuple/set). This is an explicit, deliberate scope boundary, not a bug:
        // implementing it would mean shipping/maintaining a stub database for the entire stdlib.
        [TestMethod]
        public void TypingDot_AfterKnownModule_DoesNotResolveStdlibMembers_KnownLimitation()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "import os\nos")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }
                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'.', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsFalse(editor.AutoCActive,
                    "Stdlib module member completion ('os.') is out of scope (no stub database) — " +
                    "this documents the boundary. If this starts passing, promote it to a real assertion.");

                form.Close();
            }
        }

        // Task #15 fix verification: static single-file type inference. `f = Foo(); f.` must
        // resolve `f`'s type to `Foo` (from the constructor call) and offer its members — this
        // used to silently show nothing because AddMemberCompletions only handled `self.member`
        // and a literal `ClassName.member`, never actual variable-to-type inference.
        [TestMethod]
        public void TypingDot_AfterLocalClassInstance_ShowsMemberCompletion()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                object completion = GetField(editor, "_completion");

                string src =
                    "class Foo:\n" +
                    "    def bar(self):\n" +
                    "        pass\n" +
                    "\n" +
                    "f = Foo()\n" +
                    "f";
                editor.Text = src;
                editor.GotoPosition(editor.TextLength);
                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'.', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.AreEqual(src + ".", editor.Text,
                    "the completion harness must preserve the fixture's Python indentation");

                Assert.IsTrue(editor.AutoCActive,
                    "'f.' should resolve f's type (Foo, from `f = Foo()`) and show its members. Text: '" +
                    editor.Text.Replace("\n", "\\n") + "'");

                object lastItems = GetField(completion, "_lastItems");
                int count = (int)lastItems.GetType().GetProperty("Count").GetValue(lastItems);
                Assert.AreEqual(1, count, "Foo has exactly one member (bar)");
            }
        }

        // Task #15 fix verification: `self.attr = ClassName(...)` inside a method, then
        // `self.attr.` elsewhere in the class, should resolve attr's type too.
        [TestMethod]
        public void TypingDot_AfterSelfAttributeAssignedInInit_ShowsMemberCompletion()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;
                form.Opacity = 0;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                string src =
                    "class Widget:\n" +
                    "    def paint(self):\n" +
                    "        pass\n" +
                    "\n" +
                    "class Screen:\n" +
                    "    def __init__(self):\n" +
                    "        self.widget = Widget()\n" +
                    "\n" +
                    "    def render(self):\n" +
                    "        self.widget";
                editor.Text = src;
                editor.GotoPosition(editor.TextLength);
                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'.', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.AreEqual(src + ".", editor.Text,
                    "self-attribute completion must not alter the fixture's Python indentation");
                Assert.IsTrue(editor.AutoCActive,
                    "'self.widget.' should resolve widget's type (Widget, from `self.widget = Widget()` in " +
                    "__init__) and show its members. Text: '" + editor.Text.Replace("\n", "\\n") + "'");

                form.Close();
            }
        }

        // Task #15 fix verification: a plain string-literal variable should offer str methods —
        // exercises the literal-type branch of the inference (not just class instantiation).
        [TestMethod]
        public void TypingDot_AfterStringVariable_ShowsStrMethods()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "name = 'x'\nname")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }
                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'.', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsTrue(editor.AutoCActive,
                    "'name.' should resolve name's type (str, from `name = 'x'`) and show str methods. Text: '" +
                    editor.Text.Replace("\n", "\\n") + "'");

                form.Close();
            }
        }

        [TestMethod]
        public void TypingHash_InCudaFile_ShowsPreprocessorDirectives()
        {
            using (var form = new Form())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                editor.SetFilePath("kernel.cu");
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'#', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.IsTrue(editor.AutoCActive, "typing # at the start of a CUDA line should open directive completion");
                object completion = GetField(editor, "_completion");
                var items = (System.Collections.IEnumerable)GetField(completion, "_lastItems");
                bool hasInclude = false;
                foreach (object item in items)
                {
                    string label = (string)item.GetType().GetProperty("Label").GetValue(item);
                    if (label == "include") hasInclude = true;
                }
                Assert.IsTrue(hasInclude, "the popup should offer #include");
                form.Close();
            }
        }

        [TestMethod]
        public void TypingIncludeQuote_InLoadedCudaFixture_ShowsProjectHeaderNames()
        {
            using (var form = new Form())
            {
                form.Opacity = 0;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                string cmake = FindCudaFixtureCMake();
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                editor.SetFilePath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cmake), "app_cli", "src", "main.cu"));
                editor.Solution = new HelixPythonNamespace.Core.Model.SolutionModel(cmake, "CudaCppMultiPackageWorkspace");
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "#include \"")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }

                Assert.IsTrue(editor.AutoCActive, "typing the opening quote after #include should open header completion");
                object completion = GetField(editor, "_completion");
                var items = (System.Collections.IEnumerable)GetField(completion, "_lastItems");
                bool hasHeader = false;
                foreach (object item in items)
                {
                    string label = (string)item.GetType().GetProperty("Label").GetValue(item);
                    if (label == "lib_core/animals.cuh") hasHeader = true;
                }
                Assert.IsTrue(hasHeader, "the loaded fixture's lib_core/animals.cuh should be suggested");
                form.Close();
            }
        }

        [TestMethod]
        public void TypingDotInsideIncludePath_FiltersWithoutCommittingAnotherHeader()
        {
            using (var form = new Form())
            {
                form.Opacity = 0;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Size = new Size(600, 400);
                form.ShowInTaskbar = false;

                string cmake = FindCudaFixtureCMake();
                var editor = new PythonEditorControl { Dock = DockStyle.Fill };
                editor.SetFilePath(Path.Combine(Path.GetDirectoryName(cmake), "app_cli", "src", "main.cu"));
                editor.Solution = new HelixPythonNamespace.Core.Model.SolutionModel(cmake, "CudaCppMultiPackageWorkspace");
                form.Controls.Add(editor);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                editor.CreateControl();
                editor.Focus();
                Application.DoEvents();

                foreach (char c in "#include \"lib_core/animals")
                {
                    SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)c, System.IntPtr.Zero);
                    Application.DoEvents();
                }
                Assert.IsTrue(editor.AutoCActive, "header completion should still be filtering the typed path");

                SendMessage(editor.Handle, WM_CHAR, (System.IntPtr)'.', System.IntPtr.Zero);
                Application.DoEvents();

                Assert.AreEqual("#include \"lib_core/animals.", editor.Text,
                    "a filename dot must be inserted literally, not commit the selected full header first");
                Assert.IsTrue(editor.AutoCActive,
                    "the literal dot should continue filtering toward lib_core/animals.cuh");
                form.Close();
            }
        }

        private static string FindCudaFixtureCMake()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
            {
                string dir = System.IO.Path.GetFullPath(start);
                while (!string.IsNullOrEmpty(dir))
                {
                    string candidate = System.IO.Path.Combine(dir, "tests", "fixtures", "cuda",
                        "CudaCppMultiPackageWorkspace", "CMakeLists.txt");
                    if (System.IO.File.Exists(candidate)) return candidate;
                    var parent = System.IO.Directory.GetParent(dir);
                    dir = parent == null ? null : parent.FullName;
                }
            }
            Assert.Fail("CudaCppMultiPackageWorkspace fixture was not found");
            return null;
        }

        private static object GetField(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, name + " field expected on " + obj.GetType().FullName);
            return f.GetValue(obj);
        }

        private static void InvokeMethod(object obj, string name)
        {
            MethodInfo m = obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(m, name + " method expected on " + obj.GetType().FullName);
            m.Invoke(obj, null);
        }

        /// <summary>Opens a real file in the form's editor and brings the Editor tab on screen.</summary>
        private static PythonEditorControl OpenFile(MainForm form, string path)
        {
            form.OpenInEditor(path, -1, true);
            Application.DoEvents();

            var inner = Field<TabControl>(form, "_editorInnerTabControl");
            var editorTab = Field<TabPage>(form, "_editorTab");
            var main = Field<TabControl>(form, "_mainTabControl");
            if (main.TabPages.Contains(editorTab)) main.SelectedTab = editorTab;

            var page = inner.SelectedTab as EditorTabPage;
            if (page == null) return null;
            page.Editor.CreateControl();
            Application.DoEvents();
            return page.Editor;
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
