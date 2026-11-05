using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// VS2017-style editor navigation bar: three dropdowns — Classes, Functions, Variables — that
    /// list the current file's symbols. Selecting an entry raises <see cref="NavigateToLine"/> so the
    /// editor can jump to that symbol. Populated from the real Python AST (module classes/functions,
    /// class methods as <c>Class.method</c>, and module-level assignment targets).
    /// </summary>
    public sealed class NavigationBar : Panel
    {
        private readonly ComboBox _classes = MakeCombo();
        private readonly ComboBox _functions = MakeCombo();
        private readonly ComboBox _variables = MakeCombo();
        private bool _suppress;

        /// <summary>Raised with a 1-based line number when the user picks a symbol.</summary>
        public event Action<int> NavigateToLine;

        public NavigationBar()
        {
            Dock = DockStyle.Top;
            Height = 26;
            BackColor = SystemColors.Control;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(1) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
            layout.Controls.Add(_classes, 0, 0);
            layout.Controls.Add(_functions, 1, 0);
            layout.Controls.Add(_variables, 2, 0);
            Controls.Add(layout);

            _classes.SelectedIndexChanged += (s, e) => Go(_classes);
            _functions.SelectedIndexChanged += (s, e) => Go(_functions);
            _variables.SelectedIndexChanged += (s, e) => Go(_variables);

            ResetPlaceholders();
        }

        private static ComboBox MakeCombo()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints + -0.5f),
                FlatStyle = FlatStyle.Standard,
                Margin = new Padding(1, 1, 1, 1),
            };
        }

        private sealed class NavItem
        {
            public string Name;
            public int Line;      // 1-based first line; -1 for the placeholder header
            public int EndLine;   // 1-based last line of the symbol's body (== Line for variables)
            public override string ToString() { return Name; }
        }

        private void Go(ComboBox cb)
        {
            if (_suppress) return;
            NavItem it = cb.SelectedItem as NavItem;
            if (it != null && it.Line >= 1)
            {
                Action<int> h = NavigateToLine;
                if (h != null) h(it.Line);
            }
        }

        private void ResetPlaceholders()
        {
            _suppress = true;
            try
            {
                _classes.Items.Clear(); _functions.Items.Clear(); _variables.Items.Clear();
                _classes.Items.Add(new NavItem { Name = "Classes", Line = -1 });
                _functions.Items.Add(new NavItem { Name = "Functions", Line = -1 });
                _variables.Items.Add(new NavItem { Name = "Variables", Line = -1 });
                _classes.SelectedIndex = 0; _functions.SelectedIndex = 0; _variables.SelectedIndex = 0;
            }
            finally { _suppress = false; }
        }

        /// <summary>Re-scan the source and repopulate the three dropdowns. CUDA (.cu/.cuh) files are
        /// scanned for struct/class/enum types, function/kernel definitions, and device globals;
        /// everything else is parsed as Python.</summary>
        public void Populate(string source, string filePath = null)
        {
            _suppress = true;
            try
            {
                ResetPlaceholders();

                string ext = string.IsNullOrEmpty(filePath) ? null : System.IO.Path.GetExtension(filePath);
                if (string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase))
                {
                    PopulateCuda(source ?? string.Empty);
                    return;
                }

                ModuleSyntax module = null;
                try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source ?? string.Empty, null).Module; }
                catch { /* be lenient — a partially-typed file still shows what parsed */ }
                if (module == null) return;

                CollectPythonSymbols(module.Statements, string.Empty, true);
            }
            finally { _suppress = false; UpdateEnabledState(); }
        }

        // A dropdown with only its placeholder header (no navigable symbol) is disabled, so the bar
        // reads at a glance as "no classes / functions / variables here" rather than a dead control.
        private void UpdateEnabledState()
        {
            _classes.Enabled = HasNavigableItem(_classes);
            _functions.Enabled = HasNavigableItem(_functions);
            _variables.Enabled = HasNavigableItem(_variables);
        }

        private static bool HasNavigableItem(ComboBox cb)
        {
            foreach (object o in cb.Items)
                if (o is NavItem it && it.Line >= 1) return true;
            return false;
        }


        /// <summary>
        /// Walks a suite recursively, qualifying names by their enclosing scope ("Poly._unify.per").
        ///
        /// This used to be a flat two-case walk — module statements, plus one level of class
        /// methods — so anything nested deeper was invisible to the navigator. Measured against
        /// CPython's own ast on SymPy's polytools.py (8348 lines): 10 functions were missing, every
        /// one of them a helper defined inside another function, which is idiomatic Python and
        /// exactly the kind of symbol a reader needs to jump to. Nested classes had the same gap.
        ///
        /// Only module-level assignments count as Variables: locals are not navigation targets.
        /// </summary>
        private void CollectPythonSymbols(
            System.Collections.Generic.IReadOnlyList<StatementSyntax> statements, string prefix, bool moduleLevel)
        {
            if (statements == null) return;

            foreach (StatementSyntax s in statements)
            {
                if (s is ClassDefSyntax cd)
                {
                    _classes.Items.Add(new NavItem
                    {
                        Name = prefix + cd.Name,
                        Line = cd.Span.Start.Line,
                        EndLine = cd.Span.End.Line,
                    });
                    if (cd.Body != null)
                        CollectPythonSymbols(cd.Body.Statements, prefix + cd.Name + ".", false);
                }
                else if (s is FunctionDefSyntax fd)
                {
                    _functions.Items.Add(new NavItem
                    {
                        Name = prefix + fd.Name,
                        Line = fd.Span.Start.Line,
                        EndLine = fd.Span.End.Line,
                    });
                    if (fd.Body != null)
                        CollectPythonSymbols(fd.Body.Statements, prefix + fd.Name + ".", false);
                }
                else if (moduleLevel && s is AssignmentStatement asg && asg.Targets != null)
                {
                    foreach (ExpressionSyntax t in asg.Targets)
                        if (t is NameExpression ne && !string.IsNullOrEmpty(ne.Name))
                            _variables.Items.Add(new NavItem
                            {
                                Name = ne.Name,
                                Line = asg.Span.Start.Line,
                                EndLine = asg.Span.End.Line,
                            });
                }
            }
        }

        /// <summary>
        /// Point the three dropdowns at the symbol the caret is sitting in.
        ///
        /// Without this the bar is half a feature: it lists a file's symbols and jumps to them, but
        /// never answers the question a navigation bar exists to answer — "where am I?". Caught in an
        /// iter-348 screenshot audit: caret parked on line 16, squarely inside
        /// <c>def _construct_simple(...)</c>, and all three dropdowns still read their placeholder.
        ///
        /// Selection is by containment, innermost wins, so inside <c>Poly._unify.per</c> the bar shows
        /// that helper rather than the outer function. A caret between symbols (imports, blank space
        /// at module level) correctly falls back to the placeholder instead of pointing at whichever
        /// symbol happens to be above it.
        /// </summary>
        public void SyncToLine(int line)
        {
            if (line < 1) return;
            _suppress = true;
            try
            {
                Select(_classes, line);
                Select(_functions, line);
                Select(_variables, line);
            }
            finally { _suppress = false; }
        }

        // Innermost enclosing item for this line, or the placeholder (index 0) when the caret is
        // outside every symbol in this category. Assigning SelectedIndex unconditionally would
        // repaint the combo on every caret move, so only a real change is written.
        private static void Select(ComboBox cb, int line)
        {
            int best = 0;
            int bestStart = int.MinValue;
            for (int i = 1; i < cb.Items.Count; i++)
            {
                NavItem it = cb.Items[i] as NavItem;
                if (it == null || it.Line < 1) continue;
                int end = it.EndLine >= it.Line ? it.EndLine : it.Line;
                if (line < it.Line || line > end) continue;
                if (it.Line <= bestStart) continue;   // keep the innermost (latest-starting) match
                bestStart = it.Line;
                best = i;
            }
            if (cb.SelectedIndex != best) cb.SelectedIndex = best;
        }

        // Best-effort regex scan of CUDA-C source for the three navigation categories. C is not
        // regular, so this favors the common cases: struct/class/enum names, function/kernel
        // definitions (a name followed by an arg list and an opening brace, minus control-flow
        // keywords), and __device__/__constant__/__managed__ global variables.
        private void PopulateCuda(string source)
        {
            try
            {
                foreach (Match m in Regex.Matches(source, @"\b(?:struct|class|enum)\s+([A-Za-z_]\w*)"))
                    _classes.Items.Add(new NavItem { Name = m.Groups[1].Value, Line = LineOf(source, m.Index) });

                foreach (Match m in Regex.Matches(source, @"([A-Za-z_]\w*)\s*\([^;{}]*\)\s*\{"))
                {
                    string name = m.Groups[1].Value;
                    if (name == "if" || name == "for" || name == "while" || name == "switch" || name == "catch") continue;
                    _functions.Items.Add(new NavItem { Name = name, Line = LineOf(source, m.Index) });
                }

                foreach (Match m in Regex.Matches(source,
                    @"\b(?:__device__|__constant__|__managed__)\s+[A-Za-z_][\w:<>,\*&\s]*?\b([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*[=;]"))
                    _variables.Items.Add(new NavItem { Name = m.Groups[1].Value, Line = LineOf(source, m.Index) });
            }
            catch { /* regex is best-effort; never block the UI */ }
        }

        private static int LineOf(string source, int index)
        {
            int line = 1;
            int n = Math.Min(index, source.Length);
            for (int i = 0; i < n; i++) if (source[i] == '\n') line++;
            return line;
        }
    }
}
