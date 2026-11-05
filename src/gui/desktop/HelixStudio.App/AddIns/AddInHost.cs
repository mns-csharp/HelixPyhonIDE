namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.AddIns;
    using HelixPythonNamespace.UI.Controls;

    public partial class MainForm
    {
        /// <summary>
        /// <see cref="IAddInHost"/> over the shell, one instance per add-in (spec 001, §4.4.1).
        /// Nested so it reaches the shell's own members without widening them. It records every
        /// registration so <see cref="UndoAll"/> can take the add-in back out of the IDE.
        /// </summary>
        internal sealed class AddInHost : IAddInHost
        {
            internal sealed class MenuEntry
            {
                public string Caption;
                public string ToolTip;
                public Action Run;
                public Func<bool> Visible;
            }

            private readonly MainForm _form;
            private readonly string _id;
            private readonly List<KeyValuePair<string, TabPage>> _tabs = new List<KeyValuePair<string, TabPage>>();
            private readonly List<MenuEntry> _menu = new List<MenuEntry>();
            internal sealed class Opener
            {
                public string Suffix;
                public Func<string, bool> Claims;
                public Action<string> Open;
            }

            private readonly List<Opener> _openers = new List<Opener>();
            private readonly List<KeyValuePair<string, string>> _openFilters = new List<KeyValuePair<string, string>>();
            private bool _explorerCollapsed;
            private bool _undone;
            private readonly List<KeyValuePair<string, Action<string>>> _canvasKinds = new List<KeyValuePair<string, Action<string>>>();
            private readonly List<ProjectTreePanel.FileCommand> _commands = new List<ProjectTreePanel.FileCommand>();
            private readonly List<ProjectTreePanel.TypeCommand> _typeCommands = new List<ProjectTreePanel.TypeCommand>();
            private readonly List<KeyValuePair<string, Control>> _panes = new List<KeyValuePair<string, Control>>();

            internal AddInHost(MainForm form, string id)
            {
                _form = form;
                _id = id;
            }

            internal IEnumerable<MenuEntry> MenuEntries
            {
                get { return _menu; }
            }

            public IWin32Window Owner
            {
                get { return _form; }
            }

            public string SolutionDirectory
            {
                get
                {
                    return _form._currentSolution != null && !string.IsNullOrEmpty(_form._currentSolution.SolutionDirectory)
                        ? _form._currentSolution.SolutionDirectory : null;
                }
            }

            public event EventHandler SolutionChanged;

            internal void RaiseSolutionChanged()
            {
                EventHandler handlers = SolutionChanged;
                if (handlers == null) return;
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    try { handler(this, EventArgs.Empty); }
                    catch (Exception ex) { WriteOutputLine("Add-in " + _id + ": SolutionChanged failed: " + AddInLoader.Describe(ex)); }
                }
            }

            public bool IsNonInteractive
            {
                get { return IsNonInteractiveHost(); }
            }

            /// <summary>The predicate the Tools menu used for the Python designers.</summary>
            public bool IsPythonSolutionOpen
            {
                get { return _form._currentSolution != null && !_form.IsNativeWorkspaceActive(); }
            }

            public string ActiveDocumentPath
            {
                get { return _form.ActiveDocumentPath(); }
            }

            /// <summary>A snapshot: the add-in gets a list it may keep, not a live view of the model.</summary>
            public IReadOnlyList<HelixPythonNamespace.Core.Model.TypeModel> SolutionTypes()
            {
                return _form._currentSolution == null ? null : _form._currentSolution.GetAllTypes().ToList();
            }

            public IReadOnlyList<HelixPythonNamespace.Core.Model.ProjectModel> SolutionProjects()
            {
                return _form._currentSolution == null || _form._currentSolution.Projects == null
                    ? null : _form._currentSolution.Projects.ToList();
            }

            public System.Threading.Tasks.Task RunSolutionProjectAsync(HelixPythonNamespace.Core.Model.ProjectModel project)
            {
                return _form._buildRunCoordinator.RunProjectAsync(project);
            }

            public void RunSolutionProjectTests(HelixPythonNamespace.Core.Model.ProjectModel project)
            {
                if (project == null) return;
                _form.OnTreeRunTests(_form, new HelixPythonNamespace.UI.Controls.TreeOperationEventArgs(null, project));
            }

            // ---------------- tabs ----------------

            public void RegisterTab(string key, TabPage page, bool solutionScoped, AddInTabCallbacks callbacks)
            {
                if (string.IsNullOrEmpty(key) || page == null) return;
                if (_form._knownTabs.ContainsKey(key))
                {
                    WriteOutputLine("Add-in " + _id + ": tab key '" + key + "' is already registered; its tab was not added.");
                    return;
                }
                _form._panelCoordinator.RegisterTopLevelTab(key, page, solutionScoped, resultScoped: false);
                _form.RegisterAddInTab(page, callbacks);
                _tabs.Add(new KeyValuePair<string, TabPage>(key, page));
            }

            public void ShowTab(string key)
            {
                _form.ShowTab(key);
                // After ShowTab: the router hands the panes to whatever is SELECTED, and before the
                // tab is on the strip that is still the previous document.
                _form.SyncToolboxToActiveTab();
            }

            public void CloseTab(string key)
            {
                TabPage page;
                if (key != null && _form._knownTabs.TryGetValue(key, out page)) _form.CloseTab(page);
            }

            // ---------------- menus and commands ----------------

            public void AddMenuItem(string caption, Action run, Func<bool> visible)
            {
                AddMenuItem(caption, null, run, visible);
            }

            public void AddMenuItem(string caption, string toolTip, Action run, Func<bool> visible)
            {
                _menu.Add(new MenuEntry { Caption = caption ?? string.Empty, ToolTip = toolTip, Run = run, Visible = visible });
                _form.RebuildAddInMenu();
            }

            public void RegisterFileOpener(string fileSuffix, Action<string> open)
            {
                RegisterFileOpener(fileSuffix, null, open);
            }

            public void RegisterFileOpener(string fileSuffix, Func<string, bool> claims, Action<string> open)
            {
                if (string.IsNullOrEmpty(fileSuffix) || open == null) return;
                _openers.Add(new Opener { Suffix = fileSuffix, Claims = claims, Open = open });
            }

            internal bool TryOpenFile(string path)
            {
                foreach (Opener opener in _openers.ToArray())
                {
                    if (!path.EndsWith(opener.Suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    // A declining opener (a .pdb that is MSVC debug symbols, not a structure) passes
                    // the path on; one that cannot even decide is treated as declining.
                    if (opener.Claims != null)
                    {
                        bool claimed;
                        try { claimed = opener.Claims(path); }
                        catch (Exception ex)
                        {
                            WriteOutputLine("Add-in " + _id + ": could not check " + path + ": " + AddInLoader.Describe(ex));
                            claimed = false;
                        }
                        if (!claimed) continue;
                    }
                    // The add-in claimed the path, so it is answered here even when it fails: the
                    // text editor showing raw JSON is not what the user asked for.
                    try { opener.Open(path); }
                    catch (Exception ex) { WriteOutputLine("Add-in " + _id + ": could not open " + path + ": " + AddInLoader.Describe(ex)); }
                    return true;
                }
                return false;
            }

            public void AddOpenFileFilter(string description, string patterns)
            {
                if (string.IsNullOrEmpty(description) || string.IsNullOrEmpty(patterns)) return;
                // '|' separates the dialog's fields; one inside a row would shift every row after it.
                if (description.IndexOf('|') >= 0 || patterns.IndexOf('|') >= 0) return;
                _openFilters.Add(new KeyValuePair<string, string>(description, patterns));
            }

            internal IEnumerable<KeyValuePair<string, string>> OpenFileFilters
            {
                get { return _openFilters; }
            }

            public void AddRecentFile(string path)
            {
                // A removed add-in's leftover objects (its panel, its events) no longer reach the list.
                if (!_undone) _form.RememberRecentFile(path);
            }

            public bool RunJob(string title, Action<System.Threading.CancellationToken> work, Action onFinished, Action<Exception> onFailed)
            {
                if (work == null || _form._jobRunner == null) return false;
                string id = _id;
                return _form._jobRunner.Run<bool>(title ?? id, token => { work(token); return true; },
                    ok => Guard(onFinished, "finishing \"" + title + "\""),
                    error =>
                    {
                        if (onFailed == null) { WriteOutputLine("Add-in " + id + ": \"" + title + "\" failed: " + AddInLoader.Describe(error)); return; }
                        try { onFailed(error); }
                        catch (Exception ex) { WriteOutputLine("Add-in " + id + ": \"" + title + "\" failed: " + AddInLoader.Describe(ex)); }
                    });
            }

            private void Guard(Action action, string what)
            {
                if (action == null) return;
                try { action(); }
                catch (Exception ex) { WriteOutputLine("Add-in " + _id + ": " + what + " failed: " + AddInLoader.Describe(ex)); }
            }

            public void SetExplorerCollapsed(bool collapsed)
            {
                _explorerCollapsed = collapsed;
                _form.SetExplorerSteppedAside(collapsed);
            }

            public string DataDirectory
            {
                get
                {
                    string folder = Path.Combine(AddInLoader.DataRoot, _id);
                    Directory.CreateDirectory(folder);
                    return folder;
                }
            }

            public void RegisterCanvasKind(string kind, Action<string> open)
            {
                if (open == null) return;
                // A kind the codec refuses can never match a decoded header: say so now rather than
                // leave a row that silently never answers.
                if (!HelixPythonNamespace.Core.IO.CanvasFile.IsValidKind(kind))
                {
                    WriteOutputLine("Add-in " + _id + ": canvas kind \"" + kind + "\" was not registered: a kind is 1 to "
                        + HelixPythonNamespace.Core.IO.CanvasFile.MaxKindLength + " characters of a-z, 0-9 and '-'.");
                    return;
                }
                _canvasKinds.Add(new KeyValuePair<string, Action<string>>(kind, open));
            }

            /// <summary>True when this add-in registered <paramref name="kind"/>; the path is then
            /// answered here even when the open fails, as <see cref="TryOpenFile"/> does.</summary>
            internal bool TryOpenCanvas(string kind, string path)
            {
                foreach (KeyValuePair<string, Action<string>> row in _canvasKinds.ToArray())
                {
                    if (!string.Equals(row.Key, kind, StringComparison.Ordinal)) continue;
                    try { row.Value(path); }
                    catch (Exception ex) { WriteOutputLine("Add-in " + _id + ": could not open " + path + ": " + AddInLoader.Describe(ex)); }
                    return true;
                }
                return false;
            }

            public void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo, Action<string> run)
            {
                RegisterExplorerFileCommand(caption, appliesTo, null, run);
            }

            public void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo,
                Func<string, string> disabledReason, Action<string> run)
            {
                if (_form._projectTreePanel == null) return;
                string id = _id;
                var command = new ProjectTreePanel.FileCommand
                {
                    Caption = caption,
                    AppliesTo = appliesTo,
                    DisabledReason = disabledReason,
                    Run = path =>
                    {
                        try { if (run != null) run(path); }
                        catch (Exception ex) { WriteOutputLine("Add-in " + id + ": \"" + caption + "\" failed: " + AddInLoader.Describe(ex)); }
                    },
                };
                _form._projectTreePanel.AddFileCommand(command);
                _commands.Add(command);
            }

            public void RegisterExplorerTypeCommand(string caption, Func<HelixPythonNamespace.Core.Model.TypeModel, bool> appliesTo,
                Action<HelixPythonNamespace.Core.Model.TypeModel> run)
            {
                if (_form._projectTreePanel == null) return;
                string id = _id;
                var command = new ProjectTreePanel.TypeCommand
                {
                    Caption = caption,
                    AppliesTo = appliesTo,
                    Run = type =>
                    {
                        try { if (run != null) run(type); }
                        catch (Exception ex) { WriteOutputLine("Add-in " + id + ": \"" + caption + "\" failed: " + AddInLoader.Describe(ex)); }
                    },
                };
                _form._projectTreePanel.AddTypeCommand(command);
                _typeCommands.Add(command);
            }

            // ---------------- shell panes ----------------

            public bool ClaimToolPanel(string title, Control pane, DockSide side)
            {
                if (pane == null) return false;
                if (!_panes.Exists(p => p.Key == title && ReferenceEquals(p.Value, pane)))
                    _panes.Add(new KeyValuePair<string, Control>(title, pane));
                // Ask what the panel shows rather than trusting a flag (see ReclaimToolPanel).
                if (ReferenceEquals(_form.ToolPanelContentForTests(title), pane)) return false;
                _form.ClaimToolPanel(title, pane, side == DockSide.Right ? ToolDockSide.Right : ToolDockSide.Left);
                // A pane just handed over is brought to the front of its strip, as the built-in
                // designers do after docking their Toolbox. Only on a real claim: re-activating a
                // tab must not undo a selection the user made since.
                _form.SelectToolPanel(title);
                return true;
            }

            public void ReleaseToolPanel(string title, Control pane)
            {
                _panes.RemoveAll(p => p.Key == title && ReferenceEquals(p.Value, pane));
                _form.ReleaseToolPanel(title, pane);
            }

            public void RevealToolPanel(string title)
            {
                _form.ShowToolPanel(title);
                _form.SelectToolPanel(title);
                AutoHideDock dock = _form.SideOf(title) == ToolDockSide.Right ? _form._rightAutoHide : _form._leftAutoHide;
                if (dock != null && dock.Hidden) dock.Peek();
            }

            // ---------------- output, status, editor, interpreter ----------------

            public void WriteOutputLine(string line)
            {
                _form.AppendBuildOutputLine(line);
            }

            public void ShowOutput()
            {
                _form.ShowTab("output");
            }

            public void SetStatus(string text)
            {
                if (_form._statusLabel != null) _form._statusLabel.Text = text ?? string.Empty;
            }

            public void SetStatus(string text, System.Drawing.Color color)
            {
                SetStatus(text);
                if (_form._statusLabel != null) _form._statusLabel.ForeColor = color;
            }

            public void OpenInEditor(string path, int line)
            {
                _form.OpenInEditor(path, line > 0 ? line : -1);
            }

            public string ReadSource(string path)
            {
                return _form.ReadPythonSource(path);
            }

            public void WriteSource(string path, string text)
            {
                _form.WritePythonSource(path, text);
            }

            /// <summary>The interpreter Build uses: the configured one when it exists, else the search.</summary>
            public string ResolvePythonInterpreter()
            {
                UserSettings settings = _form.AddInUserSettings;
                string configured = settings != null ? settings.SelectedPythonExecutable : null;
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
                return _form.FindPythonInterpreter();
            }

            // ---------------- settings ----------------

            public string GetSetting(string name)
            {
                Dictionary<string, string> values = Values(false);
                string value;
                return values != null && name != null && values.TryGetValue(name, out value) ? value : null;
            }

            public void SetSetting(string name, string value)
            {
                if (name == null) return;
                Dictionary<string, string> values = Values(true);
                if (values == null) return;
                if (value == null) values.Remove(name);
                else values[name] = value;
                _form.SaveAddInSettings();
            }

            private Dictionary<string, string> Values(bool create)
            {
                UserSettings settings = _form.AddInUserSettings;
                if (settings == null) return null;
                if (settings.AddInSettings == null)
                {
                    if (!create) return null;
                    settings.AddInSettings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                }
                Dictionary<string, string> values;
                if (settings.AddInSettings.TryGetValue(_id, out values) && values != null) return values;
                if (!create) return null;
                return settings.AddInSettings[_id] = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            // ---------------- undo ----------------

            /// <summary>Takes back everything this add-in registered. Each step isolated.</summary>
            internal void UndoAll()
            {
                foreach (KeyValuePair<string, TabPage> tab in _tabs.ToArray())
                {
                    try { _form.UnregisterAddInTab(tab.Key, tab.Value); }
                    catch (Exception ex) { WriteOutputLine("Add-in " + _id + ": closing tab '" + tab.Key + "' failed: " + AddInLoader.Describe(ex)); }
                }
                _tabs.Clear();

                foreach (KeyValuePair<string, Control> pane in _panes.ToArray())
                {
                    try { _form.ReleaseToolPanel(pane.Key, pane.Value); }
                    catch (Exception) { }
                }
                _panes.Clear();

                foreach (ProjectTreePanel.FileCommand command in _commands)
                    if (_form._projectTreePanel != null) _form._projectTreePanel.RemoveFileCommand(command);
                _commands.Clear();

                foreach (ProjectTreePanel.TypeCommand command in _typeCommands)
                    if (_form._projectTreePanel != null) _form._projectTreePanel.RemoveTypeCommand(command);
                _typeCommands.Clear();

                _openers.Clear();
                _openFilters.Clear();
                _canvasKinds.Clear();
                // An explorer this add-in put aside comes back with it.
                if (_explorerCollapsed) SetExplorerCollapsed(false);
                _menu.Clear();
                _undone = true;
                _form.RebuildAddInMenu();
            }
        }
    }
}
