namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    [System.ComponentModel.DesignerCategory("Form")]

    public sealed partial class ManagePackagesDialog : LunaForm
    {
        // "Refresh" button on the Installed tab: re-reads the current package list from disk.
        private void OnRefreshInstalledClick(object sender, EventArgs e) { LoadInstalledPackages(); }
        public ManagePackagesDialog()
        {
            InitializeComponent();
            // Double-click or Enter on a search result installs it (the primary action), so the user
            // doesn't have to select a row and then hunt for the Install button.
            _searchResultsListView.DoubleClick += OnSearchResultActivated;
            _searchResultsListView.KeyDown += OnSearchResultsKeyDown;
        }

        private void OnSearchResultActivated(object sender, EventArgs e)
        {
            if (_searchResultsListView.SelectedItems.Count > 0) OnInstallPackage(this, EventArgs.Empty);
        }

        private void OnSearchResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && _searchResultsListView.SelectedItems.Count > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OnInstallPackage(this, EventArgs.Empty);
            }
        }



        // Installed tab


        // Search tab





        // Output

        private readonly ProjectModel _project;

        // Real entry point: binds the dialog to a project (its .csproj drives all operations)
        // and immediately shows what packages are already installed.
        public ManagePackagesDialog(ProjectModel project) : this()
        {
            _project = project ?? throw new ArgumentNullException("project");
            // Retitle for the correct ecosystem: PEP-621/pip for a pyproject project, NuGet otherwise.
            if (IsPyproject)
                this.Text = "Manage Python Packages (pip / PEP 621) — " + project.Name;
            LoadInstalledPackages();
        }


        // Appends a line to the output log. Marshals back onto the UI thread when called from a
        // background worker (the async dotnet commands raise output events off-thread), and no-ops
        // if the dialog has already been disposed to avoid touching destroyed controls.
        private void AppendOutput(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendOutput), text);
                return;
            }
            _outputTextBox.AppendText(text + Environment.NewLine);
        }

        // Populates the Installed list by parsing the project's .csproj XML for PackageReference
        // elements. SDK-style csproj files are namespace-less, so a plain //PackageReference XPath
        // works. Any read/parse error is reported to the output log rather than thrown.
        // FR-PROJ-02: resolve the project's pyproject.toml. Python projects are PEP-621, not NuGet —
        // when a pyproject.toml is present the dialog edits [project].dependencies (pip ecosystem)
        // rather than shelling `dotnet`. Returns null for a genuine .NET (.csproj) project.
        private string ResolvePyproject()
        {
            try
            {
                if (_project == null) return null;
                if (!string.IsNullOrEmpty(_project.FilePath)
                    && _project.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(_project.FilePath))
                    return _project.FilePath;
                string dir = _project.ProjectDirectory;
                if (!string.IsNullOrEmpty(dir))
                {
                    string cand = Path.Combine(dir, "pyproject.toml");
                    if (File.Exists(cand)) return cand;
                }
            }
            catch { }
            return null;
        }

        private bool IsPyproject { get { return ResolvePyproject() != null; } }

        private void LoadInstalledPackages()
        {
            _installedListView.Items.Clear();
            _outputTextBox.Clear();

            string pyproject = ResolvePyproject();
            if (pyproject != null)
            {
                LoadPyprojectDependencies(pyproject);
                return;
            }

            if (!File.Exists(_project.FilePath)) return;

            try
            {
                // Parse csproj for PackageReference elements
                var doc = new System.Xml.XmlDocument();
                doc.Load(_project.FilePath);

                var nsMgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
                // SDK-style csproj has no namespace usually
                var nodes = doc.SelectNodes("//PackageReference");
                if (nodes != null)
                {
                    foreach (System.Xml.XmlNode node in nodes)
                    {
                        var include = node.Attributes != null ? node.Attributes["Include"] : null;
                        var version = node.Attributes != null ? node.Attributes["Version"] : null;

                        if (include != null)
                        {
                            var item = new ListViewItem(include.Value);
                            item.SubItems.Add(version != null ? version.Value : "");
                            _installedListView.Items.Add(item);
                        }
                    }
                }

                AppendOutput(string.Format("Found {0} package(s) in {1}", _installedListView.Items.Count, _project.Name));
            }
            catch (Exception ex)
            {
                AppendOutput(string.Format("Error reading packages: {0}", ex.Message));
            }
        }

        // Lists [project].dependencies (PEP 508) from pyproject.toml into the Installed grid — Package
        // column = distribution name (+ extras), Version column = the version specifier.
        private void LoadPyprojectDependencies(string pyproject)
        {
            var deps = HelixPythonNamespace.Workspace.Python.PyprojectDependencyService.List(pyproject);
            foreach (var d in deps)
            {
                var item = new ListViewItem(d.Name + (string.IsNullOrEmpty(d.Extras) ? "" : d.Extras));
                item.SubItems.Add(string.IsNullOrEmpty(d.Specifier) ? "(any)" : d.Specifier);
                _installedListView.Items.Add(item);
            }
            AppendOutput(string.Format("{0} dependency(ies) in {1} [project].dependencies (pip / PEP 621)",
                deps.Count, Path.GetFileName(pyproject)));
        }

        // Removes the selected installed package via `dotnet remove ... package`, after a Yes/No
        // confirmation. The button is disabled during the async run to prevent re-entry, and the
        // installed list is reloaded on success so the UI reflects the updated csproj.
        private async void OnRemovePackage(object sender, EventArgs e)
        {
            if (_installedListView.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select a package to remove.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var packageName = _installedListView.SelectedItems[0].Text;

            string pyproject = ResolvePyproject();
            if (pyproject != null)
            {
                var confirm = MessageBox.Show(
                    string.Format("Remove dependency '{0}' from {1} [project].dependencies?", packageName, _project.Name),
                    "Remove Dependency", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
                bool ok = HelixPythonNamespace.Workspace.Python.PyprojectDependencyService.Remove(pyproject, packageName);
                AppendOutput(ok ? "Removed " + packageName : "Not found: " + packageName);
                LoadInstalledPackages();
                return;
            }

            var result = MessageBox.Show(
                string.Format("Remove package '{0}' from {1}?", packageName, _project.Name),
                "Remove Package",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            _removeButton.Enabled = false;
            AppendOutput(string.Format("Removing {0}...", packageName));

            bool success = await RunDotNetCommandAsync(
                string.Format("remove \"{0}\" package {1}", _project.FilePath, packageName));

            _removeButton.Enabled = true;

            if (success)
            {
                AppendOutput(string.Format("Successfully removed {0}", packageName));
                LoadInstalledPackages();
            }
        }

        // Escape closes the dialog from anywhere inside it (standard modal behavior). There is no
        // Cancel button to wire, and nothing to revert — installs/removes take effect on their own
        // buttons — so handle the key at the dialog level, which also catches it from child controls.
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        // Lets Enter in the search box trigger a search. SuppressKeyPress stops the keystroke from
        // reaching the control (avoiding the Windows "ding" and any AcceptButton interaction).
        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnSearchPackages(sender, EventArgs.Empty);
            }
        }

        // Searches NuGet via `dotnet package search`. Runs the process on a background thread so
        // the UI stays responsive, parses the tabular text output, then batches the results into
        // the list view (BeginUpdate/EndUpdate suppress per-item repaint flicker).
        private async void OnSearchPackages(object sender, EventArgs e)
        {
            var query = _searchBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            _searchButton.Enabled = false;
            _searchResultsListView.Items.Clear();
            AppendOutput(string.Format("Searching for '{0}'...", query));

            try
            {
                var results = new List<string[]>();

                bool python = IsPyproject;
                await Task.Run(() =>
                {
                    // Python projects resolve against PyPI, not NuGet — otherwise a Python user's
                    // search returned .NET packages (wrong ecosystem).
                    if (python) { SearchPyPI(query, results); return; }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = string.Format("package search {0} --take 20", query),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        if (process == null) return;

                        string output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(30000);

                        ParseSearchResults(output, results);
                    }
                });

                _searchResultsListView.BeginUpdate();
                foreach (var r in results)
                {
                    var item = new ListViewItem(r[0]); // Package name
                    item.SubItems.Add(r.Length > 1 ? r[1] : ""); // Version
                    item.SubItems.Add(r.Length > 2 ? r[2] : ""); // Description
                    _searchResultsListView.Items.Add(item);
                }
                _searchResultsListView.EndUpdate();

                if (results.Count == 0 && python)
                    AppendOutput("No PyPI package named '" + query + "'. PyPI has no fuzzy search — enter an exact package name (e.g. requests, numpy).");
                else
                    AppendOutput(string.Format("Found {0} result(s)", results.Count));
            }
            catch (Exception ex)
            {
                AppendOutput(string.Format("Search failed: {0}", ex.Message));
            }

            _searchButton.Enabled = true;
        }

        // PyPI retired its public search API (pip search is disabled), so resolve the query as an
        // exact package name via the JSON API: validates the name exists and surfaces its latest
        // version + summary. Install then adds it to [project].dependencies (PEP 621). Best-effort —
        // a 404 or offline state simply yields no result.
        private static void SearchPyPI(string query, System.Collections.Generic.List<string[]> results)
        {
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "HelixStudio");
                    string json = wc.DownloadString("https://pypi.org/pypi/" + Uri.EscapeDataString(query) + "/json");
                    var info = Newtonsoft.Json.Linq.JObject.Parse(json)["info"];
                    if (info == null) return;
                    string name = (string)info["name"] ?? query;
                    string version = (string)info["version"] ?? "";
                    string summary = (string)info["summary"] ?? "";
                    results.Add(new[] { name, version, summary });
                }
            }
            catch (System.Net.WebException) { /* 404 / offline → no result */ }
            catch { /* malformed response → no result */ }
        }

        // Best-effort parser for the free-form `dotnet package search` console output, whose exact
        // format varies between SDK versions. Skips header/separator lines, then splits each data
        // row on runs of 2+ spaces into [name, version, description] tuples (columns beyond the
        // name may be missing). The inResults flag turns on once a header marker is seen.
        private static void ParseSearchResults(string output, List<string[]> results)
        {
            if (string.IsNullOrEmpty(output)) return;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool inResults = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip header lines
                if (trimmed.StartsWith(">") || trimmed.StartsWith("Source"))
                {
                    inResults = true;
                    continue;
                }

                if (!inResults) continue;
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (trimmed.StartsWith("---") || trimmed.StartsWith("===")) continue;

                // Parse: "  > PackageName    Version    Description" or table rows
                // dotnet package search output format varies; try splitting by multiple spaces
                var parts = System.Text.RegularExpressions.Regex.Split(trimmed, @"\s{2,}");
                if (parts.Length >= 2)
                {
                    // Clean up leading > or |
                    var name = parts[0].TrimStart('>', '|', ' ');
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("-"))
                    {
                        var entry = new string[3];
                        entry[0] = name;
                        entry[1] = parts.Length > 1 ? parts[1].Trim('|', ' ') : "";
                        entry[2] = parts.Length > 2 ? parts[2].Trim('|', ' ') : "";
                        results.Add(entry);
                    }
                }
            }
        }

        // Installs the selected search result via `dotnet add ... package`. If the version box is
        // filled a pinned --version is added, otherwise the latest is installed. Reloads the
        // installed list on success so the new dependency shows up.
        private async void OnInstallPackage(object sender, EventArgs e)
        {
            string pyproject = ResolvePyproject();
            if (pyproject != null)
            {
                // pip / PEP 621: add [name][extras][specifier] to [project].dependencies (native, no network).
                // The name comes from the selected search row if any, else the search box; version box pins.
                string name = _searchResultsListView.SelectedItems.Count > 0
                    ? _searchResultsListView.SelectedItems[0].Text
                    : _searchBox.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show("Type a package name (optionally a version specifier in the Version box) to add.",
                        "Add Dependency", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string spec = _versionTextBox.Text.Trim();
                string requirement = spec.Length == 0 ? name
                    : (System.Text.RegularExpressions.Regex.IsMatch(spec, @"^[<>=!~]") ? name + spec : name + "==" + spec);
                bool ok = HelixPythonNamespace.Workspace.Python.PyprojectDependencyService.Add(pyproject, requirement);
                AppendOutput(ok ? "Added dependency: " + requirement : "No change: " + requirement);
                LoadInstalledPackages();
                return;
            }

            if (_searchResultsListView.SelectedItems.Count == 0)
            {
                MessageBox.Show("Select a package from the search results to install.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var packageName = _searchResultsListView.SelectedItems[0].Text;
            var version = _versionTextBox.Text.Trim();

            _installButton.Enabled = false;
            string args;
            if (!string.IsNullOrEmpty(version))
            {
                args = string.Format("add \"{0}\" package {1} --version {2}", _project.FilePath, packageName, version);
                AppendOutput(string.Format("Installing {0} v{1}...", packageName, version));
            }
            else
            {
                args = string.Format("add \"{0}\" package {1}", _project.FilePath, packageName);
                AppendOutput(string.Format("Installing {0} (latest)...", packageName));
            }

            bool success = await RunDotNetCommandAsync(args);
            _installButton.Enabled = true;

            if (success)
            {
                AppendOutput(string.Format("Successfully installed {0}", packageName));
                LoadInstalledPackages();
            }
        }

        // Shared helper that runs a `dotnet` command off the UI thread, streaming both stdout and
        // stderr live into the output log (via the thread-marshaling AppendOutput). Waits up to
        // 60s and returns true only on a zero exit code. Exceptions are logged, not thrown.
        private async Task<bool> RunDotNetCommandAsync(string arguments)
        {
            bool success = false;

            try
            {
                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(psi))
                    {
                        if (process == null) return;

                        process.OutputDataReceived += (s, args) =>
                        {
                            if (args.Data != null) AppendOutput(args.Data);
                        };
                        process.ErrorDataReceived += (s, args) =>
                        {
                            if (args.Data != null) AppendOutput(args.Data);
                        };

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit(60000);

                        success = process.ExitCode == 0;
                    }
                });
            }
            catch (Exception ex)
            {
                AppendOutput(string.Format("Error: {0}", ex.Message));
            }

            return success;
        }

        // When a search result is selected, prefill the version box with that package's version
        // so an immediate install pins the shown version by default.
        private void OnSearchResultSelected(object sender, EventArgs e)
        {
            if (_searchResultsListView.SelectedItems.Count > 0)
            {
                var versionText = _searchResultsListView.SelectedItems[0].SubItems[1].Text;
                _versionTextBox.Text = versionText;
            }
        }
    }
}
