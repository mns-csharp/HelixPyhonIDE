namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Workspace.Services;

    using HelixPythonNamespace.App;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.AddIns.Kit;
    /// <summary>
    /// FR-X-01: the IDE's integrated terminal / REPL panel — a dark console output view the user
    /// types directly into, backed by a live <see cref="TerminalSession"/> (cmd / PowerShell /
    /// Python). Unlike the one-way read-only build/run pane, this is bidirectional. The panel is a
    /// thin view over the headless engine (NFR-03).
    ///
    /// There is no separate input textbox — typing lands straight in the console buffer (real
    /// local echo, like conhost/Windows Terminal) and Enter submits whatever was typed since
    /// <see cref="_inputStart"/>, which every <see cref="Append"/> call (session output, the
    /// restart banner) advances to "now". Edits/caret movement are clamped to that live region so
    /// the user can't type into or delete past output.
    /// </summary>
    public sealed class TerminalPanel : UserControl
    {
        private readonly RichTextBox _output;
        private readonly ComboBox _shellBox;
        private readonly ToolTip _tips = new ToolTip();
        private readonly TerminalHistory _history = new TerminalHistory();
        private TerminalSession _session;
        private string _workingDir;
        private int _inputStart;

        // Exposed for the GUI verification harness (mirrors MainForm's toolbar-field pattern) —
        // not otherwise needed by production code, which drives these via RunCommand/SelectShell.
        internal Button RestartButton { get; private set; }
        internal Button StopButton { get; private set; }
        internal Button ClearButton { get; private set; }
        internal Button SaveButton { get; private set; }

        public TerminalPanel()
        {
            _output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = false,
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                WordWrap = false,
                HideSelection = false,
            };
            _output.KeyDown += OnOutputKeyDown;

            // Toolbar: normal Luna control colors, not a dark strip — it's chrome, not console
            // surface, so it should read like every other toolbar in the IDE.
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = LunaColors.ToolbarBackground };
            _shellBox = new ComboBox { Dock = DockStyle.Left, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, Font = LunaFonts.Sized(-0.5f) };
            _shellBox.Items.AddRange(new object[] { "cmd", "powershell", "python" });
            // Offer Git Bash too, but only when it's actually installed (many Windows devs prefer it).
            if (FindBash() != null) _shellBox.Items.Add("bash");
            _shellBox.SelectedIndex = 0;
            _shellBox.SelectedIndexChanged += (s, e) => Restart();

            RestartButton = MakeIconButton(ToolbarIcons.Refresh(), "Restart the terminal session");
            RestartButton.Click += (s, e) => Restart();

            StopButton = MakeIconButton(ToolbarIcons.DebugStop(), "Stop the running command (keeps the shell open)");
            StopButton.Click += (s, e) => StopRunning();

            ClearButton = MakeIconButton(ToolbarIcons.Clear(), "Clear the screen (same as typing cls/clear)");
            ClearButton.Click += (s, e) => ClearButtonClicked();

            SaveButton = MakeIconButton(ToolbarIcons.Save(), "Save the terminal output to a .txt file");
            SaveButton.Click += (s, e) => SaveOutputToFile();

            toolbar.Controls.Add(SaveButton);
            toolbar.Controls.Add(ClearButton);
            toolbar.Controls.Add(StopButton);
            toolbar.Controls.Add(RestartButton);
            toolbar.Controls.Add(_shellBox);

            // Fill added first so it claims the space left by the Top bar.
            Controls.Add(_output);
            Controls.Add(toolbar);
        }

        // 28x24 flat icon-only button, tooltip-only label (matches the compact toolbar row height).
        private Button MakeIconButton(Image icon, string tooltip)
        {
            var btn = new Button
            {
                Dock = DockStyle.Left,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Image = icon,
                ImageAlign = ContentAlignment.MiddleCenter,
                Text = string.Empty,
                TabStop = false,
            };
            btn.FlatAppearance.BorderSize = 0;
            _tips.SetToolTip(btn, tooltip);
            return btn;
        }

        /// <summary>Directory the shell starts in (defaults to the process CWD).</summary>
        public void SetWorkingDirectory(string dir) { _workingDir = dir; }

        /// <summary>
        /// Select a shell by name ("cmd"/"powershell"/"bash") if it's one of the offered choices,
        /// (re)starting the session on that shell. Returns false (no-op) if the name isn't offered
        /// (e.g. "bash" when Git isn't installed).
        /// </summary>
        public bool SelectShell(string name)
        {
            for (int i = 0; i < _shellBox.Items.Count; i++)
            {
                if (string.Equals(_shellBox.Items[i] as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (_shellBox.SelectedIndex != i) _shellBox.SelectedIndex = i;   // triggers Restart()
                    else if (_session == null || !_session.IsRunning) Restart();
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when Git Bash was found and "bash" is offered as a shell choice.</summary>
        public static bool IsBashAvailable() => FindBash() != null;

        // Auto-start the shell the first time the terminal is actually shown (and again if a previous
        // session has ended), so opening the Terminal tab isn't a blank console until the user clicks
        // Restart. Guarded on IsHandleCreated so it never spawns a process from a non-displayed panel
        // (e.g. under test) and DesignMode so the WinForms designer stays inert.
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _session == null && IsHandleCreated && !DesignMode)
            {
                Restart();
            }
        }

        /// <summary>Start (or restart) the shell selected in the toolbar.</summary>
        public void StartSession()
        {
            StopSession();
            _session = new TerminalSession(ShellExe(_shellBox.SelectedItem as string), _workingDir);
            _session.OutputReceived += OnOutput;
            _session.Exited += code => OnOutput("[process exited: " + code + "]\n");
            try { _session.Start(); }
            catch (Exception ex) { OnOutput("terminal start failed: " + ex.Message + "\n"); }
        }

        public void StopSession()
        {
            if (_session != null)
            {
                try { _session.OutputReceived -= OnOutput; _session.Dispose(); } catch { }
                _session = null;
            }
        }

        // Kill the shell process when the panel is disposed (tab closed / app exit); otherwise the
        // cmd/powershell/python/bash child leaked as an orphan.
        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopSession(); try { _tips.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        private void Restart()
        {
            Append(Environment.NewLine + "──── starting " + (_shellBox.SelectedItem as string) + " ────" + Environment.NewLine);
            StartSession();
        }

        /// <summary>
        /// Toolbar "Stop": interrupts whatever is currently running in the shell (e.g. a
        /// long-running script the user typed in) without abandoning the terminal — tree-kills the
        /// session's process (and everything it spawned) and immediately brings up a fresh shell, so
        /// Stop reads as "cancel this and give me a new prompt", not "kill the terminal".
        /// </summary>
        private void StopRunning()
        {
            if (_session == null) return;
            Append(Environment.NewLine + "──── stopping ────" + Environment.NewLine);
            try { _session.KillTree(); } catch { }
            Restart();
        }

        // Toolbar "Clear": routes through the SAME cls/clear handling RunCommand already has (local
        // clear + forwarded to the shell) so the button behaves identically to typing the command —
        // picks the form the active shell actually supports (bash has no cls, cmd has no clear).
        private void ClearButtonClicked()
        {
            string shell = (_shellBox.SelectedItem as string ?? "cmd").ToLowerInvariant();
            RunCommand(shell == "bash" ? "clear" : "cls");
        }

        // Toolbar "Save": dumps the full visible scrollback (not just the live/unsent line) to a
        // .txt file the user picks.
        private void SaveOutputToFile()
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt",
                FileName = "terminal_output.txt",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, _output.Text); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the terminal output: " + ex.Message,
                        "Save Output", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>Run a command as if typed (echoes it, then sends to the shell). Used programmatically
        /// by callers that aren't simulating real keystrokes (the harness, tests) — interactive typing
        /// goes through <see cref="OnOutputKeyDown"/> instead, where the command is already visible
        /// because the user typed it directly into the console buffer (real local echo).</summary>
        public void RunCommand(string cmd)
        {
            if (_session == null || !_session.IsRunning) StartSession();
            _history.Add(cmd);
            if (IsClearCommand(cmd)) ClearScreen();
            else Append(cmd + Environment.NewLine);
            if (_session != null) _session.SendInput(cmd);
        }

        // cls (cmd, PowerShell) / clear (PowerShell, bash) clear the screen via a direct
        // console-buffer API call (Win32 FillConsoleOutputCharacter / terminfo escape codes) — there
        // is no text output for either, so over these REDIRECTED PIPES (not a real console/ConPTY)
        // the shell's own clear has nothing to send us and the panel just sits there unchanged.
        // Intercept locally (per the ACTIVE shell — cmd has no "clear", bash has no "cls", so only
        // recognize the command(s) that shell actually supports) and clear the RichTextBox ourselves;
        // the command is still forwarded to the shell so its own state stays consistent, but the
        // panel doesn't wait on output that will never arrive.
        private bool IsClearCommand(string cmd)
        {
            string trimmed = (cmd ?? string.Empty).Trim();
            if (trimmed.Length == 0) return false;
            switch ((_shellBox.SelectedItem as string ?? "cmd").ToLowerInvariant())
            {
                case "bash": return string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase);
                case "powershell": return string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase);
                default: return string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void ClearScreen()
        {
            _output.Clear();
            _inputStart = 0;
        }

        // The console buffer IS the input — there is no separate textbox. Typed characters land at
        // the caret like any editable RichTextBox; this handler only needs to (a) submit the current
        // line on Enter, (b) recall history on Up/Down, and (c) stop the user from editing or
        // navigating into the read-only scrollback above the live line, exactly like a real console.
        private void OnOutputKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+A (select all) and Ctrl+C (copy) are read-only: let RichTextBox's built-in
            // shortcut handling run untouched, including a selection that starts above the live
            // line — clamping SelectionStart below would collapse the very selection being copied,
            // breaking Ctrl+C for anything in the scrollback.
            if (e.Control && (e.KeyCode == Keys.A || e.KeyCode == Keys.C)) return;

            if (_output.SelectionStart < _inputStart) _output.SelectionStart = _output.TextLength;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    SubmitCurrentLine();
                    break;

                // Up/Down recall previous commands, like cmd/PowerShell/bash. Recalled text
                // replaces the live line with the caret at the end so it is ready to edit or run.
                case Keys.Up:
                    e.SuppressKeyPress = true;
                    SetCurrentLine(_history.Prev());
                    break;
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    SetCurrentLine(_history.Next());
                    break;

                case Keys.Back:
                case Keys.Left:
                    if (_output.SelectionStart <= _inputStart && _output.SelectionLength == 0)
                        e.SuppressKeyPress = true;
                    break;
                case Keys.Home:
                    e.SuppressKeyPress = true;
                    _output.SelectionStart = _inputStart;
                    _output.SelectionLength = 0;
                    break;
            }
        }

        // Send whatever the user typed since _inputStart to the shell, echoing a newline locally
        // (the shell's own reply/prompt streams back asynchronously via OnOutput).
        private void SubmitCurrentLine()
        {
            string cmd = _output.TextLength > _inputStart ? _output.Text.Substring(_inputStart) : string.Empty;
            if (_session == null || !_session.IsRunning) StartSession();
            if (!string.IsNullOrEmpty(cmd)) _history.Add(cmd);
            if (IsClearCommand(cmd)) ClearScreen();
            else Append(Environment.NewLine);
            if (_session != null) _session.SendInput(cmd);
        }

        // Apply a history recall result to the live (unsent) line. null means "no history — leave
        // as-is"; an empty string (walked past the newest entry) clears the line for a fresh command.
        private void SetCurrentLine(string recalled)
        {
            if (recalled == null) return;
            _output.Select(_inputStart, _output.TextLength - _inputStart);
            _output.SelectedText = recalled;
            _output.SelectionStart = _output.TextLength;
            _output.SelectionLength = 0;
        }

        // OutputReceived fires off the raw reader thread with whatever chunk of bytes just arrived
        // (no line-boundary guarantee -- a bare prompt with no trailing newline is a normal chunk).
        // Strip ANSI/VT escape sequences first: bash's clear (and other real-terminal tools) emit
        // cursor/screen-control codes that a plain RichTextBox can't interpret -- left in, they show
        // up as literal garbage text instead of having any effect.
        private void OnOutput(string chunk)
        {
            if (IsDisposed) return;
            string clean = AnsiEscape.Replace(chunk, "");
            if (clean.Length == 0) return;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _uiThreadId) { Append(clean); return; }
            // Off the UI thread. InvokeRequired cannot be the test: it answers false on EVERY thread
            // while there is no handle, so a chunk arriving as the panel was closed used to be
            // appended right here on the reader thread -- creating the RichTextBox's handle there,
            // racing the UI thread's Dispose ("Dispose() cannot be called while doing
            // CreateHandle()"). With no handle there is nobody to show the chunk to; drop it.
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed) Append(clean); })); } catch { }
        }

        // The thread that built the panel, i.e. the one that owns its controls.
        private readonly int _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        // CSI sequences (ESC '[' params final-byte) cover cursor movement/screen erase/color -- the
        // ones a shell actually emits over a plain pipe. OSC sequences (ESC ']' ... BEL or ESC
        // backslash) cover window-title changes, which some shells also send. Built from character
        // codes rather than typed escape-sequence literals in source, and Regex.Escape()d, so the
        // pattern text is unambiguous regardless of how this file gets edited/transported.
        private static readonly System.Text.RegularExpressions.Regex AnsiEscape = BuildAnsiEscapeRegex();

        private static System.Text.RegularExpressions.Regex BuildAnsiEscapeRegex()
        {
            string esc = new string((char)27, 1);
            string bel = new string((char)7, 1);
            string backslash = new string((char)92, 1);
            string csi = System.Text.RegularExpressions.Regex.Escape(esc + "[") + "[0-9;?]*[a-zA-Z]";
            string osc = System.Text.RegularExpressions.Regex.Escape(esc + "]") + "[^" + bel + "]*(?:" +
                bel + "|" + System.Text.RegularExpressions.Regex.Escape(esc + backslash) + ")";
            return new System.Text.RegularExpressions.Regex(csi + "|" + osc,
                System.Text.RegularExpressions.RegexOptions.Compiled);
        }

        // Appends text and advances _inputStart to "now" — everything before this point is
        // scrollback (session output, banners); everything from here on is the user's live,
        // editable line, until the next Append moves the boundary again.
        private void Append(string s)
        {
            _output.AppendText(s ?? "");
            _output.SelectionStart = _output.TextLength;
            _inputStart = _output.TextLength;
            _output.ScrollToCaret();
        }

        private static string ShellExe(string sel)
        {
            switch ((sel ?? "cmd").ToLowerInvariant())
            {
                case "powershell": return "powershell.exe";
                case "python": return "python.exe";
                case "bash": return FindBash();
                default: return null; // null → engine defaults to %ComSpec% (cmd.exe)
            }
        }

        private static bool _bashResolved;
        private static string _bashPath;
        // Locate Git-for-Windows' bash.exe (common install dirs, then PATH). Cached; null when absent,
        // in which case the "bash" shell option is never offered.
        private static string FindBash()
        {
            if (_bashResolved) return _bashPath;
            _bashResolved = true;
            string[] candidates =
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            };
            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) { _bashPath = c; return c; } } catch { }
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in path.Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "bash.exe");
                    if (File.Exists(p)) { _bashPath = p; return p; }
                }
                catch { }
            }
            return null;
        }
    }
}
