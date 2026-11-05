using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// The Modules pane: the codebases on this machine that this notebook can import from.
    ///
    /// <para>
    /// A notebook usually sits nowhere near the library it is about. The book this was built for
    /// keeps its example package <c>cms</c> under its own <c>code</c> folder on another drive
    /// entirely, while the notebook being written can be anywhere at all. Registering that folder
    /// is what turns <c>from cms.tensor import device</c> from a <c>ModuleNotFoundError</c> into an
    /// import — and this pane is where the user sees which folders are registered and what each one
    /// actually bought them.
    /// </para>
    ///
    /// <para>
    /// <b>Every disk read here happens off the UI thread.</b> A registered root can be a repository
    /// with a hundred thousand files under it, or a network share that is no longer mounted — and
    /// <c>Directory.Exists</c> on a dead UNC path blocks for seconds. Rows appear immediately with
    /// the path; what each root makes importable is filled in when the background look-up returns.
    /// A stale look-up is dropped by generation number rather than applied to rows that have been
    /// replaced since it started.
    /// </para>
    /// </summary>
    public sealed class NotebookModulesPanel : NotebookSidePanel
    {
        private readonly ListView _list;
        private readonly Button _remove;
        private readonly Button _browse;
        private readonly Label _empty;
        private readonly ToolTip _tips = new ToolTip();
        private int _generation;

        /// <summary>How many importable names a row lists before it says "and N more".</summary>
        private const int NamePreviewLimit = 6;

        /// <summary>Raised when the user wants to pick a Python file and make it importable.</summary>
        public event EventHandler AddFileRequested;

        /// <summary>Raised when the user wants to put a whole folder on the import path as it is.</summary>
        public event EventHandler AddFolderRequested;

        /// <summary>Raised with the root the user wants taken off the import path.</summary>
        public event EventHandler<string> RemoveRequested;

        /// <summary>
        /// Raised with a root the user wants to look inside.
        ///
        /// <para>
        /// Registering a folder answers "can I import this"; it says nothing about what the code
        /// in it actually does. A user reading a book's example package needs to open
        /// <c>tensor.py</c> and see how <c>device()</c> is written, and this pane knows the folder
        /// while the Files pane knows how to walk one — so this hands it over rather than growing
        /// a second tree.
        /// </para>
        /// </summary>
        public event EventHandler<string> BrowseRequested;

        public NotebookModulesPanel() : base("Modules")
        {
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
            };
            // The FOLDER NAME, not the whole path. This pane is a narrow rail panel, and a column
            // wide enough for a long workspace path pushes the only column
            // that answers the user's actual question off the right edge — which is how a row
            // reading "(nothing importable here)" got shipped displaying "(nothi". The full path is
            // on the row's tooltip and in the status bar when it is added.
            _list.Columns.Add("Folder", 110);
            _list.Columns.Add("Makes importable", 260);
            _list.ShowItemToolTips = true;
            _list.SelectedIndexChanged += delegate { SyncRemoveButton(); };
            // Double-click is how a folder gets opened everywhere else in this IDE.
            _list.DoubleClick += delegate { RaiseBrowse(); };

            _empty = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 10, 12, 10),
                ForeColor = SystemColors.GrayText,
                Text = "No extra folders yet."
                     + Environment.NewLine + Environment.NewLine
                     + "Add a module to import code that lives anywhere on this machine. "
                     + "Pick the .py file you want to import and HelixPython works out the rest — "
                     + "which folder goes on the path, and what the import statement reads.",
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(6),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            buttons.Controls.Add(Action("Add a module…",
                "Pick a .py file anywhere on disk and make it importable",
                delegate { Fire(AddFileRequested); }));
            buttons.Controls.Add(Action("Add a folder…",
                "Put a folder on the import path exactly as it is",
                delegate { Fire(AddFolderRequested); }));
            _browse = Action("Browse…", "Look inside the selected folder and read its code",
                             delegate { RaiseBrowse(); });
            buttons.Controls.Add(_browse);
            _remove = Action("Remove", "Take the selected folder off the import path", delegate
            {
                string root = SelectedRoot();
                EventHandler<string> handler = RemoveRequested;
                if (root != null && handler != null) handler(this, root);
            });
            buttons.Controls.Add(_remove);

            Controls.Add(_list);
            Controls.Add(_empty);
            Controls.Add(buttons);
            SyncRemoveButton();
        }

        private Button Action(string caption, string tooltip, EventHandler onClick)
        {
            var button = new Button
            {
                Text = caption,
                AutoSize = true,
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0, 0, 6, 0),
            };
            button.Click += onClick;
            _tips.SetToolTip(button, tooltip);
            return button;
        }

        private void Fire(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>The root the user has selected, or null.</summary>
        public string SelectedRoot()
        {
            return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as string;
        }

        private void SyncRemoveButton()
        {
            bool haveRoot = SelectedRoot() != null;
            _remove.Enabled = haveRoot;
            _browse.Enabled = haveRoot;
        }

        private void RaiseBrowse()
        {
            string root = SelectedRoot();
            EventHandler<string> handler = BrowseRequested;
            if (root != null && handler != null) handler(this, root);
        }

        public override void Refresh(NotebookDocument document)
        {
            IList<string> roots = document == null ? new List<string>() : document.ModuleSearchPaths;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (string root in roots)
                {
                    // The name goes up now; what it holds is filled in when the disk answers.
                    var row = new ListViewItem(LeafName(root)) { Tag = root, ToolTipText = root };
                    row.SubItems.Add("…");
                    _list.Items.Add(row);
                }
            }
            finally { _list.EndUpdate(); }

            _list.Visible = roots.Count > 0;
            _empty.Visible = roots.Count == 0;
            SyncRemoveButton();

            if (roots.Count > 0) DescribeInBackground(new List<string>(roots), ++_generation);
        }

        /// <summary>
        /// Ask the disk what each root exposes, on a pool thread, and post the answers back.
        ///
        /// <para>
        /// The generation check is not ceremony: a user can add, remove and re-add roots faster
        /// than a network share answers, and applying a look-up to a list that has been rebuilt
        /// since would label the wrong row with the wrong contents.
        /// </para>
        /// </summary>
        private void DescribeInBackground(IList<string> roots, int generation)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var described = new List<string>();
                foreach (string root in roots) described.Add(Describe(root));

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate { ApplyDescriptions(described, generation); });
                }
                catch (ObjectDisposedException) { }      // the pane closed while the disk was answering
                catch (InvalidOperationException) { }
            });
        }

        private void ApplyDescriptions(IList<string> described, int generation)
        {
            if (generation != _generation) return;
            for (int i = 0; i < _list.Items.Count && i < described.Count; i++)
            {
                ListViewItem row = _list.Items[i];
                row.SubItems[1].Text = described[i];
                // The tooltip carries both halves, because neither column is wide enough to be
                // the whole answer on its own in a rail panel.
                row.ToolTipText = (row.Tag as string) + Environment.NewLine + described[i];
            }
        }

        /// <summary>The final directory name in a path.</summary>
        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;      // a drive root has no leaf
        }

        /// <summary>What one root offers, or why it offers nothing. Runs on a pool thread.</summary>
        internal static string Describe(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return "(this folder is missing)";

                int total;
                IList<string> names = PythonModulePath.ImportableNames(root, NamePreviewLimit, out total);
                if (total == 0) return "(nothing importable here)";

                var listed = new List<string>(names);
                string text = string.Join(", ", listed.ToArray());
                int hidden = total - listed.Count;
                return hidden > 0 ? text + ", and " + hidden + " more" : text;
            }
            catch (Exception ex)
            {
                // Reporting the kind of failure beats an empty cell that reads as "nothing here".
                return "(" + ex.GetType().Name + ")";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
