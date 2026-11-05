using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// Colab's chrome around a notebook: the title strip, the menu bar (File / Edit / View / Insert
    /// / Runtime / Tools / Help), the action row, the left icon rail with its panes, and the status
    /// strip along the bottom.
    ///
    /// <para>
    /// This is a layout and a command surface, nothing more — every command is a delegate the
    /// hosting <see cref="NotebookTabPage"/> supplies, so the shell knows nothing about kernels and
    /// the tab knows nothing about where a button sits. That split is what makes it possible to
    /// assert the commands in a test without building the chrome.
    /// </para>
    /// </summary>
    public sealed class NotebookWorkspace : UserControl
    {
        private readonly Panel _rail;
        private readonly Panel _sidePanel;
        private readonly Label _sideTitle;
        private readonly Panel _sideBody;
        private readonly PaneResizer _resizer;
        private readonly Label _title;
        private readonly Label _runtimeStatus;
        private readonly Label _executionStatus;
        private readonly MenuStrip _menu;
        private readonly Panel _actions;
        private readonly ToolTip _tips = new ToolTip();
        private readonly List<Button> _railButtons = new List<Button>();
        private readonly Dictionary<string, NotebookSidePanel> _panes =
            new Dictionary<string, NotebookSidePanel>(StringComparer.Ordinal);

        private string _openPane;
        private readonly Control _page;

        public NotebookWorkspace(Control page)
        {
            if (page == null) throw new ArgumentNullException("page");
            _page = page;
            Dock = DockStyle.Fill;
            BackColor = Color.White;

            // ---- title strip -----------------------------------------------------------
            _title = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(12, 6, 12, 0),
                Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
                ForeColor = Color.FromArgb(60, 64, 67),
                Text = "Untitled.ipynb",
            };

            _menu = new MenuStrip
            {
                Dock = DockStyle.Top,
                BackColor = Color.White,
                Renderer = new ToolStripProfessionalRenderer(),
                Padding = new Padding(8, 0, 0, 0),
            };

            _actions = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.White };

            // ---- status strip ----------------------------------------------------------
            var status = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = Color.FromArgb(248, 249, 250) };
            _runtimeStatus = new Label
            {
                Dock = DockStyle.Right,
                Width = 300,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 10, 0),
                ForeColor = Color.FromArgb(95, 99, 104),
                Text = "Not connected",
            };
            _executionStatus = new Label
            {
                Dock = DockStyle.Left,
                Width = 420,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Color.FromArgb(95, 99, 104),
                Text = string.Empty,
            };
            status.Controls.Add(_executionStatus);
            status.Controls.Add(_runtimeStatus);

            // ---- left rail + side panel ------------------------------------------------
            _rail = new Panel { Dock = DockStyle.Left, Width = 40, BackColor = Color.FromArgb(248, 249, 250) };

            _sideTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Padding = new Padding(10, 6, 0, 0),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 64, 67),
            };
            _sideBody = new Panel { Dock = DockStyle.Fill };
            _sidePanel = new Panel { Dock = DockStyle.Left, Width = 300, Visible = false, BackColor = Color.White };
            _resizer = new PaneResizer(this);

            // Fill first so the docked strips claim their edges around it; the resizer last so it
            // claims the right edge before Fill does.
            _sidePanel.Controls.Add(_sideBody);
            _sidePanel.Controls.Add(_sideTitle);
            _sidePanel.Controls.Add(_resizer);

            // The workspace owns this row's layout, so it states the page's dock rather than
            // trusting every caller to have set it. A page left at its designer default sits at
            // 200x100 in the corner while the rail and pane lay out correctly around it — which
            // looks like a paint bug, not a missing property.
            page.Dock = DockStyle.Fill;

            Controls.Add(page);
            Controls.Add(_sidePanel);
            Controls.Add(_rail);
            Controls.Add(status);
            Controls.Add(_actions);
            Controls.Add(_menu);
            Controls.Add(_title);
        }

        /// <summary>The menu bar, so the host can hang its commands on it.</summary>
        public MenuStrip Menu { get { return _menu; } }

        /// <summary>Which pane is open, or null when the rail is collapsed.</summary>
        public string OpenPane { get { return _openPane; } }

        /// <summary>The icon rail's width — what the resizer has to leave room for.</summary>
        internal int RailWidth { get { return _rail.Width; } }

        /// <summary>How wide the side pane currently is. The user can drag this.</summary>
        public int SidePanelWidth
        {
            get { return _sidePanel.Width; }
            set { _sidePanel.Width = value; }
        }

        public string NotebookTitle
        {
            get { return _title.Text; }
            set { _title.Text = value; }
        }

        public void SetRuntimeStatus(string text) { _runtimeStatus.Text = text ?? string.Empty; }

        public void SetExecutionStatus(string text) { _executionStatus.Text = text ?? string.Empty; }

        /// <summary>Add one pane and its rail icon. Panes are created up front and shown on demand.</summary>
        public void AddPane(string key, string glyph, NotebookSidePanel panel)
        {
            if (string.IsNullOrEmpty(key) || panel == null) return;
            _panes[key] = panel;
            panel.Visible = false;
            _sideBody.Controls.Add(panel);

            var button = new Button
            {
                Width = 32,
                Height = 32,
                Left = 4,
                Top = 6 + _railButtons.Count * 36,
                Text = glyph,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Font = new Font("Segoe UI Symbol", 10f),
                ForeColor = Color.FromArgb(95, 99, 104),
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { TogglePane(key); };
            _tips.SetToolTip(button, panel.Title);
            _rail.Controls.Add(button);
            _railButtons.Add(button);
        }

        /// <summary>Open a pane, or close it when it is already the open one — Colab's rail behaviour.</summary>
        public void TogglePane(string key)
        {
            if (string.Equals(_openPane, key, StringComparison.Ordinal)) { ClosePane(); return; }
            ShowPane(key);
        }

        public void ShowPane(string key)
        {
            NotebookSidePanel panel;
            if (!_panes.TryGetValue(key, out panel)) return;

            foreach (KeyValuePair<string, NotebookSidePanel> pair in _panes)
                pair.Value.Visible = ReferenceEquals(pair.Value, panel);

            _openPane = key;
            _sideTitle.Text = panel.Title.ToUpperInvariant();
            _sidePanel.Visible = true;
            SyncRailHighlight();
            RepaintPage();
        }

        public void ClosePane()
        {
            _openPane = null;
            _sidePanel.Visible = false;
            foreach (NotebookSidePanel panel in _panes.Values) panel.Visible = false;
            SyncRailHighlight();
            RepaintPage();
        }

        public NotebookSidePanel Pane(string key)
        {
            NotebookSidePanel panel;
            return _panes.TryGetValue(key, out panel) ? panel : null;
        }

        public IEnumerable<NotebookSidePanel> Panes { get { return _panes.Values; } }

        /// <summary>Add a caption button to the action row (`+ Code`, `▶ Run all`, …).</summary>
        public Button AddAction(string caption, string tooltip, EventHandler onClick)
        {
            var button = new Button
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Text = caption,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Padding = new Padding(8, 0, 8, 0),
                ForeColor = Color.FromArgb(60, 64, 67),
            };
            button.FlatAppearance.BorderSize = 0;
            if (onClick != null) button.Click += onClick;
            if (!string.IsNullOrEmpty(tooltip)) _tips.SetToolTip(button, tooltip);
            _actions.Controls.Add(button);
            return button;
        }

        public void AddActionSeparator()
        {
            _actions.Controls.Add(new Label { Dock = DockStyle.Left, Width = 12, Text = string.Empty });
        }

        /// <summary>
        /// Force the page to lay out and repaint after a pane opens or closes.
        ///
        /// The page is a custom-painted AutoScroll panel: docking a sibling beside it changes its
        /// client width, but nothing invalidates what it already drew — so the cells stay painted at
        /// their old x, and the notebook appears TWICE, once behind the pane and once beside it.
        /// </summary>
        private void RepaintPage()
        {
            if (_page == null || _page.IsDisposed) return;
            _page.PerformLayout();
            _page.Invalidate(true);
            _page.Update();
        }

        private void SyncRailHighlight()
        {
            int i = 0;
            foreach (KeyValuePair<string, NotebookSidePanel> pair in _panes)
            {
                if (i >= _railButtons.Count) break;
                bool open = string.Equals(pair.Key, _openPane, StringComparison.Ordinal);
                _railButtons[i].BackColor = open ? Color.FromArgb(232, 240, 254) : _rail.BackColor;
                i++;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
