namespace HelixPythonNamespace.AddIns.GuiDesigner
{
    using System;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.GuiDesigner.Emit;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.GuiDesigner.Persistence;

    /// <summary>
    /// The GUI designer's file commands: the design is a sidecar next to the generated source, generation writes both, and
    /// "save as" makes an independent copy rather than a second window onto the same file.
    ///
    /// <para>They used to be File ▸ Regenerate Source / Start Syncing / View Code on a menu bar of
    /// this designer's own, with no Save As and no Open at all — the only route in was whatever the
    /// Solution Explorer had double-clicked. They are buttons on the shared command strip now.</para>
    /// </summary>
    public sealed partial class GuiDesignerForm
    {
        /// <summary>
        /// Opens a design as its own document. Supplied by the shell, because which tab a design
        /// opens in is the shell's business — and because <c>_pySourcePath</c> is readonly for a
        /// good reason: a designer window is bound to one file for its life, so "save as" opens the
        /// copy rather than quietly repointing this one at it.
        /// </summary>
        internal Action<GuiForm, string> OpenDocumentRequested;

        /// <summary>Asks where to save. The shell supplies it; standalone falls back to a dialog so
        /// the designer still works on its own.</summary>
        internal Func<string> RequestSavePath;

        /// <summary>
        /// A blank form, as its own document.
        ///
        /// <para>Opened rather than loaded in place for the same reason Save As opens its copy: this
        /// window is bound to the file it was created for, and replacing what is on its canvas would
        /// throw away an unsaved design with no way back.</para>
        /// </summary>
        private void NewFormCommand()
        {
            if (OpenDocumentRequested == null) return;
            var blank = new GuiForm
            {
                FormClassName = "Form1",
                Title = "Form1",
                Framework = _form.Framework,
            };
            OpenDocumentRequested(blank, null);
        }

        private void ShowDesignerHelp()
        {
            MessageBox.Show(this,
                "Drag controls from the Toolbox. Select a control to edit its properties and events.",
                "GUI Designer Canvas");
        }

        private void ShowGeneratedFileInExplorer()
        {
            if (_pySourcePath == null)
            {
                MessageBox.Show(this,
                    "This form is a preview — it was not opened from a project file, so there is "
                    + "nothing on disk to show yet. Save Design As… writes one.",
                    "GUI Designer Canvas");
                return;
            }
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _pySourcePath + "\"");
        }

        /// <summary>Hides the Start Syncing button unless it has something to offer: a real file
        /// this session is not already writing to. The menu item asked the same question on
        /// DropDownOpening; a button has to be asked whenever the answer can change.</summary>
        private void UpdateSyncCommandVisibility()
        {
            if (_startSyncingButton != null)
                _startSyncingButton.Visible = _pySourcePath != null && !_adopted;
        }

        internal void SaveDesignAsCommand()
        {
            string path;
            if (RequestSavePath != null) path = RequestSavePath();
            else using (var dialog = new SaveFileDialog
            {
                Filter = "Python source|*.py",
                FileName = (_form.FormClassName ?? "form") + ".py",
            })
                path = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            if (path != null) SaveDesignAs(path);
        }

        /// <summary>
        /// Writes the generated source and its design sidecar to <paramref name="path"/>, then opens
        /// the copy as its own document.
        ///
        /// <para>Opening it is not a flourish: this window is bound to the file it was created for,
        /// so without that step "Save As" would write a file nobody is editing and leave the user
        /// looking at the original, which is the shape of bug where an afternoon's work goes into
        /// the wrong file.</para>
        /// </summary>
        internal bool SaveDesignAs(string path)
        {
            try
            {
                GuiForm copy = GuiFormHistory.Clone(_form);
                copy.FormClassName = ClassNameFor(path, copy.FormClassName);

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                IGuiCodeEmitter emitter = EmitterFactory.EmitterFor(copy.Framework);
                File.WriteAllText(path, emitter.Emit(copy, string.Empty) ?? string.Empty);
                WriteTkinterRuntimeIfNeeded(directory);
                _repository.Save(GuiFormRepository.DesignerJsonFor(path), copy);

                if (OpenDocumentRequested != null) OpenDocumentRequested(copy, path);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Not saved: " + ex.Message, "GUI Designer Canvas");
                return false;
            }
        }

        /// <summary>A Python identifier from the file name, so the saved copy's class matches the
        /// file it now lives in. Falls back to the old name when the stem is not usable.</summary>
        private static string ClassNameFor(string path, string fallback)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem)) return fallback;
            var name = new System.Text.StringBuilder();
            foreach (char c in stem)
                if (char.IsLetterOrDigit(c) || c == '_') name.Append(c);
            if (name.Length == 0 || char.IsDigit(name[0])) return fallback;
            return name.ToString();
        }

        private void OpenDesignCommand()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "GUI design or Python source|*.designer.json;*.py",
            })
                if (dialog.ShowDialog(this) == DialogResult.OK) OpenDesign(dialog.FileName);
        }

        /// <summary>
        /// Opens the design beside <paramref name="path"/> — the path may be either half of the
        /// pair, the <c>.py</c> or its <c>.designer.json</c>, because a user picking a file does not
        /// think of it as two.
        /// </summary>
        internal bool OpenDesign(string path)
        {
            string sidecar = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? path : GuiFormRepository.DesignerJsonFor(path);
            string source = GuiFormRepository.PySourceFor(sidecar);

            GuiForm loaded = _repository.Load(sidecar);
            if (loaded == null)
            {
                MessageBox.Show(this,
                    "There is no design beside that file. A GUI design is a .designer.json written "
                    + "next to the source it generates; opening the source alone cannot recover the "
                    + "layout.",
                    "GUI Designer Canvas");
                return false;
            }

            if (OpenDocumentRequested == null) return false;
            OpenDocumentRequested(loaded, source);
            return true;
        }
    }
}
