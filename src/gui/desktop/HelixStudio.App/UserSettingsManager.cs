namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;

    // Loads/saves the single user-settings JSON blob under %APPDATA%\HelixStudio and
    // exposes the deserialized object. All failures are non-fatal: settings are a
    // convenience, so any error falls back to defaults and is logged, never thrown.
    public sealed class UserSettingsManager
    {
        private const string AppDataFolder = "HelixPython";
        private const string SettingsFileName = "user-settings.json";
        private UserSettings _settings;

        // Start from defaults, then overlay whatever was persisted last session.
        public UserSettingsManager()
        {
            _settings = new UserSettings();
            Load();
        }

        public UserSettings Settings
        {
            get { return _settings; }
        }

        public void Load()
        {
            try
            {
                string filePath = GetFilePath();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    UserSettings loaded = JsonConvert.DeserializeObject<UserSettings>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable settings: discard and continue with defaults.
                _settings = new UserSettings();
                TryLogError("Load", ex);
            }
        }

        public void Save()
        {
            try
            {
                string filePath = GetFilePath();
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                // Settings save failure is non-fatal but user should be able to diagnose via the log.
                TryLogError("Save", ex);
            }
        }

        // Append a timestamped diagnostic line to errors.log. Wrapped in its own try so
        // a logging failure can never propagate out of Load/Save.
        private static void TryLogError(string op, Exception ex)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, AppDataFolder);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string logPath = Path.Combine(dir, "errors.log");
                File.AppendAllText(logPath,
                    string.Format("[{0:u}] UserSettingsManager.{1}: {2}\r\n",
                        DateTime.UtcNow, op, ex));
            }
            catch
            {
                // Log-write failure is terminal; nothing more we can do.
            }
        }

        private static string GetFilePath()
        {
            // Expanded, so the override can be written portably -- `%TEMP%\HelixPython-tests`
            // in a runsettings file rather than one machine's absolute path. Without this the
            // literal "%TEMP%" became a directory of that name next to the test host.
            string isolatedState = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            if (!string.IsNullOrWhiteSpace(isolatedState))
                return Path.Combine(Environment.ExpandEnvironmentVariables(isolatedState), SettingsFileName);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppDataFolder, SettingsFileName);
        }
    }

    /// <summary>
    /// Persisted docking state for a solution's workspace layout.
    /// </summary>
    public sealed class DockingLayoutState
    {
        public DockingLayoutState()
        {
            AnalysisResultsDocked = true;
            BuildResultsDocked = true;
            ActiveDesignerTab = "All";
        }

        public bool AnalysisResultsDocked { get; set; }
        public bool BuildResultsDocked { get; set; }

        // Floating window bounds (only meaningful when undocked)
        public int AnalysisResultsX { get; set; }
        public int AnalysisResultsY { get; set; }
        public int AnalysisResultsWidth { get; set; }
        public int AnalysisResultsHeight { get; set; }

        public int BuildResultsX { get; set; }
        public int BuildResultsY { get; set; }
        public int BuildResultsWidth { get; set; }
        public int BuildResultsHeight { get; set; }

        /// <summary>Name of the active designer sub-tab (All, Interfaces, Abstract Classes, General Classes).</summary>
        public string ActiveDesignerTab { get; set; }

        /// <summary>Name of the active top-level tab.</summary>
        public string ActiveTopLevelTab { get; set; }
    }

    // The full serialized settings object. The constructor seeds first-run defaults for
    // the value-type fields; reference-type members (dictionaries) are initialized at
    // their declarations so they are non-null even after JSON round-trips omit them.
    public sealed class UserSettings
    {
        public UserSettings()
        {
            LeftSplitterDistance = 200;
            RightDockWidth = 320;
            RightSplitterDistance = 400;
            MainSplitterDistance = 900;
            WindowWidth = 1280;
            WindowHeight = 800;
            WindowMaximized = false;
            Theme = "Blue";   // Luna accent theme: Blue (default) / Green / Crimson
            NativeCMakeToolchain = "Auto";
            NativeCxxStandard = "20";
            NativeCudaStandard = "20";
            GridSpacing = 10f;
            SnapToGrid = true;
            ActiveTool = "Pan";
            ToolsPaletteVisible = true;
            ToolsPaletteX = -1;
            ToolsPaletteY = -1;
            ShowWelcomeOnStartup = true;
            MinimapVisible = false;
            MinimapX = -1;
            MinimapY = -1;
            SolutionProjectVisibility = new Dictionary<string, List<string>>();
            SolutionDockingLayouts = new Dictionary<string, DockingLayoutState>();
            LastImportSourceDirectory = string.Empty;
            LastImportSolutionName = string.Empty;
            LastImportDestinationDirectory = string.Empty;
            LastNewProjectName = string.Empty;
            LastNewProjectLocation = string.Empty;
            LastNewSolutionName = string.Empty;
            LastNewSolutionLocation = string.Empty;
        }

        public int LeftSplitterDistance { get; set; }

        /// <summary>
        /// How wide the right-hand tool dock (Properties) was left.
        /// </summary>
        ///
        /// <remarks>
        /// The left dock has persisted its width since there was a left dock; this side never did,
        /// so a width the user set survived until they quit and no longer. Reported twice from the
        /// running IDE as the pane "becoming bigger again" -- the first time because a re-layout was
        /// overwriting it mid-session, the second time because a relaunch was. Not the same bug, the
        /// same complaint, and nobody can be expected to tell a width that was DISCARDED from one
        /// that was never REMEMBERED.
        ///
        /// NOT <see cref="RightSplitterDistance"/>, which is dead: nothing reads or writes it, and a
        /// splitter DISTANCE is the document's width rather than the dock's, so restoring one as the
        /// other would be wrong at every window size but the one it was saved at.
        /// </remarks>
        public int RightDockWidth { get; set; }

        public int RightSplitterDistance { get; set; }
        public int MainSplitterDistance { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }

        /// <summary>
        /// Which toolbox categories each designer had folded away, keyed by designer.
        ///
        /// <para>A map rather than one property per canvas, so a fourth designer needs no new
        /// setting and cannot be forgotten here. Names rather than indices: a palette is rebuilt
        /// whenever its catalogue changes, and an index would restore the fold onto whatever
        /// category had moved into that slot.</para>
        ///
        /// <para>Null and missing keys both mean "nothing folded", which is what a first run and a
        /// settings file written before this existed both look like.</para>
        /// </summary>
        public Dictionary<string, List<string>> ToolboxCollapsed { get; set; }

        /// <summary>
        /// Each add-in's own settings, keyed by the add-in's <c>Id</c>, then by setting name. The
        /// host reads and writes these for the add-in (<c>IAddInHost.GetSetting/SetSetting</c>), so
        /// an add-in never touches this file itself. Default empty at the declaration (Chapter 3,
        /// §3.6.1): a settings file written before add-ins existed has no such key.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> AddInSettings { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>Add-in ids the user removed. The loader skips them in both add-in roots.</summary>
        public List<string> RemovedAddIns { get; set; } = new List<string>();

        /// <summary>
        /// User-installed add-in folders to delete at the next start, before loading. A loaded
        /// add-in's DLL is locked by Windows, so Remove cannot delete its folder at once.
        /// </summary>
        public List<string> PendingAddInDeletes { get; set; } = new List<string>();

        /// <summary>Luna accent theme name: "Blue" (default), "Green", or "Crimson".</summary>
        public string Theme { get; set; }

        public string DefaultSdkVersion { get; set; }

        /// <summary>Import Python Directory wizard: last-used source directory, pre-filled next time.</summary>
        public string LastImportSourceDirectory { get; set; }

        /// <summary>Import Python Directory wizard: last-used solution name, pre-filled next time.</summary>
        public string LastImportSolutionName { get; set; }

        /// <summary>Import Python Directory wizard: last-used destination directory — used to seed the
        /// Browse dialog next time, not to pre-fill the field (a fresh auto-suggestion is preferred there).</summary>
        public string LastImportDestinationDirectory { get; set; }

        /// <summary>New Project wizard: last-used project name, pre-filled next time (auto-incremented
        /// when that folder already exists in the remembered location).</summary>
        public string LastNewProjectName { get; set; }

        /// <summary>New Project wizard: last-used location directory, pre-filled next time.</summary>
        public string LastNewProjectLocation { get; set; }

        /// <summary>Standalone New Solution wizard state. This is intentionally separate from
        /// Add Project, whose fixed location is the currently open solution directory.</summary>
        public string LastNewSolutionName { get; set; }
        public string LastNewSolutionLocation { get; set; }

        /// <summary>Native CMake compiler choice: Auto, MSVC, or GCC.</summary>
        public string NativeCMakeToolchain { get; set; }

        /// <summary>Optional user-selected g++.exe; blank means automatic discovery.</summary>
        public string NativeGccCppCompilerPath { get; set; }

        /// <summary>Optional user-selected Visual Studio x64 environment script.</summary>
        public string NativeMsvcEnvironmentScriptPath { get; set; }

        /// <summary>Requested C++ language dialect: 98, 11, 14, 17, 20, 23, or 26.</summary>
        public string NativeCxxStandard { get; set; }

        /// <summary>Requested CUDA C++ dialect: 03, 11, 14, 17, 20, 23, or 26.</summary>
        public string NativeCudaStandard { get; set; }

        /// <summary>Canvas grid pixel spacing (snap distance). Minimum 2, default 10.</summary>
        public float GridSpacing { get; set; }

        /// <summary>Whether dragging snaps nodes to the grid.</summary>
        public bool SnapToGrid { get; set; }

        /// <summary>View &gt; Word Wrap: wrap long lines in the editor and Output pane.</summary>
        public bool WordWrap { get; set; }

        /// <summary>Active canvas tool name: "Select" or "Pan".</summary>
        public string ActiveTool { get; set; }

        /// <summary>Whether the floating tools palette is visible.</summary>
        public bool ToolsPaletteVisible { get; set; }

        /// <summary>Last-known X screen position of the tools palette. -1 = center on parent.</summary>
        public int ToolsPaletteX { get; set; }

        /// <summary>Last-known Y screen position of the tools palette. -1 = center on parent.</summary>
        public int ToolsPaletteY { get; set; }

        /// <summary>Show the welcome panel on startup when no solution is loaded.</summary>
        public bool ShowWelcomeOnStartup { get; set; }

        /// <summary>Editor font family (default "Consolas").</summary>
        public string EditorFontName { get; set; } = "Consolas";

        /// <summary>Editor font size in points (default 11).</summary>
        public float EditorFontSize { get; set; } = 11f;

        /// <summary>Override path to pdebug.exe. When empty, HelixPythonNamespace.Debugger uses the sibling binary.</summary>
        public string DebuggerExecutablePath { get; set; }

        /// <summary>
        /// Custom keybindings: binding-name → key-chord string (e.g. "Ctrl+B").
        /// Only overrides are stored; defaults are supplied by <see cref="HelixPythonNamespace.App.Services.KeybindingManager"/>.
        /// </summary>
        public Dictionary<string, string> KeyBindings { get; set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Which platform-flavoured chord preset (Windows / Mac-style / Linux-style) the
        /// user last applied. Persists across sessions so Tools → Keyboard remembers.
        /// </summary>
        public HelixPythonNamespace.App.Services.KeybindingPreset KeybindingPreset { get; set; }
            = HelixPythonNamespace.App.Services.KeybindingPreset.Windows;

        /// <summary>
        /// Path to the user-selected Python interpreter (set via Run → Select Python Interpreter).
        /// Null/empty means "use the auto-discovered default".
        /// </summary>
        public string SelectedPythonExecutable { get; set; }

        /// <summary>When true, the navigation tree is detached into a floating window.</summary>
        public bool TreeFloating { get; set; }

        /// <summary>
        /// Files open in the Editor tab for each solution, keyed by absolute pyproject.toml path.
        /// Restored automatically when that solution re-opens.
        /// </summary>
        public Dictionary<string, List<string>> LastOpenEditorFiles { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public List<string> GetLastOpenEditorFiles(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || LastOpenEditorFiles == null) return new List<string>();
            return LastOpenEditorFiles.TryGetValue(solutionPath, out var list) && list != null
                ? new List<string>(list)
                : new List<string>();
        }

        public void SetLastOpenEditorFiles(string solutionPath, IEnumerable<string> files)
        {
            if (string.IsNullOrEmpty(solutionPath)) return;
            if (LastOpenEditorFiles == null)
                LastOpenEditorFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            LastOpenEditorFiles[solutionPath] = files != null ? new List<string>(files) : new List<string>();
        }

        /// <summary>
        /// Expanded Solution Explorer node paths per solution (TreeNode.FullPath strings),
        /// restored when the solution re-opens so the user's place in the tree survives a
        /// restart instead of collapsing back to the roots.
        /// </summary>
        public Dictionary<string, List<string>> ExpandedTreePaths { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public List<string> GetExpandedTreePaths(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || ExpandedTreePaths == null) return new List<string>();
            return ExpandedTreePaths.TryGetValue(solutionPath, out var list) && list != null
                ? new List<string>(list)
                : new List<string>();
        }

        public void SetExpandedTreePaths(string solutionPath, IEnumerable<string> paths)
        {
            if (string.IsNullOrEmpty(solutionPath)) return;
            if (ExpandedTreePaths == null)
                ExpandedTreePaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ExpandedTreePaths[solutionPath] = paths != null ? new List<string>(paths) : new List<string>();
        }

        /// <summary>
        /// Visibility of each top-level tab in MainForm, keyed by stable name
        /// (e.g. "designer", "sourceViewer", "editor", "debug", "analysis", "buildResults").
        /// Missing keys default to <c>true</c>.
        /// </summary>
        public Dictionary<string, bool> VisibleTopLevelTabs { get; set; }
            = new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Minimap window visibility.</summary>
        public bool MinimapVisible { get; set; }

        /// <summary>
        /// Whether the AI Terminal is on screen. True by default, which is what a first run gets.
        ///
        /// This records the user's INTENT, not a transient state: closing the terminal is a
        /// deliberate act that must survive a restart, in the same way the tools palette and the
        /// minimap already do. Initialised at its declaration rather than in the constructor so a
        /// settings file written before this property existed — where the key is simply absent —
        /// deserialises to "showing" rather than to false, which would silently hide the terminal
        /// from every existing user on upgrade.
        /// </summary>
        public bool AiTerminalVisible { get; set; } = true;

        /// <summary>
        /// SRS solution_explorer_rules §8.2: edit-time background analysis (the CUDA
        /// virtual compiler + non-Python linter/analyzer passes on typing pauses)
        /// runs only when the user explicitly enables it. Default false.
        /// </summary>
        public bool EditorBackgroundAnalysis { get; set; }

        /// <summary>
        /// §3.2/§3.4: true renders virtual C#-style braces on Python blocks; false
        /// renders the Bézier scope connectors instead. Default true (braces).
        /// </summary>
        public bool EditorVirtualBraces { get; set; } = true;

        /// <summary>§3.3: virtual inferred-type inlay markers on assignments. Default true.</summary>
        public bool EditorTypeMarkers { get; set; } = true;

        /// <summary>
        /// Whether the IDE accepts procedural (non-object-oriented) Python source.
        ///
        /// False (the default) keeps rule PY8002 in force: user-written code must live in classes,
        /// every module-level <c>def</c> other than the PSM-mandated <c>def main()</c> is an error,
        /// and Run/Debug refuse to launch while any remain. True relaxes the rule to nothing —
        /// PY8002 is neither reported by the virtual compiler nor treated as a launch blocker —
        /// for users opening existing procedural code they are not in a position to restructure.
        /// Workspace membership (PY8001) is unaffected: the IDE still refuses to execute source
        /// it cannot show, compile-check or breakpoint.
        /// </summary>
        public bool AllowNonObjectOrientedCode { get; set; }


        /// <summary>
        /// API key for the AI Terminal's Anthropic provider. Empty means "fall back to the
        /// ANTHROPIC_API_KEY environment variable or an `ant auth login` profile", which is the
        /// preferred setup — a key here is stored in plain text in user-settings.json, so it is
        /// offered as a convenience for users without a configured environment, not as the
        /// recommended path.
        /// </summary>
        public string AnthropicApiKey { get; set; }

        /// <summary>
        /// API key for the AI Terminal's OpenAI provider. Same plain-text caveat as
        /// <see cref="AnthropicApiKey"/>; prefer the OPENAI_API_KEY environment variable.
        /// </summary>
        public string OpenAiApiKey { get; set; }

        /// <summary>Last-known X/Y screen position of the minimap window.</summary>
        public int MinimapX { get; set; }
        public int MinimapY { get; set; }

        /// <summary>
        /// Maps solution file path to list of visible project names.
        /// </summary>
        public Dictionary<string, List<string>> SolutionProjectVisibility { get; set; }

        /// <summary>
        /// Maps solution file path to the user's preferred top-level tab order.
        /// Values are tab keys from <c>MainForm._knownTabs</c>
        /// (e.g. "designer", "editor", "debug", "analysis", "buildResults", "problems").
        /// Keys missing from the saved list fall back to their registration order.
        /// </summary>
        public Dictionary<string, List<string>> SolutionTopLevelTabOrder { get; set; }

        /// <summary>Return the saved top-level tab order for a solution, or null when none.</summary>
        public List<string> GetTopLevelTabOrder(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || SolutionTopLevelTabOrder == null) return null;
            List<string> order;
            return SolutionTopLevelTabOrder.TryGetValue(solutionPath, out order) ? order : null;
        }

        /// <summary>Persist the top-level tab order for a solution.</summary>
        public void SetTopLevelTabOrder(string solutionPath, List<string> order)
        {
            if (string.IsNullOrEmpty(solutionPath)) return;
            if (SolutionTopLevelTabOrder == null)
            {
                SolutionTopLevelTabOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
            SolutionTopLevelTabOrder[solutionPath] = order != null ? new List<string>(order) : new List<string>();
        }

        /// <summary>
        /// Maps solution file path to docking layout state.
        /// </summary>
        public Dictionary<string, DockingLayoutState> SolutionDockingLayouts { get; set; }

        /// <summary>
        /// Maps solution file path to the project name chosen as the Run-menu
        /// startup target. Consumed by <c>Run Startup App</c> (Ctrl+F5) to
        /// invoke <c>python -m &lt;name&gt;</c>.
        /// </summary>
        public Dictionary<string, string> SolutionStartupProject { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Return the startup project name for the given solution, or null.</summary>
        public string GetStartupProject(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || SolutionStartupProject == null) return null;
            string name;
            return SolutionStartupProject.TryGetValue(solutionPath, out name) ? name : null;
        }

        /// <summary>Persist a startup project selection for a solution.</summary>
        public void SetStartupProject(string solutionPath, string projectName)
        {
            if (string.IsNullOrEmpty(solutionPath)) return;
            if (SolutionStartupProject == null)
            {
                SolutionStartupProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            SolutionStartupProject[solutionPath] = projectName ?? string.Empty;
        }

        /// <summary>
        /// Get visible projects for a solution.
        /// </summary>
        public List<string> GetVisibleProjects(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || SolutionProjectVisibility == null)
            {
                return new List<string>();
            }

            List<string> projects;
            if (SolutionProjectVisibility.TryGetValue(solutionPath, out projects))
            {
                return projects ?? new List<string>();
            }
            return new List<string>();
        }

        /// <summary>
        /// Set visible projects for a solution.
        /// </summary>
        public void SetVisibleProjects(string solutionPath, List<string> projectNames)
        {
            if (string.IsNullOrEmpty(solutionPath))
            {
                return;
            }

            if (SolutionProjectVisibility == null)
            {
                SolutionProjectVisibility = new Dictionary<string, List<string>>();
            }

            SolutionProjectVisibility[solutionPath] = projectNames ?? new List<string>();
        }

        /// <summary>
        /// Get docking layout for a solution.
        /// </summary>
        public DockingLayoutState GetDockingLayout(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || SolutionDockingLayouts == null)
            {
                return new DockingLayoutState();
            }

            DockingLayoutState layout;
            if (SolutionDockingLayouts.TryGetValue(solutionPath, out layout))
            {
                return layout ?? new DockingLayoutState();
            }
            return new DockingLayoutState();
        }

        /// <summary>
        /// Set docking layout for a solution.
        /// </summary>
        public void SetDockingLayout(string solutionPath, DockingLayoutState layout)
        {
            if (string.IsNullOrEmpty(solutionPath))
            {
                return;
            }

            if (SolutionDockingLayouts == null)
            {
                SolutionDockingLayouts = new Dictionary<string, DockingLayoutState>();
            }

            SolutionDockingLayouts[solutionPath] = layout ?? new DockingLayoutState();
        }
    }
}
