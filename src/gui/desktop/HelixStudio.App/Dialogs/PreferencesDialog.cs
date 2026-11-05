namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.App.Services;

    /// <summary>
    /// Unified tabbed preferences dialog. Aggregates settings that used to be scattered
    /// across Grid Settings and Build Tool Settings dialogs. Does NOT replace those
    /// entry points (left as legacy shortcuts) — just gives a single "Preferences..."
    /// surface.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class PreferencesDialog : LunaForm
    {
        public PreferencesDialog()
        {
            InitializeComponent();
        }

        // Backing services: the settings store this dialog reads/writes, and the SDK detector
        // used to populate the Build Tools tab. _detector may be null (tab loads no SDK rows).
        private readonly UserSettingsManager _settings;
        private readonly BuildToolDetector _detector;

        // Editor-tab controls (built in code and added to the designer's TabControl).
        private ComboBox _fontNameCombo;
        private NumericUpDown _fontSizeInput;
        private Label _fontPreview;

        // AI tab. Masked because these are credentials; see BuildAiTab for why the box is the
        // fallback rather than the recommended route.
        private TextBox _anthropicKeyInput;
        private TextBox _openAiKeyInput;
        private Label _aiKeyWarning;

        // General-tab theme picker (Blue / Green / Crimson).
        private ComboBox _themeCombo;

        // General-tab solution-policy toggle: relaxes rule PY8002 (object orientation).
        private CheckBox _allowProceduralCheck;

        private static readonly HelixPythonNamespace.UI.Controls.LunaThemeKind[] ThemeOrder =
        {
            HelixPythonNamespace.UI.Controls.LunaThemeKind.Blue,
            HelixPythonNamespace.UI.Controls.LunaThemeKind.Green,
            HelixPythonNamespace.UI.Controls.LunaThemeKind.Crimson,
        };





        /// <summary>True on OK — caller should re-apply derived state (snap flag, grid, etc.).</summary>
        public bool SettingsChanged { get; private set; }

        // Real constructor: captures the services and immediately mirrors persisted settings
        // into the controls. settings is required; detector is optional.
        public PreferencesDialog(UserSettingsManager settings, BuildToolDetector detector) : this()
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings;
            _detector = detector;

            PopulateGeneralTab();
            BuildEditorTab();
            BuildAiTab();
            LoadFromSettings();
            RefreshAddInList();
        }

        // AI tab: API keys for the AI Terminal's providers.
        //
        // The environment variable is presented as the PRIMARY route and the text box as the
        // fallback, not the other way round: a key typed here is persisted in plain text in
        // user-settings.json, which is the wrong default for a credential. The box exists because
        // a user without a configured environment otherwise has no in-product way to get started.
        private void BuildAiTab()
        {
            var tab = new TabPage("AI") { Padding = new Padding(12) };
            var f = LunaFonts.Ui;
            var hintColor = Color.FromArgb(120, 120, 120);

            tab.Controls.Add(new Label
            {
                Text = "Keys for the AI Terminal. Leave a field empty to use the environment "
                     + "variable instead — that is the recommended setup.",
                Location = new Point(14, 12),
                Size = new Size(460, 32),
                ForeColor = hintColor,
                Font = LunaFonts.Sized(-0.5f),
            });

            tab.Controls.Add(new Label { Text = "Claude:", Location = new Point(14, 56), Size = new Size(70, 20), Font = f });
            _anthropicKeyInput = new TextBox
            {
                Location = new Point(90, 53),
                Size = new Size(380, 24),
                Font = f,
                // Keys are shoulder-surfable secrets; never render one in clear text.
                UseSystemPasswordChar = true,
            };
            tab.Controls.Add(_anthropicKeyInput);
            tab.Controls.Add(new Label
            {
                Text = "Otherwise ANTHROPIC_API_KEY, or an `ant auth login` profile.",
                Location = new Point(90, 80),
                Size = new Size(380, 18),
                ForeColor = hintColor,
                Font = LunaFonts.Sized(-0.5f),
            });

            tab.Controls.Add(new Label { Text = "OpenAI:", Location = new Point(14, 112), Size = new Size(70, 20), Font = f });
            _openAiKeyInput = new TextBox
            {
                Location = new Point(90, 109),
                Size = new Size(380, 24),
                Font = f,
                UseSystemPasswordChar = true,
            };
            tab.Controls.Add(_openAiKeyInput);
            tab.Controls.Add(new Label
            {
                Text = "Otherwise OPENAI_API_KEY.",
                Location = new Point(90, 136),
                Size = new Size(380, 18),
                ForeColor = hintColor,
                Font = LunaFonts.Sized(-0.5f),
            });

            _aiKeyWarning = new Label
            {
                Location = new Point(14, 172),
                Size = new Size(460, 46),
                ForeColor = Color.FromArgb(150, 90, 30),
                Font = LunaFonts.Sized(-0.5f),
                Text = "A key entered here is stored unencrypted in user-settings.json under your "
                     + "profile. Prefer the environment variable on a shared machine.",
            };
            tab.Controls.Add(_aiKeyWarning);

            tab.Controls.Add(new Label
            {
                Text = "The agent can edit files and run commands in the open solution. Every write "
                     + "and every command is confirmed before it happens, and \"Undo edits\" rolls "
                     + "back everything one run changed.",
                Location = new Point(14, 222),
                Size = new Size(460, 48),
                ForeColor = hintColor,
                Font = LunaFonts.Sized(-0.5f),
            });

            tabs.Controls.Add(tab);
        }

        // Editor tab: pick the monospace font + size used by every editor document. Built in code
        // (the designer owns the other three tabs) and inserted before the Build Tools tab.
        private void BuildEditorTab()
        {
            var tab = new TabPage("Editor") { Padding = new Padding(12) };
            var f = LunaFonts.Ui;

            tab.Controls.Add(new Label { Text = "Font:", Location = new Point(14, 16), Size = new Size(70, 20), Font = f });
            _fontNameCombo = new ComboBox
            {
                Location = new Point(90, 13),
                Size = new Size(220, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = f,
            };
            foreach (string name in MonospaceFontNames()) _fontNameCombo.Items.Add(name);
            tab.Controls.Add(_fontNameCombo);

            tab.Controls.Add(new Label { Text = "Size:", Location = new Point(14, 52), Size = new Size(70, 20), Font = f });
            _fontSizeInput = new NumericUpDown
            {
                Location = new Point(90, 49),
                Size = new Size(70, 24),
                Minimum = 7,
                Maximum = 32,
                Font = f,
            };
            tab.Controls.Add(_fontSizeInput);

            tab.Controls.Add(new Label
            {
                Text = "Applies to all open and newly-opened editor documents.",
                Location = new Point(14, 86),
                Size = new Size(440, 18),
                ForeColor = Color.FromArgb(120, 120, 120),
                Font = LunaFonts.Sized(-0.5f),
            });

            // Live preview: shows the chosen font + size on a code-like sample so the user sees the
            // result before applying. Updated whenever the font or size changes.
            _fontPreview = new Label
            {
                Location = new Point(14, 116),
                Size = new Size(452, 96),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 30, 30),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8),
                Text = "class Sample:  # AaBbCc 0123\r\n    def area(self, r): return 3.14 * r * r\r\n    items = [1, 2, 3]  # ()[]{}  != => <=",
            };
            tab.Controls.Add(_fontPreview);

            _fontNameCombo.SelectedIndexChanged += (s, e) => UpdateFontPreview();
            _fontSizeInput.ValueChanged += (s, e) => UpdateFontPreview();

            // Append the tab. Adding via Controls (as the designer does for the other tabs) is
            // reliable before the TabControl's handle exists, unlike TabPages.Insert.
            tabs.Controls.Add(tab);
        }

        // Render the sample text in the currently-selected editor font + size. Falls back silently if
        // the font can't be created (e.g. a name that no longer resolves).
        private void UpdateFontPreview()
        {
            if (_fontPreview == null) return;
            string name = _fontNameCombo?.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            float size = (float)(_fontSizeInput?.Value ?? 10m);
            try
            {
                Font old = _fontPreview.Font;
                _fontPreview.Font = new Font(name, size);
                if (old != null && old != Font) old.Dispose();
            }
            catch { /* keep the previous preview font */ }
        }

        // Common coding fonts that are actually installed, plus the current selection so it is
        // always representable even if it isn't in the preferred list. Never empty.
        private IEnumerable<string> MonospaceFontNames()
        {
            string[] preferred =
            {
                "Consolas", "Cascadia Code", "Cascadia Mono", "JetBrains Mono", "Fira Code",
                "Courier New", "Lucida Console", "Lucida Sans Typewriter", "DejaVu Sans Mono",
            };
            var installed = new HashSet<string>(
                FontFamily.Families.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var result = preferred.Where(installed.Contains).ToList();

            string current = _settings?.Settings?.EditorFontName;
            if (!string.IsNullOrEmpty(current)
                && !result.Any(r => r.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                result.Insert(0, current);
            }
            if (result.Count == 0) result.Add("Consolas");
            return result;
        }


        // General tab: theme picker plus the snap-to-grid and solution-policy toggles.
        //
        // The General / Canvas / Build Tools TabPages are created by the DESIGNER
        // (InitializeComponent), not here — so this ADDS to the designer's `generalTab` rather
        // than building a page of its own. An earlier `BuildGeneralTab()` returned a brand-new
        // TabPage that nothing ever called or added, which is why the theme picker and the
        // solution-policy toggle below were unreachable in the running IDE: the fields stayed
        // null and every read of them was null-guarded into a no-op. Anything added to a
        // designer-owned tab must go through a method like this one, called from the constructor.
        private void PopulateGeneralTab()
        {
            themeLabel.Text = "Theme:";
            themeLabel.Location = new Point(14, 16);
            themeLabel.Size = new Size(60, 20);
            themeLabel.Font = LunaFonts.Ui;

            _themeCombo = new ComboBox();
            _themeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _themeCombo.Location = new Point(78, 12);
            _themeCombo.Size = new Size(200, 24);
            _themeCombo.Font = LunaFonts.Ui;
            foreach (var kind in ThemeOrder)
                _themeCombo.Items.Add(HelixPythonNamespace.UI.Controls.LunaThemes.DisplayName(kind));

            _snapCheck.Location = new Point(14, 50);
            _snapCheck.Size = new Size(400, 24);
            _snapCheck.Font = LunaFonts.Ui;

            // Solution policy: rule PY8002 ("user-written code is object-oriented") is a hard
            // blocker by default — the virtual compiler reports every module-level def as an error
            // and Run/Debug refuse to launch. This relaxes it for solutions of existing procedural
            // code. Rule PY8001 (workspace membership) is deliberately NOT offered as a toggle.
            //
            // The hint spells out the test-project carve-out (TestProtocolFunctionRule) because
            // this switch is IDE-wide: a user who hits PY8002 on an ordinary pytest test and finds
            // only this checkbox will trade the OOP guarantee on all of their production code, in
            // every solution, for something the rule already allows.
            var policyLabel = new Label();
            policyLabel.Text = "Solution policy:";
            policyLabel.Location = new Point(14, 88);
            policyLabel.Size = new Size(140, 20);
            policyLabel.Font = LunaFonts.Ui;

            _allowProceduralCheck = new CheckBox();
            _allowProceduralCheck.Name = "allowProceduralCheckBox";
            _allowProceduralCheck.Text = "Allow non-object-oriented source (module-level functions)";
            _allowProceduralCheck.Location = new Point(14, 112);
            _allowProceduralCheck.Size = new Size(440, 24);
            _allowProceduralCheck.Font = LunaFonts.Ui;

            var policyHint = new Label();
            policyHint.Text =
                "When off, every module-level def other than def main() is reported as PY8002 and " +
                "blocks Run and Debug. Test projects are exempt for test_* functions, " +
                "@pytest.fixture and pytest hooks — you do not need this switch to write tests.";
            policyHint.Location = new Point(32, 136);
            policyHint.Size = new Size(450, 48);
            policyHint.Font = LunaFonts.Sized(-0.5f);
            policyHint.ForeColor = Color.FromArgb(120, 120, 120);

            generalTab.Controls.Add(_themeCombo);
            generalTab.Controls.Add(policyLabel);
            generalTab.Controls.Add(_allowProceduralCheck);
            generalTab.Controls.Add(policyHint);
        }

        // ---------------- Install Add-in tab (spec 001, §4.6) ----------------

        /// <summary>The tab's text, exactly as the spec names it.</summary>
        internal const string InstallAddInTabText = "Install Add-in";

        // The shell whose add-ins the tab manages; null when the dialog is opened without one (the
        // tab then lists nothing and Install stays disabled).
        private MainForm _addInShell;
        private List<AddInCatalogEntry> _addInEntries = new List<AddInCatalogEntry>();

        /// <summary>Seam (Chapter 4, §4.6.4): returns the DLL path to install, or null for Cancel.
        /// Null means the real OpenFileDialog.</summary>
        internal Func<string> PickAddInDll;

        /// <summary>Seam: asks to confirm the removal (the question is the argument). Null means a real MessageBox.</summary>
        internal Func<string, bool> ConfirmAddInRemoval;

        /// <summary>Seam: tells the user why an install was refused. Null means a real MessageBox.</summary>
        internal Action<string> ShowAddInMessage;

        internal void AttachAddInShell(MainForm shell)
        {
            _addInShell = shell;
            RefreshAddInList();
        }

        /// <summary>Selects the tab whose text is <paramref name="text"/>; false when there is none.</summary>
        internal bool SelectTab(string text)
        {
            foreach (TabPage page in tabs.TabPages)
            {
                if (page.Text != text) continue;
                tabs.SelectedTab = page;
                return true;
            }
            return false;
        }

        /// <summary>The rows the list shows now, in order.</summary>
        internal IList<AddInCatalogEntry> VisibleAddInRows
        {
            get { return _addInList.Items.Cast<AddInCatalogEntry>().ToList(); }
        }

        private void RefreshAddInList()
        {
            _addInEntries = _addInShell != null ? AddInCatalog.List(_addInShell) : new List<AddInCatalogEntry>();
            ApplyAddInFilter();
        }

        private void ApplyAddInFilter()
        {
            string selected = _addInList.SelectedItem is AddInCatalogEntry s ? s.Id : null;
            _addInList.BeginUpdate();
            _addInList.Items.Clear();
            foreach (AddInCatalogEntry entry in AddInCatalog.Filter(_addInEntries, _addInSearch.Text))
            {
                int at = _addInList.Items.Add(entry);
                if (entry.Id == selected) _addInList.SelectedIndex = at;
            }
            _addInList.EndUpdate();
            _installAddInButton.Enabled = _addInShell != null;
            OnAddInSelectionChanged(this, EventArgs.Empty);
        }

        private void OnAddInSearchChanged(object sender, EventArgs e)
        {
            ApplyAddInFilter();
        }

        private void OnAddInSelectionChanged(object sender, EventArgs e)
        {
            _removeAddInButton.Enabled = _addInShell != null && _addInList.SelectedItem != null;
        }

        private void OnInstallAddInClick(object sender, EventArgs e)
        {
            if (_addInShell == null) return;
            string dll = PickAddInDll != null ? PickAddInDll() : AskForAddInDll();
            if (string.IsNullOrEmpty(dll)) return;

            string refused = AddInInstaller.Install(_addInShell, dll);
            RefreshAddInList();
            if (refused != null) TellAddInUser(refused);
        }

        private void OnRemoveAddInClick(object sender, EventArgs e)
        {
            var entry = _addInList.SelectedItem as AddInCatalogEntry;
            if (_addInShell == null || entry == null) return;
            string question = "Remove the add-in \"" + entry.DisplayName + "\"?\r\n\r\nIt leaves the IDE now."
                + (entry.Kind == AddInKind.User ? " Its files are deleted at the next start." : "");
            bool yes = ConfirmAddInRemoval != null
                ? ConfirmAddInRemoval(question)
                : MessageBox.Show(this, question, "Remove Add-in", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!yes) return;

            _addInShell.RemoveAddIn(entry.Id);
            RefreshAddInList();
        }

        private string AskForAddInDll()
        {
            using (var dialog = new OpenFileDialog { Title = "Install Add-in", Filter = "Add-in (*.dll)|*.dll", CheckFileExists = true })
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        private void TellAddInUser(string message)
        {
            if (ShowAddInMessage != null) ShowAddInMessage(message);
            else MessageBox.Show(this, message, "Install Add-in", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Mirrors persisted settings into the controls: snap flag, grid spacing (re-clamped to the
        // NumericUpDown's range), and the detected SDK list with the persisted default pre-checked.
        private void LoadFromSettings()
        {
            var s = _settings.Settings;
            _snapCheck.Checked = s.SnapToGrid;
            if (_allowProceduralCheck != null) _allowProceduralCheck.Checked = s.AllowNonObjectOrientedCode;

            // The real key is loaded into the masked box so the user can see one IS set and can
            // clear it. Substituting a placeholder here would be worse: OK would then overwrite
            // the stored key with the placeholder text.
            if (_anthropicKeyInput != null) _anthropicKeyInput.Text = s.AnthropicApiKey ?? string.Empty;
            if (_openAiKeyInput != null) _openAiKeyInput.Text = s.OpenAiApiKey ?? string.Empty;

            if (_themeCombo != null)
            {
                var current = HelixPythonNamespace.UI.Controls.LunaThemes.FromName(s.Theme);
                int ti = Array.IndexOf(ThemeOrder, current);
                _themeCombo.SelectedIndex = ti >= 0 ? ti : 0;
            }

            int gs = (int)Math.Round(s.GridSpacing);
            if (gs < 2) gs = 2;
            if (gs > 100) gs = 100;
            _gridSpacingInput.Value = gs;

            // Editor font name (case-insensitive match against the combo) and size (clamped).
            if (_fontNameCombo != null)
            {
                int fi = -1;
                for (int i = 0; i < _fontNameCombo.Items.Count; i++)
                    if (string.Equals(_fontNameCombo.Items[i] as string, s.EditorFontName, StringComparison.OrdinalIgnoreCase)) { fi = i; break; }
                _fontNameCombo.SelectedIndex = fi >= 0 ? fi : (_fontNameCombo.Items.Count > 0 ? 0 : -1);
            }
            if (_fontSizeInput != null)
            {
                decimal fs = (decimal)s.EditorFontSize;
                if (fs < _fontSizeInput.Minimum) fs = _fontSizeInput.Minimum;
                if (fs > _fontSizeInput.Maximum) fs = _fontSizeInput.Maximum;
                _fontSizeInput.Value = fs;
            }

            // Initialise the preview to the loaded font/size (in case the change events above didn't
            // fire, e.g. the value was already the default).
            UpdateFontPreview();

            // Detector is optional; when absent the Build Tools list stays empty.
            if (_detector != null)
            {
                var sdks = _detector.DetectSdks();
                foreach (var sdk in sdks)
                {
                    var item = new ListViewItem(sdk.Version);
                    item.SubItems.Add(sdk.Path);
                    item.Tag = sdk;
                    item.Checked = string.Equals(sdk.Version, s.DefaultSdkVersion, StringComparison.Ordinal);
                    _sdkList.Items.Add(item);
                }
            }
        }

        // ListView has real checkboxes, but only one SDK may be the default — so checking a row
        // unchecks the others, giving radio-button semantics.
        private void OnSdkItemCheck(object sender, ItemCheckEventArgs e)
        {
            // Enforce single-selection (radio behavior).
            if (e.NewValue == CheckState.Checked)
            {
                for (int i = 0; i < _sdkList.Items.Count; i++)
                {
                    if (i != e.Index && _sdkList.Items[i].Checked)
                    {
                        _sdkList.Items[i].Checked = false;
                    }
                }
            }
        }

        // OK handler: writes each control back into the settings model, persists to disk, flags
        // SettingsChanged for the caller, pushes the two derived values into live static state so
        // the change takes effect without a restart, then closes with OK.
        private void OnOk(object sender, EventArgs e)
        {
            var s = _settings.Settings;
            s.SnapToGrid = _snapCheck.Checked;
            s.GridSpacing = (float)_gridSpacingInput.Value;
            if (_allowProceduralCheck != null) s.AllowNonObjectOrientedCode = _allowProceduralCheck.Checked;

            // Trimmed because a pasted key routinely carries trailing whitespace, and an API key
            // with a stray space fails authentication with an opaque 401. Empty means "unset" —
            // the provider then falls back to the environment variable.
            if (_anthropicKeyInput != null)
            {
                string key = (_anthropicKeyInput.Text ?? string.Empty).Trim();
                s.AnthropicApiKey = key.Length == 0 ? null : key;
            }
            if (_openAiKeyInput != null)
            {
                string key = (_openAiKeyInput.Text ?? string.Empty).Trim();
                s.OpenAiApiKey = key.Length == 0 ? null : key;
            }

            // Luna accent theme: persist the name and apply it live so open windows recolor at once.
            if (_themeCombo != null && _themeCombo.SelectedIndex >= 0)
            {
                var kind = ThemeOrder[_themeCombo.SelectedIndex];
                s.Theme = kind.ToString();
                HelixPythonNamespace.UI.Controls.LunaThemes.Apply(kind);
            }

            // Editor font.
            if (_fontNameCombo != null && _fontNameCombo.SelectedItem is string fontName && fontName.Length > 0)
                s.EditorFontName = fontName;
            if (_fontSizeInput != null)
                s.EditorFontSize = (float)_fontSizeInput.Value;

            // Persist the checked SDK (if any) as the default version.

            for (int i = 0; i < _sdkList.Items.Count; i++)
            {
                if (_sdkList.Items[i].Checked)
                {
                    var sdk = _sdkList.Items[i].Tag as DotNetSdkInfo;
                    if (sdk != null) s.DefaultSdkVersion = sdk.Version;
                    break;
                }
            }

            _settings.Save();
            SettingsChanged = true;

            // Apply derived runtime state immediately.
            HelixPythonNamespace.UI.Rendering.GridSnapper.Enabled = s.SnapToGrid;
            HelixPythonNamespace.UI.Rendering.LayoutConstants.GridSpacing = s.GridSpacing;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
