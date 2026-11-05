namespace HelixPythonNamespace.AddIns
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;

    /// <summary>What the IDE shell offers an add-in. The shell implements it; an add-in only calls it.</summary>
    public interface IAddInHost
    {
        IWin32Window Owner { get; }

        /// <summary>The open solution's directory, or null when no solution is open.</summary>
        string SolutionDirectory { get; }

        /// <summary>Raised when a solution is opened or closed, or a new workspace starts.</summary>
        event EventHandler SolutionChanged;

        /// <summary>True when the IDE runs without a user (tests, automation): show no modal dialog.</summary>
        bool IsNonInteractive { get; }

        /// <summary>True when a Python solution is open: a solution is loaded and it is not a
        /// native (CMake) workspace. The Python-only designers show their menu items on this.</summary>
        bool IsPythonSolutionOpen { get; }

        /// <summary>The path of the document the user is working on (the editor document on
        /// screen, else the selected one), or null when there is none.</summary>
        string ActiveDocumentPath { get; }

        /// <summary>Every type the open solution defines, as the parser read it; null when no
        /// solution is open (an open solution with no types gives an empty list).</summary>
        IReadOnlyList<TypeModel> SolutionTypes();

        /// <summary>A snapshot of projects in the open solution, or null when none is loaded.</summary>
        IReadOnlyList<ProjectModel> SolutionProjects();

        /// <summary>Launches a solution project through the IDE's normal policy-aware Run path.</summary>
        System.Threading.Tasks.Task RunSolutionProjectAsync(ProjectModel project);

        /// <summary>Runs tests for a solution project through the IDE's normal language test runner.</summary>
        void RunSolutionProjectTests(ProjectModel project);

        // ---- tabs: keys keep today's values so saved settings survive the move ----

        void RegisterTab(string key, TabPage page, bool solutionScoped, AddInTabCallbacks callbacks);
        void ShowTab(string key);
        void CloseTab(string key);

        // ---- menus and commands ----

        /// <summary>Adds an item under Add-in ▸ (this add-in's DisplayName). <paramref name="visible"/>
        /// is checked each time the menu opens; null means always visible.</summary>
        void AddMenuItem(string caption, Action run, Func<bool> visible);

        /// <summary>As above, with a tooltip shown when the pointer rests on the item.</summary>
        void AddMenuItem(string caption, string toolTip, Action run, Func<bool> visible);
        void RegisterFileOpener(string fileSuffix, Action<string> open);

        /// <summary>As above, but the add-in may decline a path with that suffix:
        /// <paramref name="claims"/> is asked first, and a path it refuses (or throws on) goes on
        /// to the next opener and then to the shell. For a suffix two formats share, such as
        /// <c>.pdb</c> (a protein structure, or MSVC debug symbols).</summary>
        void RegisterFileOpener(string fileSuffix, Func<string, bool> claims, Action<string> open);

        /// <summary>A row in File ▸ Open's file-type list, for example
        /// <c>("Protein Data Bank structures (*.pdb;*.ent)", "*.pdb;*.ent")</c>. Its patterns also
        /// join the dialog's first, "everything this IDE opens" row.</summary>
        void AddOpenFileFilter(string description, string patterns);

        /// <summary>Puts an existing file at the top of File ▸ Recent and the Welcome page's list.
        /// A path that is empty or not a file on disk is ignored.</summary>
        void AddRecentFile(string path);

        /// <summary>
        /// Runs <paramref name="work"/> on a background thread as the IDE's foreground job: status
        /// text, the marquee and the Cancel button. <paramref name="onFinished"/> or
        /// <paramref name="onFailed"/> runs afterwards on the UI thread (neither after a Cancel).
        /// Returns false, and runs nothing, when another job is already running.
        /// </summary>
        bool RunJob(string title, Action<System.Threading.CancellationToken> work, Action onFinished, Action<Exception> onFailed);

        /// <summary>Opens a <c>*.canvas</c> file whose header names <paramref name="kind"/> (for
        /// example <c>"cg-mc"</c>). The shell reads and checks the whole file first, so
        /// <paramref name="open"/> is only handed a complete, undamaged file of that kind.
        /// A kind is 1 to 32 characters of a-z, 0-9 and '-'; any other kind is not registered and
        /// the shell writes one Output line saying so.</summary>
        void RegisterCanvasKind(string kind, Action<string> open);

        void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo, Action<string> run);

        /// <summary>As above, but the command can be shown disabled: where <paramref name="disabledReason"/>
        /// returns text for a file, the item is greyed out and the text is its tooltip, so the user
        /// learns what the command wants instead of wondering why it is missing.</summary>
        void RegisterExplorerFileCommand(string caption, Func<string, bool> appliesTo,
            Func<string, string> disabledReason, Action<string> run);

        /// <summary>A command on the Solution Explorer's type nodes (a class, and the other nodes
        /// that carry the "Open in ..." verbs). It is shown on each of them and enabled where
        /// <paramref name="appliesTo"/> says so for the node's type. A file node is handed over as
        /// a stand-in type: its <see cref="TypeModel.Name"/> is the file name without extension,
        /// its <see cref="TypeModel.SourceFilePath"/> is the file, and it has no base types.</summary>
        void RegisterExplorerTypeCommand(string caption, Func<TypeModel, bool> appliesTo, Action<TypeModel> run);

        // ---- shell panes (Toolbox left, Properties right) ----

        bool ClaimToolPanel(string title, Control pane, DockSide side);
        void ReleaseToolPanel(string title, Control pane);

        /// <summary>Show and select the pane, and peek it out if its side is auto-hidden.</summary>
        void RevealToolPanel(string title);

        /// <summary>
        /// True: the Solution Explorer steps aside (its column collapses) so a tab can have the
        /// width; false: it comes back as it was (width, and a tree the user had already
        /// collapsed stays collapsed). A floating explorer is left alone. Never saved as a user
        /// preference. Call it from the tab's Activated and Deactivated callbacks.
        /// </summary>
        void SetExplorerCollapsed(bool collapsed);

        // ---- output, status, editor, interpreter ----

        void WriteOutputLine(string line);
        void ShowOutput();
        void SetStatus(string text);

        /// <summary>As above, in <paramref name="color"/> (for example a warning in red).</summary>
        void SetStatus(string text, System.Drawing.Color color);

        /// <summary>Open a file in the editor; <paramref name="line"/> &lt;= 0 means no jump.</summary>
        void OpenInEditor(string path, int line);

        /// <summary>The editor buffer when the file is open, otherwise the file on disk.</summary>
        string ReadSource(string path);

        /// <summary>One undoable edit when the file is open in the editor, otherwise a disk write.</summary>
        void WriteSource(string path, string text);

        string ResolvePythonInterpreter();

        // ---- per-add-in persisted settings (UserSettings.AddInSettings[Id]) ----

        string GetSetting(string name);
        void SetSetting(string name, string value);

        /// <summary>This add-in's own folder for data it keeps between sessions (a download cache,
        /// for one): <c>%LOCALAPPDATA%\HelixPython\addin-data\&lt;Id&gt;</c>, or
        /// <c>&lt;HELIXSTUDIO_STATE_DIR&gt;\addin-data\&lt;Id&gt;</c> when the state directory is
        /// redirected. Created on first use.</summary>
        string DataDirectory { get; }
    }
}
