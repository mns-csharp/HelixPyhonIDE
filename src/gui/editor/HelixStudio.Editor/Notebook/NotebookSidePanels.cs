using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// The panes behind Colab's left icon rail. Each is a plain <see cref="UserControl"/> that knows
    /// only the document (and, for Variables, what the kernel last reported): the rail owns which one
    /// is showing, and the tab owns the kernel.
    /// </summary>
    public abstract class NotebookSidePanel : UserControl
    {
        protected NotebookSidePanel(string title)
        {
            Title = title;
            Dock = DockStyle.Fill;
            BackColor = Color.White;
        }

        public string Title { get; private set; }

        /// <summary>Raised when the user picks something that should scroll the notebook.</summary>
        public event EventHandler<int> CellActivated;

        protected void RaiseCellActivated(int index)
        {
            EventHandler<int> handler = CellActivated;
            if (handler != null) handler(this, index);
        }

        /// <summary>Rebuild from the document. Called whenever the notebook changes underneath.</summary>
        public abstract void Refresh(NotebookDocument document);
    }

    /// <summary>
    /// Table of contents: the markdown headings, nested by level, each jumping to its cell.
    ///
    /// The corpus is exactly the case this exists for — a 78-cell notebook where the only structure
    /// is `## Section` markdown cells, and scrolling is the only way to find anything.
    /// </summary>
    public sealed class NotebookTocPanel : NotebookSidePanel
    {
        private readonly TreeView _tree;

        public NotebookTocPanel() : base("Table of contents")
        {
            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                ShowLines = false,
                ShowRootLines = false,
                FullRowSelect = true,
                Indent = 14,
            };
            _tree.AfterSelect += delegate (object sender, TreeViewEventArgs e)
            {
                if (e.Node != null && e.Node.Tag is int) RaiseCellActivated((int)e.Node.Tag);
            };
            Controls.Add(_tree);
        }

        public override void Refresh(NotebookDocument document)
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                if (document == null) return;

                // A stack of the open heading at each level, so `###` nests under the `##` above it
                // even when the notebook skips a level — which real notebooks do constantly.
                var openAtLevel = new Dictionary<int, TreeNode>();
                for (int i = 0; i < document.Cells.Count; i++)
                {
                    NotebookCell cell = document.Cells[i];
                    if (cell.Kind != NotebookCellKind.Markdown) continue;

                    foreach (string line in (cell.Source ?? string.Empty).Split('\n'))
                    {
                        string text = line.TrimStart();
                        int level = 0;
                        while (level < text.Length && text[level] == '#') level++;
                        if (level == 0 || level > 6) continue;
                        string heading = text.Substring(level).Trim().Trim('*', '_', '`').Trim();
                        if (heading.Length == 0) continue;

                        var node = new TreeNode(heading) { Tag = i };
                        TreeNode parent = null;
                        for (int up = level - 1; up >= 1 && parent == null; up--)
                            openAtLevel.TryGetValue(up, out parent);

                        if (parent == null) _tree.Nodes.Add(node); else parent.Nodes.Add(node);
                        openAtLevel[level] = node;
                        for (int deeper = level + 1; deeper <= 6; deeper++) openAtLevel.Remove(deeper);
                    }
                }
                _tree.ExpandAll();

                if (_tree.Nodes.Count == 0)
                    _tree.Nodes.Add(new TreeNode("(no markdown headings in this notebook)"));
            }
            finally { _tree.EndUpdate(); }
        }
    }

    /// <summary>Search every cell's source and jump to the hit — Colab's magnifying glass.</summary>
    public sealed class NotebookFindPanel : NotebookSidePanel
    {
        private readonly TextBox _query;
        private readonly ListBox _hits;
        private readonly List<int> _hitCells = new List<int>();
        private NotebookDocument _document;

        public NotebookFindPanel() : base("Find")
        {
            _query = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle };
            _query.TextChanged += delegate { Search(); };
            _hits = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
            _hits.SelectedIndexChanged += delegate
            {
                if (_hits.SelectedIndex >= 0 && _hits.SelectedIndex < _hitCells.Count)
                    RaiseCellActivated(_hitCells[_hits.SelectedIndex]);
            };
            Controls.Add(_hits);
            Controls.Add(_query);
        }

        public override void Refresh(NotebookDocument document)
        {
            _document = document;
            Search();
        }

        private void Search()
        {
            _hits.BeginUpdate();
            try
            {
                _hits.Items.Clear();
                _hitCells.Clear();
                string needle = _query.Text;
                if (_document == null || string.IsNullOrEmpty(needle)) return;

                for (int i = 0; i < _document.Cells.Count; i++)
                {
                    string source = _document.Cells[i].Source ?? string.Empty;
                    int at = source.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;

                    // Show the matching LINE, not the cell: a hit reported as "cell 41" is a hit the
                    // user still has to go and find.
                    int lineStart = source.LastIndexOf('\n', Math.Min(at, source.Length - 1)) + 1;
                    int lineEnd = source.IndexOf('\n', at);
                    if (lineEnd < 0) lineEnd = source.Length;
                    string line = source.Substring(lineStart, lineEnd - lineStart).Trim();
                    if (line.Length > 60) line = line.Substring(0, 60) + "…";

                    _hits.Items.Add("[" + (i + 1) + "] " + line);
                    _hitCells.Add(i);
                }
                if (_hits.Items.Count == 0) _hits.Items.Add("(no matches)");
            }
            finally { _hits.EndUpdate(); }
        }

        public void FocusQuery() { _query.Focus(); _query.SelectAll(); }
    }

    /// <summary>
    /// The Variables pane: what the RUNTIME currently holds, not what the source mentions.
    ///
    /// It is populated from the kernel, so it is empty until something has run — which is the honest
    /// state: a notebook that has not run has no variables, however many the code declares.
    /// </summary>
    public sealed class NotebookVariablesPanel : NotebookSidePanel
    {
        private readonly ListView _list;
        private readonly Label _hint;

        public NotebookVariablesPanel() : base("Variables")
        {
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            _list.Columns.Add("Name", 110);
            _list.Columns.Add("Type", 90);
            _list.Columns.Add("Shape", 80);
            _list.Columns.Add("Value", 320);

            _hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(8, 8, 8, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Run a cell to see what the runtime holds.",
            };
            Controls.Add(_list);
            Controls.Add(_hint);
        }

        public override void Refresh(NotebookDocument document) { /* driven by the kernel, not the file */ }

        /// <summary>Apply a `variables` message from the kernel.</summary>
        public void Apply(string messageJson)
        {
            JArray variables;
            try { variables = JObject.Parse(messageJson)["variables"] as JArray; }
            catch (Exception) { return; }

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                if (variables == null) return;
                foreach (JToken token in variables)
                {
                    var item = new ListViewItem(Value(token, "name"));
                    item.SubItems.Add(Value(token, "type"));
                    item.SubItems.Add(Value(token, "shape"));
                    item.SubItems.Add(Value(token, "value"));
                    _list.Items.Add(item);
                }
                _hint.Text = _list.Items.Count == 0
                    ? "The runtime has no variables yet."
                    : _list.Items.Count + " variable(s) in the runtime.";
            }
            finally { _list.EndUpdate(); }
        }

        public void SetDisconnected()
        {
            _list.Items.Clear();
            _hint.Text = "No runtime is connected. Run a cell to start one.";
        }

        private static string Value(JToken token, string name)
        {
            JToken value = token[name];
            return value == null || value.Type == JTokenType.Null ? string.Empty : value.ToString();
        }
    }

    /// <summary>
    /// The Files pane. Colab's shows the runtime's virtual disk; this one shows the folder the
    /// notebook actually lives in, which is the local equivalent and the thing a relative
    /// `pd.read_csv("data.csv")` in a cell will resolve against.
    /// </summary>
    /// <summary>One "open this file" ask from the Files tree.</summary>
    public sealed class NotebookFileOpenRequest : EventArgs
    {
        public NotebookFileOpenRequest(string path, bool? readOnly)
        {
            Path = path;
            ReadOnly = readOnly;
        }

        public string Path { get; private set; }

        /// <summary>
        /// True for read-only, false for editable, and NULL when the user did not say — a plain
        /// double-click — which leaves the choice to the host, the only part that knows whether
        /// the file belongs to the notebook or to a library on its import path.
        /// </summary>
        public bool? ReadOnly { get; private set; }
    }

    public sealed class NotebookFilesPanel : NotebookSidePanel
    {
        private readonly TreeView _tree;
        private string _root;
        private bool _pinnedByUser;

        /// <summary>The node the current click began on — see the MouseDown handler for why.</summary>
        private TreeNode _pressedNode;

        /// <summary>
        /// Raised when the user asks to open a file. <c>ReadOnly</c> says which of the two ways
        /// they asked for — see <see cref="BuildContextMenu"/> for why there are two.
        /// </summary>
        public event EventHandler<NotebookFileOpenRequest> FileActivated;

        /// <summary>Raised when the user asks for a Python file here to become importable.</summary>
        public event EventHandler<string> MakeImportableRequested;

        /// <summary>Raised when the user wants to point this tree at a different folder.</summary>
        public event EventHandler BrowseRequested;

        public NotebookFilesPanel() : base("Files")
        {
            _tree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false };
            _tree.BeforeExpand += delegate (object sender, TreeViewCancelEventArgs e) { Populate(e.Node); };
            // Remember what was under the pointer when the pair of clicks STARTED.
            //
            // A double-click on a folder also collapses it, and collapsing a folder with fifty
            // children pulls everything below it up the screen. `NodeMouseDoubleClick` hit-tests at
            // the moment it fires -- by which time a different node is under the unmoved pointer --
            // so double-clicking the `cms` folder opened `restraint.py`: a file the user never
            // clicked, chosen purely by where it landed after the tree reflowed. Recorded on the
            // FIRST click of the pair, which is the only moment guaranteed to precede the toggle.
            _tree.MouseDown += delegate (object sender, MouseEventArgs e)
            {
                if (e.Clicks == 1) _pressedNode = _tree.GetNodeAt(e.Location);
            };
            _tree.NodeMouseDoubleClick += delegate (object sender, TreeNodeMouseClickEventArgs e)
            {
                // Double-click takes the DEFAULT, and the host decides what that is: the notebook's
                // own files open for editing, a registered module's files open for reading.
                TreeNode clicked = _pressedNode ?? e.Node;
                RaiseOpen(clicked == null ? null : clicked.Tag as string, null);
            };
            // A right click has to select first: WinForms shows the menu without moving the
            // selection, so without this the command would act on whatever was highlighted before.
            _tree.NodeMouseClick += delegate (object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node;
            };
            _tree.ContextMenuStrip = BuildContextMenu();
            Controls.Add(_tree);
        }

        /// <summary>
        /// The right-click menu on the tree: make a Python file importable, or go and look
        /// somewhere else.
        ///
        /// <para>
        /// "Somewhere else" is the load-bearing half. This tree starts at the notebook's own folder,
        /// and the whole point of registering a module is that it lives somewhere the notebook does
        /// not — a repository on another drive, a book's example package. Without a way out of the
        /// starting folder, the browser cannot reach the thing the user is trying to import.
        /// </para>
        /// </summary>
        private ContextMenuStrip BuildContextMenu()
        {
            var importable = new ToolStripMenuItem("Make this importable", null, delegate
            {
                string path = _tree.SelectedNode == null ? null : _tree.SelectedNode.Tag as string;
                EventHandler<string> handler = MakeImportableRequested;
                if (path != null && handler != null) handler(this, path);
            });
            var browse = new ToolStripMenuItem("Look in another folder…", null, delegate
            {
                EventHandler handler = BrowseRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            });

            // Both ways, spelled out. Reading somebody's library and changing it are different
            // intentions, and which one a double-click means depends on whose file it is — so the
            // menu offers each explicitly rather than making the user infer the default.
            var openRead = new ToolStripMenuItem("Open read-only", null,
                delegate { RaiseOpen(SelectedPath(), true); });
            var openWrite = new ToolStripMenuItem("Open for editing", null,
                delegate { RaiseOpen(SelectedPath(), false); });

            var menu = new ContextMenuStrip();
            menu.Items.Add(openRead);
            menu.Items.Add(openWrite);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(importable);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(browse);
            menu.Opening += delegate (object sender, System.ComponentModel.CancelEventArgs e)
            {
                string path = SelectedPath();
                bool isFile = path != null && !Directory.Exists(path);
                openRead.Enabled = isFile;
                openWrite.Enabled = isFile;
                importable.Enabled = path != null && PythonModulePath.IsImportableExtension(path);
            };
            return menu;
        }

        public void SetRoot(string directory)
        {
            _root = directory;
            _pressedNode = null;               // the node it referred to is about to stop existing
            _tree.Nodes.Clear();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            var root = new TreeNode(Path.GetFileName(directory.TrimEnd('\\', '/'))) { Tag = directory };
            _tree.Nodes.Add(root);
            Populate(root);
            root.Expand();
        }

        private string SelectedPath()
        {
            return _tree.SelectedNode == null ? null : _tree.SelectedNode.Tag as string;
        }

        /// <summary>Ask the host to open a file. A null <paramref name="readOnly"/> means "you decide".</summary>
        private void RaiseOpen(string path, bool? readOnly)
        {
            if (path == null || Directory.Exists(path)) return;
            EventHandler<NotebookFileOpenRequest> handler = FileActivated;
            if (handler != null) handler(this, new NotebookFileOpenRequest(path, readOnly));
        }

        /// <summary>
        /// Point the tree somewhere the user chose. Sticky: after this, a document refresh must not
        /// yank the tree back to the notebook's own folder, which is precisely the folder they just
        /// navigated away from.
        /// </summary>
        public void BrowseTo(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            _pinnedByUser = true;
            SetRoot(directory);
        }

        public override void Refresh(NotebookDocument document)
        {
            if (_pinnedByUser) return;
            string directory = document == null || string.IsNullOrEmpty(document.FilePath)
                ? null
                : Path.GetDirectoryName(document.FilePath);
            if (!string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase)) SetRoot(directory);
        }

        /// <summary>
        /// One level, on demand. A notebook next to a dataset directory can sit beside tens of
        /// thousands of files, and walking all of it to draw a tree nobody expanded is how a panel
        /// freezes the IDE for a minute.
        /// </summary>
        private void Populate(TreeNode node)
        {
            string path = node.Tag as string;
            if (path == null || !Directory.Exists(path)) return;
            if (node.Nodes.Count > 0 && node.Nodes[0].Tag != null) return;   // already filled

            node.Nodes.Clear();
            try
            {
                foreach (string directory in Directory.GetDirectories(path))
                {
                    var child = new TreeNode(Path.GetFileName(directory)) { Tag = directory };
                    child.Nodes.Add(new TreeNode("…"));      // a placeholder makes it expandable
                    node.Nodes.Add(child);
                }
                foreach (string file in Directory.GetFiles(path))
                    node.Nodes.Add(new TreeNode(Path.GetFileName(file)) { Tag = file });
            }
            catch (UnauthorizedAccessException) { node.Nodes.Add(new TreeNode("(access denied)")); }
            catch (IOException) { node.Nodes.Add(new TreeNode("(unreadable)")); }
        }
    }

    /// <summary>
    /// Colab's resource meter: how much RAM and disk the runtime is using. Rendered as two labelled
    /// bars, updated from the kernel — and honest about which numbers it could not get.
    /// </summary>
    public sealed class NotebookResourcesPanel : NotebookSidePanel
    {
        private readonly Label _text;

        public NotebookResourcesPanel() : base("Resources")
        {
            _text = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ForeColor = SystemColors.ControlText,
                Text = "No runtime is connected.",
            };
            Controls.Add(_text);
        }

        public override void Refresh(NotebookDocument document) { }

        public void Apply(string messageJson)
        {
            JObject resources;
            try { resources = JObject.Parse(messageJson)["resources"] as JObject; }
            catch (Exception) { return; }
            if (resources == null) return;

            var lines = new List<string>();
            lines.Add("Runtime: Python " + Value(resources, "python"));
            string ram = Pair(resources, "ram_used_bytes", "ram_total_bytes");
            if (ram != null) lines.Add("Process RAM: " + ram);
            string systemRam = Pair(resources, "system_ram_used_bytes", "ram_total_bytes");
            if (systemRam != null) lines.Add("System RAM: " + systemRam);
            string disk = Pair(resources, "disk_used_bytes", "disk_total_bytes");
            if (disk != null) lines.Add("Disk: " + disk);
            if (string.Equals(Value(resources, "source"), "stdlib", StringComparison.Ordinal))
                lines.Add(string.Empty + Environment.NewLine
                          + "(psutil is not installed in this interpreter, so some figures are missing "
                          + "rather than estimated.)");

            _text.Text = string.Join(Environment.NewLine, lines.ToArray());
        }

        public void SetDisconnected() { _text.Text = "No runtime is connected. Run a cell to start one."; }

        private static string Pair(JObject resources, string usedKey, string totalKey)
        {
            long used, total;
            if (!TryRead(resources, usedKey, out used)) return null;
            if (!TryRead(resources, totalKey, out total) || total <= 0) return Bytes(used);
            return Bytes(used) + " of " + Bytes(total)
                 + " (" + (used * 100.0 / total).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
        }

        private static bool TryRead(JObject resources, string key, out long value)
        {
            value = 0;
            JToken token = resources[key];
            if (token == null || token.Type == JTokenType.Null) return false;
            return long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string Bytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = value;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return size.ToString(size >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string Value(JObject resources, string key)
        {
            JToken token = resources[key];
            return token == null ? "?" : token.ToString();
        }
    }
}
