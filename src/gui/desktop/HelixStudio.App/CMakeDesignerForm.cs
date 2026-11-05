namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.Workspace.Cuda;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// FR-DES-06: a visual CMake project designer. A toolbox
    /// adds executable/library targets to a <see cref="CMakeListsMetadata"/>; a graph canvas draws each target
    /// and its <c>target_link_libraries</c> edges (seeded, when opened on a project, from the parsed model);
    /// "Generate CMakeLists" emits a real <c>CMakeLists.txt</c> via <see cref="CMakeListsWriter"/> (FR-GEN-06).
    /// Launched from Tools ▸ CMake Designer.
    /// </summary>
    public sealed class CMakeDesignerForm : LunaForm
    {
        private static CMakeDesignerForm _instance;

        private readonly CMakeListsMetadata _model = BuildSampleModel();
        private readonly Panel _canvas;
        private readonly ListBox _toolbox;
        private readonly TextBox _codeBox;
        private CMakeTarget _selected;
        private readonly Dictionary<string, Rectangle> _nodeRects = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);

        public CMakeDesignerForm()
        {
            // These designer windows are plain Forms, so without this they fall back to the
            // WinForms default (Microsoft Sans Serif) instead of the shell UI font.
            Font = LunaFonts.Ui;
            Text = "CMake Designer";
            Width = 1120;
            Height = 720;
            StartPosition = FormStartPosition.CenterParent;

            var top = new Panel { Dock = DockStyle.Top, Height = 34 };
            var gen = new Button { Text = "Generate CMakeLists", Location = new Point(8, 5), Width = 150 };
            gen.Click += delegate { Regenerate(); };
            var addLink = new Button { Text = "Link selected → …", Location = new Point(166, 5), Width = 130 };
            addLink.Click += OnLinkSelected;
            top.Controls.Add(gen); top.Controls.Add(addLink);

            _toolbox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _toolbox.Items.AddRange(new object[] { "Executable", "Static Library", "Shared Library", "Custom Target" });
            _toolbox.DoubleClick += OnToolboxDoubleClick;
            var toolboxPanel = new Panel { Dock = DockStyle.Left, Width = 150 };
            toolboxPanel.Controls.Add(_toolbox);
            toolboxPanel.Controls.Add(new Label { Text = "Add Target", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) });

            _canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true };
            _canvas.Paint += OnCanvasPaint;
            _canvas.MouseDown += OnCanvasMouseDown;

            // TabStop=false keeps this read-only preview from being the initially-focused
            // control (a focused read-only TextBox selects its entire content, rendering the
            // whole generated listing as one blue block).
            _codeBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f), TabStop = false, HideSelection = true };
            var codePanel = new Panel { Dock = DockStyle.Right, Width = 440 };
            codePanel.Controls.Add(_codeBox);
            codePanel.Controls.Add(new Label { Text = "Generated CMakeLists.txt", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) });

            Controls.Add(_canvas);
            Controls.Add(codePanel);
            Controls.Add(toolboxPanel);
            Controls.Add(top);

            Regenerate();
        }

        public static void ShowSingleton(IWin32Window owner)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new CMakeDesignerForm();
                _instance.FormClosed += delegate { _instance = null; };
                _instance.Show(owner);
            }
            else { _instance.BringToFront(); _instance.Activate(); }
        }

        /// <summary>SRS solution_explorer_rules 9: closing a solution closes every graphical designer.</summary>
        public static void CloseIfOpen()
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                try { _instance.Close(); } catch { }
            }
            _instance = null;
        }

        private static CMakeListsMetadata BuildSampleModel()
        {
            var m = new CMakeListsMetadata { ProjectName = "myapp", CMakeMinimumVersion = "3.20", CxxStandard = "17" };
            m.Languages.Add("CXX");
            var core = new CMakeTarget("core", CMakeTargetKind.StaticLibrary); core.Sources.Add("core.cpp");
            var util = new CMakeTarget("util", CMakeTargetKind.StaticLibrary); util.Sources.Add("util.cpp");
            var app = new CMakeTarget("app", CMakeTargetKind.Executable); app.Sources.Add("main.cpp");
            app.LinkDependencies.Add("core"); app.LinkDependencies.Add("util");
            core.LinkDependencies.Add("util");
            m.Targets.Add(util); m.Targets.Add(core); m.Targets.Add(app);
            return m;
        }

        private void OnToolboxDoubleClick(object sender, EventArgs e)
        {
            if (_toolbox.SelectedItem == null) return;
            CMakeTargetKind kind =
                _toolbox.SelectedItem.ToString() == "Executable" ? CMakeTargetKind.Executable :
                _toolbox.SelectedItem.ToString() == "Shared Library" ? CMakeTargetKind.SharedLibrary :
                _toolbox.SelectedItem.ToString() == "Custom Target" ? CMakeTargetKind.Custom :
                CMakeTargetKind.StaticLibrary;
            int n = _model.Targets.Count + 1;
            string name = (kind == CMakeTargetKind.Executable ? "exe" : "lib") + n;
            var t = new CMakeTarget(name, kind);
            if (kind != CMakeTargetKind.Custom) t.Sources.Add(name + ".cpp");
            _model.Targets.Add(t);
            _selected = t;
            Regenerate();
        }

        private void OnLinkSelected(object sender, EventArgs e)
        {
            if (_selected == null) { MessageBox.Show(this, "Select a target node first."); return; }
            var others = _model.Targets.FindAll(t => t != _selected);
            if (others.Count == 0) return;
            using (var dlg = new LunaForm { Text = "Link " + _selected.Name + " →", Width = 260, Height = 120, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
                foreach (var o in others) combo.Items.Add(o.Name);
                combo.SelectedIndex = 0;
                var ok = new Button { Text = "Link", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                dlg.Controls.Add(combo); dlg.Controls.Add(ok); dlg.AcceptButton = ok;
                if (dlg.ShowDialog(this) == DialogResult.OK && combo.SelectedItem != null)
                {
                    string dep = combo.SelectedItem.ToString();
                    if (!_selected.LinkDependencies.Contains(dep)) _selected.LinkDependencies.Add(dep);
                    Regenerate();
                }
            }
        }

        private void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            _selected = null;
            foreach (var kv in _nodeRects)
                if (kv.Value.Contains(e.Location)) { _selected = _model.Targets.Find(t => string.Equals(t.Name, kv.Key, StringComparison.OrdinalIgnoreCase)); break; }
            _canvas.Invalidate();
        }

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            g.TranslateTransform(_canvas.AutoScrollPosition.X, _canvas.AutoScrollPosition.Y);

            // Layout: targets stacked vertically.
            _nodeRects.Clear();
            int y = 20;
            foreach (var t in _model.Targets)
            {
                _nodeRects[t.Name] = new Rectangle(60, y, 280, 54);
                y += 84;
            }

            // Link edges.
            using (var pen = new Pen(Color.FromArgb(120, 130, 150), 2))
            {
                pen.CustomEndCap = new AdjustableArrowCap(5, 5);
                foreach (var t in _model.Targets)
                {
                    Rectangle rs = _nodeRects[t.Name];
                    foreach (string dep in t.LinkDependencies)
                    {
                        Rectangle rt;
                        if (!_nodeRects.TryGetValue(dep, out rt)) continue; // external lib, no node
                        g.DrawLine(pen, rs.Right, rs.Top + rs.Height / 2, rt.Left, rt.Top + rt.Height / 2);
                    }
                }
            }

            foreach (var t in _model.Targets)
            {
                Rectangle r = _nodeRects[t.Name];
                bool exe = t.Kind == CMakeTargetKind.Executable;
                Color fill = exe ? Color.FromArgb(226, 240, 226) : Color.FromArgb(228, 236, 248);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                using (var pen = new Pen(t == _selected ? Color.OrangeRed : Color.FromArgb(120, 130, 150), t == _selected ? 2 : 1))
                    g.DrawRectangle(pen, r);
                using (var bold = new Font(Font, FontStyle.Bold))
                {
                    g.DrawString(t.Name + "  [" + t.Kind + "]", bold, Brushes.Black, r.X + 8, r.Y + 6);
                    string srcs = string.Join(", ", t.Sources.ToArray());
                    if (srcs.Length > 36) srcs = srcs.Substring(0, 35) + "…";
                    g.DrawString(srcs.Length > 0 ? srcs : "(no sources)", Font, Brushes.DimGray, r.X + 8, r.Y + 28);
                }
            }
        }

        private void Regenerate()
        {
            try { _codeBox.Text = (CMakeListsWriter.WriteToString(_model) ?? string.Empty).Replace("\n", "\r\n"); }
            catch (Exception ex) { _codeBox.Text = "# generation error: " + ex.Message; }
            _canvas.Invalidate();
        }

        internal string GeneratedCode { get { return _codeBox.Text; } }
        internal int TargetCount { get { return _model.Targets.Count; } }
    }
}
