namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.App.Services;

    [System.ComponentModel.DesignerCategory("Form")]

    public sealed partial class BuildToolSettingsDialog : LunaForm
    {
        public BuildToolSettingsDialog()
        {
            InitializeComponent();
        }





        // Inputs: the SDK detector that enumerates installed .NET SDKs, and the currently persisted
        // default version so the matching row can be pre-checked.
        private readonly BuildToolDetector _detector;
        private readonly string _currentDefault;

        // The version the user chose (the checked row); read by the caller after DialogResult.OK.
        public string SelectedSdkVersion { get; private set; }
        public string SelectedNativeToolchain
        {
            get
            {
                string selected = _nativeToolchainCombo.SelectedItem as string ?? "Auto";
                return selected.StartsWith("Helix ", StringComparison.Ordinal) ? "Emulator" : selected;
            }
        }
        public string SelectedGccCompilerPath
        {
            get { return (_gccPathTextBox.Text ?? string.Empty).Trim(); }
        }
        public string SelectedMsvcEnvironmentScriptPath
        {
            get { return (_msvcPathTextBox.Text ?? string.Empty).Trim(); }
        }
        public string SelectedCxxStandard
        {
            get { return StandardSuffix(_cxxStandardCombo.SelectedItem as string, "20"); }
        }
        public string SelectedCudaStandard
        {
            get { return StandardSuffix(_cudaStandardCombo.SelectedItem as string, "20"); }
        }

        // Real constructor: chains to InitializeComponent, stores inputs (detector is required),
        // and populates the SDK list immediately.
        public BuildToolSettingsDialog(BuildToolDetector detector, string currentDefault,
            string currentNativeToolchain = "Auto", string currentGccCompilerPath = null,
            string currentMsvcEnvironmentScriptPath = null, string currentCxxStandard = "20",
            string currentCudaStandard = "20") : this()
        {
            _detector = detector ?? throw new ArgumentNullException("detector");
            _currentDefault = currentDefault;
            string displayedToolchain = string.Equals(currentNativeToolchain, "Emulator",
                StringComparison.OrdinalIgnoreCase)
                ? "Helix CUDA/C++ Emulator"
                : (string.IsNullOrWhiteSpace(currentNativeToolchain) ? "Auto" : currentNativeToolchain);
            int nativeIndex = _nativeToolchainCombo.Items.IndexOf(displayedToolchain);
            _nativeToolchainCombo.SelectedIndex = nativeIndex >= 0 ? nativeIndex : 0;
            _gccPathTextBox.Text =
                HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindGccCppCompiler(currentGccCompilerPath)
                ?? currentGccCompilerPath ?? string.Empty;
            _msvcPathTextBox.Text =
                HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindMsvcEnvironmentScript(
                    currentMsvcEnvironmentScriptPath) ?? currentMsvcEnvironmentScriptPath ?? string.Empty;
            SelectStandard(_cxxStandardCombo, "C++" + (currentCxxStandard ?? "20"));
            SelectStandard(_cudaStandardCombo, "CUDA C++" + (currentCudaStandard ?? "20"));
            UpdateNativeToolStatus();
            LoadSdks();
        }

        private static void SelectStandard(ComboBox combo, string display)
        {
            int index = combo.Items.IndexOf(display);
            combo.SelectedIndex = index >= 0 ? index : combo.Items.IndexOf(
                combo.Name == "cudaStandardCombo" ? "CUDA C++20" : "C++20");
        }

        private static string StandardSuffix(string display, string fallback)
        {
            if (string.IsNullOrEmpty(display)) return fallback;
            int marker = display.LastIndexOf("++", StringComparison.Ordinal);
            return marker >= 0 ? display.Substring(marker + 2) : fallback;
        }


        // (Re)fills the SDK ListView from the detector. Each row shows version/path and stashes the
        // DotNetSdkInfo in Tag; the persisted default (or the latest when none is set) is pre-checked.
        private void LoadSdks()
        {
            _sdkListView.Items.Clear();
            var sdks = _detector.DetectSdks();

            // No SDKs installed: show an install-hint row and disable OK (nothing to pick).
            if (sdks.Count == 0)
            {
                var item = new ListViewItem("No .NET SDKs found");
                item.SubItems.Add("Install .NET SDK from https://dotnet.microsoft.com");
                _sdkListView.Items.Add(item);
                _okButton.Enabled = false;
                return;
            }

            _okButton.Enabled = true;
            bool hasDefault = false;

            for (int i = 0; i < sdks.Count; i++)
            {
                var sdk = sdks[i];
                var item = new ListViewItem(sdk.Version);
                item.SubItems.Add(sdk.Path);
                item.Tag = sdk;

                if (sdk.Version == _currentDefault)
                {
                    item.Checked = true;
                    hasDefault = true;
                }

                _sdkListView.Items.Add(item);
            }

            // If no default set, check the latest version
            if (!hasDefault && _sdkListView.Items.Count > 0)
            {
                _sdkListView.Items[_sdkListView.Items.Count - 1].Checked = true;
            }
        }

        // Turns the checkbox ListView into a single-select (radio) control and tracks the choice:
        // checking a row unchecks the rest and records SelectedSdkVersion; unchecking is vetoed when
        // it would leave nothing checked (there must always be exactly one default).
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.NewValue == CheckState.Checked)
            {
                // Uncheck all others (radio-button behavior)
                for (int i = 0; i < _sdkListView.Items.Count; i++)
                {
                    if (i != e.Index && _sdkListView.Items[i].Checked)
                    {
                        _sdkListView.Items[i].Checked = false;
                    }
                }

                var sdk = _sdkListView.Items[e.Index].Tag as DotNetSdkInfo;
                if (sdk != null)
                {
                    SelectedSdkVersion = sdk.Version;
                }
            }
            else if (e.NewValue == CheckState.Unchecked)
            {
                // Don't allow unchecking the only checked item
                bool anyOtherChecked = false;
                for (int i = 0; i < _sdkListView.Items.Count; i++)
                {
                    if (i != e.Index && _sdkListView.Items[i].Checked)
                    {
                        anyOtherChecked = true;
                        break;
                    }
                }
                if (!anyOtherChecked)
                {
                    e.NewValue = CheckState.Checked;
                }
            }
        }

        // Refresh: re-scan the machine for SDKs (clearing the detector's cache) and rebuild the list.
        private void OnRefresh(object sender, EventArgs e)
        {
            _detector.Refresh();
            string detected =
                HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindGccCppCompiler(_gccPathTextBox.Text);
            if (!string.IsNullOrEmpty(detected)) _gccPathTextBox.Text = detected;
            string msvc =
                HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindMsvcEnvironmentScript(_msvcPathTextBox.Text);
            if (!string.IsNullOrEmpty(msvc)) _msvcPathTextBox.Text = msvc;
            UpdateNativeToolStatus();
            LoadSdks();
        }

        private void OnBrowseGcc(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select GNU C++ compiler";
                dialog.Filter = "GNU C++ compiler (g++.exe)|g++.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*";
                dialog.CheckFileExists = true;
                if (File.Exists(_gccPathTextBox.Text))
                    dialog.InitialDirectory = Path.GetDirectoryName(_gccPathTextBox.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _gccPathTextBox.Text = dialog.FileName;
                    UpdateNativeToolStatus();
                }
            }
        }

        private void OnBrowseMsvc(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select Visual Studio x64 environment script";
                dialog.Filter = "Visual Studio x64 environment (vcvars64.bat)|vcvars64.bat|Batch files (*.bat)|*.bat|All files (*.*)|*.*";
                dialog.CheckFileExists = true;
                if (File.Exists(_msvcPathTextBox.Text))
                    dialog.InitialDirectory = Path.GetDirectoryName(_msvcPathTextBox.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _msvcPathTextBox.Text = dialog.FileName;
                    UpdateNativeToolStatus();
                }
            }
        }

        private void UpdateNativeToolStatus()
        {
            bool emulator = string.Equals(SelectedNativeToolchain, "Emulator",
                StringComparison.OrdinalIgnoreCase);
            _gccPathTextBox.Enabled = _gccBrowseButton.Enabled = !emulator;
            _msvcPathTextBox.Enabled = _msvcBrowseButton.Enabled = !emulator;
            if (emulator)
            {
                _nativeStatusLabel.Text =
                    "Built in: managed CUDA/C++ compiler + deterministic CPU CUDA runtime; no GPU required";
                _nativeStatusLabel.ForeColor = Color.DarkGreen;
                return;
            }

            var gcc = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.ProbeGccCppCompiler(
                _gccPathTextBox.Text);
            string msvc = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindMsvcEnvironmentScript(
                _msvcPathTextBox.Text);
            _nativeStatusLabel.Text = "Verified: GCC " +
                (gcc.Usable ? gcc.Version + " [" + gcc.Target + "]" : gcc.Reason) +
                " | MSVC " + (msvc == null ? "not found" : Path.GetDirectoryName(msvc));
            _nativeStatusLabel.ForeColor = gcc.Usable ? SystemColors.ControlText : Color.DarkRed;
        }

        private void OnNativeToolchainChanged(object sender, EventArgs e)
        {
            UpdateNativeToolStatus();
        }
    }
}
