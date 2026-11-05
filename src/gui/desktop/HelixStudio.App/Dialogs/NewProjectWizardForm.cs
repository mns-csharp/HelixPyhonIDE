namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Workspace.Templates;

    using HelixPythonNamespace.UI.Controls;
    /// <summary>
    /// New Project / Solution Wizard (docs/New_project_sln.md) — a VS2022-style, search-first, template-driven
    /// project creator. A thin WinForms view over the data-driven engine (<see cref="TemplateCatalog"/>,
    /// <see cref="TemplateSearchEngine"/>, <see cref="ProjectGenerator"/>): Page 1 selects a template
    /// (search + facet filters + description), Page 2 configures name/location/options, the Summary confirms,
    /// and Create runs the transactional generator. No project-type-specific code lives here.
    /// </summary>
    public sealed class NewProjectWizardForm : LunaForm
    {
        private readonly TemplateCatalog _catalog;
        private ProjectTemplate _selected;
        private int _page;
        private string _createdRoot;
        // Dynamic per-template configuration pages (SRS §6), inserted between Configure and Summary.
        private readonly System.Collections.Generic.List<Panel> _configPanels = new System.Collections.Generic.List<Panel>();
        private readonly System.Collections.Generic.Dictionary<string, Control> _fieldCtrls = new System.Collections.Generic.Dictionary<string, Control>();

        // Page 1
        private readonly Panel _p1;
        private readonly TextBox _search;
        private readonly ComboBox _langFilter, _typeFilter;
        private ComboBox _categoryFilter;
        private ImageList _templateIcons;   // runtime-drawn colored language monograms
        private readonly ListView _list;
        private readonly RichTextBox _desc;
        private readonly CheckBox _favOnly;
        private readonly Button _favBtn;
        private readonly TemplateUsageStore _usage;   // recently-used + favorites (SRS §4), persisted
        // Page 2
        private readonly Panel _p2;
        private readonly TextBox _name, _location;
        private readonly CheckBox _git, _readme, _gitignore, _license, _restore;
        private readonly Label _validation;
        // Page 3
        private readonly Panel _p3;
        private readonly RichTextBox _summary;
        private Panel _progressPanel;          // shown while Create runs (venv/restore take seconds)
        private ProgressBar _progress;
        private Label _progressStatus;
        internal bool ProgressWasShownDuringCreate;   // observability for headless tests
        // Nav
        private readonly Button _back, _next, _create, _cancel;
        private readonly Label _header;
        private readonly Panel _content;   // single Fill host for all pages (keeps header/nav docking correct)

        private readonly UserSettingsManager _settingsManager;   // optional; null = nothing remembered
        private System.Collections.Generic.HashSet<string> _eligibleIds;      // null = every template listed
        private System.Collections.Generic.List<string> _eligibleIdOrder;     // display order of the restriction
        private Button _browseLocation;
        private FlowLayoutPanel _optionsRow;   // git/README/.gitignore/LICENSE/restore checkboxes
        private bool _addToSolutionMode;       // adding to an OPEN solution: scaffolding extras off
        private bool _standaloneSolutionMode;
        private string _openSolutionRoot;

        public NewProjectWizardForm() : this(TemplateCatalog.Default(), null) { }

        public NewProjectWizardForm(TemplateCatalog catalog) : this(catalog, null) { }

        public NewProjectWizardForm(TemplateCatalog catalog, UserSettingsManager settingsManager)
        {
            _catalog = catalog ?? TemplateCatalog.BuiltIn();
            _settingsManager = settingsManager;
            string usagePath = Environment.GetEnvironmentVariable("HELIX_USAGE_PATH");
            _usage = TemplateUsageStore.Load(string.IsNullOrEmpty(usagePath) ? TemplateUsageStore.DefaultPath() : usagePath);
            Text = "New Project";
            Size = new Size(940, 620);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = LunaFonts.Ui;

            _header = new Label { Dock = DockStyle.Top, Height = 40, Text = "  Create a new project", Font = LunaFonts.Sized(4f), TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White };

            var nav = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10) };
            _cancel = MakeButton("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            _create = MakeButton("Create", (s, e) => DoCreate());
            _next = MakeButton("Next >", (s, e) => Advance());
            _back = MakeButton("< Back", (s, e) => GoTo(_page - 1));
            _cancel.Dock = _create.Dock = _next.Dock = _back.Dock = DockStyle.Right;
            // Order matters: with Dock=Right the first-added control sits leftmost. Add Back → Next →
            // Create → Cancel so the row reads "< Back | Next >/Create | Cancel" left-to-right — the
            // Windows Wizard-97 / VS2022 convention (Back to the LEFT of Next, not after it).
            nav.Controls.Add(_back); nav.Controls.Add(_next); nav.Controls.Add(_create); nav.Controls.Add(_cancel);
            // Esc cancels the wizard from any page (AcceptButton — Enter — is set per page in GoTo,
            // since the primary action changes from "Next >" to "Create" on the summary).
            CancelButton = _cancel;

            _p1 = BuildPage1(out _search, out _langFilter, out _typeFilter, out _list, out _desc, out _favOnly, out _favBtn);
            _p2 = BuildPage2(out _name, out _location, out _git, out _readme, out _gitignore, out _license, out _restore, out _validation);
            _p3 = BuildPage3(out _summary);

            _content = new Panel { Dock = DockStyle.Fill };
            _content.Controls.Add(_p1); _content.Controls.Add(_p2); _content.Controls.Add(_p3);
            // Add the Fill host FIRST (docked last -> gets the area between the Top header and Bottom nav).
            Controls.Add(_content); Controls.Add(nav); Controls.Add(_header);

            _search.TextChanged += (s, e) => RefreshList();
            _langFilter.SelectedIndexChanged += (s, e) => RefreshList();
            _typeFilter.SelectedIndexChanged += (s, e) => RefreshList();
            _categoryFilter.SelectedIndexChanged += (s, e) => RefreshList();
            _list.SelectedIndexChanged += (s, e) => OnTemplateSelected();
            _favOnly.CheckedChanged += (s, e) => RefreshList();
            _favBtn.Click += (s, e) => ToggleFavoriteSelected();
            _name.TextChanged += (s, e) => Validate2();
            _location.TextChanged += (s, e) => Validate2();

            PopulateFilters();
            RefreshList();
            RestoreLastNameAndLocation();
            GoTo(0);
        }

        // "Configure your new project" remembers the last-used Project name and Location.
        // The name is bumped with a numeric suffix when its folder already exists in the
        // remembered location (it always does right after the previous Create), so the
        // page never opens directly onto a "folder already exists" validation error.
        private void RestoreLastNameAndLocation()
        {
            if (_settingsManager == null) return;
            string loc = _settingsManager.Settings.LastNewProjectLocation;
            string name = _settingsManager.Settings.LastNewProjectName;
            if (!string.IsNullOrEmpty(loc)) _location.Text = loc;
            if (!string.IsNullOrEmpty(name)) _name.Text = UniqueProjectName(_location.Text.Trim(), name);
        }

        /// <summary>MyApp → MyApp (free), else MyApp2, MyApp3, … (a trailing number is treated
        /// as an existing counter and incremented, so MyApp2 suggests MyApp3, not MyApp22).</summary>
        internal static string UniqueProjectName(string location, string baseName)
        {
            bool Taken(string n)
            {
                try { return Directory.Exists(Path.Combine(location, n)); }
                catch { return false; }
            }
            if (!Taken(baseName)) return baseName;

            int digits = 0;
            while (digits < baseName.Length && char.IsDigit(baseName[baseName.Length - 1 - digits])) digits++;
            string stem = baseName.Substring(0, baseName.Length - digits);
            int counter;
            if (digits == 0 || !int.TryParse(baseName.Substring(baseName.Length - digits), out counter)) counter = 1;
            if (stem.Length == 0) { stem = baseName; counter = 1; }   // all-digit name: append, don't replace

            for (int n = counter + 1; n < counter + 10000; n++)
            {
                string candidate = stem + n;
                if (!Taken(candidate)) return candidate;
            }
            return baseName;
        }

        // ---- pages ----

        private Panel BuildPage1(out TextBox search, out ComboBox lang, out ComboBox type, out ListView list, out RichTextBox desc, out CheckBox favOnly, out Button favBtn)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };

            var top = new Panel { Dock = DockStyle.Top, Height = 34 };
            search = new TextBox { Dock = DockStyle.Fill, Font = LunaFonts.Sized(1f) };
            var searchLbl = new Label { Dock = DockStyle.Left, Width = 60, Text = "Search", TextAlign = ContentAlignment.MiddleLeft };
            // With dozens of templates (and room for hundreds), three facet filters: the
            // category combo joins language + type. Right-docked: first-added sits rightmost.
            _categoryFilter = new ComboBox { Dock = DockStyle.Right, Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
            type = new ComboBox { Dock = DockStyle.Right, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            lang = new ComboBox { Dock = DockStyle.Right, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            top.Controls.Add(search); top.Controls.Add(lang); top.Controls.Add(type); top.Controls.Add(_categoryFilter); top.Controls.Add(searchLbl);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            // Colored language icons: the old emoji prefixes rendered as monochrome GDI
            // glyphs inside the ListView — a real ImageList is the only way to get color.
            _templateIcons = BuildTemplateIcons();
            list.SmallImageList = _templateIcons;
            list.Columns.Add("Template", 260);
            list.Columns.Add("Language", 110);
            list.Columns.Add("Type", 150);

            var listBar = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            favBtn = new Button { Dock = DockStyle.Left, Width = 140, Text = "★ Toggle favorite", FlatStyle = FlatStyle.System };
            favOnly = new CheckBox { Dock = DockStyle.Left, Width = 120, Text = "Favorites only", TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
            listBar.Controls.Add(favOnly); listBar.Controls.Add(favBtn);

            desc = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248, 248, 248) };
            split.Panel1.Controls.Add(list);
            split.Panel1.Controls.Add(listBar);
            split.Panel2.Controls.Add(desc);

            p.Controls.Add(split);
            p.Controls.Add(top);
            return p;
        }

        private Panel BuildPage2(out TextBox name, out TextBox location, out CheckBox git, out CheckBox readme, out CheckBox gitignore, out CheckBox license, out CheckBox restore, out Label validation)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, Height = 200, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

            name = new TextBox { Dock = DockStyle.Fill, Text = "MyApp" };
            location = new TextBox { Dock = DockStyle.Fill, Text = DefaultLocation() };
            TextBox loc = location;   // local alias: an out param can't be captured in the lambda below
            var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
            browse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) loc.Text = d.SelectedPath; } };
            _browseLocation = browse;

            grid.Controls.Add(new Label { Text = "Project name", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
            grid.Controls.Add(name, 1, 0);
            grid.Controls.Add(new Label(), 2, 0);
            grid.Controls.Add(new Label { Text = "Location", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
            grid.Controls.Add(location, 1, 1);
            grid.Controls.Add(browse, 2, 1);

            var opts = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(120, 4, 0, 0) };
            git = new CheckBox { Text = "Git repository", AutoSize = true, Checked = true };
            readme = new CheckBox { Text = "README", AutoSize = true, Checked = true, Margin = new Padding(16, 3, 3, 3) };
            gitignore = new CheckBox { Text = ".gitignore", AutoSize = true, Checked = true, Margin = new Padding(16, 3, 3, 3) };
            license = new CheckBox { Text = "LICENSE (MIT)", AutoSize = true, Checked = false, Margin = new Padding(16, 3, 3, 3) };
            restore = new CheckBox { Text = "Restore dependencies", AutoSize = true, Checked = true, Margin = new Padding(16, 3, 3, 3) };
            opts.Controls.Add(git); opts.Controls.Add(readme); opts.Controls.Add(gitignore); opts.Controls.Add(license); opts.Controls.Add(restore);
            _optionsRow = opts;

            validation = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Color.Firebrick, Padding = new Padding(120, 4, 0, 0) };

            p.Controls.Add(validation);
            p.Controls.Add(opts);
            p.Controls.Add(grid);
            return p;
        }

        private Panel BuildPage3(out RichTextBox summary)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
            summary = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10f) };

            // Progress strip for Create: hidden until DoCreate runs. Marquee — venv creation
            // and dependency restore are external processes with no measurable progress.
            _progressStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 16, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            _progressPanel = new Panel { Dock = DockStyle.Bottom, Height = 46, Visible = false, Padding = new Padding(0, 4, 0, 2) };
            _progressPanel.Controls.Add(_progressStatus);
            _progressPanel.Controls.Add(_progress);

            p.Controls.Add(summary);
            p.Controls.Add(_progressPanel);
            return p;
        }

        // ---- behaviour ----

        private void PopulateFilters()
        {
            _langFilter.Items.Add("All languages");
            foreach (string v in _catalog.Facet("Language")) _langFilter.Items.Add(v);
            _langFilter.SelectedIndex = 0;
            _typeFilter.Items.Add("All types");
            foreach (string v in _catalog.Facet("ProjectType")) _typeFilter.Items.Add(v);
            _typeFilter.SelectedIndex = 0;
            _categoryFilter.Items.Add("All categories");
            foreach (string v in _catalog.Facet("Category")) _categoryFilter.Items.Add(v);
            _categoryFilter.SelectedIndex = 0;
        }

        // ---- colored language icons (drawn at runtime — no image assets, crisp at any DPI) ----

        private static ImageList BuildTemplateIcons()
        {
            var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 20) };
            AddLanguageIcon(icons, "Python", Color.FromArgb(55, 118, 171), "Py");
            AddLanguageIcon(icons, "CUDA C", Color.FromArgb(118, 185, 0), "Cu");
            AddLanguageIcon(icons, "C++", Color.FromArgb(0, 89, 156), "C+");
            AddLanguageIcon(icons, "", Color.FromArgb(120, 124, 132), "?");   // unknown-language fallback
            return icons;
        }

        private static void AddLanguageIcon(ImageList icons, string key, Color fill, string code)
        {
            var bmp = new Bitmap(20, 20);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var back = new SolidBrush(fill))
                    g.FillEllipse(back, 1, 1, 17, 17);
                using (var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point))
                using (var ink = new SolidBrush(Color.White))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(code, font, ink, new RectangleF(0, 0.5f, 19.5f, 19f), fmt);
                }
            }
            icons.Images.Add(key, bmp);
        }

        private string LanguageIconKey(string language)
        {
            return (_templateIcons != null && language != null && _templateIcons.Images.ContainsKey(language))
                ? language
                : "";
        }

        private void RefreshList()
        {
            var filter = new TemplateFilter
            {
                Language = _langFilter.SelectedIndex > 0 ? (string)_langFilter.SelectedItem : null,
                ProjectType = _typeFilter.SelectedIndex > 0 ? (string)_typeFilter.SelectedItem : null,
                Category = (_categoryFilter != null && _categoryFilter.SelectedIndex > 0)
                    ? (string)_categoryFilter.SelectedItem : null,
            };
            var hits = TemplateSearchEngine.Search(_catalog, _search.Text, filter);
            if (_eligibleIds != null) hits.RemoveAll(h => !_eligibleIds.Contains(h.Template.Id));
            bool browsing = string.IsNullOrEmpty(_search.Text.Trim());
            if (browsing && _eligibleIdOrder != null)
            {
                // Restricted browse ("Add New Project"): the eligibility list's own order wins.
                hits.Sort((a, b) => _eligibleIdOrder.IndexOf(a.Template.Id)
                    .CompareTo(_eligibleIdOrder.IndexOf(b.Template.Id)));
                browsing = false;   // skip the favorites/recents re-sort below
            }
            if (browsing)
            {
                // Default browse order (SRS §4): favorites first, then most-recently-used, then score/name.
                hits.Sort((a, b) =>
                {
                    int fa = _usage.IsFavorite(a.Template.Id) ? 1 : 0, fb = _usage.IsFavorite(b.Template.Id) ? 1 : 0;
                    if (fa != fb) return fb.CompareTo(fa);
                    int cmp = _usage.LastUsed(b.Template.Id).CompareTo(_usage.LastUsed(a.Template.Id));
                    if (cmp != 0) return cmp;
                    return string.Compare(a.Template.Name, b.Template.Name, StringComparison.OrdinalIgnoreCase);
                });
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var h in hits)
            {
                if (_favOnly.Checked && !_usage.IsFavorite(h.Template.Id)) continue;
                string star = _usage.IsFavorite(h.Template.Id) ? "★ " : "";
                // Colored icon from the ImageList — the old emoji text prefix rendered
                // monochrome under GDI, so it's replaced, not kept alongside.
                var it = new ListViewItem(star + h.Template.Name) { ImageKey = LanguageIconKey(h.Template.Language) };
                it.SubItems.Add(h.Template.Language);
                it.SubItems.Add(h.Template.ProjectType);
                it.Tag = h.Template;
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            else { _selected = null; _desc.Clear(); }
        }

        private void OnTemplateSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            _selected = _list.SelectedItems[0].Tag as ProjectTemplate;
            if (_selected == null) return;
            var t = _selected;
            _desc.Clear();
            AppendBold(t.Name + "\n");
            _desc.AppendText(t.Description + "\n\n");
            AppendKV("Language", t.Language);
            AppendKV("Framework", t.Framework);
            AppendKV("Type", t.ProjectType);
            AppendKV("Platform", t.Platform);
            AppendKV("Category", t.Category);
            AppendKV("Author", t.Author);
            AppendKV("Version", t.Version);
            AppendKV("Installed by", t.InstalledBy);
            if (t.Dependencies.Count > 0) AppendKV("Dependencies", string.Join(", ", t.Dependencies.ToArray()));
        }

        private void Validate2()
        {
            _validation.Text = ValidationMessage() ?? "";
        }

        private string ValidationMessage()
        {
            string n = _name.Text.Trim();
            if (string.IsNullOrEmpty(n)) return "Project name is required.";
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Project name contains illegal characters.";
            string loc = _location.Text.Trim();
            if (string.IsNullOrEmpty(loc)) return "Location is required.";
            try { Path.GetFullPath(loc); } catch { return "Location is not a valid path."; }
            if (_standaloneSolutionMode && IsSameOrDescendant(loc, _openSolutionRoot))
                return "A new solution must be created outside the currently open solution.";
            if (Directory.Exists(Path.Combine(loc, n))) return "A folder named '" + n + "' already exists in this location.";
            return null;
        }

        // Steps: 0 = Select, 1 = Configure, 2..(2+D-1) = the D template config pages, (2+D) = Summary.
        private int TotalSteps { get { return 2 + _configPanels.Count + 1; } }
        private int SummaryStep { get { return 2 + _configPanels.Count; } }

        // Forward navigation: build the (template-dependent) config pages when leaving Configure.
        private void Advance()
        {
            if (_page == 1) BuildConfigPanels();
            GoTo(_page + 1);
        }

        private void GoTo(int step)
        {
            if (step < 0) step = 0;
            if (step > SummaryStep) step = SummaryStep;
            if (step == SummaryStep) BuildSummary();
            _page = step;

            _p1.Visible = step == 0;
            _p2.Visible = step == 1;
            _p3.Visible = step == SummaryStep;
            for (int i = 0; i < _configPanels.Count; i++)
                _configPanels[i].Visible = (step == 2 + i);

            _back.Enabled = step > 0;
            _next.Visible = step < SummaryStep;
            _create.Visible = step == SummaryStep;

            // Enter triggers the current primary action: advance through the pages, then Create on the
            // summary (VS2022 parity). A single-line TextBox/ListView doesn't consume Enter, so this
            // fires from the name/search fields too.
            AcceptButton = (step == SummaryStep) ? _create : _next;

            if (step == 0) _header.Text = "  Select a template";
            else if (step == 1) { _header.Text = "  Configure your new project"; Validate2(); }
            else if (step == SummaryStep) _header.Text = "  Summary";
            else _header.Text = "  " + _selected.ConfigPages[step - 2].Title + " options";
        }

        // Rebuild the config-page panels for the currently-selected template (SRS §6). Called on each
        // forward transition out of Configure so changing the template re-derives the pages.
        private void BuildConfigPanels()
        {
            foreach (Panel p in _configPanels) { _content.Controls.Remove(p); p.Dispose(); }
            _configPanels.Clear();
            _fieldCtrls.Clear();
            if (_selected == null) return;

            foreach (TemplateConfigPage page in _selected.ConfigPages)
            {
                var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
                // (The blue window header already reads "<Title> options"; no in-panel title needed.)
                var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                int row = 0;
                foreach (TemplateConfigField f in page.Fields)
                {
                    var lbl = new Label { Text = f.Label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Height = 28 };
                    Control ctrl = MakeFieldControl(f);
                    ctrl.Tag = f;
                    _fieldCtrls[f.Name] = ctrl;
                    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                    grid.Controls.Add(lbl, 0, row);
                    grid.Controls.Add(ctrl, 1, row);
                    row++;
                }
                panel.Controls.Add(grid);
                _content.Controls.Add(panel);
                _configPanels.Add(panel);
            }

            // Adding to an open solution: the workspace root owns the venv — a per-member
            // .venv is never the right default there (the tick stays available to override).
            if (_addToSolutionMode)
            {
                Control venv;
                if (_fieldCtrls.TryGetValue("UseVenv", out venv) && venv is CheckBox venvCb)
                    venvCb.Checked = false;
            }
        }

        private static Control MakeFieldControl(TemplateConfigField f)
        {
            if (f.Type == ConfigFieldType.Bool)
                return new CheckBox { AutoSize = true, Checked = string.Equals(f.Default, "true", StringComparison.OrdinalIgnoreCase), Margin = new Padding(3, 4, 3, 3) };
            if (f.Type == ConfigFieldType.Choice)
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
                foreach (string c in f.Choices) combo.Items.Add(c);
                int idx = combo.Items.IndexOf(f.Default);
                combo.SelectedIndex = idx >= 0 ? idx : (combo.Items.Count > 0 ? 0 : -1);
                return combo;
            }
            return new TextBox { Text = f.Default, Width = 220 };
        }

        // Read the config-field values (their {{Name}} bindings) from the rendered controls.
        private System.Collections.Generic.Dictionary<string, string> ReadConfigValues()
        {
            var vals = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in _fieldCtrls)
            {
                Control c = kv.Value;
                var f = c.Tag as TemplateConfigField;
                string v;
                var cb = c as CheckBox;
                var combo = c as ComboBox;
                if (cb != null) v = cb.Checked ? (f != null ? f.BoolTrue : "true") : (f != null ? f.BoolFalse : "false");
                else if (combo != null) v = combo.SelectedItem != null ? combo.SelectedItem.ToString() : "";
                else v = c.Text;
                vals[kv.Key] = v;
            }
            return vals;
        }

        private void BuildSummary()
        {
            _summary.Clear();
            if (_selected == null) { _summary.AppendText("No template selected."); return; }
            _summary.AppendText(
                "Template   : " + _selected.Name + "\n" +
                "Language   : " + _selected.Language + "\n" +
                "Framework  : " + _selected.Framework + "\n" +
                "Project    : " + _name.Text.Trim() + "\n" +
                "Location   : " + _location.Text.Trim() + "\n" +
                "Full path  : " + Path.Combine(_location.Text.Trim(), _name.Text.Trim()) + "\n" +
                "Git        : " + (_git.Checked ? "yes" : "no") + "\n" +
                "README     : " + (_readme.Checked ? "yes" : "no") + "\n" +
                ".gitignore : " + (_gitignore.Checked ? "yes" : "no") + "\n");
            var cfg = ReadConfigValues();
            if (cfg.Count > 0)
            {
                _summary.AppendText("\n-- template options --\n");
                foreach (var kv in cfg) _summary.AppendText(("  " + kv.Key).PadRight(24) + ": " + kv.Value + "\n");
            }
        }

        private void DoCreate()
        {
            string msg = ValidationMessage();
            if (msg != null) { MessageBox.Show(this, msg, "Cannot create", MessageBoxButtons.OK, MessageBoxIcon.Warning); GoTo(1); return; }
            var opt = new GenerationOptions { TargetRoot = _location.Text.Trim() };
            opt.Variables["ProjectName"] = _name.Text.Trim();
            opt.Variables["Description"] = _selected.Description;
            foreach (var kv in ReadConfigValues()) opt.Variables[kv.Key] = kv.Value;   // SRS §6 field values

            // Create can run for many seconds (venv creation, dependency restore are external
            // processes) — show the summary page's progress strip and lock the nav for the
            // duration; the message loop keeps pumping so the marquee animates.
            _back.Enabled = _next.Enabled = _create.Enabled = _cancel.Enabled = false;
            ShowProgress("Generating project files…");
            try
            {
                var r = ProjectGenerator.Generate(_selected, opt);
                if (!r.Success) { MessageBox.Show(this, "Generation failed: " + r.Error, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                _createdRoot = Path.Combine(opt.TargetRoot, _name.Text.Trim());

                // Post-generation actions (SRS §8): README / .gitignore / LICENSE, git init, venv.
                // Runs on a worker so the UI can repaint; each action reports itself just before
                // it executes ("Creating virtual environment (.venv)…" while python -m venv runs).
                var requests = BuildPostGenRequests();
                var work = System.Threading.Tasks.Task.Run(() => PostGenRunner.Run(_createdRoot, requests, ReportPostGenStep));
                while (!work.IsCompleted) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                _postGen = work.Result;

                // Record usage for "recently used" ranking (SRS §4).
                try { _usage.RecordUse(_selected.Id, DateTime.UtcNow); _usage.Save(); } catch { }

                if (_settingsManager != null)
                {
                    // A member project's location is forcibly the active solution root. Persisting
                    // it poisoned the next standalone New Solution dialog and physically nested the
                    // new solution inside the old one. Member creation must never update either
                    // standalone or general creation history.
                    if (!_addToSolutionMode)
                    {
                        if (_standaloneSolutionMode)
                        {
                            _settingsManager.Settings.LastNewSolutionName = _name.Text.Trim();
                            _settingsManager.Settings.LastNewSolutionLocation = _location.Text.Trim();
                        }
                        else
                        {
                            _settingsManager.Settings.LastNewProjectName = _name.Text.Trim();
                            _settingsManager.Settings.LastNewProjectLocation = _location.Text.Trim();
                        }
                        _settingsManager.Save();
                    }
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            finally
            {
                HideProgress();
                _back.Enabled = _next.Enabled = _create.Enabled = _cancel.Enabled = true;
            }
        }

        private void ShowProgress(string status)
        {
            ProgressWasShownDuringCreate = true;
            _progressStatus.Text = status;
            _progressPanel.Visible = true;
            // Paint immediately — the very next call blocks the UI thread in the generator.
            try { _progressPanel.Refresh(); } catch { }
        }

        private void HideProgress()
        {
            _progressPanel.Visible = false;
        }

        // Called on the post-gen WORKER thread just before each action executes.
        private void ReportPostGenStep(PostGenRequest req)
        {
            string text = StepText(req.Type);
            try
            {
                if (_progressStatus.IsHandleCreated)
                    _progressStatus.BeginInvoke((Action)(() => _progressStatus.Text = text));
                else
                    _progressStatus.Text = text;   // headless drivers: no handle, direct set is safe
            }
            catch { }   // form torn down mid-create: the step still runs, only the label update is lost
        }

        private static string StepText(PostGenActionType t)
        {
            switch (t)
            {
                case PostGenActionType.Venv: return "Creating virtual environment (.venv)… this can take a moment";
                case PostGenActionType.Restore: return "Restoring dependencies…";
                case PostGenActionType.GitInit: return "Initialising Git repository…";
                case PostGenActionType.Readme: return "Writing README.md…";
                case PostGenActionType.Gitignore: return "Writing .gitignore…";
                case PostGenActionType.License: return "Writing LICENSE…";
                default: return "Running post-generation steps…";
            }
        }

        // Build the post-generation action list from the Page-2 options + the selected template.
        private System.Collections.Generic.List<PostGenRequest> BuildPostGenRequests()
        {
            var list = new System.Collections.Generic.List<PostGenRequest>();
            string name = _name.Text.Trim();
            if (_readme.Checked) list.Add(new PostGenRequest { Type = PostGenActionType.Readme, ProjectName = name, Description = _selected.Description });
            if (_gitignore.Checked) list.Add(new PostGenRequest { Type = PostGenActionType.Gitignore, Language = _selected.Language });
            if (_license.Checked) list.Add(new PostGenRequest { Type = PostGenActionType.License, Author = Environment.UserName, Year = DateTime.Now.Year });
            if (_git.Checked) list.Add(new PostGenRequest { Type = PostGenActionType.GitInit });
            string useVenv;
            if (string.Equals(_selected.Language, "Python", StringComparison.OrdinalIgnoreCase)
                && ReadConfigValues().TryGetValue("UseVenv", out useVenv) && string.Equals(useVenv, "true", StringComparison.OrdinalIgnoreCase))
                list.Add(new PostGenRequest { Type = PostGenActionType.Venv });
            if (_restore.Checked)   // dependency restore last, once files + venv exist (SRS §8)
                list.Add(new PostGenRequest { Type = PostGenActionType.Restore, Language = _selected.Language });
            return list;
        }

        private PostGenReport _postGen;
        /// <summary>Post-generation action results (null until Create runs).</summary>
        public PostGenReport PostGenResults { get { return _postGen; } }

        /// <summary>Root directory of the project that was created (null until Create succeeds).</summary>
        public string CreatedProjectRoot { get { return _createdRoot; } }
        /// <summary>The template chosen (for callers/tests).</summary>
        public ProjectTemplate SelectedTemplate { get { return _selected; } }

        // ---- drive hooks (used by the GUI harness) ----
        public void SetSearch(string q) { _search.Text = q ?? ""; }
        public void SelectFirstTemplate()
        {
            if (_list.Items.Count == 0) return;
            _list.Items[0].Selected = true;
            // On an unshown form the ListView has no handle yet, so Selected=true raises no
            // SelectedIndexChanged — bind _selected directly so headless drivers still work.
            if (_selected == null) _selected = _list.Items[0].Tag as ProjectTemplate;
        }
        public void SetOptions(bool git, bool readme, bool gitignore, bool license) { _git.Checked = git; _readme.Checked = readme; _gitignore.Checked = gitignore; _license.Checked = license; }
        public void ToggleFavorite() { ToggleFavoriteSelected(); }
        public void SetFavoritesOnly(bool on) { _favOnly.Checked = on; }

        // Toggle the selected template's favorite flag, persist, and refresh while keeping it selected.
        private void ToggleFavoriteSelected()
        {
            if (_selected == null) return;
            string keepId = _selected.Id;
            _usage.ToggleFavorite(keepId);
            _usage.Save();
            RefreshList();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                var t = _list.Items[i].Tag as ProjectTemplate;
                if (t != null && t.Id == keepId) { _list.Items[i].Selected = true; _list.Items[i].EnsureVisible(); break; }
            }
        }
        /// <summary>Set name/location and run Create (generation + post-gen). Returns true on success.</summary>
        public bool CreateNow(string location, string name)
        {
            _location.Text = location; _name.Text = name;
            if (_selected == null) SelectFirstTemplate();
            DoCreate();
            return DialogResult == DialogResult.OK && !string.IsNullOrEmpty(_createdRoot);
        }
        public void GoNext() { Advance(); }
        public int CurrentPage { get { return _page; } }
        internal string ProjectNameText => _name.Text;
        internal string LocationText => _location.Text;

        /// <summary>
        /// Restrict the Select Template page to the given template ids ("Add New Project"
        /// eligibility per the open solution's language — see TemplateEligibility). Null or
        /// empty clears the restriction.
        /// </summary>
        public void RestrictToTemplates(System.Collections.Generic.IEnumerable<string> templateIds)
        {
            var order = templateIds == null
                ? null
                : new System.Collections.Generic.List<string>(templateIds);
            if (order != null && order.Count == 0) order = null;
            _eligibleIdOrder = order;
            _eligibleIds = order == null
                ? null
                : new System.Collections.Generic.HashSet<string>(order, StringComparer.Ordinal);
            RefreshList();
        }

        /// <summary>
        /// "Add New Project" into an OPEN solution: the standalone-solution scaffolding
        /// extras are wrong there and are switched off and hidden — git init would nest a
        /// repo inside the solution's repo, .gitignore/LICENSE/README duplicate the
        /// solution's own, a per-member .venv fights the workspace-root venv, and
        /// dependency restore belongs to the workspace. (VS2022 parity: its Add New
        /// Project dialog offers none of these either.)
        /// </summary>
        public void SetAddToSolutionMode()
        {
            _addToSolutionMode = true;
            _git.Checked = _readme.Checked = _gitignore.Checked = _license.Checked = _restore.Checked = false;
            if (_optionsRow != null) _optionsRow.Visible = false;
        }

        /// <summary>Configures File - New Solution. Its remembered parent directory is isolated
        /// from Add Project, and the active solution tree is an invalid destination.</summary>
        internal void SetStandaloneSolutionMode(string openSolutionRoot)
        {
            _standaloneSolutionMode = true;
            _openSolutionRoot = openSolutionRoot;

            string location = _settingsManager != null
                ? _settingsManager.Settings.LastNewSolutionLocation
                : null;
            if (string.IsNullOrWhiteSpace(location) || IsSameOrDescendant(location, openSolutionRoot))
                location = DefaultLocation();

            string name = _settingsManager != null
                ? _settingsManager.Settings.LastNewSolutionName
                : null;
            if (string.IsNullOrWhiteSpace(name)) name = "MyApp";
            _location.Text = location;
            _name.Text = UniqueProjectName(location, name);
            Validate2();
        }

        private static bool IsSameOrDescendant(string candidate, string root)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                string fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Lock Location to a fixed directory ("Add New Project": workspace members are
        /// root-relative, so the new project must be created in the solution root).
        /// </summary>
        public void SetFixedLocation(string directory)
        {
            _location.Text = directory ?? string.Empty;
            _location.ReadOnly = true;
            if (_browseLocation != null) _browseLocation.Enabled = false;
            // The remembered name was uniquified against the OLD location — redo it here.
            _name.Text = UniqueProjectName(_location.Text.Trim(), _name.Text.Trim());
            Validate2();
        }

        /// <summary>Template ids currently listed on the Select Template page (top-down), for tests.</summary>
        internal System.Collections.Generic.List<string> ListedTemplateIds
        {
            get
            {
                var ids = new System.Collections.Generic.List<string>();
                foreach (ListViewItem it in _list.Items)
                {
                    var t = it.Tag as ProjectTemplate;
                    if (t != null) ids.Add(t.Id);
                }
                return ids;
            }
        }

        /// <summary>Set a template config-page bool field (e.g. "UseVenv") from a headless driver.
        /// Only valid after the config pages exist (i.e. after advancing past Configure).</summary>
        internal bool SetConfigBool(string fieldName, bool value)
        {
            Control c;
            if (!_fieldCtrls.TryGetValue(fieldName, out c)) return false;
            var cb = c as CheckBox;
            if (cb == null) return false;
            cb.Checked = value;
            return true;
        }

        // ---- helpers ----
        private Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Width = 92, Height = 28, Margin = new Padding(6, 0, 0, 0) };
            b.Click += onClick;
            return b;
        }
        private void AppendBold(string s) { _desc.SelectionFont = LunaFonts.SizedBold(2f); _desc.AppendText(s); _desc.SelectionFont = _desc.Font; }
        private void AppendKV(string k, string v) { if (string.IsNullOrEmpty(v)) return; _desc.SelectionColor = Color.Gray; _desc.AppendText(k + ": "); _desc.SelectionColor = Color.Black; _desc.AppendText(v + "\n"); }
        private static string DefaultLocation()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { return Environment.CurrentDirectory; }
        }
    }
}
