namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.ImportEngine.Analysis;
    using HelixPythonNamespace.ImportEngine.Execution;
    using HelixPythonNamespace.ImportEngine.Planning;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Python;
    using HelixPythonNamespace.Workspace.Services;
    using HelixPythonNamespace.App;

    /// <summary>
    /// Import Python Directory wizard (SRS solution_explorer_rules §4) — a
    /// step-by-step view over the HelixPythonNamespace.ImportEngine: Page 1 picks the
    /// source and target format, Page 2 reviews the read-only analysis (packages,
    /// venvs, artifacts, tests, docs, orphans, duplicates, entry points), Page 3
    /// decides per-package disposition / rename / startup, Page 4 chooses the
    /// workspace layout and entry-point resolution, and the Summary runs the
    /// executor. Follows the NewProjectWizardForm layout conventions.
    /// </summary>
    public sealed class ImportWizardForm : LunaForm
    {
        private int _page;
        private ImportAnalysis _analysis;
        private ImportPlan _plan;
        private ImportExecutionResult _result;
        private readonly UserSettingsManager _settingsManager;

        // Page 1 — source
        private readonly Panel _p1;
        private readonly TextBox _source;
        private readonly TextBox _destination;
        private readonly TextBox _name;
        private readonly RadioButton _tomlRadio, _vsRadio, _bothRadio;
        private readonly CheckBox _overwrite;
        private readonly Label _p1Validation;
        // Last value UpdateAutoDestination() computed — lets us tell an auto-suggested
        // destination apart from one the user typed/browsed to themselves, so typing in
        // Source/Solution name never clobbers a manual destination choice.
        private string _autoDestination;
        // Page 2 — analysis review
        private readonly Panel _p2;
        private readonly Label _classification;
        private readonly ListView _findings;
        // Page 3 — projects
        private readonly Panel _p3;
        private readonly DataGridView _grid;
        private readonly Label _p3Validation;
        // Page 4 — layout
        private readonly Panel _p4;
        private readonly RadioButton _inPlaceRadio, _srcLayoutRadio;
        private readonly CheckBox _resolveEntries;
        private readonly CheckBox _generateCudaScaffold;
        private readonly Label _entryInfo;
        // Page 5 — summary
        private readonly Panel _p5;
        private readonly RichTextBox _summary;
        // Nav
        private readonly Button _back, _next, _import, _cancel;
        private readonly Label _header;
        private readonly Panel _content;

        public ImportWizardForm(string initialSourcePath = null, UserSettingsManager settingsManager = null)
        {
            _settingsManager = settingsManager;
            Text = "Import Python Directory";
            Size = new Size(860, 600);
            MinimumSize = new Size(720, 500);
            StartPosition = FormStartPosition.CenterParent;
            Font = LunaFonts.Ui;

            _header = new Label
            {
                Dock = DockStyle.Top, Height = 40, Text = "  Import Python Directory",
                Font = LunaFonts.Sized(4f), TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White
            };

            var nav = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10) };
            _cancel = MakeButton("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            _import = MakeButton("Import", (s, e) => DoImport());
            _next = MakeButton("Next >", (s, e) => Advance());
            _back = MakeButton("< Back", (s, e) => GoTo(_page - 1));
            _cancel.Dock = _import.Dock = _next.Dock = _back.Dock = DockStyle.Right;
            // Back → Next → Import → Cancel so Dock=Right renders Wizard-97 order.
            nav.Controls.Add(_back); nav.Controls.Add(_next); nav.Controls.Add(_import); nav.Controls.Add(_cancel);
            CancelButton = _cancel;

            _p1 = BuildSourcePage(out _source, out _destination, out _name, out _tomlRadio, out _vsRadio, out _bothRadio,
                                  out _overwrite, out _p1Validation);
            _p2 = BuildAnalysisPage(out _classification, out _findings);
            _p3 = BuildProjectsPage(out _grid, out _p3Validation);
            _p4 = BuildLayoutPage(out _inPlaceRadio, out _srcLayoutRadio, out _resolveEntries, out _generateCudaScaffold, out _entryInfo);
            _p5 = BuildSummaryPage(out _summary);

            _content = new Panel { Dock = DockStyle.Fill };
            _content.Controls.Add(_p1); _content.Controls.Add(_p2); _content.Controls.Add(_p3);
            _content.Controls.Add(_p4); _content.Controls.Add(_p5);
            Controls.Add(_content); Controls.Add(nav); Controls.Add(_header);

            if (!string.IsNullOrEmpty(initialSourcePath))
            {
                _source.Text = initialSourcePath;
                _name.Text = DeriveName(initialSourcePath);
            }
            else if (_settingsManager != null)
            {
                // No caller-supplied path (opened via File > Import Python Directory with
                // nothing pre-selected, e.g. drag-drop) — pre-fill from the last import so
                // re-running the wizard on the same project doesn't mean re-typing everything.
                _source.Text = _settingsManager.Settings.LastImportSourceDirectory ?? string.Empty;
                _name.Text = _settingsManager.Settings.LastImportSolutionName ?? string.Empty;
                // Destination is deliberately NOT restored from settings: the remembered value
                // is the directory the previous import actually wrote into, so it already exists
                // and is non-empty — restoring it verbatim would make the wizard immediately
                // fail validation next time it's opened. UpdateAutoDestination() below computes
                // a fresh, guaranteed-free suggestion instead.
            }
            UpdateAutoDestination();
            _source.TextChanged += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = DeriveName(_source.Text);
                UpdateAutoDestination();
                ValidateSourcePage();
            };
            _name.TextChanged += (s, e) =>
            {
                UpdateAutoDestination();
                ValidateSourcePage();
            };
            _destination.TextChanged += (s, e) => ValidateSourcePage();
            _overwrite.CheckedChanged += (s, e) => ValidateSourcePage();

            GoTo(0);
        }

        // ---- results the caller reads after ShowDialog() == OK ----

        /// <summary>The executed import (non-null once the wizard finished successfully).</summary>
        public ImportExecutionResult Result
        {
            get { return _result; }
        }

        public ImportPlan Plan
        {
            get { return _plan; }
        }

        public ImportAnalysis Analysis
        {
            get { return _analysis; }
        }

        // ---- page builders ----

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Width = 96, FlatStyle = FlatStyle.System };
            b.Click += onClick;
            return b;
        }

        private Panel BuildSourcePage(out TextBox source, out TextBox destination, out TextBox name, out RadioButton toml,
                                      out RadioButton vs, out RadioButton both, out CheckBox overwrite,
                                      out Label validation)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

            source = new TextBox { Dock = DockStyle.Fill };
            TextBox src = source;
            var browseSource = new Button { Text = "Browse…", Dock = DockStyle.Fill };
            browseSource.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    string start = ResolveBrowseStart(src.Text, _settingsManager?.Settings.LastImportSourceDirectory);
                    if (start != null) d.SelectedPath = start;
                    if (d.ShowDialog(this) == DialogResult.OK) src.Text = d.SelectedPath;
                }
            };

            destination = new TextBox { Dock = DockStyle.Fill };
            TextBox dst = destination;
            var browseDestination = new Button { Text = "Browse…", Dock = DockStyle.Fill };
            browseDestination.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    string start = ResolveBrowseStart(dst.Text, _settingsManager?.Settings.LastImportDestinationDirectory);
                    if (start != null) d.SelectedPath = start;
                    if (d.ShowDialog(this) == DialogResult.OK) dst.Text = d.SelectedPath;
                }
            };

            name = new TextBox { Dock = DockStyle.Fill };

            grid.Controls.Add(new Label { Text = "Source directory", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            grid.Controls.Add(source, 1, 0);
            grid.Controls.Add(browseSource, 2, 0);
            grid.Controls.Add(new Label { Text = "Destination directory", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
            grid.Controls.Add(destination, 1, 1);
            grid.Controls.Add(browseDestination, 2, 1);
            grid.Controls.Add(new Label { Text = "Solution name", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
            grid.Controls.Add(name, 1, 2);

            var fmt = new GroupBox { Text = "Target format", Dock = DockStyle.Top, Height = 110, Padding = new Padding(12) };
            toml = new RadioButton { Text = "TOML workspace (pyproject.toml) — recommended", Dock = DockStyle.Top, Checked = true };
            vs = new RadioButton { Text = "Visual Studio (.sln + .pyproj)", Dock = DockStyle.Top };
            both = new RadioButton { Text = "Both", Dock = DockStyle.Top };
            fmt.Controls.Add(both); fmt.Controls.Add(vs); fmt.Controls.Add(toml);

            overwrite = new CheckBox { Text = "Overwrite existing solution / project files", Dock = DockStyle.Top, Height = 28, Checked = true };
            validation = new Label { Dock = DockStyle.Top, Height = 40, ForeColor = Color.Firebrick };

            p.Controls.Add(validation);
            p.Controls.Add(overwrite);
            p.Controls.Add(fmt);
            p.Controls.Add(grid);
            return p;
        }

        private Panel BuildAnalysisPage(out Label classification, out ListView findings)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            classification = new Label { Dock = DockStyle.Top, Height = 32, Font = LunaFonts.Sized(1f) };
            findings = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false
            };
            findings.Columns.Add("Category", 170);
            findings.Columns.Add("Path / detail", 420);
            findings.Columns.Add("Notes", 180);
            p.Controls.Add(findings);
            p.Controls.Add(classification);
            return p;
        }

        private Panel BuildProjectsPage(out DataGridView grid, out Label validation)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            grid = new DataGridView
            {
                // Content-driven column widths (not Fill) so long project/directory names
                // are never clipped — columns grow to fit their longest value and a
                // horizontal scrollbar appears if the total exceeds the visible width,
                // rather than every column being squeezed to share the panel width.
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeColumns = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                ScrollBars = ScrollBars.Both, ShowCellToolTips = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            var dispositionCol = new DataGridViewComboBoxColumn
            {
                Name = "colDisposition", HeaderText = "Import as", MinimumWidth = 130,
                DataSource = new[] { "Separate project", "Part of parent", "Excluded" }
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDetected", HeaderText = "Detected name", ReadOnly = true, MinimumWidth = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colName", HeaderText = "Project name", MinimumWidth = 110 });
            grid.Columns.Add(dispositionCol);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colStartup", HeaderText = "Startup", MinimumWidth = 60 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colDir", HeaderText = "Directory", ReadOnly = true, MinimumWidth = 200 });
            validation = new Label { Dock = DockStyle.Bottom, Height = 36, ForeColor = Color.Firebrick };
            var hint = new Label
            {
                Dock = DockStyle.Top, Height = 30,
                Text = "Rename projects, merge nested packages into their parent, exclude directories, and pick the startup project."
            };
            p.Controls.Add(grid);
            p.Controls.Add(validation);
            p.Controls.Add(hint);
            return p;
        }

        private Panel BuildLayoutPage(out RadioButton inPlace, out RadioButton srcLayout,
                                      out CheckBox resolveEntries, out CheckBox generateCudaScaffold, out Label entryInfo)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            var layout = new GroupBox { Text = "Workspace layout", Dock = DockStyle.Top, Height = 100, Padding = new Padding(12) };
            // Rearrange is the primary/default choice (IDE-compliant layout is what makes
            // the rest of the tool — Run, Debug, references — work the way the rest of the
            // IDE expects); Keep-in-place is the secondary, opt-in choice. Dock=Top stacks
            // the LAST-added control at the top, so srcLayout is added after inPlace here.
            inPlace = new RadioButton { Text = "Keep files where they are (generate TOMLs in place)", Dock = DockStyle.Top };
            srcLayout = new RadioButton { Text = "Rearrange: move each project package under src\\<name> (IDE-compliant layout) — recommended", Dock = DockStyle.Top, Checked = true };
            layout.Controls.Add(inPlace); layout.Controls.Add(srcLayout);

            resolveEntries = new CheckBox
            {
                Text = "Resolve duplicate entry points (rename surplus main.py variants)",
                Dock = DockStyle.Top, Height = 28
            };
            // Only enabled/visible-as-meaningful when CudaKernelScaffoldAnalyzer found inline
            // kernels (see PopulateLayoutPage) — extracts each into its own .cu file and
            // generates a "cuda_kernel_debug" native CMake companion project (one host
            // executable per kernel) for real-debugger stepping (SS §linked-cmake-project).
            generateCudaScaffold = new CheckBox
            {
                Text = "Generate CUDA kernel-debug scaffold (extract kernels to .cu, add cuda_kernel_debug\\)",
                Dock = DockStyle.Top, Height = 28
            };
            entryInfo = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };

            p.Controls.Add(entryInfo);
            p.Controls.Add(generateCudaScaffold);
            p.Controls.Add(resolveEntries);
            p.Controls.Add(layout);
            return p;
        }

        private Panel BuildSummaryPage(out RichTextBox summary)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            summary = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(248, 248, 248), Font = new Font("Consolas", 9f)
            };
            p.Controls.Add(summary);
            return p;
        }

        // ---- navigation ----

        private const int PageSource = 0;
        private const int PageAnalysis = 1;
        private const int PageProjects = 2;
        private const int PageLayout = 3;
        private const int PageSummary = 4;

        public int CurrentPage
        {
            get { return _page; }
        }

        private void Advance()
        {
            if (_page == PageSource)
            {
                if (!ValidateSourcePage()) return;
                if (!RunAnalysis()) return;
            }
            if (_page == PageProjects && !ValidateProjectsPage()) return;
            if (_page == PageProjects) ReadGridIntoPlan();
            if (_page == PageLayout) ReadLayoutIntoPlan();
            GoTo(_page + 1);
        }

        private void GoTo(int page)
        {
            _page = Math.Max(0, Math.Min(page, PageSummary));
            _p1.Visible = _page == PageSource;
            _p2.Visible = _page == PageAnalysis;
            _p3.Visible = _page == PageProjects;
            _p4.Visible = _page == PageLayout;
            _p5.Visible = _page == PageSummary;
            _back.Enabled = _page > 0;
            _next.Visible = _page < PageSummary;
            _import.Visible = _page == PageSummary;
            AcceptButton = _page == PageSummary ? _import : _next;
            _header.Text = "  Import Python Directory — " + PageTitle(_page);
            if (_page == PageSummary) PopulateSummary();
        }

        private static string PageTitle(int page)
        {
            switch (page)
            {
                case PageSource: return "Source";
                case PageAnalysis: return "Analysis";
                case PageProjects: return "Projects";
                case PageLayout: return "Layout";
                default: return "Summary";
            }
        }

        // ---- page logic ----

        private static string DeriveName(string path)
        {
            try
            {
                string trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
                string leaf = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? "imported" : leaf;
            }
            catch (ArgumentException)
            {
                return "imported";
            }
        }

        /// <summary>
        /// Folder to open a Browse dialog at: the field's current value if it exists, else
        /// that value's parent if THAT exists (e.g. an as-yet-uncreated suggested destination
        /// still starts the browse next to where it would land), else the persisted last-used
        /// directory from a previous import, else null (let the dialog use its own default).
        /// </summary>
        internal static string ResolveBrowseStart(string currentText, string lastPersisted)
        {
            string current = (currentText ?? string.Empty).Trim();
            if (current.Length > 0)
            {
                if (Directory.Exists(current)) return current;
                try
                {
                    string parent = Path.GetDirectoryName(current.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
                }
                catch (ArgumentException) { }
            }
            if (!string.IsNullOrEmpty(lastPersisted) && Directory.Exists(lastPersisted)) return lastPersisted;
            return null;
        }

        private bool ValidateSourcePage()
        {
            string message = null;
            if (string.IsNullOrWhiteSpace(_source.Text) || !Directory.Exists(_source.Text.Trim()))
                message = "Choose an existing source directory.";
            else if (string.IsNullOrWhiteSpace(_name.Text))
                message = "Enter a solution name.";
            else if (_name.Text.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                message = "The solution name contains invalid characters.";
            else if (string.IsNullOrWhiteSpace(_destination.Text))
                message = "Choose a destination directory.";
            else
                IsValidDestination(_source.Text.Trim(), _destination.Text.Trim(), _overwrite.Checked, out message);
            _p1Validation.Text = message ?? string.Empty;
            return message == null;
        }

        /// <summary>
        /// Recomputes the suggested destination ("&lt;solution&gt;_helix_import" next to the
        /// source) whenever Source/Solution name change — but only while the destination
        /// field still holds a value THIS method produced. The moment the user types or
        /// browses their own path into it, it no longer matches <see cref="_autoDestination"/>
        /// and this stops touching it, so a manual choice is never silently overwritten.
        /// </summary>
        private void UpdateAutoDestination()
        {
            if (string.IsNullOrWhiteSpace(_source.Text)) return;
            string current = (_destination.Text ?? string.Empty).Trim();
            if (current.Length > 0 && !string.Equals(current, _autoDestination, StringComparison.OrdinalIgnoreCase))
                return;
            string suggestion = SuggestDestination(_source.Text.Trim(), _name.Text.Trim());
            _destination.Text = suggestion;
            _autoDestination = suggestion;
        }

        /// <summary>
        /// First free "&lt;parent-of-source&gt;\&lt;solution&gt;_helix_import[_N]" candidate —
        /// mirrors the naming the wizard always used before the destination was user-editable,
        /// so the default offered to the user never collides with an existing directory.
        /// </summary>
        private static string SuggestDestination(string sourcePath, string solutionName)
        {
            string trimmed = sourcePath.TrimEnd('\\', '/');
            string parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent)) parent = trimmed;
            string baseName = string.IsNullOrWhiteSpace(solutionName) ? DeriveName(trimmed) : solutionName.Trim();

            string candidate = Path.Combine(parent, baseName + "_helix_import");
            int n = 2;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(parent, baseName + "_helix_import_" + n);
                n++;
            }
            return candidate;
        }

        private static bool IsValidDestination(string source, string destination, bool allowOverwrite, out string error)
        {
            error = null;
            string fullSource, fullDest;
            try
            {
                fullSource = Path.GetFullPath(source).TrimEnd('\\', '/');
                fullDest = Path.GetFullPath(destination).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                error = "The destination path is not valid.";
                return false;
            }
            if (string.Equals(fullSource, fullDest, StringComparison.OrdinalIgnoreCase) ||
                fullDest.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "The destination directory cannot be the source directory or a subdirectory of it.";
                return false;
            }
            // A pre-existing, non-empty destination (e.g. re-running the wizard against the
            // same output folder) is fine as long as "Overwrite existing" is checked — the copy
            // step below overwrites matching files in place. Without it, refuse rather than
            // silently mix the new import in with whatever is already there.
            if (!allowOverwrite && Directory.Exists(fullDest) && Directory.GetFileSystemEntries(fullDest).Length > 0)
            {
                error = "The destination directory already exists and is not empty — choose an empty directory, or check \"Overwrite existing solution / project files\".";
                return false;
            }
            return true;
        }

        private bool RunAnalysis()
        {
            string originalSource = _source.Text.Trim();
            string destination = _destination.Text.Trim();

            // The import (rearranging files, renaming duplicate main.py/def main(), writing
            // TOMLs) is destructive by nature — never run it against what the user actually
            // pointed the wizard at. Copy it to the chosen destination directory first and do
            // everything else against the copy; the original is only ever READ, here, once.
            string importRoot;
            try
            {
                importRoot = CopySourceForImport(originalSource, destination);
            }
            catch (Exception ex)
            {
                _p1Validation.Text = "Could not create a working copy at the destination directory: " + ex.Message;
                return false;
            }

            _analysis = new ImportAnalyzer().Analyze(importRoot);
            if (!_analysis.Success)
            {
                _p1Validation.Text = _analysis.ErrorMessage;
                return false;
            }
            _plan = ImportPlanFactory.CreateDefault(_analysis, _name.Text.Trim());
            _plan.OriginalSourceRoot = originalSource;
            _plan.TargetFormat = _tomlRadio.Checked ? ImportTargetFormat.TomlWorkspace
                : _vsRadio.Checked ? ImportTargetFormat.VisualStudio
                : ImportTargetFormat.Both;
            _plan.OverwriteExisting = _overwrite.Checked;
            PopulateAnalysisPage();
            PopulateProjectsGrid();
            PopulateLayoutPage();
            return true;
        }

        /// <summary>
        /// Copies <paramref name="sourcePath"/> into <paramref name="destinationPath"/> (the
        /// directory chosen on the Source page) so the import is free to mutate a copy,
        /// skipping the same venv/cache/VCS directories the loader ignores. ValidateSourcePage
        /// already rejected a destination that is the source or nested inside it; a destination
        /// that already exists (same-named directory from a previous run) is fine — matching
        /// files are overwritten in place, new ones added.
        /// </summary>
        private static string CopySourceForImport(string sourcePath, string destinationPath)
        {
            string trimmed = sourcePath.TrimEnd('\\', '/');
            CopyDirectoryRecursive(trimmed, destinationPath);
            return destinationPath;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                if (SolutionService.IgnoredDirectories.Contains(Path.GetFileName(dir))) continue;
                CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
            }
            foreach (string file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        private void PopulateAnalysisPage()
        {
            _classification.Text = "Classification: " + (_analysis.RepositoryClassification ?? "Unknown") +
                                   "   —   " + _analysis.Packages.Count + " package(s) detected";
            _findings.BeginUpdate();
            _findings.Items.Clear();
            Action<string, string, string> add = (cat, path, note) =>
                _findings.Items.Add(new ListViewItem(new[] { cat, path, note ?? string.Empty }));

            foreach (PackageDescriptor pkg in _analysis.Packages)
                add("Package", pkg.DirectoryPath, pkg.IsSynthesized ? "synthesized (loose root files)" : pkg.Name);
            foreach (DetectedDirectory d in _analysis.NamespaceDirectories) add("Namespace dir", d.Path, d.Reason);
            foreach (string f in _analysis.OrphanFiles) add("Orphan file", f, null);
            foreach (DetectedDirectory d in _analysis.VirtualEnvironments) add("Virtual env", d.Path, d.Reason);
            foreach (DetectedDirectory d in _analysis.BuildArtifacts) add("Build artifact", d.Path, d.Reason);
            foreach (DetectedDirectory d in _analysis.TestDirectories) add("Tests", d.Path, d.Reason);
            foreach (DetectedDirectory d in _analysis.DocumentationDirectories) add("Documentation", d.Path, d.Reason);
            foreach (DuplicateNameGroup g in _analysis.DuplicateNames)
                add("Duplicate name", string.Join("; ", g.DirectoryPaths), "'" + g.Name + "'");
            foreach (string m in _analysis.MainModules) add("Entry module", m, null);
            foreach (string m in _analysis.MainFunctionFiles) add("def main()", m, null);
            _findings.EndUpdate();
        }

        private void PopulateProjectsGrid()
        {
            _grid.Rows.Clear();
            foreach (PackagePlan pp in _plan.Packages)
            {
                int row = _grid.Rows.Add();
                DataGridViewRow r = _grid.Rows[row];
                r.Cells["colDetected"].Value = pp.Source.Name;
                r.Cells["colName"].Value = pp.FinalName;
                r.Cells["colDisposition"].Value = DispositionText(pp.Disposition);
                r.Cells["colStartup"].Value = string.Equals(pp.FinalName, _plan.StartupProjectName, StringComparison.OrdinalIgnoreCase);
                r.Cells["colDir"].Value = pp.Source.DirectoryPath;
                r.Tag = pp;
            }
        }

        private static string DispositionText(PackageDisposition d)
        {
            switch (d)
            {
                case PackageDisposition.PartOfParent: return "Part of parent";
                case PackageDisposition.Excluded: return "Excluded";
                default: return "Separate project";
            }
        }

        private static PackageDisposition DispositionFromText(string text)
        {
            if (string.Equals(text, "Part of parent", StringComparison.OrdinalIgnoreCase)) return PackageDisposition.PartOfParent;
            if (string.Equals(text, "Excluded", StringComparison.OrdinalIgnoreCase)) return PackageDisposition.Excluded;
            return PackageDisposition.SeparateProject;
        }

        private bool ValidateProjectsPage()
        {
            _grid.EndEdit();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int startups = 0;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (DispositionFromText(Convert.ToString(r.Cells["colDisposition"].Value)) != PackageDisposition.SeparateProject)
                    continue;
                string projName = Convert.ToString(r.Cells["colName"].Value);
                if (string.IsNullOrWhiteSpace(projName))
                {
                    _p3Validation.Text = "Every included project needs a name.";
                    return false;
                }
                if (!names.Add(projName.Trim()))
                {
                    _p3Validation.Text = "Duplicate project name '" + projName.Trim() + "' — rename one of them.";
                    return false;
                }
                if (Convert.ToBoolean(r.Cells["colStartup"].Value ?? false)) startups++;
            }
            if (names.Count == 0)
            {
                _p3Validation.Text = "At least one package must be imported as a separate project.";
                return false;
            }
            if (startups > 1)
            {
                _p3Validation.Text = "Only one project can be the startup project.";
                return false;
            }
            _p3Validation.Text = string.Empty;
            return true;
        }

        private void ReadGridIntoPlan()
        {
            _grid.EndEdit();
            _plan.StartupProjectName = null;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                var pp = (PackagePlan)r.Tag;
                pp.FinalName = (Convert.ToString(r.Cells["colName"].Value) ?? pp.Source.Name).Trim();
                pp.Disposition = DispositionFromText(Convert.ToString(r.Cells["colDisposition"].Value));
                if (pp.Disposition == PackageDisposition.SeparateProject &&
                    Convert.ToBoolean(r.Cells["colStartup"].Value ?? false))
                    _plan.StartupProjectName = pp.FinalName;
            }
        }

        private void PopulateLayoutPage()
        {
            bool hasEntryIssues = _analysis.AuditReport != null && !_analysis.AuditReport.IsCompliant;
            _resolveEntries.Enabled = hasEntryIssues;
            _resolveEntries.Checked = hasEntryIssues;

            bool hasCudaKernels = _analysis.DetectedCudaScaffold != null && _analysis.DetectedCudaScaffold.HasKernels;
            _generateCudaScaffold.Enabled = hasCudaKernels;
            _generateCudaScaffold.Checked = hasCudaKernels;
            _generateCudaScaffold.Text = hasCudaKernels
                ? "Generate CUDA kernel-debug scaffold (" + _analysis.DetectedCudaScaffold.Kernels.Count +
                  " kernel(s) found — extract to .cu, add cuda_kernel_debug\\)"
                : "Generate CUDA kernel-debug scaffold (no inline CUDA kernels detected)";

            var sb = new StringBuilder();
            if (hasEntryIssues)
            {
                sb.AppendLine("Entry-point findings:");
                foreach (SolutionAuditFinding f in _analysis.AuditReport.Findings)
                    sb.AppendLine("  • " + f.Message);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("No duplicate entry points detected.");
                sb.AppendLine();
            }
            if (hasCudaKernels)
            {
                sb.AppendLine("Inline CUDA kernels found (compiled via cp.RawKernel/NVRTC at runtime):");
                foreach (DetectedCudaKernel k in _analysis.DetectedCudaScaffold.Kernels)
                    sb.AppendLine("  • " + k.KernelName + " (" + k.Parameters.Count + " parameter(s))");
            }
            _entryInfo.Text = sb.ToString();
        }

        private void ReadLayoutIntoPlan()
        {
            _plan.Layout = _srcLayoutRadio.Checked ? ImportLayout.RearrangeToSrc : ImportLayout.InPlace;
            _plan.ResolveEntryPoints = _resolveEntries.Checked;
            _plan.GenerateCudaDebugScaffold = _generateCudaScaffold.Checked;
            _plan.TargetFormat = _tomlRadio.Checked ? ImportTargetFormat.TomlWorkspace
                : _vsRadio.Checked ? ImportTargetFormat.VisualStudio
                : ImportTargetFormat.Both;
            _plan.OverwriteExisting = _overwrite.Checked;
        }

        private void PopulateSummary()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(_plan.OriginalSourceRoot))
            {
                sb.AppendLine("Original:    " + _plan.OriginalSourceRoot + "  (left untouched)");
                sb.AppendLine("Destination: " + _plan.SourceRoot + "  (the imported project is created here)");
            }
            else
            {
                sb.AppendLine("Source:    " + _plan.SourceRoot);
            }
            sb.AppendLine("Solution:  " + _plan.SolutionName);
            sb.AppendLine("Format:    " + _plan.TargetFormat);
            sb.AppendLine("Layout:    " + (_plan.Layout == ImportLayout.RearrangeToSrc
                ? "rearrange packages under src\\ (files will be MOVED)"
                : "in place (no files moved)"));
            sb.AppendLine();
            sb.AppendLine("Projects:");
            foreach (PackagePlan pp in _plan.Packages)
            {
                string suffix = pp.Disposition == PackageDisposition.SeparateProject
                    ? (pp.IsRenamed ? "  (renamed from " + pp.Source.Name + ")" : string.Empty)
                    : "  [" + DispositionText(pp.Disposition) + "]";
                string startup = string.Equals(pp.FinalName, _plan.StartupProjectName, StringComparison.OrdinalIgnoreCase)
                    ? "  ← startup" : string.Empty;
                sb.AppendLine("  " + pp.FinalName + suffix + startup);
            }
            if (_plan.ResolveEntryPoints)
            {
                sb.AppendLine();
                sb.AppendLine("Surplus main.py variants will be renamed to *_entry.py.");
            }
            _summary.Text = sb.ToString();
        }

        private void DoImport()
        {
            ReadLayoutIntoPlan();
            _result = new ImportPlanExecutor().Execute(_plan);
            if (!_result.Success)
            {
                // Headless callers (tests/harness) read the failure off _result; a modal
                // here would block their message-loop-less run forever.
                if (!string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", StringComparison.Ordinal))
                {
                    MessageBox.Show(this, _result.ErrorMessage, "Import failed",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            if (_settingsManager != null)
            {
                // Remember what the user actually typed on the Source page (not the
                // working-copy path _plan.SourceRoot got rewritten to), so re-opening the
                // wizard next time starts from where they left off.
                _settingsManager.Settings.LastImportSourceDirectory = _source.Text.Trim();
                _settingsManager.Settings.LastImportSolutionName = _name.Text.Trim();
                _settingsManager.Settings.LastImportDestinationDirectory = _destination.Text.Trim();
                _settingsManager.Save();
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- automation hooks (headless tests / harness drive the wizard through these) ----

        internal void SetSource(string path)
        {
            _source.Text = path;
        }

        internal string SourceText => _source.Text;

        internal void SetDestination(string path)
        {
            _destination.Text = path;
        }

        internal string DestinationText => _destination.Text;

        internal void SetSolutionName(string name)
        {
            _name.Text = name;
        }

        internal string SolutionNameText => _name.Text;

        internal void SetOverwrite(bool overwrite)
        {
            _overwrite.Checked = overwrite;
        }

        internal bool GoNext()
        {
            int before = _page;
            Advance();
            return _page != before;
        }

        internal void GoBack()
        {
            GoTo(_page - 1);
        }

        internal bool SetDisposition(string detectedName, PackageDisposition disposition)
        {
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (!string.Equals(Convert.ToString(r.Cells["colDetected"].Value), detectedName, StringComparison.OrdinalIgnoreCase))
                    continue;
                r.Cells["colDisposition"].Value = DispositionText(disposition);
                return true;
            }
            return false;
        }

        internal bool RenameProject(string detectedName, string newName)
        {
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (!string.Equals(Convert.ToString(r.Cells["colDetected"].Value), detectedName, StringComparison.OrdinalIgnoreCase))
                    continue;
                r.Cells["colName"].Value = newName;
                return true;
            }
            return false;
        }

        // Row-index variant for fixtures where detected names collide (duplicate
        // package names make the name-based hook ambiguous).
        internal bool RenameProjectAt(int rowIndex, string newName)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return false;
            _grid.Rows[rowIndex].Cells["colName"].Value = newName;
            return true;
        }

        internal bool SetStartup(string projectName)
        {
            bool found = false;
            foreach (DataGridViewRow r in _grid.Rows)
            {
                bool match = string.Equals(Convert.ToString(r.Cells["colName"].Value), projectName, StringComparison.OrdinalIgnoreCase);
                r.Cells["colStartup"].Value = match;
                if (match) found = true;
            }
            return found;
        }

        internal bool IsRearrangeToSrcChecked => _srcLayoutRadio.Checked;

        internal void SetLayout(ImportLayout layout)
        {
            _srcLayoutRadio.Checked = layout == ImportLayout.RearrangeToSrc;
            _inPlaceRadio.Checked = layout == ImportLayout.InPlace;
        }

        internal void SetResolveEntryPoints(bool resolve)
        {
            if (_resolveEntries.Enabled) _resolveEntries.Checked = resolve;
        }

        internal bool CudaScaffoldAvailable => _generateCudaScaffold.Enabled;

        internal void SetGenerateCudaDebugScaffold(bool generate)
        {
            if (_generateCudaScaffold.Enabled) _generateCudaScaffold.Checked = generate;
        }

        internal bool RunImportNow()
        {
            DoImport();
            return _result != null && _result.Success;
        }

        internal int FindingCount
        {
            get { return _findings.Items.Count; }
        }
    }
}
