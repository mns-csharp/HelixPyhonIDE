namespace HelixPythonNamespace.App
{
    using System;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.App.Dialogs;

    /// <summary>
    /// FR-DES (OOP canvas): a reusable window that renders a language's types as UML class boxes on the
    /// shared GDI+ <see cref="DiagramCanvas"/> — the same canvas Python uses. It encapsulates the wiring the
    /// designers need: build a <see cref="SolutionModel"/> from a language's parser via that language's
    /// <c>TypeExtractor</c>, load it into the canvas, mark the project visible, and frame the nodes. Launched
    /// from Tools ▸ TOML Config Canvas. This is how the "Class Designer Canvas" pillar (the GDI+ OOP class
    /// canvas) is delivered per language: <c>parser → TypeExtractor → TypeModel → DiagramCanvas</c>.
    /// </summary>
    public sealed class OopClassCanvasForm : LunaForm
    {
        private readonly DiagramCanvas _canvas;

        // TOML edit mode: the parsed document + its file, retained so a double-clicked table can be
        // edited and written straight back. Without these the canvas is a viewer — you can look at
        // your config but not change it, which is not a designer.
        private HelixPythonNamespace.Workspace.Toml.TomlDocument _tomlDoc;
        private string _tomlPath;

        public OopClassCanvasForm(string title)
        {
            Text = title;
            Width = 1120;
            Height = 720;
            StartPosition = FormStartPosition.CenterParent;
            _canvas = new DiagramCanvas { Dock = DockStyle.Fill };
            Controls.Add(_canvas);
        }

        /// <summary>
        /// Turns this canvas into a TOML *designer*: double-clicking a table box opens its scalar
        /// keys for editing and the result is written back through TomlWriter.
        /// </summary>
        private void EnableTomlEditing(HelixPythonNamespace.Workspace.Toml.TomlDocument doc, string path)
        {
            _tomlDoc = doc;
            _tomlPath = path;
            _canvas.NodeDoubleClicked += OnTomlNodeDoubleClicked;
        }

        private void OnTomlNodeDoubleClicked(object sender, NodeVisualEventArgs e)
        {
            if (_tomlDoc == null || e == null || e.NodeVisual == null) return;

            string tableName = e.NodeVisual.DisplayName ?? string.Empty;
            HelixPythonNamespace.Workspace.Toml.TomlTable table = ResolveTable(_tomlDoc, tableName);
            if (table == null) return;

            using (var dlg = new TomlTableEditDialog(tableName, table))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.Changed) return;
            }

            if (!string.IsNullOrEmpty(_tomlPath))
            {
                try
                {
                    System.IO.File.WriteAllText(_tomlPath,
                        HelixPythonNamespace.Workspace.Toml.TomlWriter.Write(_tomlDoc));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not save the TOML file.\n\n" + ex.Message,
                        "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            ReloadToml();
        }

        /// <summary>Re-parses the file we just wrote and redraws, so the canvas shows the saved truth.</summary>
        private void ReloadToml()
        {
            if (string.IsNullOrEmpty(_tomlPath) || !System.IO.File.Exists(_tomlPath)) return;
            string text = System.IO.File.ReadAllText(_tomlPath);
            _tomlDoc = HelixPythonNamespace.Workspace.Toml.TomlParser.Parse(text);

            var sln = new SolutionModel(Path.Combine(Path.GetTempPath(), "toml_canvas.sln"), "TomlCanvas");
            var proj = new ProjectModel(Path.Combine(Path.GetTempPath(), "pyproject.toml"), "toml_config", sln);
            sln.Projects.Add(proj);
            HelixPythonNamespace.Workspace.Toml.TomlTypeExtractor.ExtractInto(proj, _tomlDoc, _tomlPath);
            foreach (TypeModel t in proj.Types)
                foreach (TypeReference r in t.BaseTypes)
                    r.Bind(sln);

            TypeCount = proj.Types.Count;
            Text = "TOML Config Canvas  —  " + proj.Types.Count + " types";
            LoadSolution(sln, proj.Name);
        }

        /// <summary>
        /// Maps a canvas box title back to its table. Boxes are titled by dotted path ("tool.uv"),
        /// with "(root)" for the document root.
        /// </summary>
        public static HelixPythonNamespace.Workspace.Toml.TomlTable ResolveTable(
            HelixPythonNamespace.Workspace.Toml.TomlDocument doc, string dottedName)
        {
            if (doc == null) return null;
            if (string.IsNullOrEmpty(dottedName) || dottedName == "(root)") return doc.Root;

            HelixPythonNamespace.Workspace.Toml.TomlTable current = doc.Root;
            foreach (string part in dottedName.Split('.'))
            {
                if (current == null) return null;
                HelixPythonNamespace.Workspace.Toml.TomlItem item = current.Get(part);
                current = item as HelixPythonNamespace.Workspace.Toml.TomlTable;
                if (current == null)
                {
                    // Array-of-tables box ("[[a.b]]"): edit the first element, which is what the box shows.
                    var arr = item as HelixPythonNamespace.Workspace.Toml.TomlTableArray;
                    if (arr != null && arr.Elements.Count > 0) current = arr.Elements[0];
                    else return null;
                }
            }
            return current;
        }

        /// <summary>Render a solution's types on the canvas (call after the form is shown so the control has a handle).</summary>
        public void LoadSolution(SolutionModel solution, string projectName)
        {
            var store = new JsonLayoutMetadataStore(solution.FilePath);
            SolutionProjectionBuilder builder = _canvas.LoadFromSolution(solution, store);
            builder.SetVisibleProjects(new[] { projectName });   // nothing renders until a project is visible
            builder.RebuildFromSolution(solution);
            _canvas.ZoomToFit(80f);                              // LoadFromSolution leaves the viewport at origin
            _canvas.Invalidate();
        }

        /// <summary>Number of types currently projected (for harness/verification).</summary>
        internal int TypeCount { get; private set; }

        /// <summary>
        /// FR-DES-05 / FR-PROJ-07: browse a <c>pyproject.toml</c> or standalone <c>.toml</c> as a diagram on the
        /// GDI+ OOP canvas — tables become boxes (scalar keys as typed fields), sub-tables and array-of-tables
        /// become linked boxes. The design mandate: every FR-DES surface is the graphical canvas, not a tree.
        /// </summary>
        public static OopClassCanvasForm ShowForToml(IWin32Window owner, string source, string filePath)
        {
            var sln = new SolutionModel(Path.Combine(Path.GetTempPath(), "toml_canvas.sln"), "TomlCanvas");
            var proj = new ProjectModel(Path.Combine(Path.GetTempPath(), "pyproject.toml"), "toml_config", sln);
            sln.Projects.Add(proj);
            var doc = HelixPythonNamespace.Workspace.Toml.TomlParser.Parse(source ?? string.Empty);
            HelixPythonNamespace.Workspace.Toml.TomlTypeExtractor.ExtractInto(proj, doc, filePath ?? "pyproject.toml");
            OopClassCanvasForm form = ShowFor(owner, "TOML Config Canvas", sln, proj);
            // A designer edits, so keep the parsed document and let a double-clicked box write back.
            form.EnableTomlEditing(doc, filePath);
            form.Text = "TOML Config Canvas  —  " + proj.Types.Count +
                        " types  —  double-click a table to edit";
            return form;
        }

        /// <summary>Open a .toml file and render it on the canvas (FR-PROJ-07 graphical config editor entry point).</summary>
        public static OopClassCanvasForm ShowForTomlFile(IWin32Window owner, string tomlPath)
        {
            string src = System.IO.File.Exists(tomlPath) ? System.IO.File.ReadAllText(tomlPath) : "";
            return ShowForToml(owner, src, tomlPath);
        }

        /// <summary>
        /// FR-PROJ-07 in-app entry: prompt for a <c>pyproject.toml</c>/standalone <c>.toml</c> and open it as a
        /// diagram on the GDI+ OOP canvas. When <paramref name="preferredPath"/> exists (e.g. the active editor's
        /// file or the solution's pyproject.toml) it is opened directly without a dialog.
        /// </summary>
        public static void OpenTomlConfigOnCanvas(IWin32Window owner, string preferredPath = null)
        {
            if (!string.IsNullOrEmpty(preferredPath) && System.IO.File.Exists(preferredPath))
            {
                ShowForTomlFile(owner, preferredPath);
                return;
            }
            using (var dlg = new OpenFileDialog { Filter = "TOML config (*.toml)|*.toml|All files (*.*)|*.*", Title = "Open a TOML config on the canvas" })
            {
                if (dlg.ShowDialog(owner) == DialogResult.OK) ShowForTomlFile(owner, dlg.FileName);
            }
        }

        private static OopClassCanvasForm ShowFor(IWin32Window owner, string title, SolutionModel sln, ProjectModel proj)
        {
            foreach (TypeModel t in proj.Types)
                foreach (TypeReference r in t.BaseTypes)
                    r.Bind(sln);
            // Code canvases are read-only VIEWS: the language parsers are signature extractors that
            // skip method bodies, so there is no non-destructive way to write the diagram back (see
            // CanvasWriteBackSafetyTests). Say so in the caption rather than let the user discover it
            // by looking for a Save. The TOML canvas is a real designer and overwrites this title.
            var form = new OopClassCanvasForm(
                title + "  —  " + proj.Types.Count + " types  —  read-only view");
            form.TypeCount = proj.Types.Count;
            Track(form);
            if (owner != null) form.Show(owner); else form.Show();
            form.LoadSolution(sln, proj.Name);
            return form;
        }

        // §9: canvas windows are per-invocation and unowned, so a registry is the
        // only way "close every graphical designer" can reach them on solution close.
        private static readonly System.Collections.Generic.List<OopClassCanvasForm> _openInstances =
            new System.Collections.Generic.List<OopClassCanvasForm>();

        private static void Track(OopClassCanvasForm form)
        {
            _openInstances.Add(form);
            form.FormClosed += delegate { _openInstances.Remove(form); };
        }

        /// <summary>Closes every open OOP-canvas window (solution-close teardown, §9).</summary>
        public static void CloseAllOpen()
        {
            foreach (OopClassCanvasForm form in _openInstances.ToArray())
            {
                if (!form.IsDisposed)
                {
                    try { form.Close(); } catch { }
                }
            }
            _openInstances.Clear();
        }

        public static void ShowTomlSample(IWin32Window owner) { ShowForToml(owner, TomlSample, "pyproject.toml"); }

        private const string TomlSample =
            "[project]\nname = \"myapp\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n" +
            "dependencies = [\"numpy>=1.24\", \"requests\"]\n\n" +
            "[project.optional-dependencies]\ntest = [\"pytest\"]\n\n" +
            "[build-system]\nrequires = [\"setuptools>=61\"]\nbuild-backend = \"setuptools.build_meta\"\n\n" +
            "[tool.helixstudio]\nkind = \"app\"\ninterface-style = \"protocol\"\n\n" +
            "[[tool.helixstudio.targets]]\nname = \"cli\"\nkind = \"console\"\n";




    }
}
