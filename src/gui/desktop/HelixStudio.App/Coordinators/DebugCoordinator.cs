namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.CudaEmulator;   // Dim3

    using HelixPythonNamespace.UI.Controls;
    /// <summary>
    /// Owns the Debug tab and the debug session controller: builds the output +
    /// breakpoints UI, relays controller events to the editor (current-line marker,
    /// status text), and exposes the start/stop/step/toggle-breakpoint commands the
    /// Debug menu invokes. <see cref="MainForm"/> keeps thin internal wrappers so the
    /// menu wiring is unchanged.
    /// </summary>
    public sealed partial class DebugCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        private HelixPythonNamespace.App.Services.DebugController _debugController;
        private TabPage _debugTab;
        private TabControl _debugInnerTabControl;   // the Debug tab's own inner tabs (Output/Variables/Call Stack/Breakpoints/Immediate)
        private RichTextBox _debugOutput;
        private ListView _breakpointsList;
        private Label _breakpointsEmptyLabel;
        private TreeView _variablesTree;
        private ListView _callStackList;
        private TextBox _watchInput;
        private RichTextBox _immediateOutput;
        private TextBox _immediateInput;
        private int _selectedFrameId = -1;

        // Watch expressions persist across stops and sessions; they are re-evaluated against the
        // selected frame on every stop and rendered above the locals in the Variables list.
        private readonly List<string> _watchExpressions = new List<string>();

        // Expansion state (by node path key) survives the per-stop rebuild so an expanded
        // list/dict/object stays open as you step. See RefreshVariables / OnVarBeforeExpand.
        private readonly HashSet<string> _expandedVarPaths = new HashSet<string>();

        // Tag carried by each Variables tree node: its VariablesReference (>0 = expandable),
        // whether it's a watch row (for double-click removal), and a lazy-load guard.
        private sealed class VarNode
        {
            public string Path;       // unique key for expansion persistence
            public int Ref;           // VariablesReference; >0 means it has children
            public bool IsWatch;      // a ⊙ watch row
            public string WatchExpr;  // expression text (watch rows only)
            public bool Loaded;       // children already fetched?
        }
        private const string LazyPlaceholder = "…"; // "…" dummy child so the [+] shows

        // ICoordinator lifecycle: cache the shared context + direct host reference.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Drop references on teardown so the coordinator doesn't pin the form.
        public void Detach()
        {
            // The data tip is a top-level window of our own plus a timer; neither is parented to the
            // form, so neither goes away with it.
            try { DisposeDataTip(); } catch { }
            _context = null;
            host = null;
        }

        /// <summary>The live debug controller (null until <see cref="Initialize"/> runs).</summary>
        internal HelixPythonNamespace.App.Services.DebugController Controller
        {
            get { return _debugController; }
        }

        // One-time construction of the debug controller and the whole Debug tab UI (Output,
        // Variables+watch bar, Call Stack, Breakpoints inner tabs), then registers the tab as a
        // permanent top-level tab on the host. Wires controller events (output/stopped/session-end)
        // and the breakpoint-change relay, restores persisted breakpoints, and sets initial toolbar
        // state. Called once during MainForm startup.
        public void Initialize()
        {
            _debugController = new HelixPythonNamespace.App.Services.DebugController(host);
            _debugController.OutputReceived += OnDebugOutput;
            _debugController.Stopped += OnDebugStopped;
            _debugController.SessionEnded += OnDebugSessionEnded;
            _debugController.EngineFailedToStart += OnEngineFailedToStart;

            TabControl inner = _debugInnerTabControl = new TabControl { Dock = DockStyle.Fill, Font = LunaFonts.Sized(-0.5f) };

            TabPage outputTab = new TabPage("Output");
            _debugOutput = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true,
                Font = new Font("Consolas", 9f), BackColor = Color.Black, ForeColor = Color.LightGray };
            outputTab.Controls.Add(_debugOutput);
            inner.TabPages.Add(outputTab);

            TabPage varsTab = new TabPage("Variables");
            // A TreeView (not a flat ListView) so structured values — lists, dicts, object
            // instances (VariablesReference > 0) — can be expanded lazily on demand.
            _variablesTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false,
                Font = new Font("Consolas", 9f), ShowRootLines = true, ShowPlusMinus = true };
            _variablesTree.BeforeExpand += OnVarBeforeExpand;
            _variablesTree.AfterExpand += (s, e) => { if (e.Node.Tag is VarNode vn) _expandedVarPaths.Add(vn.Path); };
            _variablesTree.AfterCollapse += (s, e) => { if (e.Node.Tag is VarNode vn) _expandedVarPaths.Remove(vn.Path); };
            _variablesTree.NodeMouseDoubleClick += OnVariableDoubleClick;

            // Watch bar: type an expression + Enter (or "Watch") to evaluate it in the
            // selected frame; the result is pinned to the top of the list with a ⊙ marker.
            Panel watchBar = new Panel { Dock = DockStyle.Top, Height = 26 };
            _watchInput = new TextBox { Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f) };
            _watchInput.KeyDown += OnWatchKeyDown;
            Button watchBtn = new Button { Dock = DockStyle.Right, Text = "Watch", Width = 64, FlatStyle = FlatStyle.System };
            watchBtn.Click += (s, e) => AddWatch();
            Label watchLbl = new Label { Dock = DockStyle.Left, Text = "Watch:", Width = 46, TextAlign = ContentAlignment.MiddleLeft };
            watchBar.Controls.Add(_watchInput);
            watchBar.Controls.Add(watchBtn);
            watchBar.Controls.Add(watchLbl);
            varsTab.Controls.Add(_variablesTree);
            varsTab.Controls.Add(watchBar);
            inner.TabPages.Add(varsTab);

            TabPage callStackTab = new TabPage("Call Stack");
            _callStackList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            _callStackList.Columns.Add("#", 32);
            _callStackList.Columns.Add("Function", 240);
            _callStackList.Columns.Add("File", 240);
            _callStackList.Columns.Add("Line", 60);
            _callStackList.SelectedIndexChanged += OnCallStackFrameSelected;
            _callStackList.DoubleClick += OnCallStackFrameActivated;
            ListViewClipboard.AttachCopyMenu(_callStackList);
            callStackTab.Controls.Add(_callStackList);
            inner.TabPages.Add(callStackTab);

            TabPage bpTab = new TabPage("Breakpoints");
            // CheckBoxes give VS-style enable/disable: an unchecked breakpoint stays listed but won't stop.
            _breakpointsList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true };
            _breakpointsList.Columns.Add("File", 240);
            _breakpointsList.Columns.Add("Line", 50);
            _breakpointsList.Columns.Add("Condition", 200);
            _breakpointsList.Columns.Add("Hit", 70);
            _breakpointsList.DoubleClick += OnBreakpointDoubleClick;
            _breakpointsList.ItemChecked += OnBreakpointItemChecked;
            // Right-click a breakpoint to set a condition / hit-count, enable/disable, or remove it.
            ContextMenuStrip bpMenu = new ContextMenuStrip();
            bpMenu.Items.Add("Edit &Condition…", null, (s, e) => EditSelectedBreakpointCondition());
            bpMenu.Items.Add("Set &Tracepoint (Log Message)…", null, (s, e) => SetSelectedBreakpointLogMessage());
            bpMenu.Items.Add("Enable / &Disable Breakpoint", null, (s, e) => ToggleSelectedBreakpointEnabled());
            bpMenu.Items.Add("&Remove Breakpoint", null, (s, e) => RemoveSelectedBreakpoint());
            _breakpointsList.ContextMenuStrip = bpMenu;
            // Keys only: the list already owns bpMenu as its right-click menu.
            ListViewClipboard.AttachKeyboardShortcuts(_breakpointsList);
            // Empty-state placeholder: shown (in place of the list) while no breakpoints exist, so the
            // tab reads as an intentional empty state rather than a blank grid. Only one of the label
            // and the Dock=Fill list is Visible at a time (two visible Fill siblings collapse each other).
            _breakpointsEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "No breakpoints.\r\n\r\nClick a source line's left margin, or press F9, to add one.",
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = LunaFonts.Sized(0.75f),
                Visible = false, // RefreshBreakpointsList() sets the real state on init
            };
            bpTab.Controls.Add(_breakpointsEmptyLabel);
            bpTab.Controls.Add(_breakpointsList);
            inner.TabPages.Add(bpTab);

            // Immediate window (a.k.a. Debug Console): type an arbitrary expression + Enter to evaluate
            // it in the SELECTED call-stack frame at the current stop, echoing "> expr" then the result.
            TabPage immediateTab = new TabPage("Immediate");
            _immediateOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.Black,
                ForeColor = Color.LightGray,
                Text = "Immediate window — evaluate expressions in the selected frame while paused." + Environment.NewLine,
            };
            Panel immediateBar = new Panel { Dock = DockStyle.Bottom, Height = 26 };
            _immediateInput = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
            _immediateInput.KeyDown += OnImmediateKeyDown;
            Button immediateBtn = new Button { Dock = DockStyle.Right, Text = "Eval", Width = 64, FlatStyle = FlatStyle.System };
            immediateBtn.Click += (s, e) => EvaluateImmediate();
            Label immediateLbl = new Label { Dock = DockStyle.Left, Text = ">", Width = 18, TextAlign = ContentAlignment.MiddleCenter };
            immediateBar.Controls.Add(_immediateInput);
            immediateBar.Controls.Add(immediateBtn);
            immediateBar.Controls.Add(immediateLbl);
            immediateTab.Controls.Add(_immediateOutput);
            immediateTab.Controls.Add(immediateBar);
            inner.TabPages.Add(immediateTab);

            _debugTab = new TabPage("Debug");
            // Short. The shortcuts live on the Debug menu, where they are discoverable next to the
            // commands they belong to, rather than in a tip that has to be hovered to be read.
            _debugTab.ToolTipText = "Debug output and breakpoints";
            _debugTab.Controls.Add(inner);
            host._mainTabControl.TabPages.Add(_debugTab);
            host._permanentTabs.Add(_debugTab);
            host.RegisterTopLevelTab("debug", _debugTab);

            _debugController.Breakpoints.BreakpointChanged += (s, e) =>
            {
                RefreshBreakpointsList();
                SyncEditorMarginForChange(e);
            };
            // Restore breakpoints (with conditions) persisted from the last session, then show them.
            try { _debugController.Breakpoints.Load(); } catch { }
            RefreshBreakpointsList();
            UpdateDebugToolbar();
        }

        /// <summary>
        /// Persists the current breakpoints (with conditions) to disk so they survive a restart.
        /// Called from <see cref="MainForm.OnFormClosing"/>.
        /// </summary>
        public void PersistBreakpoints()
        {
            if (_debugController == null) return;
            try { _debugController.Breakpoints.Save(); } catch { }
        }

        /// <summary>
        /// Enables/disables the debug toolbar buttons to match the session state: Continue acts as
        /// Start when idle and resume when paused (disabled while the program is running, since
        /// there's nothing to resume); Stop is enabled whenever a session is live; the Step buttons
        /// are enabled only while paused; Toggle Breakpoint is always available. Called on every
        /// state transition (stop, session end, and each command).
        /// </summary>
        private void UpdateDebugToolbar()
        {
            if (host == null || _debugController == null) return;
            DebugToolbarState st = ComputeToolbarState(_debugController.IsDebugging, _debugController.IsPaused);
            SetEnabled(host._dbgContinueButton, st.Continue);
            SetEnabled(host._dbgPauseButton, st.Pause);
            SetEnabled(host._dbgRestartButton, st.Restart);
            SetEnabled(host._dbgStopButton, st.Stop);
            SetEnabled(host._dbgStepOverButton, st.Step);
            SetEnabled(host._dbgStepIntoButton, st.Step);
            SetEnabled(host._dbgStepOutButton, st.Step);
            SetEnabled(host._dbgToggleBpButton, st.ToggleBreakpoint);

            // The Debug MENU carries the keyboard chords (F10 / F11 / Shift+F11 / Shift+F5 /
            // Ctrl+Shift+F5 / Ctrl+F10) as ToolStripMenuItem.ShortcutKeys, and WinForms will not
            // dispatch a shortcut whose menu item is disabled. The menu used to refresh itself only
            // on DropDownOpening, so opening it once with no session left the Step items disabled
            // and every later F10/F11 was silently swallowed while the toolbar buttons above worked
            // — the debugger looked wedged at a breakpoint. Refresh it on the same transitions.
            try { host.RefreshDebugMenuState(); } catch { /* chrome update is best-effort */ }

            // Session state also decides which debug buttons are on screen at all (the session-only
            // ones appear when a session starts and disappear when it ends), so re-scope the toolbar
            // on every state change.
            try { host.UpdateToolbarWorkflowVisibility(); } catch { /* chrome update is best-effort */ }

            // While a session is live the side panels stand down and give the window to the source
            // and the Debug panel; leaving the session gives the layout straight back. Idempotent,
            // so driving it from every transition is correct and needs no edge tracking here.
            try { host.SetDebugFocusMode(_debugController.IsDebugging); } catch { }

            // A data tip states what an expression IS at the current stop. The moment the program
            // moves — step, resume, session end — that claim is stale, so the tip comes down rather
            // than sitting there confidently wrong. Re-hovering shows fresh values.
            try { HideDataTip(); } catch { }
        }

        /// <summary>Which debug toolbar buttons are enabled for a given session state.</summary>
        public struct DebugToolbarState
        {
            public bool Continue;          // Start when idle, resume when paused
            public bool Pause;             // running (live but not paused) — break into the debugger
            public bool Restart;           // any live session — stop + start again
            public bool Stop;              // any live session
            public bool Step;              // over/into/out — only while paused
            public bool ToggleBreakpoint;  // always
        }

        /// <summary>
        /// Pure mapping from session state to toolbar button enablement (extracted so the invariant
        /// is unit-testable without a Form): Continue is enabled when idle (acts as Start) or paused
        /// (resume) but NOT while the program is running (nothing to resume); Pause only while a session
        /// is live and running (not already paused); Restart and Stop whenever a session is live; the
        /// Step buttons only while paused; Toggle Breakpoint always.
        /// </summary>
        public static DebugToolbarState ComputeToolbarState(bool debugging, bool paused)
        {
            return new DebugToolbarState
            {
                Continue = !debugging || paused,
                Pause = debugging && !paused,
                Restart = debugging,
                Stop = debugging,
                Step = paused,
                ToggleBreakpoint = true,
            };
        }

        private static void SetEnabled(System.Windows.Forms.ToolStripButton b, bool on)
        {
            if (b != null) b.Enabled = on;
        }

        // Append program/debugger output to the Output pane; the controller already marshals this
        // onto the UI thread, so just guard against a disposed control during teardown.
        private void OnDebugOutput(object sender, string text)
        {
            if (_debugOutput == null || _debugOutput.IsDisposed) return;
            _debugOutput.AppendText(text);
        }

        // Bug fix: a non-Python debug session (CUDA/C/C++) that ends without ever
        // stopping at a breakpoint — the external tool is missing, or the build failed — previously only
        // wrote a line to the Debug ▸ Output tab, which the user had no reason to be looking at, so
        // clicking "Start Debug" appeared to do nothing. Focus the Debug tab's Output sub-tab, append the
        // reason in the same red/error styling OnDebugStopped uses for an exception, and pop a MessageBox
        // so the failure is impossible to miss.
        private void OnEngineFailedToStart(object sender, string reason)
        {
            if (host == null) return;
            if (host._mainTabControl != null && _debugTab != null) host._mainTabControl.SelectedTab = _debugTab;
            if (_debugInnerTabControl != null && _debugInnerTabControl.TabPages.Count > 0)
                _debugInnerTabControl.SelectedTab = _debugInnerTabControl.TabPages[0]; // "Output" (see Initialize)

            if (_debugOutput != null && !_debugOutput.IsDisposed)
            {
                _debugOutput.SelectionStart = _debugOutput.TextLength;
                _debugOutput.SelectionColor = Color.OrangeRed;
                _debugOutput.AppendText(string.Format(
                    "{0}Debug session failed to start: {1}{0}",
                    Environment.NewLine, reason));
                _debugOutput.SelectionColor = _debugOutput.ForeColor;
                _debugOutput.ScrollToCaret();
            }
            if (host._statusLabel != null) host._statusLabel.Text = "Debug session failed to start: " + reason;

            MessageBox.Show(host, "Debug session failed to start:\n\n" + reason, "Debug",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Debugger paused (breakpoint/step/entry): update the status line, open the stopped file in
        // the editor and paint the current-line marker, then refresh the call stack + variables and
        // the toolbar to the paused state.
        private void OnDebugStopped(object sender, HelixPythonNamespace.Debugger.DebugStoppedEventArgs e)
        {
            bool isException = string.Equals(e.Reason, "exception", StringComparison.OrdinalIgnoreCase);
            if (isException && !string.IsNullOrEmpty(e.ExceptionType))
            {
                // Surface the exception's type + message (VS shows a popup; we put it on the status bar
                // and in the Output pane in red so it's captured in a screenshot).
                string detail = e.ExceptionType
                    + (string.IsNullOrEmpty(e.ExceptionMessage) ? "" : ": " + e.ExceptionMessage);
                host._statusLabel.Text = "Exception thrown: " + detail
                    + " at " + Path.GetFileName(e.FilePath ?? "") + ":" + e.Line;
                if (_debugOutput != null && !_debugOutput.IsDisposed)
                {
                    _debugOutput.SelectionStart = _debugOutput.TextLength;
                    _debugOutput.SelectionColor = Color.OrangeRed;
                    _debugOutput.AppendText(string.Format(
                        "{0}Exception thrown: {1}{2}   at {3}:{4}{2}",
                        Environment.NewLine, detail, Environment.NewLine,
                        Path.GetFileName(e.FilePath ?? ""), e.Line));
                    _debugOutput.SelectionColor = _debugOutput.ForeColor;
                    _debugOutput.ScrollToCaret();
                }
            }
            else
            {
                // Inside a kernel the file and line are not enough: the same line is executing in
                // every thread, and which one you are looking at is the first thing to say.
                string lane = _debugController != null && _debugController.IsPausedInKernel
                    ? " [CUDA " + _debugController.KernelSession.LaneDescription() + "]"
                    : string.Empty;
                host._statusLabel.Text = "Debugger stopped: " + (e.Reason ?? "unknown") + " at "
                    + Path.GetFileName(e.FilePath ?? "") + ":" + e.Line + lane;
            }
            if (!string.IsNullOrEmpty(e.FilePath))
            {
                host.OpenInEditor(e.FilePath, e.Line);
                if (host._openEditorTabs.TryGetValue(Path.GetFullPath(e.FilePath), out TabPage tab)
                    && tab is HelixPythonNamespace.Editor.EditorTabPage etp)
                {
                    try { etp.Editor.SetCurrentLineMarker(e.Line); } catch { }
                }
            }
            RefreshCallStack();
            RefreshVariables();
            UpdateDebugToolbar();
        }

        /// <summary>
        /// Rebuilds the Call Stack list from the engine's current frames (innermost first) and
        /// selects the top frame, which drives the Variables panel.
        /// </summary>
        private void RefreshCallStack()
        {
            if (_callStackList == null || _callStackList.IsDisposed || _debugController == null) return;
            _callStackList.SelectedIndexChanged -= OnCallStackFrameSelected;
            _callStackList.Items.Clear();
            int index = 0;
            _selectedFrameId = -1;
            foreach (HelixPythonNamespace.Debugger.DebugStackFrame f in _debugController.GetCallStack())
            {
                ListViewItem item = new ListViewItem(index.ToString());
                item.SubItems.Add(string.IsNullOrEmpty(f.Name) ? "<module>" : f.Name);
                item.SubItems.Add(Path.GetFileName(f.FilePath ?? ""));
                item.SubItems.Add(f.Line.ToString());
                item.Tag = f;
                _callStackList.Items.Add(item);
                if (index == 0) _selectedFrameId = f.Id;
                index++;
            }
            if (_callStackList.Items.Count > 0) _callStackList.Items[0].Selected = true;
            _callStackList.SelectedIndexChanged += OnCallStackFrameSelected;
        }

        /// <summary>
        /// Rebuilds the Variables list: the persistent watch expressions (re-evaluated against the
        /// selected frame) first, then the locals of that frame. Called on every stop and whenever
        /// a watch is added/removed, so watches stay visible and update as you step — instead of
        /// being wiped by the per-stop locals refresh.
        /// </summary>
        private void RefreshVariables()
        {
            if (_variablesTree == null || _variablesTree.IsDisposed) return;
            _variablesTree.BeginUpdate();
            try
            {
                _variablesTree.Nodes.Clear();
                bool paused = _debugController != null && _debugController.IsPaused;
                foreach (string expr in _watchExpressions)
                {
                    HelixPythonNamespace.Debugger.DebugVariable wv = paused ? _debugController.Evaluate(expr) : null;
                    string value = paused ? (wv != null ? wv.Value : "<error>") : "<not evaluated>";
                    string type = wv != null ? wv.Type : "";
                    int childRef = wv != null ? wv.VariablesReference : 0;
                    _variablesTree.Nodes.Add(MakeVariableNode("⊙ " + expr, value, type, childRef, true, expr, "⊙" + expr));
                }
                if (paused && _selectedFrameId >= 0)
                    foreach (HelixPythonNamespace.Debugger.DebugVariable v in _debugController.GetLocals(_selectedFrameId))
                        _variablesTree.Nodes.Add(MakeVariableNode(v.Name, v.Value, v.Type, v.VariablesReference, false, null, v.Name));

                // Restore expansion state so an open list/dict/object stays open as you step.
                foreach (TreeNode n in _variablesTree.Nodes) RestoreExpansion(n);
            }
            finally { _variablesTree.EndUpdate(); }
        }

        // Build a Variables tree node. Expandable nodes (childRef > 0) get a placeholder child so
        // the [+] glyph appears; real children are fetched lazily in OnVarBeforeExpand.
        private TreeNode MakeVariableNode(string display, string value, string type, int childRef,
            bool isWatch, string watchExpr, string pathKey)
        {
            string label = display + " = " + (value ?? "");
            if (!string.IsNullOrEmpty(type)) label += "  (" + type + ")";
            TreeNode node = new TreeNode(label)
            {
                Tag = new VarNode { Path = pathKey, Ref = childRef, IsWatch = isWatch, WatchExpr = watchExpr }
            };
            if (isWatch) node.ForeColor = Color.SteelBlue;
            if (childRef > 0) node.Nodes.Add(new TreeNode(LazyPlaceholder)); // gives the expander
            return node;
        }

        // Re-expand a node (and recurse) if the user had it open before the rebuild. Expanding a
        // lazy node triggers OnVarBeforeExpand, which loads its children; we then recurse into them.
        private void RestoreExpansion(TreeNode node)
        {
            if (!(node.Tag is VarNode vn) || vn.Ref <= 0) return;
            if (_expandedVarPaths.Contains(vn.Path))
            {
                node.Expand(); // fires OnVarBeforeExpand → lazy load
                foreach (TreeNode child in node.Nodes) RestoreExpansion(child);
            }
        }

        // Lazily fetch a structured variable's members the first time its node is expanded.
        private void OnVarBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (!(e.Node.Tag is VarNode vn) || vn.Loaded || vn.Ref <= 0 || _debugController == null) return;
            vn.Loaded = true;
            e.Node.Nodes.Clear(); // drop the placeholder
            foreach (HelixPythonNamespace.Debugger.DebugVariable child in _debugController.GetChildren(vn.Ref))
                e.Node.Nodes.Add(MakeVariableNode(child.Name, child.Value, child.Type,
                    child.VariablesReference, false, null, vn.Path + "/" + child.Name));
        }

        // Selecting a different call-stack frame re-scopes the Variables panel to that frame's
        // locals (and re-evaluates watches against it).
        private void OnCallStackFrameSelected(object sender, EventArgs e)
        {
            if (_callStackList.SelectedItems.Count == 0) return;
            if (_callStackList.SelectedItems[0].Tag is HelixPythonNamespace.Debugger.DebugStackFrame f)
            {
                _selectedFrameId = f.Id;
                RefreshVariables();
            }
        }

        // Double-clicking a frame jumps the editor to that frame's source location.
        private void OnCallStackFrameActivated(object sender, EventArgs e)
        {
            if (_callStackList.SelectedItems.Count == 0) return;
            if (_callStackList.SelectedItems[0].Tag is HelixPythonNamespace.Debugger.DebugStackFrame f
                && !string.IsNullOrEmpty(f.FilePath))
                host.OpenInEditor(f.FilePath, f.Line);
        }

        // Enter in the watch box adds the watch; SuppressKeyPress silences the WinForms ding.
        private void OnWatchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; AddWatch(); }
        }

        private void AddWatch()
        {
            if (_watchInput == null) return;
            string expr = _watchInput.Text.Trim();
            if (expr.Length == 0) return;
            // A watch can be added any time; it shows "<not evaluated>" until the next pause, then
            // updates on every stop. Double-click a ⊙ row to remove it.
            if (!_watchExpressions.Contains(expr)) _watchExpressions.Add(expr);
            _watchInput.Clear();
            RefreshVariables();
            host._statusLabel.Text = (_debugController != null && _debugController.IsPaused)
                ? "Watching: " + expr
                : "Watch added: " + expr + " (evaluates when paused). Double-click to remove.";
        }

        private void OnImmediateKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; EvaluateImmediate(); }
        }

        /// <summary>
        /// Immediate/Debug Console: evaluate the typed expression in the currently-selected call-stack
        /// frame and echo "&gt; expr" then the result (or an error) to the console. No-op when not paused.
        /// </summary>
        private void EvaluateImmediate()
        {
            if (_immediateInput == null || _immediateOutput == null) return;
            string expr = _immediateInput.Text.Trim();
            if (expr.Length == 0) return;

            AppendImmediate("> " + expr + Environment.NewLine);
            _immediateInput.Clear();

            if (_debugController == null || !_debugController.IsPaused)
            {
                AppendImmediate("  (not paused — start a debug session and stop at a breakpoint first)" + Environment.NewLine);
                return;
            }

            try
            {
                HelixPythonNamespace.Debugger.DebugVariable r = _debugController.Evaluate(expr, _selectedFrameId);
                if (r == null)
                {
                    AppendImmediate("  <error: could not evaluate>" + Environment.NewLine);
                }
                else
                {
                    string val = r.Value ?? "<null>";
                    string type = string.IsNullOrEmpty(r.Type) ? "" : "  (" + r.Type + ")";
                    AppendImmediate("  " + val + type + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                AppendImmediate("  <error: " + ex.Message + ">" + Environment.NewLine);
            }
        }

        private void AppendImmediate(string text)
        {
            if (_immediateOutput == null || _immediateOutput.IsDisposed) return;
            _immediateOutput.AppendText(text);
            _immediateOutput.SelectionStart = _immediateOutput.TextLength;
            _immediateOutput.ScrollToCaret();
        }

        /// <summary>
        /// Double-clicking a ⊙ watch row removes that watch. Double-clicking a non-watch
        /// (local/member) node falls through to the TreeView's default expand/collapse.
        /// </summary>
        private void OnVariableDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node == null || !(e.Node.Tag is VarNode vn) || !vn.IsWatch) return; // only watch rows
            string expr = vn.WatchExpr;
            if (!string.IsNullOrEmpty(expr) && _watchExpressions.Remove(expr))
            {
                _expandedVarPaths.Remove(vn.Path);
                RefreshVariables();
                host._statusLabel.Text = "Watch removed: " + expr;
            }
        }

        private void OnDebugSessionEnded(object sender, EventArgs e)
        {
            host._statusLabel.Text = "Debug session ended.";
            _selectedFrameId = -1;
            if (_callStackList != null && !_callStackList.IsDisposed) _callStackList.Items.Clear();
            // Keep the watch expressions visible (as "<not evaluated>") so they survive into the
            // next run; only the locals drop away.
            RefreshVariables();
            foreach (TabPage tab in host._editorInnerTabControl.TabPages)
            {
                if (tab is HelixPythonNamespace.Editor.EditorTabPage etp)
                    try { etp.Editor.ClearCurrentLineMarker(); } catch { }
            }
            UpdateDebugToolbar();
        }

        // Rebuild the Breakpoints list from the manager: one row per (file,line), showing any
        // condition/hit-count; conditional breakpoints are tinted orange so they stand out.
        private bool _suppressBpCheck;

        private void RefreshBreakpointsList()
        {
            if (_breakpointsList == null || _breakpointsList.IsDisposed) return;
            _suppressBpCheck = true;
            try
            {
                _breakpointsList.Items.Clear();
                foreach (string file in _debugController.Breakpoints.GetFilesWithBreakpoints())
                {
                    foreach (int line in _debugController.Breakpoints.GetBreakpoints(file))
                    {
                        HelixPythonNamespace.Debugger.BreakpointCondition c = _debugController.Breakpoints.GetCondition(file, line);
                        bool enabled = _debugController.Breakpoints.IsEnabled(file, line);
                        bool isTrace = c != null && c.IsTracepoint;
                        // Show a tracepoint's log message in the Condition column, prefixed so it's obvious.
                        string condCol = c == null ? "" : (isTrace ? "log: " + c.LogMessage : (c.Condition ?? ""));
                        ListViewItem item = new ListViewItem(file);
                        item.SubItems.Add(line.ToString());
                        item.SubItems.Add(condCol);
                        item.SubItems.Add(c != null ? (c.HitCondition ?? "") : "");
                        item.Checked = enabled;
                        item.Name = file + "|" + line; // key for the check/toggle handlers
                        if (!enabled) item.ForeColor = Color.Gray;                    // disabled → greyed
                        else if (isTrace) item.ForeColor = Color.MediumVioletRed;     // tracepoint (logpoint)
                        else if (c != null && !c.IsEmpty) item.ForeColor = Color.DarkOrange; // conditional stands out
                        _breakpointsList.Items.Add(item);
                    }
                }
            }
            finally { _suppressBpCheck = false; }
            UpdateBreakpointsEmptyState();
        }

        // Show the "no breakpoints" placeholder only while the list is empty. The label and the list
        // both Dock=Fill, so exactly one may be Visible at a time.
        private void UpdateBreakpointsEmptyState()
        {
            if (_breakpointsEmptyLabel == null || _breakpointsList == null || _breakpointsList.IsDisposed) return;
            bool empty = _breakpointsList.Items.Count == 0;
            _breakpointsEmptyLabel.Visible = empty;
            _breakpointsList.Visible = !empty;
        }

        // Checkbox toggled in the Breakpoints panel → enable/disable that breakpoint (VS-style).
        private void OnBreakpointItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_suppressBpCheck || e.Item == null) return;
            string key = e.Item.Name;
            int sep = key != null ? key.LastIndexOf('|') : -1;
            if (sep <= 0) return;
            string file = key.Substring(0, sep);
            if (!int.TryParse(key.Substring(sep + 1), out int line)) return;
            _debugController.Breakpoints.SetEnabled(file, line, e.Item.Checked);
            host._statusLabel.Text = string.Format("Breakpoint {0}: {1}:{2}",
                e.Item.Checked ? "enabled" : "disabled", Path.GetFileName(file), line);
        }

        // Context-menu "Enable / Disable" on the selected breakpoint row.
        private void ToggleSelectedBreakpointEnabled()
        {
            if (_breakpointsList == null || _breakpointsList.SelectedItems.Count == 0) return;
            ListViewItem sel = _breakpointsList.SelectedItems[0];
            string key = sel.Name;
            int sep = key != null ? key.LastIndexOf('|') : -1;
            if (sep <= 0) return;
            string file = key.Substring(0, sep);
            if (!int.TryParse(key.Substring(sep + 1), out int line)) return;
            bool nowEnabled = _debugController.Breakpoints.ToggleEnabled(file, line);
            host._statusLabel.Text = string.Format("Breakpoint {0}: {1}:{2}",
                nowEnabled ? "enabled" : "disabled", Path.GetFileName(file), line);
        }

        // Double-clicking a breakpoint row opens that file at the breakpoint's line in the editor.
        private void OnBreakpointDoubleClick(object sender, EventArgs e)
        {
            if (_breakpointsList.SelectedItems.Count == 0) return;
            ListViewItem it = _breakpointsList.SelectedItems[0];
            string file = it.Text;
            if (int.TryParse(it.SubItems[1].Text, out int line)) host.OpenInEditor(file, line);
        }

        /// <summary>
        /// Keeps the editor's margin dot in sync with a breakpoint change that arrived from a
        /// non-editor channel (the Breakpoints panel's remove / set-condition, or a restore while the
        /// file is open). If the changed file is open, add/remove its margin marker via
        /// <see cref="HelixPythonNamespace.Editor.PythonEditorControl.SetBreakpointMarker"/> — which does NOT
        /// raise BreakpointToggled, so the manager isn't re-toggled (the #11 trap). The editor's own
        /// F9 path already painted its marker, so re-affirming it here is harmless/idempotent.
        /// </summary>
        private void SyncEditorMarginForChange(HelixPythonNamespace.Debugger.BreakpointChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.FilePath) || host == null || host._openEditorTabs == null) return;
            string full;
            try { full = Path.GetFullPath(e.FilePath); } catch { full = e.FilePath; }
            if (host._openEditorTabs.TryGetValue(full, out TabPage tab)
                && tab is HelixPythonNamespace.Editor.EditorTabPage etp)
            {
                try
                {
                    if (e.IsAdded)
                    {
                        // Added/updated (incl. an enable/disable toggle): draw solid or greyed per state.
                        bool enabled = _debugController.Breakpoints.IsEnabled(e.FilePath, e.Line);
                        etp.Editor.ShowBreakpoint(e.Line, enabled);
                    }
                    else
                    {
                        etp.Editor.SetBreakpointMarker(e.Line, false); // removed → clear markers
                    }
                }
                catch { }
            }
        }

        /// <summary>Returns the (file,line) of the selected breakpoint row, or false if none.</summary>
        private bool TrySelectedBreakpoint(out string file, out int line)
        {
            file = null; line = 0;
            if (_breakpointsList == null || _breakpointsList.SelectedItems.Count == 0) return false;
            ListViewItem it = _breakpointsList.SelectedItems[0];
            file = it.Text;
            return int.TryParse(it.SubItems[1].Text, out line);
        }

        /// <summary>
        /// Edits the condition / hit-count on the selected breakpoint via a small dialog, then
        /// stores it on the BreakpointManager (re-sent to debugpy on the next run).
        /// </summary>
        private void EditSelectedBreakpointCondition()
        {
            string file; int line;
            if (!TrySelectedBreakpoint(out file, out line)) return;
            HelixPythonNamespace.Debugger.BreakpointCondition existing = _debugController.Breakpoints.GetCondition(file, line);
            string condition, hit;
            if (BreakpointConditionDialog.Show(host, Path.GetFileName(file), line,
                    existing != null ? existing.Condition : null,
                    existing != null ? existing.HitCondition : null,
                    out condition, out hit))
            {
                _debugController.Breakpoints.SetCondition(file, line, condition, hit);
                bool armed = !(string.IsNullOrWhiteSpace(condition) && string.IsNullOrWhiteSpace(hit));
                host._statusLabel.Text = armed
                    ? "Breakpoint condition set on " + Path.GetFileName(file) + ":" + line + " (applies next run)."
                    : "Breakpoint condition cleared on " + Path.GetFileName(file) + ":" + line + ".";
            }
        }

        // Context-menu "Set Tracepoint": prompt for a log message. When set, the breakpoint becomes a
        // logpoint — it logs the (interpolated, e.g. "i={i}") message and continues instead of stopping.
        private void SetSelectedBreakpointLogMessage()
        {
            string file; int line;
            if (!TrySelectedBreakpoint(out file, out line)) return;
            HelixPythonNamespace.Debugger.BreakpointCondition existing = _debugController.Breakpoints.GetCondition(file, line);
            string current = existing != null ? existing.LogMessage : null;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Set Tracepoint",
                "Log message for " + Path.GetFileName(file) + ":" + line + "  (use {expr} to evaluate; blank clears):",
                current ?? string.Empty) { AllowEmpty = true })   // blank is a valid input here (clears)
            {
                if (dlg.ShowDialog(host) != System.Windows.Forms.DialogResult.OK) return;
                string msg = dlg.Value;
                _debugController.Breakpoints.SetLogMessage(file, line, msg);
                host._statusLabel.Text = string.IsNullOrWhiteSpace(msg)
                    ? "Tracepoint cleared on " + Path.GetFileName(file) + ":" + line + "."
                    : "Tracepoint set on " + Path.GetFileName(file) + ":" + line + " (logs + continues; applies next run).";
            }
        }

        // Breakpoints panel context-menu "Remove": delete the selected breakpoint from the manager
        // (the BreakpointChanged relay then clears its editor margin dot and refreshes this list).
        private void RemoveSelectedBreakpoint()
        {
            string file; int line;
            if (!TrySelectedBreakpoint(out file, out line)) return;
            _debugController.Breakpoints.RemoveBreakpoint(file, line);
            host._statusLabel.Text = "Breakpoint removed: " + Path.GetFileName(file) + ":" + line;
        }

        // Which script F5 debugs. VS2017-style: if a startup package is designated, debug its
        // entry-point file (main.py / __main__.py / *_main.py) regardless of the active tab.
        // Otherwise fall back to whatever file is in the currently selected editor tab.
        private string ResolveActiveScriptPath()
        {
            string startupEntry = host.ResolveStartupEntryFile(host._currentSolution);
            if (!string.IsNullOrEmpty(startupEntry) && System.IO.File.Exists(startupEntry))
                return startupEntry;
            if (host._editorInnerTabControl != null && host._editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)
                return etp.FilePath;
            return null;
        }

        // Debug commands the Debug menu/toolbar invoke via MainForm wrappers. Each drives the
        // controller and then re-syncs the toolbar enablement to the new session state.
        public void Start()
        {
            // A loose file goes through the single-file path, which applies its rules first. Debug
            // and Run share that gate deliberately: a hard blocker that stops one launch and not
            // the other is not a hard blocker, and Debug is the launch a user reaches for when Run
            // has just refused.
            if (host.IsSingleFileMode) { host.DebugSingleFileFromMenu(); return; }

            string script = ResolveActiveScriptPath();
            ApplyWorkspaceLaunchSettings(script);
            _debugController?.Start(script);
            UpdateDebugToolbar();
        }

        /// <summary>
        /// Debug one file that is open on its own. Called only by
        /// <see cref="SingleFileCoordinator"/>, after its gate has passed.
        ///
        /// <para>
        /// The import path is CLEARED rather than inherited. PY8005 guarantees a lone script can
        /// reach no user-written code, and that guarantee is only true while <c>sys.path</c> is the
        /// script's own directory plus the installation — so the debuggee must not be handed the
        /// solution roots (there are none), nor whatever PYTHONPATH this process inherited.
        /// </para>
        /// </summary>
        public void StartSingleFile(string script, string workingDirectory)
        {
            if (_debugController == null || string.IsNullOrEmpty(script)) return;
            if (_debugController.IsPaused) { _debugController.Continue(); UpdateDebugToolbar(); return; }

            try { _debugController.ExtraPythonPath = null; } catch { }
            // No solution environment either, for the same reason: the CUDA emulator shim would
            // arrive on sys.path, and PY8005's guarantee is about what sys.path contains.
            try { _debugController.ExtraEnvironment = null; } catch { }
            try { _debugController.ScriptArguments = null; } catch { }
            try { _debugController.WorkingDirectory = workingDirectory; } catch { }

            _debugController.Start(script);
            UpdateDebugToolbar();
        }

        // "Debug Tests" on a test-package Solution Explorer node — same PYTHONPATH setup as a
        // normal Debug launch (so a test importing sibling workspace packages resolves them), but
        // launches pytest scoped to the one test project instead of a single script.
        public void StartTests(string testProjectDir)
        {
            // pytest supplies its own argv — a package's run-args would be meaningless here.
            ApplyWorkspaceLaunchSettings(null);
            _debugController?.StartTests(testProjectDir);
            UpdateDebugToolbar();
        }

        // Feed the solution's project roots to the controller as PYTHONPATH so debugging a startup
        // package's main.py resolves its sibling-package imports (parity with the Run command), and
        // the launched script's own [tool.helixstudio].run-args as its argv, so Debug and Run
        // launch the program with the SAME command line. Passing a null script clears the argv.
        private void ApplyWorkspaceLaunchSettings(string scriptPath)
        {
            if (_debugController == null) return;
            ApplyCudaBackend();

            try
            {
                var env = host.BuildPythonPathEnv(host._currentSolution);
                string pp;
                _debugController.ExtraPythonPath = (env != null && env.TryGetValue("PYTHONPATH", out pp)) ? pp : null;
                // The WHOLE environment, not just the import path: it is what carries the CUDA
                // emulator switches, so a debug run reaches the same CUDA the Run command does.
                // Without this, a Run would be emulated and a Debug of the same program would go
                // looking for a GPU -- the sort of difference that makes a bug unreproducible.
                _debugController.ExtraEnvironment = env;
            }
            catch { _debugController.ExtraPythonPath = null; _debugController.ExtraEnvironment = null; }

            try
            {
                _debugController.ScriptArguments =
                    host.ResolveRunArgumentsForScript(host._currentSolution, scriptPath);
            }
            catch { _debugController.ScriptArguments = null; }

            try
            {
                // VS parity: the debuggee runs in its project folder, the same directory Run uses,
                // so a relative path in run-args resolves identically either way.
                _debugController.WorkingDirectory = string.IsNullOrEmpty(scriptPath)
                    ? null
                    : host.ResolveWorkingDirectoryForScript(host._currentSolution, scriptPath);
            }
            catch { _debugController.WorkingDirectory = null; }
        }

        public void Stop() { _debugController?.Stop(); UpdateDebugToolbar(); }

        // Break into a running program: asks the engine to pause. The engine raises Stopped when it
        // actually suspends, which drives the paused UI (current-line marker, call stack, variables).
        public void Pause() { _debugController?.Pause(); UpdateDebugToolbar(); }

        // Restart: stop the current session (if any) and start a fresh one on the same active script.
        // Breakpoints persist across the restart (they live in the shared BreakpointManager), so the
        // program runs to the same first breakpoint again — the VS Ctrl+Shift+F5 behaviour. When a
        // session is live we defer the fresh Start until it has FULLY ended (SessionEnded), so the new
        // debug adapter isn't launched while the old one is still terminating — launching into a
        // half-torn-down adapter races and can hang the new session.
        public void Restart()
        {
            if (_debugController == null) return;

            if (!_debugController.IsDebugging)
            {
                string restartScript = ResolveActiveScriptPath();
                ApplyWorkspaceLaunchSettings(restartScript);
                _debugController.Start(restartScript);
                UpdateDebugToolbar();
                return;
            }

            EventHandler onEnded = null;
            onEnded = (s, e) =>
            {
                try { _debugController.SessionEnded -= onEnded; } catch { }
                Action fresh = () =>
                {
                    string freshScript = ResolveActiveScriptPath();
                    ApplyWorkspaceLaunchSettings(freshScript);
                    _debugController.Start(freshScript);
                    UpdateDebugToolbar();
                };
                // Marshal back onto the UI thread; the old session's SessionEnded may arrive off-thread.
                try
                {
                    if (host != null && host.IsHandleCreated) host.BeginInvoke(fresh);
                    else fresh();
                }
                catch { try { fresh(); } catch { } }
            };
            _debugController.SessionEnded += onEnded;
            _debugController.Stop();
            UpdateDebugToolbar();
        }

        // Continue doubles as Start: resume if paused, otherwise begin a new session on the active
        // script (matches the toolbar where one button is both Start and Resume).
        public void Continue()
        {
            if (_debugController == null) return;
            if (_debugController.IsPaused) _debugController.Continue();
            else
            {
                string script = ResolveActiveScriptPath();
                ApplyWorkspaceLaunchSettings(script);
                _debugController.Start(script);
            }
            UpdateDebugToolbar();
        }

        /// <summary>
        /// Starts a debug session on a specific script — the entry point of the package the user
        /// chose in the Solution Explorer — rather than on whatever the active editor happens to
        /// show. A session already paused is resumed instead of restarted, matching what the
        /// Start/Continue button does.
        /// </summary>
        public void StartForScript(string script)
        {
            if (_debugController == null || string.IsNullOrEmpty(script)) return;
            if (_debugController.IsPaused) { _debugController.Continue(); UpdateDebugToolbar(); return; }
            ApplyWorkspaceLaunchSettings(script);
            _debugController.Start(script);
            UpdateDebugToolbar();
        }

        /// <summary>Run to Cursor: resume until execution reaches (filePath, line). Requires a paused
        /// Python session. Returns true when the run was started.</summary>
        public bool RunToCursor(string filePath, int line)
        {
            if (_debugController == null) return false;
            bool ok = _debugController.RunToCursor(filePath, line);
            if (ok) host._statusLabel.Text = string.Format("Run to cursor: {0}:{1}", Path.GetFileName(filePath), line);
            UpdateDebugToolbar();
            return ok;
        }

        /// <summary>Run to Cursor at the active editor tab's current caret line.</summary>
        public bool RunToCursorAtCaret()
        {
            if (host._editorInnerTabControl != null
                && host._editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)
            {
                string path = etp.FilePath;
                int line = etp.Editor.LineFromPosition(etp.Editor.CurrentPosition) + 1; // 0-based → 1-based
                return RunToCursor(path, line);
            }
            return false;
        }

        public void StepOver() { _debugController?.StepOver(); UpdateDebugToolbar(); }

        public void StepInto() { _debugController?.StepInto(); UpdateDebugToolbar(); }

        public void StepOut() { _debugController?.StepOut(); UpdateDebugToolbar(); }

        /// <summary>
        /// Whether the given debugpy exception filter ("raised"/"uncaught") is currently armed —
        /// lets the Debug menu show a check mark in sync with the controller state.
        /// </summary>
        public bool IsExceptionFilterArmed(string filter)
        {
            System.Collections.Generic.IList<string> f = _debugController?.ExceptionBreakFilters;
            if (f == null) return false;
            foreach (string x in f) if (string.Equals(x, filter, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Arms/disarms a debugpy exception filter ("raised" or "uncaught"). Takes effect on the
        /// next <see cref="Start"/> (debugpy reads exception filters during DAP configuration).
        /// Returns the new armed state so the caller can update the menu check.
        /// </summary>
        public bool ToggleExceptionFilter(string filter)
        {
            if (_debugController == null) return false;
            System.Collections.Generic.List<string> list =
                _debugController.ExceptionBreakFilters == null
                    ? new System.Collections.Generic.List<string>()
                    : new System.Collections.Generic.List<string>(_debugController.ExceptionBreakFilters);
            bool armed;
            int idx = list.FindIndex(x => string.Equals(x, filter, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { list.RemoveAt(idx); armed = false; }
            else { list.Add(filter); armed = true; }
            _debugController.ExceptionBreakFilters = list.Count == 0 ? null : list;
            host._statusLabel.Text = "Break on " + filter + " exceptions: " + (armed ? "ON" : "off")
                + (_debugController.IsDebugging ? " (applies next run)" : "");
            return armed;
        }

        /// <summary>Whether "stop on entry" is currently armed (lets the Debug menu show a check).</summary>
        public bool IsStopOnEntryArmed
        {
            get { return _debugController != null && _debugController.StopOnEntry; }
        }

        /// <summary>
        /// Arms/disarms "stop on entry". Takes effect on the next <see cref="Start"/>: the session
        /// pauses on the script's first executable line. Returns the new armed state so the menu
        /// check stays truthful.
        /// </summary>
        public bool ToggleStopOnEntry()
        {
            if (_debugController == null) return false;
            bool armed = !_debugController.StopOnEntry;
            _debugController.StopOnEntry = armed;
            host._statusLabel.Text = "Break on entry: " + (armed ? "ON" : "off")
                + (_debugController.IsDebugging ? " (applies next run)" : "");
            return armed;
        }

        /// <summary>
        /// Arm/disarm break-on-kernel-entry: stop at the first statement of every CUDA kernel the
        /// program launches, whether or not a breakpoint is set in it.
        ///
        /// <para>
        /// It is the only way into a kernel written INLINE in the <c>.py</c> as a string, because
        /// there is no file to put a breakpoint in until it has stopped once and the debugger has
        /// written the source out. For a kernel that lives in a <c>.cu</c> or <c>.txt</c> file an
        /// ordinary breakpoint is better, and this can stay off.
        /// </para>
        /// </summary>
        /// <summary>
        /// Tell the controller which CUDA a <c>.cu</c> debug session should use, from the SAME solution
        /// toolchain setting that decides how its C++ half is built and where a Python run's kernels go.
        ///
        /// <para>
        /// One setting for the whole solution, rather than one per command, because "which CUDA am I
        /// developing against" has one answer. A project whose kernels are emulated for Python and
        /// debugged on real hardware for C++ is two machines in one solution, and every difference
        /// between them would read as a bug in the code.
        /// </para>
        /// </summary>
        private void ApplyCudaBackend()
        {
            if (_debugController == null) return;
            try
            {
                bool ignored;
                var toolchain = host.EffectiveCMakeToolchain(out ignored);
                _debugController.CudaToolchainIsRealGpu =
                    toolchain != HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator;
                _debugController.CudaBackend =
                    HelixPythonNamespace.App.Services.DebugController.CudaDebugBackend.Auto;
            }
            catch
            {
                // Unreadable toolchain setting: the emulator is the safe answer, because it is the one
                // that works on any machine.
                _debugController.CudaToolchainIsRealGpu = false;
            }
        }

        public bool ToggleStopOnKernelEntry()
        {
            if (_debugController == null) return false;
            bool armed = !_debugController.StopOnKernelEntry;
            _debugController.StopOnKernelEntry = armed;
            host._statusLabel.Text = "Break on CUDA kernel entry: " + (armed ? "ON" : "off")
                + (_debugController.IsDebugging ? " (applies next run)" : "");
            return armed;
        }

        /// <summary>
        /// Choose which CUDA thread the debugger follows.
        ///
        /// <para>
        /// One lane, not all of them: a launch of 1024 threads that stopped in every one of them at
        /// the same breakpoint would be unusable. Every OTHER thread still runs to completion, so
        /// the kernel's results stay correct — what the choice selects is whose variables you see.
        /// </para>
        /// </summary>
        public void ChooseCudaLane()
        {
            if (_debugController == null) return;
            Dim3 block = _debugController.CudaDebugBlock;
            Dim3 thread = _debugController.CudaDebugThread;

            string answer = Microsoft.VisualBasic.Interaction.InputBox(
                "Which CUDA thread should the debugger follow?" + Environment.NewLine + Environment.NewLine
                + "Enter block and thread coordinates as  bx,by,bz : tx,ty,tz" + Environment.NewLine
                + "(every other thread still runs; this only chooses whose variables you see)",
                "CUDA Debug Lane",
                Format(block) + " : " + Format(thread));
            if (string.IsNullOrWhiteSpace(answer)) return;

            Dim3 newBlock, newThread;
            if (!TryParseLane(answer, out newBlock, out newThread))
            {
                MessageBox.Show(host, "Could not read that as a lane. Expected  bx,by,bz : tx,ty,tz  "
                    + "— for example  0,0,0 : 5,0,0",
                    "CUDA Debug Lane", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _debugController.CudaDebugBlock = newBlock;
            _debugController.CudaDebugThread = newThread;
            host._statusLabel.Text = "CUDA debug lane: block (" + Format(newBlock)
                + ") thread (" + Format(newThread) + ")"
                + (_debugController.IsDebugging ? " (applies next run)" : "");
        }

        private static string Format(Dim3 value)
        {
            return value.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                 + value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                 + value.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Reads "bx,by,bz : tx,ty,tz". Missing components default to 0, as a bare "5" means thread 5.</summary>
        internal static bool TryParseLane(string text, out Dim3 block, out Dim3 thread)
        {
            block = new Dim3(0, 0, 0);
            thread = new Dim3(0, 0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] halves = text.Split(':');
            if (halves.Length > 2) return false;
            // One half alone is the THREAD, not the block: "which thread" is the question people
            // actually have, and block 0 is where a small launch puts everything anyway.
            string blockText = halves.Length == 2 ? halves[0] : "0";
            string threadText = halves.Length == 2 ? halves[1] : halves[0];
            return TryParseDim(blockText, out block) && TryParseDim(threadText, out thread);
        }

        private static bool TryParseDim(string text, out Dim3 value)
        {
            value = new Dim3(0, 0, 0);
            var parts = new List<ulong> { 0, 0, 0 };
            string[] pieces = (text ?? string.Empty).Split(',');
            if (pieces.Length > 3) return false;
            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i].Trim();
                if (piece.Length == 0) continue;
                ulong number;
                if (!ulong.TryParse(piece, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out number))
                    return false;
                parts[i] = number;
            }
            value = new Dim3(parts[0], parts[1], parts[2]);
            return true;
        }

        public void ToggleBreakpoint()
        {
            string path = ResolveActiveScriptPath();
            if (string.IsNullOrEmpty(path) || _debugController == null) return;
            if (host._editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)
            {
                int line = etp.Editor.LineFromPosition(etp.Editor.CurrentPosition) + 1;
                // ToggleBreakpointAtLine flips the margin marker AND raises BreakpointToggled,
                // which the editor-open wiring (EditorCoordinator.OpenInEditor) already routes to
                // BreakpointManager.ToggleBreakpoint. Toggling the manager directly here as well
                // would double-toggle (add then immediately remove) — leaving the red margin dot
                // visible but NO actual breakpoint registered, so the debugger ran straight past it.
                // Let the editor be the single source of truth for both the marker and the manager.
                try { etp.Editor.ToggleBreakpointAtLine(line); } catch { }
            }
        }
    }
}
