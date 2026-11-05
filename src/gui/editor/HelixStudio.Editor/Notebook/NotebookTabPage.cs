using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using HelixPythonNamespace.Notebooks;
using HelixPythonNamespace.Notebooks.Kernel;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// One open `.ipynb`, presented the way Colab presents one: the title, the File / Edit / View /
    /// Insert / Runtime / Tools / Help menus, the action row, the left icon rail with its panes, the
    /// notebook page, and a status strip carrying the runtime's state.
    ///
    /// <para>
    /// <b>One kernel per notebook, started on the first Run.</b> Not on open: opening ten notebooks
    /// to read them must not start ten Python processes. The kernel outlives individual cells —
    /// that is what makes it a notebook rather than a folder of scripts — and dies with the tab.
    /// </para>
    ///
    /// <para>
    /// Outputs land in the DOCUMENT as they arrive, which is what makes them survive a save. Kernel
    /// messages arrive on the reader thread, so everything here marshals through
    /// <see cref="Control.BeginInvoke(Delegate)"/> before touching the model or the surface.
    /// </para>
    /// </summary>
    public sealed class NotebookTabPage : TabPage
    {
        private readonly NotebookViewControl _view;
        private readonly NotebookWorkspace _workspace;
        private readonly Timer _resourcePoll;
        private readonly Timer _runWatchdog;

        private NotebookKernelClient _kernel;
        private NotebookDocument _document;
        private string _filePath;
        private bool _isModified;
        private readonly Queue<int> _runQueue = new Queue<int>();
        private int _runningCell = -1;
        private DateTime _runSentUtc;
        private DateTime _lastStreamPaintUtc;
        private readonly List<NotebookOutput> _pendingKernelOutputs = new List<NotebookOutput>();
        private bool _stallReported;
        private NotebookCell _clipboardCell;

        /// <summary>Where the last module browse ended, so the next one opens there and not at home.</summary>
        private string _lastBrowsedDirectory;

        public NotebookTabPage()
        {
            _view = new NotebookViewControl { Dock = DockStyle.Fill };
            _view.RunCellRequested += (s, e) => RunCell(e.Index);
            _view.DocumentModified += delegate { IsModified = true; RefreshPanes(); };
            _view.CopyRequested += delegate (object s, NotebookCopyPart part) { CopyCell(part); };
            _view.ContextMenuStrip = BuildCellContextMenu();

            _workspace = new NotebookWorkspace(_view);
            BuildPanes();
            BuildMenus();
            BuildActions();
            Controls.Add(_workspace);

            // Colab keeps its resource meter live. Only polls while a pane that shows it is open.
            _resourcePoll = new Timer { Interval = 3000 };
            _resourcePoll.Tick += delegate { PollRuntime(); };

            // The stall watchdog. Runs only while a cell is running — see CheckForAStalledRun.
            _runWatchdog = new Timer { Interval = 2000 };
            _runWatchdog.Tick += delegate { CheckForAStalledRun(); };
        }

        public NotebookTabPage(string filePath) : this()
        {
            LoadFile(filePath);
        }

        /// <summary>
        /// Where the kernel's Python comes from. The Editor assembly deliberately does not know how
        /// to find an interpreter — the shell does, and it already has that answer for Run.
        /// </summary>
        public Func<string> InterpreterResolver { get; set; }

        /// <summary>
        /// Opens a file that is not this notebook — the Files pane hands paths to the shell. The
        /// second argument asks for it read-only.
        /// </summary>
        public Action<string, bool> OpenFileRequested { get; set; }

        /// <summary>
        /// Create a brand new notebook, and open an existing one. Handed to the shell because only
        /// it owns the tab strip, but they must be reachable FROM HERE.
        ///
        /// <para>
        /// The shell's menu bar is hidden while a notebook is on screen
        /// (<c>EditorCoordinator.SyncShellMenusForNotebookMode</c>), deliberately, so that this
        /// Colab-shaped bar is the only one visible. That makes this the ONLY File menu a user
        /// looking at a notebook has — and it offered Save, Save a copy and Revert, so from inside
        /// a notebook there was no way to make another one or open a different one. A shell
        /// command the user cannot see is not a route to anything.
        /// </para>
        /// </summary>
        public Action NewNotebookRequested { get; set; }

        /// <summary>Prompt for an existing <c>.ipynb</c> and open it. See <see cref="NewNotebookRequested"/>.</summary>
        public Action OpenNotebookRequested { get; set; }

        /// <summary>Reported to the host so it can put messages on the status bar.</summary>
        public event EventHandler<string> StatusChanged;

        public event EventHandler ModifiedChanged;

        public NotebookViewControl View { get { return _view; } }

        public NotebookWorkspace Workspace { get { return _workspace; } }

        public NotebookDocument Document { get { return _document; } }

        public string FilePath { get { return _filePath; } }

        public bool IsModified
        {
            get { return _isModified; }
            private set
            {
                if (_isModified == value) return;
                _isModified = value;
                UpdateTitle();
                EventHandler handler = ModifiedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        // ------------------------------------------------------------------ load / save

        /// <summary>Returns the error, or null on success. The caller reports it — a tab does not own a message box.</summary>
        public string LoadFile(string path)
        {
            NotebookReadResult result = NotebookReader.ReadFile(path);
            if (!result.Succeeded) return result.Error;

            _document = result.Document;
            _filePath = path;
            _view.FilePath = path;
            _view.Document = _document;
            IsModified = false;
            UpdateTitle();
            RefreshPanes();
            return null;
        }

        public bool Save()
        {
            if (_document == null || string.IsNullOrEmpty(_filePath)) return false;
            _view.CommitEdit();     // the cell being typed in is part of the document being saved
            try
            {
                NotebookWriter.WriteFile(_document, _filePath);
                IsModified = false;
                RaiseStatus("Saved " + Path.GetFileName(_filePath));
                return true;
            }
            catch (Exception ex)
            {
                RaiseStatus("Could not save " + Path.GetFileName(_filePath) + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Save if there is anything to save — called before the tab or the IDE closes.</summary>
        public void FlushPendingSave()
        {
            _view.CommitEdit();
            if (_isModified) Save();
        }

        private void UpdateTitle()
        {
            string name = string.IsNullOrEmpty(_filePath) ? "Untitled.ipynb" : Path.GetFileName(_filePath);
            Text = _isModified ? name + " *" : name;
            ToolTipText = _filePath;
            _workspace.NotebookTitle = name + (_isModified ? "   •   unsaved changes" : string.Empty);
        }

        // ------------------------------------------------------------------ running

        /// <summary>Run one cell. Markdown and raw cells are skipped without complaint.</summary>
        public void RunCell(int index)
        {
            if (_document == null || index < 0 || index >= _document.Cells.Count) return;
            if (_document.Cells[index].Kind != NotebookCellKind.Code) return;

            if (!_document.IsPython)
            {
                RaiseStatus("This notebook declares the `" + (_document.KernelName ?? "unknown")
                            + "` kernel; HelixPython can only run Python notebooks.");
                return;
            }

            _runQueue.Enqueue(index);
            PumpQueue();
        }

        public void RunAllCells() { RunRange(0, int.MaxValue); }

        /// <summary>Colab's "Run before": every code cell above the selected one.</summary>
        public void RunCellsBeforeSelection() { RunRange(0, _view.SelectedIndex - 1); }

        /// <summary>Colab's "Run after": the selected cell and everything below it.</summary>
        public void RunCellsAfterSelection() { RunRange(Math.Max(0, _view.SelectedIndex), int.MaxValue); }

        private void RunRange(int first, int last)
        {
            if (_document == null) return;
            _view.CommitEdit();
            for (int i = Math.Max(0, first); i <= last && i < _document.Cells.Count; i++)
                if (_document.Cells[i].Kind == NotebookCellKind.Code) _runQueue.Enqueue(i);
            PumpQueue();
        }

        /// <summary>Colab's "Restart and run all", which is how a notebook is checked end to end.</summary>
        public void RestartAndRunAll()
        {
            RestartKernel();
            RunAllCells();
        }

        /// <summary>
        /// Cells run one at a time, in order. A notebook is a sequence — cell 7 depends on what
        /// cell 3 bound — so "Run all" must not fire fifty executes at a kernel that will interleave
        /// their output.
        /// </summary>
        private void PumpQueue()
        {
            if (_runningCell >= 0 || _runQueue.Count == 0) return;
            if (!EnsureKernel()) { _runQueue.Clear(); return; }

            int index = _runQueue.Dequeue();
            if (index >= _document.Cells.Count) { PumpQueue(); return; }

            NotebookCell cell = _document.Cells[index];
            cell.ClearOutputs();
            cell.IsRunning = true;
            cell.RunStartedUtc = DateTime.UtcNow;
            _lastStreamPaintUtc = DateTime.MinValue;
            _pendingKernelOutputs.Clear();
            _runningCell = index;
            IsModified = true;
            _view.Refresh(index);

            _workspace.SetExecutionStatus("Executing cell " + (index + 1) + " of " + _document.Cells.Count);
            if (!_kernel.Execute(CellId(index), cell.Source ?? string.Empty))
            {
                cell.IsRunning = false;
                cell.RunStartedUtc = null;
                _runningCell = -1;
                RaiseStatus("The runtime is not running.");
                _view.Refresh(index);
                return;
            }
            _runSentUtc = DateTime.UtcNow;
            _stallReported = false;
            _runWatchdog.Enabled = true;
        }

        public void InterruptKernel()
        {
            if (_kernel == null) return;
            _kernel.Interrupt();
            _runQueue.Clear();
            RaiseStatus("Interrupting the runtime…");
        }

        /// <summary>
        /// Restart: the process is killed and a new one started, so every name the notebook bound is
        /// gone. Execution counts are NOT cleared — Jupyter keeps them, and they are the record of
        /// what ran before the restart.
        /// </summary>
        public void RestartKernel()
        {
            _runQueue.Clear();
            _runningCell = -1;
            if (_document != null)
                foreach (NotebookCell cell in _document.Cells) { cell.IsRunning = false; cell.RunStartedUtc = null; }
            if (_kernel == null) { EnsureKernel(); return; }
            _kernel.Restart();
            RaiseStatus("Runtime restarted.");
            _view.Refresh(-1);
        }

        public void ShutdownKernel()
        {
            _resourcePoll.Stop();
            if (_kernel == null) return;
            _kernel.Stop();
            _kernel.Dispose();
            _kernel = null;
            _workspace.SetRuntimeStatus("Not connected");
            VariablesPane().SetDisconnected();
            ResourcesPane().SetDisconnected();
        }

        private bool EnsureKernel()
        {
            if (_kernel != null) return true;

            string interpreter = InterpreterResolver != null ? InterpreterResolver() : null;
            if (string.IsNullOrEmpty(interpreter))
            {
                RaiseStatus("No Python interpreter was found — install Python, or set one in Preferences.");
                return false;
            }

            string script = KernelHostScriptPath();
            _kernel = new NotebookKernelClient(interpreter, script,
                string.IsNullOrEmpty(_filePath) ? null : Path.GetDirectoryName(_filePath));
            _kernel.Message += OnKernelMessage;
            // Before Start(), so the very first thing the host is told is where to import from.
            // The client re-sends this to every kernel it starts, so a restart cannot quietly drop
            // an import that worked a minute ago.
            _kernel.SetModuleSearchPaths(_document == null
                ? new List<string>()
                : _document.ModuleSearchPaths);
            _workspace.SetRuntimeStatus("Connecting…");
            if (_kernel.Start()) return true;

            _kernel.Dispose();
            _kernel = null;
            return false;
        }

        /// <summary>
        /// The host script ships beside the notebook assembly. Resolved at run time rather than
        /// baked in, because the IDE runs from `artifacts\bin` in development and from an install
        /// directory in the field.
        /// </summary>
        private static string KernelHostScriptPath()
        {
            string directory = Path.GetDirectoryName(
                new Uri(typeof(NotebookKernelClient).Assembly.CodeBase).LocalPath);
            return Path.Combine(directory ?? string.Empty, Path.Combine("Kernel", "helix_kernel_host.py"));
        }

        private string CellId(int index)
        {
            NotebookCell cell = _document.Cells[index];
            // The id has to identify the cell even if cells move while a run is in flight, so it is
            // the cell's identity hash rather than its index alone.
            return "c" + index + "-" + cell.GetHashCode().ToString("X");
        }

        private int IndexOfCellId(string id)
        {
            if (_document == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < _document.Cells.Count; i++)
                if (string.Equals(CellId(i), id, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// How long a cell may run in silence before the IDE asks the runtime whether it is
        /// actually running it. Generous on purpose: a training loop that prints nothing for ten
        /// seconds is completely ordinary, and a watchdog that cries at three would be turned off
        /// within a day.
        /// </summary>
        private static readonly TimeSpan StallSuspicion = TimeSpan.FromSeconds(12);

        /// <summary>
        /// Ask the runtime whether it is idle, once a cell has been quiet for
        /// <see cref="StallSuspicion"/>.
        ///
        /// <para>
        /// A notebook cell that never finishes is the worst failure this surface has, because it
        /// looks exactly like a cell that is merely slow: "Executing cell 1 of 1" and a counter
        /// ticking up. Reported against a two-line cell that sat at 1m 6s having printed nothing.
        /// The IDE could not tell the two apart because it never asked — so now it asks. The host
        /// answers requests one at a time on its main thread, so a pong that comes back during a
        /// cell is proof that the cell is not what it is busy with.
        /// </para>
        ///
        /// <para>
        /// It never kills anything and never re-runs anything. Its whole job is to replace a
        /// spinner with a sentence.
        /// </para>
        /// </summary>
        private void CheckForAStalledRun()
        {
            if (_runningCell < 0 || _kernel == null) { _runWatchdog.Enabled = false; return; }
            if (DateTime.UtcNow - _runSentUtc < StallSuspicion) return;
            if (!_kernel.IsReady) return;
            _kernel.Ping();
        }

        /// <summary>
        /// Give up on a run the runtime is demonstrably not performing: clear it, stop the
        /// spinner, and say what happened in words the user can act on.
        /// </summary>
        private void AbandonStalledRun()
        {
            int index = _runningCell;
            _runningCell = -1;
            _runWatchdog.Enabled = false;
            if (index >= 0 && index < _document.Cells.Count)
            {
                NotebookCell cell = _document.Cells[index];
                cell.IsRunning = false;
                cell.RunStartedUtc = null;
            }
            _runQueue.Clear();
            _stallReported = true;
            _view.Refresh(-1);
            _workspace.SetExecutionStatus("The runtime did not run cell " + (index + 1));
            RaiseStatus("The runtime is idle but never reported finishing cell " + (index + 1)
                        + " — its request or its reply was lost. Nothing was re-run; run it again "
                        + "if you need its output, or Runtime ▸ Restart for a clean kernel.");
        }

        /// <summary>True once a run has been abandoned as stalled. Read by the tests.</summary>
        internal bool StallWasReported { get { return _stallReported; } }

        private void OnKernelMessage(object sender, KernelMessageEventArgs e)
        {
            if (IsDisposed) return;
            try
            {
                if (!IsHandleCreated) { HandleKernelMessage(e); return; }
                BeginInvoke((MethodInvoker)delegate { HandleKernelMessage(e); });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void HandleKernelMessage(KernelMessageEventArgs e)
        {
            if (_document == null || IsDisposed) return;

            switch (e.Kind)
            {
                case "ready":
                    _workspace.SetRuntimeStatus("Connected   ·   " + (e.Detail ?? "Python"));
                    PollRuntime();
                    return;

                case "sys_path":
                    ReportModulePathResult(e.Detail);
                    return;

                case "output":
                    {
                        int index = e.CellId == null ? _runningCell : IndexOfCellId(e.CellId);
                        if (index < 0 || index >= _document.Cells.Count) return;
                        _pendingKernelOutputs.Add(e.Output);
                        IsModified = true;
                        if (ShouldPaintStream(e.Output))
                        {
                            FlushPendingOutputs(_document.Cells[index]);
                            _view.Refresh(index);
                            // Kernel messages are marshalled as BeginInvoke callbacks. A print-heavy
                            // loop can keep that callback queue non-empty until execution finishes,
                            // so an Invalidate alone is not visible: WM_PAINT waits behind the whole
                            // output. Force the bounded batch to the screen before consuming more.
                            _view.Update();
                        }
                        return;
                    }

                case "status":
                    {
                        int index = e.CellId == null ? _runningCell : IndexOfCellId(e.CellId);
                        if (index >= 0 && index < _document.Cells.Count)
                        {
                            NotebookCell cell = _document.Cells[index];
                            FlushPendingOutputs(cell);
                            cell.IsRunning = false;
                            // Colab shows how long the cell took, under its run button. Session
                            // state, so it is measured here and never written to the file.
                            if (cell.RunStartedUtc.HasValue)
                                cell.LastRunDuration = DateTime.UtcNow - cell.RunStartedUtc.Value;
                            cell.RunStartedUtc = null;
                            cell.LastRunFailed = !e.Ok;
                            if (e.ExecutionCount.HasValue) cell.ExecutionCount = e.ExecutionCount;
                        }
                        _runningCell = -1;
                        _runWatchdog.Enabled = false;
                        _view.Refresh(-1);
                        _workspace.SetExecutionStatus(e.Ok ? string.Empty : "A cell raised an exception");
                        RaiseStatus(e.Ok ? "Cell finished." : "Cell raised an exception.");
                        // A failed cell stops the queue: running the rest against a broken state is
                        // how a notebook produces a page of cascading errors nobody can read.
                        if (!e.Ok) _runQueue.Clear();
                        PollRuntime();
                        PumpQueue();
                        return;
                    }

                // The runtime answered a ping while we still believe a cell is running. The host
                // runs one request at a time, so it cannot have answered from inside that cell:
                // the execute, or the status that ended it, was lost. Say so and let go of the
                // run, rather than spinning on a cell nothing is executing.
                //
                // NOT a re-send. The cell may have run to completion and only its status gone
                // missing, and re-running somebody's `df.to_sql(...)` to tidy up a spinner is a
                // trade this IDE does not get to make. The user is told, and decides.
                case "pong":
                    if (_runningCell < 0) return;
                    AbandonStalledRun();
                    return;

                case "variables":
                    VariablesPane().Apply(e.Detail);
                    return;

                case "resources":
                    ResourcesPane().Apply(e.Detail);
                    return;

                case "failed":
                case "exit":
                    if (_runningCell >= 0 && _runningCell < _document.Cells.Count)
                        _document.Cells[_runningCell].IsRunning = false;
                    _runningCell = -1;
                    _runWatchdog.Enabled = false;
                    _runQueue.Clear();
                    _workspace.SetRuntimeStatus("Not connected");
                    VariablesPane().SetDisconnected();
                    ResourcesPane().SetDisconnected();
                    if (!string.IsNullOrEmpty(e.Detail)) RaiseStatus(e.Detail);
                    _view.Refresh(-1);
                    return;
            }
        }

        /// <summary>
        /// Append, merging consecutive stream chunks of the same name. A loop that prints a thousand
        /// lines arrives as a thousand messages, and one output per message would make the saved
        /// notebook a thousand entries long where Jupyter writes one.
        /// </summary>
        private static void AppendOutput(NotebookCell cell, NotebookOutput output)
        {
            if (output == null) return;
            if (output.Kind == NotebookOutputKind.Stream && cell.Outputs.Count > 0)
            {
                NotebookOutput last = cell.Outputs[cell.Outputs.Count - 1];
                if (last.Kind == NotebookOutputKind.Stream
                    && string.Equals(last.StreamName, output.StreamName, StringComparison.Ordinal))
                {
                    last.Text = (last.Text ?? string.Empty) + (output.Text ?? string.Empty);
                    return;
                }
            }
            cell.Outputs.Add(output);
        }

        private void FlushPendingOutputs(NotebookCell cell)
        {
            int index = 0;
            while (index < _pendingKernelOutputs.Count)
            {
                NotebookOutput output = _pendingKernelOutputs[index++];
                if (output != null && output.Kind == NotebookOutputKind.Stream)
                {
                    var text = new StringBuilder(output.Text ?? string.Empty);
                    while (index < _pendingKernelOutputs.Count)
                    {
                        NotebookOutput next = _pendingKernelOutputs[index];
                        if (next == null || next.Kind != NotebookOutputKind.Stream
                            || !string.Equals(next.StreamName, output.StreamName, StringComparison.Ordinal)) break;
                        text.Append(next.Text ?? string.Empty);
                        index++;
                    }
                    output.Text = text.ToString();
                }
                AppendOutput(cell, output);
            }
            _pendingKernelOutputs.Clear();
        }

        /// <summary>
        /// Decide when accumulated kernel output must become visible. Python commonly implements
        /// one <c>print</c> as two writes (payload, then newline), and repainting/re-laying out the
        /// whole notebook for every write becomes quadratic. Slow streams paint on the first chunk;
        /// bursts paint at a fixed frame cadence or bounded chunk count. The status message performs the
        /// final unconditional refresh.
        /// </summary>
        private bool ShouldPaintStream(NotebookOutput output)
        {
            return ShouldPaintStream(output, DateTime.UtcNow);
        }

        internal bool ShouldPaintStream(NotebookOutput output, DateTime now)
        {
            if (output == null || output.Kind != NotebookOutputKind.Stream) return true;

            bool elapsed = _lastStreamPaintUtc == DateTime.MinValue
                || now - _lastStreamPaintUtc >= TimeSpan.FromMilliseconds(75);
            if (!elapsed && _pendingKernelOutputs.Count < 512) return false;

            _lastStreamPaintUtc = now;
            return true;
        }

        // ------------------------------------------------------------------ cell commands

        public void InsertBelow(NotebookCellKind kind)
        {
            int at = _view.SelectedIndex < 0 ? 0 : _view.SelectedIndex + 1;
            _view.InsertCell(at, kind);
            _view.SelectedIndex = at;
        }

        public void CopySelectedCell()
        {
            NotebookCell cell = _view.SelectedCell;
            if (cell == null) return;
            _clipboardCell = new NotebookCell(cell.Kind) { Source = cell.Source };
            RaiseStatus("Cell copied.");
        }

        public void PasteCellBelow()
        {
            if (_clipboardCell == null || _document == null) return;
            int at = _view.SelectedIndex < 0 ? 0 : _view.SelectedIndex + 1;
            _view.InsertCell(at, _clipboardCell.Kind);
            _document.Cells[at].Source = _clipboardCell.Source;
            _view.SelectedIndex = at;
            _view.Refresh(at);
        }

        public void DuplicateSelectedCell()
        {
            CopySelectedCell();
            PasteCellBelow();
        }

        // ------------------------------------------------------------------ copying a cell as text

        /// <summary>Put the selected cell's code on the clipboard, with nothing its run produced.</summary>
        public void CopyCellSource() { CopyCell(NotebookCopyPart.Source); }

        /// <summary>Put what the selected cell printed, returned or raised on the clipboard — none of its code.</summary>
        public void CopyCellOutput() { CopyCell(NotebookCopyPart.Output); }

        /// <summary>Put the selected cell's code AND what it produced on the clipboard, as one block.</summary>
        public void CopyCellSourceAndOutput() { CopyCell(NotebookCopyPart.SourceAndOutput); }

        /// <summary>
        /// What the shell's Edit ▸ Copy (Ctrl+C) means on a notebook tab.
        ///
        /// <para>
        /// With a cell open for editing the user is inside a text editor looking at a selection,
        /// and Ctrl+C there can only mean that selection — so it goes to the editor, exactly as it
        /// does in a `.py` tab. With no cell being edited there is no selection to speak of, and
        /// the thing the user is pointing at is a whole cell, so it copies that cell's source.
        /// </para>
        /// </summary>
        public void CopyForEditCommand()
        {
            PythonEditorControl editor = _view.Editor;
            if (_view.IsEditing && editor != null && !string.IsNullOrEmpty(editor.SelectedText))
            {
                editor.Copy();
                return;
            }
            CopyCellSource();
        }

        internal void CopyCell(NotebookCopyPart part)
        {
            // The text being copied has to be the text on screen: a cell open in the editor holds
            // its newest keystrokes there, not in the model, until this commits them.
            _view.CommitEdit();

            NotebookCell cell = _view.SelectedCell;
            if (cell == null) { RaiseStatus("Select a cell first."); return; }

            string text = NotebookCellClipboard.TextFor(cell, part);
            if (text.Length == 0) { RaiseStatus(NothingToCopyMessage(cell, part)); return; }

            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException ex)
            {
                // Another process can hold the clipboard open and Windows refuses the write. There
                // is nothing to retry here, and a copy that silently did nothing is the worst of
                // the three outcomes — so say which one happened.
                RaiseStatus("Could not copy: the clipboard is in use by another program (" + ex.Message + ").");
                return;
            }

            RaiseStatus(CopiedMessage(part) + " — "
                        + NotebookCellClipboard.LineCount(text).ToString("N0") + " lines.");
        }

        private static string CopiedMessage(NotebookCopyPart part)
        {
            switch (part)
            {
                case NotebookCopyPart.Output: return "Copied the cell's output";
                case NotebookCopyPart.SourceAndOutput: return "Copied the cell's source and output";
                default: return "Copied the cell's source";
            }
        }

        /// <summary>
        /// Why nothing was copied. "Nothing happened" is the message a user cannot act on, and the
        /// three reasons want three different responses: run the cell, look at it again, or accept
        /// that a figure has no text in it.
        /// </summary>
        private static string NothingToCopyMessage(NotebookCell cell, NotebookCopyPart part)
        {
            if (part == NotebookCopyPart.Output)
            {
                return NotebookCellClipboard.HasOnlyNonTextOutputs(cell)
                    ? "That cell's output has no text to copy — it is an image or a widget."
                    : "That cell has not produced any output yet.";
            }
            return "That cell is empty — there is nothing to copy.";
        }

        // ------------------------------------------------------------------ importing from disk

        /// <summary>
        /// Pick a Python file anywhere on the machine and make it importable from this notebook.
        ///
        /// <para>
        /// The user chooses a FILE, not a folder, because the file is the thing they can actually
        /// point at: they know they want <c>tensor.py</c>. Which directory has to go on
        /// <c>sys.path</c>, and what the resulting import statement reads, are then worked out from
        /// the file — see <see cref="PythonModulePath"/>. Nobody is asked about package structure,
        /// and nothing is relative to anything.
        /// </para>
        /// </summary>
        public void AddModuleFromFile()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Pick the Python file you want to import",
                Filter = "Python files (*.py;*.pyw)|*.py;*.pyw|All files (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = LastBrowsedDirectory(),
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                RegisterModuleFile(dialog.FileName);
            }
        }

        /// <summary>
        /// Resolve one file and put its root on the path. Separate from the dialog so the same
        /// route serves the Files pane's "Make this importable" and the tests.
        /// </summary>
        public void RegisterModuleFile(string pythonFilePath)
        {
            if (_document == null) return;

            PythonModuleResolution resolved = PythonModulePath.Resolve(pythonFilePath);
            if (!resolved.Succeeded) { RaiseStatus(resolved.Error); return; }

            _lastBrowsedDirectory = Path.GetDirectoryName(resolved.FilePath);

            bool added = _document.AddModuleSearchPath(resolved.RootDirectory);
            if (added) { IsModified = true; ApplyModuleSearchPaths(); PersistModulePaths(); }
            RefreshPanes();

            RaiseStatus((added ? "Added " : "Already on the import path: ")
                        + resolved.RootDirectory + "   ·   you can now write: "
                        + resolved.ImportStatement);
        }

        /// <summary>
        /// The escape hatch: put a folder on the path exactly as chosen, with no interpretation.
        ///
        /// <para>
        /// Every layout the resolver cannot name — a PEP 420 namespace package, a source tree whose
        /// import root is decided by an installed <c>.pth</c> file, a folder of loose scripts — is
        /// one click away here, and the user is the one who knows which folder it is. A feature that
        /// only handles the shapes it recognises leaves the rest with nothing.
        /// </para>
        /// </summary>
        public void AddModuleFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Pick a folder to put on this notebook's import path",
                ShowNewFolderButton = false,
            })
            {
                string start = LastBrowsedDirectory();
                if (!string.IsNullOrEmpty(start)) dialog.SelectedPath = start;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                RegisterModuleFolder(dialog.SelectedPath);
            }
        }

        public void RegisterModuleFolder(string directory)
        {
            if (_document == null || string.IsNullOrEmpty(directory)) return;
            if (!Directory.Exists(directory)) { RaiseStatus("There is no folder at " + directory + "."); return; }

            _lastBrowsedDirectory = directory;
            bool added = _document.AddModuleSearchPath(directory);
            if (added) { IsModified = true; ApplyModuleSearchPaths(); PersistModulePaths(); }
            RefreshPanes();
            RaiseStatus((added ? "Added " : "Already on the import path: ") + directory);
            ReportWhatAFolderOffers(directory);
        }

        /// <summary>
        /// Say what the folder just registered actually makes importable — and say so loudly when
        /// the answer is "nothing".
        ///
        /// <para>
        /// This is the failure the feature is most likely to produce, and it produced it: a user
        /// picked a repository root, got "Added D:\…\sample_workspace", and had no idea the folder held
        /// no top-level package at all. "Added" answered the wrong question. The right one is
        /// whether the import they came here to write will now resolve, and the folder route — the
        /// one that interprets nothing — is exactly where that has to be checked out loud.
        /// </para>
        ///
        /// <para>
        /// Off the UI thread, because a chosen folder can be a repository or a slow share, and the
        /// answer is worth waiting for but not worth freezing for.
        /// </para>
        /// </summary>
        private void ReportWhatAFolderOffers(string directory)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int total;
                IList<string> names = PythonModulePath.ImportableNames(directory, 6, out total);
                string message = total == 0
                    ? "…but nothing in " + Path.GetFileName(directory.TrimEnd('\\', '/'))
                      + " can be imported: it holds no package (a folder with an __init__.py) and no "
                      + ".py file at its top level. Use \"Add a module…\" and pick the .py file you "
                      + "want — HelixPython finds the right folder from it."
                    : "…which makes " + string.Join(", ", new List<string>(names).ToArray())
                      + (total > names.Count ? " and " + (total - names.Count) + " more" : string.Empty)
                      + " importable.";
                try
                {
                    if (IsDisposed) return;
                    // No handle yet means no message loop to marshal onto — report directly rather
                    // than dropping it. Exactly what OnKernelMessage does, and for the same reason:
                    // silently swallowing the answer is the failure this method exists to prevent.
                    if (!IsHandleCreated) { RaiseStatus(message); return; }
                    BeginInvoke((MethodInvoker)delegate { RaiseStatus(message); });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        public void RemoveModulePath(string directory)
        {
            if (_document == null || !_document.RemoveModuleSearchPath(directory)) return;
            IsModified = true;
            ApplyModuleSearchPaths();
            PersistModulePaths();
            RefreshPanes();
            // Honest about what removing does and does not do: sys.path is only read at import
            // time, so anything already imported stays imported until the runtime restarts.
            RaiseStatus("Removed " + directory
                        + ". Modules already imported stay until the runtime is restarted.");
        }

        /// <summary>
        /// Write the notebook NOW, so a registered path is durable the moment it is set.
        ///
        /// <para>
        /// The import path is a setting, not a keystroke. A user who registers a folder, sees it
        /// listed and sees their import resolve has been told the job is done — and requiring a
        /// Ctrl+S they were never prompted for turns that into a trap the next restart springs.
        /// Reported exactly that way: "after IDE reboot, the added path is gone".
        /// </para>
        ///
        /// <para>
        /// The path guard is belt and braces, not a case with a message: a notebook only ever gets
        /// a document through <c>LoadFile</c>, which sets the file path in the same breath, and
        /// File ▸ New writes the file to disk BEFORE opening it. There is currently no way to hold
        /// a notebook with no path, so there is nothing to warn about.
        /// </para>
        /// </summary>
        private void PersistModulePaths()
        {
            if (_document == null || string.IsNullOrEmpty(_filePath)) return;
            Save();
        }

        /// <summary>
        /// Hand the current set to the kernel. Deliberately does NOT start one: registering a path
        /// is something a user does while reading, and it must not launch a Python process.
        /// </summary>
        private void ApplyModuleSearchPaths()
        {
            if (_document == null) return;

            // Two consumers, not one. The kernel needs the paths to IMPORT the code; the type
            // inference behind the inlays needs them to READ it. Sending them to only the kernel
            // is what produced a notebook that ran a cell correctly while every inlay above it
            // read "couldn't infer type" — the analyzer was searching the disk around the .ipynb
            // and could not reach the package the user had already pointed at.
            if (_view != null && !_view.IsDisposed) _view.RefreshModuleSearchPaths();
            if (_kernel == null) return;
            _kernel.SetModuleSearchPaths(_document.ModuleSearchPaths);
        }

        /// <summary>
        /// What the kernel made of the paths it was handed.
        ///
        /// <para>
        /// The <c>shadowed</c> case is the one worth the code: <c>sys.path</c> is consulted at
        /// import time only, so adding a folder cannot dislodge a module Python has already
        /// imported from somewhere else. Left unsaid, that produces the most confusing outcome this
        /// feature has — the folder is listed, the import runs, and the code that executes is the
        /// other copy.
        /// </para>
        /// </summary>
        private void ReportModulePathResult(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            JObject message;
            try { message = JObject.Parse(json); }
            catch (Exception) { return; }

            var missing = Names(message["missing"]);
            var shadowed = Names(message["shadowed"]);
            if (missing.Count == 0 && shadowed.Count == 0) return;

            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add("not on disk any more: " + string.Join(", ", missing.ToArray()));
            if (shadowed.Count > 0)
                parts.Add("already imported from elsewhere — restart the runtime to pick up the new copy: "
                          + string.Join(", ", shadowed.ToArray()));
            RaiseStatus("Import path: " + string.Join("; ", parts.ToArray()));
        }

        private static List<string> Names(JToken token)
        {
            var names = new List<string>();
            var array = token as JArray;
            if (array == null) return names;
            foreach (JToken entry in array)
                if (entry != null && entry.Type != JTokenType.Null) names.Add(entry.ToString());
            return names;
        }

        /// <summary>Where the next browse should open: where the last one ended, else the notebook.</summary>
        private string LastBrowsedDirectory()
        {
            if (!string.IsNullOrEmpty(_lastBrowsedDirectory) && Directory.Exists(_lastBrowsedDirectory))
                return _lastBrowsedDirectory;
            return string.IsNullOrEmpty(_filePath) ? null : Path.GetDirectoryName(_filePath);
        }

        // ------------------------------------------------------------------ chrome

        private void BuildPanes()
        {
            var toc = new NotebookTocPanel();
            var find = new NotebookFindPanel();
            var variables = new NotebookVariablesPanel();
            var files = new NotebookFilesPanel();
            var modules = new NotebookModulesPanel();
            var resources = new NotebookResourcesPanel();

            foreach (NotebookSidePanel panel in new NotebookSidePanel[]
                     { toc, find, variables, files, modules, resources })
                panel.CellActivated += delegate (object sender, int index) { _view.SelectedIndex = index; };

            files.FileActivated += delegate (object sender, NotebookFileOpenRequest request)
            {
                OpenBrowsedFile(request.Path, request.ReadOnly);
            };
            // The Files tree is the browser the user already has open; a Python file in it is one
            // right click away from being importable, without a dialog at all.
            files.MakeImportableRequested += delegate (object sender, string path) { RegisterModuleFile(path); };
            files.BrowseRequested += delegate { BrowseFilesPaneElsewhere(files); };

            modules.BrowseRequested += delegate (object sender, string root) { BrowseModuleRoot(files, root); };
            modules.AddFileRequested += delegate { AddModuleFromFile(); };
            modules.AddFolderRequested += delegate { AddModuleFolder(); };
            modules.RemoveRequested += delegate (object sender, string root) { RemoveModulePath(root); };

            _workspace.AddPane("toc", "☰", toc);
            _workspace.AddPane("find", "🔍", find);
            _workspace.AddPane("variables", "{x}", variables);
            _workspace.AddPane("files", "🗀", files);
            // A BMP symbol, not an emoji: the rail renders in Segoe UI Symbol at 10pt, and a glyph
            // that font lacks comes out as a tofu box rather than an icon.
            _workspace.AddPane("modules", "⊞", modules);
            _workspace.AddPane("resources", "▤", resources);
        }

        /// <summary>
        /// Open one of the browsed files.
        ///
        /// <para>
        /// The default — a plain double-click, <paramref name="readOnly"/> null — is decided by
        /// WHOSE file it is. A file under a registered import root belongs to a library the user is
        /// reading, so it opens read-only: this tab autosaves on a 400 ms debounce, and a stray
        /// keystroke while reading somebody else's package would edit it with no prompt and no way
        /// to say no. Anything else is the user's own file beside their notebook, and opens the way
        /// it always has. Either default can be overridden from the Files pane's own menu, which
        /// offers both openings by name.
        /// </para>
        /// </summary>
        internal void OpenBrowsedFile(string path, bool? readOnly)
        {
            Action<string, bool> open = OpenFileRequested;
            if (open == null || string.IsNullOrEmpty(path)) return;
            open(path, readOnly ?? IsUnderAModuleSearchPath(path));
        }

        /// <summary>
        /// Ask the shell for a new notebook. Says so on the status bar when nothing is listening,
        /// rather than looking like a menu item that does nothing — an unwired callback is exactly
        /// the failure this menu's own contract ("every entry does something") forbids.
        /// </summary>
        internal void RequestNewNotebook()
        {
            Action handler = NewNotebookRequested;
            if (handler == null) { RaiseStatus("New notebook is not available here."); return; }
            handler();
        }

        /// <summary>Ask the shell to prompt for an existing notebook. See <see cref="RequestNewNotebook"/>.</summary>
        internal void RequestOpenNotebook()
        {
            Action handler = OpenNotebookRequested;
            if (handler == null) { RaiseStatus("Open notebook is not available here."); return; }
            handler();
        }

        /// <summary>True when this file lives inside one of the notebook's registered import roots.</summary>
        internal bool IsUnderAModuleSearchPath(string path)
        {
            if (_document == null || string.IsNullOrEmpty(path)) return false;

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return false; }

            foreach (string root in _document.ModuleSearchPaths)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix;
                try
                {
                    prefix = Path.GetFullPath(root)
                                 .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                }
                catch (Exception) { continue; }
                // The separator matters: without it "…\code" would also claim "…\code_backup".
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Show a registered import root in the Files tree, so its code can be read.</summary>
        private void BrowseModuleRoot(NotebookFilesPanel files, string root)
        {
            if (files == null || string.IsNullOrEmpty(root)) return;
            if (!Directory.Exists(root)) { RaiseStatus("That folder is not there any more: " + root); return; }

            files.BrowseTo(root);
            _workspace.ShowPane("files");
            _lastBrowsedDirectory = root;
            RaiseStatus("Browsing " + root
                        + "   ·   double-click a file to read it, or right-click to open it for editing.");
        }

        /// <summary>Point the Files tree at another folder, so it can reach a codebase elsewhere.</summary>
        private void BrowseFilesPaneElsewhere(NotebookFilesPanel files)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Pick a folder to browse",
                ShowNewFolderButton = false,
            })
            {
                string start = LastBrowsedDirectory();
                if (!string.IsNullOrEmpty(start)) dialog.SelectedPath = start;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _lastBrowsedDirectory = dialog.SelectedPath;
                files.BrowseTo(dialog.SelectedPath);
            }
        }

        private NotebookVariablesPanel VariablesPane()
        {
            return (NotebookVariablesPanel)_workspace.Pane("variables");
        }

        private NotebookResourcesPanel ResourcesPane()
        {
            return (NotebookResourcesPanel)_workspace.Pane("resources");
        }

        private void RefreshPanes()
        {
            foreach (NotebookSidePanel panel in _workspace.Panes) panel.Refresh(_document);
        }

        /// <summary>
        /// Ask the runtime what it holds. Cheap, and only asked when something might have changed —
        /// a cell finished, the runtime connected, or a pane that shows it is open.
        /// </summary>
        private void PollRuntime()
        {
            if (_kernel == null || !_kernel.IsReady) return;
            _kernel.RequestVariables();
            _kernel.RequestResources();
        }

        /// <summary>
        /// Colab's action row: <c>+ Code  + Text  |  ▶ Run all  |  Interrupt  Restart  Clear
        /// outputs</c>. Added in REVERSE, because a Dock=Left child is placed by z-order and the
        /// last one added ends up leftmost — add them in reading order and the row reads backwards.
        /// </summary>
        private void BuildActions()
        {
            _workspace.AddAction("Clear outputs", "Drop every cell's output", delegate { _view.ClearOutputs(-1); });
            _workspace.AddAction("Restart", "Restart the runtime — every name the notebook defined is forgotten",
                delegate { RestartKernel(); });
            _workspace.AddAction("Interrupt", "Interrupt the running cell; the runtime keeps its namespace",
                delegate { InterruptKernel(); });
            _workspace.AddActionSeparator();
            _workspace.AddAction("▶ Run all", "Run every cell, in order", delegate { RunAllCells(); });
            _workspace.AddActionSeparator();
            _workspace.AddAction("+ Text", "Add a text (markdown) cell below",
                delegate { InsertBelow(NotebookCellKind.Markdown); });
            _workspace.AddAction("+ Code", "Add a code cell below",
                delegate { InsertBelow(NotebookCellKind.Code); });
        }

        /// <summary>
        /// Colab's menu bar, with Colab's own wording and grouping. Every entry does something: a
        /// menu of greyed-out names would be a worse imitation than no menu at all.
        /// </summary>
        private void BuildMenus()
        {
            // "New notebook" and "Open notebook…" head this menu for the same reason they head
            // Colab's — and for one more that is specific to us: while a notebook is showing, the
            // shell's own menu bar is hidden, so this is the only File menu the user has. Without
            // these two entries a notebook was a dead end: you could save the one in front of you
            // and nothing else, and making a second one meant closing this one first to get the
            // shell menu back. Neither entry involves a solution.
            ToolStripMenuItem file = Menu("&File",
                Item("New notebook", Keys.None, delegate { RequestNewNotebook(); }),
                Item("Open notebook…", Keys.Control | Keys.O, delegate { RequestOpenNotebook(); }),
                null,
                Item("Save", Keys.Control | Keys.S, delegate { Save(); }),
                Item("Save a copy…", Keys.None, delegate { SaveACopy(); }),
                null,
                Item("Revert to the saved version", Keys.None, delegate { Revert(); }));

            ToolStripMenuItem edit = Menu("&Edit",
                Item("Copy cell", Keys.None, delegate { CopySelectedCell(); }),
                Item("Paste cell below", Keys.None, delegate { PasteCellBelow(); }),
                Item("Duplicate cell", Keys.None, delegate { DuplicateSelectedCell(); }),
                Item("Delete cell", Keys.None, delegate { _view.DeleteSelectedCell(); }),
                null,
                // Three, not one with a setting: the source alone is what you paste into a file,
                // the output alone is what you paste into a bug report, and the pair is what you
                // paste when the question is why this code did that.
                Item("Copy source to clipboard", "Ctrl+C", delegate { CopyCellSource(); }),
                Item("Copy output to clipboard", null, delegate { CopyCellOutput(); }),
                Item("Copy source and output to clipboard", "Ctrl+Shift+C",
                    delegate { CopyCellSourceAndOutput(); }),
                null,
                Item("Move cell up", Keys.None, delegate { _view.MoveSelectedCell(-1); }),
                Item("Move cell down", Keys.None, delegate { _view.MoveSelectedCell(1); }),
                null,
                Item("Clear this cell's output", Keys.None, delegate { _view.ClearOutputs(_view.SelectedIndex); }),
                Item("Clear all outputs", Keys.None, delegate { _view.ClearOutputs(-1); }));

            ToolStripMenuItem view = Menu("&View",
                Item("Table of contents", Keys.None, delegate { _workspace.TogglePane("toc"); }),
                Item("Find in notebook", Keys.Control | Keys.F, delegate { OpenFind(); }),
                Item("Variables", Keys.None, delegate { ShowPaneAndPoll("variables"); }),
                Item("Files", Keys.None, delegate { _workspace.TogglePane("files"); }),
                Item("Resources", Keys.None, delegate { ShowPaneAndPoll("resources"); }));

            ToolStripMenuItem insert = Menu("&Insert",
                Item("Code cell", Keys.None, delegate { InsertBelow(NotebookCellKind.Code); }),
                Item("Text cell", Keys.None, delegate { InsertBelow(NotebookCellKind.Markdown); }),
                null,
                Item("Turn the selected cell into code", Keys.None,
                    delegate { _view.SetSelectedCellKind(NotebookCellKind.Code); }),
                Item("Turn the selected cell into text", Keys.None,
                    delegate { _view.SetSelectedCellKind(NotebookCellKind.Markdown); }));

            ToolStripMenuItem runtime = Menu("&Runtime",
                Item("Run all", Keys.Control | Keys.F9, delegate { RunAllCells(); }),
                Item("Run before", Keys.None, delegate { RunCellsBeforeSelection(); }),
                Item("Run the focused cell", Keys.None, delegate { _view.RunSelectedCell(); }),
                Item("Run after", Keys.None, delegate { RunCellsAfterSelection(); }),
                null,
                null,
                // Under Runtime because that is what it changes: the import path belongs to the
                // Python process, and this is where a user goes when a runtime cannot find something.
                Item("Add a module from disk…", Keys.None, delegate { AddModuleFromFile(); }),
                Item("Add a folder to the import path…", Keys.None, delegate { AddModuleFolder(); }),
                Item("Manage the import path", Keys.None, delegate { _workspace.ShowPane("modules"); }),
                null,
                Item("Interrupt execution", Keys.None, delegate { InterruptKernel(); }),
                Item("Restart runtime", Keys.None, delegate { RestartKernel(); }),
                Item("Restart and run all", Keys.None, delegate { RestartAndRunAll(); }),
                Item("Disconnect and delete runtime", Keys.None, delegate { ShutdownKernel(); }),
                null,
                Item("View resources", Keys.None, delegate { ShowPaneAndPoll("resources"); }));

            ToolStripMenuItem tools = Menu("&Tools",
                Item("Show the notebook's code as one Python file", Keys.None, delegate { ShowJoinedSource(); }));

            ToolStripMenuItem help = Menu("&Help",
                Item("What works, and what does not, in this notebook view", Keys.None,
                    delegate { ShowCapabilities(); }));

            _workspace.Menu.Items.AddRange(new ToolStripItem[] { file, edit, view, insert, runtime, tools, help });
        }

        private void ShowPaneAndPoll(string pane)
        {
            _workspace.TogglePane(pane);
            if (string.Equals(_workspace.OpenPane, pane, StringComparison.Ordinal))
            {
                PollRuntime();
                _resourcePoll.Start();
            }
            else _resourcePoll.Stop();
        }

        private void OpenFind()
        {
            _workspace.ShowPane("find");
            var find = _workspace.Pane("find") as NotebookFindPanel;
            if (find != null) find.FocusQuery();
        }

        private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items)
        {
            var menu = new ToolStripMenuItem(text);
            foreach (ToolStripItem item in items)
                menu.DropDownItems.Add(item ?? new ToolStripSeparator());
            return menu;
        }

        /// <summary>
        /// A menu entry that TEACHES a key without CLAIMING it.
        ///
        /// <para>
        /// Setting <see cref="ToolStripMenuItem.ShortcutKeys"/> registers the key form-wide, and
        /// form-wide is wrong for Ctrl+C: the shell's own Edit ▸ Copy already owns it, and a cell
        /// open for editing needs Ctrl+C to stay the embedded editor's "copy my selection".
        /// <see cref="ToolStripItem.ShortcutKeyDisplayString"/> prints the key beside the item and
        /// registers nothing, which is exactly the half that is wanted — the copy keys are handled
        /// where they can tell whether a cell is being edited
        /// (<see cref="NotebookViewControl.OnKeyDown"/> and the shell's Edit ▸ Copy).
        /// </para>
        /// </summary>
        private static ToolStripMenuItem Item(string text, string shortcutDisplay, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            if (!string.IsNullOrEmpty(shortcutDisplay))
            {
                item.ShortcutKeyDisplayString = shortcutDisplay;
                item.ShowShortcutKeys = true;
            }
            return item;
        }

        /// <summary>
        /// The right-click menu on a cell, carrying the three copies and nothing else.
        ///
        /// <para>
        /// Right-clicking a cell is how a user reaches "give me that traceback" without hunting
        /// through a menu bar, and it is the only place in the surface where the pointer has
        /// already said which cell is meant. Everything else a cell can do already has a button in
        /// its hover toolbar or a line in the Edit menu; repeating them here would bury the three
        /// items this menu exists for.
        /// </para>
        /// </summary>
        private ContextMenuStrip BuildCellContextMenu()
        {
            var copySource = new ToolStripMenuItem("Copy source", null, delegate { CopyCellSource(); });
            var copyOutput = new ToolStripMenuItem("Copy output", null, delegate { CopyCellOutput(); });
            var copyBoth = new ToolStripMenuItem("Copy source and output", null,
                                                 delegate { CopyCellSourceAndOutput(); });

            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] { copySource, copyOutput, copyBoth });
            menu.Opening += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                NotebookCell cell = _view.SelectedCell;
                if (cell == null) { e.Cancel = true; return; }

                // A cell that has not run has no output, and offering to copy it would be offering
                // something that cannot happen. "Copy source and output" stays live either way —
                // with no output it is still a perfectly good copy of the source.
                bool hasOutputText = NotebookCellClipboard.OutputText(cell).Length > 0;
                copyOutput.Enabled = hasOutputText;
                copySource.Enabled = NotebookCellClipboard.SourceText(cell).Length > 0;
                copyBoth.Enabled = copySource.Enabled || hasOutputText;
            };
            return menu;
        }

        private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            if (shortcut != Keys.None)
            {
                // ShowShortcutKeys without ShortcutKeys prints nothing; setting both is what makes
                // the menu teach the keyboard, which is most of what a menu is for.
                item.ShortcutKeys = shortcut;
                item.ShowShortcutKeys = true;
            }
            return item;
        }

        private void SaveACopy()
        {
            if (_document == null) return;
            using (var dialog = new SaveFileDialog
            {
                Filter = "Jupyter notebook (*.ipynb)|*.ipynb|All files (*.*)|*.*",
                DefaultExt = "ipynb",
                FileName = string.IsNullOrEmpty(_filePath)
                    ? "Untitled.ipynb"
                    : Path.GetFileNameWithoutExtension(_filePath) + " copy.ipynb",
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _view.CommitEdit();
                try
                {
                    NotebookWriter.WriteFile(_document, dialog.FileName);
                    RaiseStatus("Saved a copy to " + dialog.FileName);
                }
                catch (Exception ex) { RaiseStatus("Could not save the copy: " + ex.Message); }
            }
        }

        private void Revert()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            if (_isModified && MessageBox.Show(this,
                    "Discard the changes made since this notebook was opened?",
                    "Revert", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            string error = LoadFile(_filePath);
            RaiseStatus(error ?? ("Reverted to the saved " + Path.GetFileName(_filePath)));
        }

        /// <summary>
        /// What every Python engine in the IDE sees when it reads this notebook — the code cells
        /// joined, with magics neutralised. Shown because "why does completion think that?" is a
        /// question this answers in one look.
        /// </summary>
        private void ShowJoinedSource()
        {
            if (_document == null) return;
            NotebookCodeView code = NotebookCodeView.Build(_document);
            using (var form = new Form
            {
                Text = "Notebook code — what the language engines read",
                Size = new Size(900, 700),
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                form.Controls.Add(new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Font = new Font("Consolas", 9f),
                    Text = code.Source.Replace("\n", Environment.NewLine),
                });
                form.ShowDialog(this);
            }
        }

        private void ShowCapabilities()
        {
            MessageBox.Show(this,
                "This notebook view runs on your machine, with your interpreter and your GPU."
                + Environment.NewLine + Environment.NewLine
                + "Working: opening and saving .ipynb without moving a byte you did not edit, running "
                + "cells in one persistent runtime, magics and rich output when IPython is installed, "
                + "figures, tracebacks, interrupt without losing the namespace, notebook-wide "
                + "IntelliSense, table of contents, find, variables, files and resources."
                + Environment.NewLine + Environment.NewLine
                + "Not yet: LaTeX in text cells, ipywidgets, drag-to-reorder, forms (#@param), "
                + "and the google.colab module.",
                "Notebook support", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RaiseStatus(string message)
        {
            EventHandler<string> handler = StatusChanged;
            if (handler != null) handler(this, message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resourcePoll.Stop();
                _resourcePoll.Dispose();
                _runWatchdog.Stop();
                _runWatchdog.Dispose();
                // Not a child control, so nothing else disposes it.
                if (_view.ContextMenuStrip != null) _view.ContextMenuStrip.Dispose();
                ShutdownKernel();
            }
            base.Dispose(disposing);
        }
    }
}
