namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Owns symbol navigation and rename: go-to-type/source/function/designer from the
    /// editor and tree, resolving editor symbols, prompting renames, and rewriting
    /// imports across the solution. References the host form via <c>host</c>.
    /// </summary>
    public sealed class NavigationCoordinator : ICoordinator
    {
        // Shared context handed in at Attach; `host` is cached from it for terse access
        // to the MainForm's WinForms surface and solution/model state.
        private MainFormContext _context;
        private MainForm host;
        private readonly List<SourceLocation> _backHistory = new List<SourceLocation>();
        private readonly List<SourceLocation> _forwardHistory = new List<SourceLocation>();
        private bool _replayingHistory;
        private const int MaxHistory = 100;

        private sealed class SourceLocation
        {
            public string FilePath;
            public int Line;
            public int Column;
        }

        // Wire up to the host form once its controls exist. Caching host lets every
        // handler below reach MainForm state (e.g. _currentSolution) without a property chain.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Release references on form close so the coordinator doesn't pin the form.
        public void Detach()
        {
            _backHistory.Clear();
            _forwardHistory.Clear();
            _context = null;
            host = null;
        }

        /// <summary>
        /// Called by the central editor-open path before it changes document/caret. Records the
        /// location the user is leaving, so every source navigation route (definition, references,
        /// diagnostics, tree) participates in one browser-style history.
        /// </summary>
        internal void RecordNavigationTarget(string filePath, int line, int column)
        {
            if (_replayingHistory || host == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;
            SourceLocation current = CurrentLocation();
            if (current == null) { UpdateHistoryButtons(); return; }
            string target = Path.GetFullPath(filePath);
            int targetLine = line > 0 ? line : current.Line;
            if (string.Equals(current.FilePath, target, StringComparison.OrdinalIgnoreCase)
                && (line <= 0 || current.Line == targetLine)) return;
            AddBounded(_backHistory, current);
            _forwardHistory.Clear();
            UpdateHistoryButtons();
        }

        internal void NavigateBackward()
        {
            if (_backHistory.Count == 0 || host == null) return;
            SourceLocation current = CurrentLocation();
            SourceLocation target = Pop(_backHistory);
            if (current != null) AddBounded(_forwardHistory, current);
            Replay(target);
        }

        internal void NavigateForward()
        {
            if (_forwardHistory.Count == 0 || host == null) return;
            SourceLocation current = CurrentLocation();
            SourceLocation target = Pop(_forwardHistory);
            if (current != null) AddBounded(_backHistory, current);
            Replay(target);
        }

        private void Replay(SourceLocation location)
        {
            if (location == null || !File.Exists(location.FilePath)) { UpdateHistoryButtons(); return; }
            _replayingHistory = true;
            try { host.OpenInEditor(location.FilePath, location.Line, true, location.Column); }
            finally { _replayingHistory = false; UpdateHistoryButtons(); }
        }

        private SourceLocation CurrentLocation()
        {
            if (host == null || host._editorInnerTabControl == null) return null;
            HelixPythonNamespace.Editor.EditorTabPage tab =
                host._editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage;
            if (tab == null || tab.Editor == null || string.IsNullOrEmpty(tab.FilePath)) return null;
            int position = tab.Editor.CurrentPosition;
            int lineIndex = tab.Editor.LineFromPosition(position);
            int lineStart = tab.Editor.Lines[lineIndex].Position;
            return new SourceLocation
            {
                FilePath = Path.GetFullPath(tab.FilePath),
                Line = lineIndex + 1,
                Column = position - lineStart + 1,
            };
        }

        private static void AddBounded(List<SourceLocation> history, SourceLocation location)
        {
            if (location == null) return;
            history.Add(location);
            if (history.Count > MaxHistory) history.RemoveAt(0);
        }

        private static SourceLocation Pop(List<SourceLocation> history)
        {
            int index = history.Count - 1;
            SourceLocation location = history[index];
            history.RemoveAt(index);
            return location;
        }

        private void UpdateHistoryButtons()
        {
            if (host == null || host._toolStrip == null) return;
            ToolStripItem back = host._toolStrip.Items["tbNavigateBackward"];
            ToolStripItem forward = host._toolStrip.Items["tbNavigateForward"];
            if (back != null) back.Enabled = _backHistory.Count > 0;
            if (forward != null) forward.Enabled = _forwardHistory.Count > 0;
        }

        // Rename support: rewrite Python `import` statements referencing oldNs to newNs
        // across every .py file in every project. Returns (files touched, total edits).
        // excludePath skips the file the caller is already editing itself.
        internal (int filesChanged, int totalHits) RewriteImportsAcrossSolution(
            string oldNs, string newNs, string excludePath)
        {
            int filesChanged = 0, totalHits = 0;
            // Nothing to rewrite without a loaded solution.
            if (host._currentSolution == null) return (0, 0);

            // Track already-visited full paths so overlapping project directories don't
            // double-process the same file (case-insensitive on Windows).
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Walk every project's directory tree for .py files. Missing dirs and
            // enumeration failures are skipped rather than aborting the whole rewrite.
            foreach (ProjectModel project in host._currentSolution.Projects)
            {
                string dir = project.ProjectDirectory;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (string f in files)
                {
                    // Skip files already seen (overlapping dirs) and the excluded path.
                    if (!seen.Add(Path.GetFullPath(f))) continue;
                    if (!string.IsNullOrEmpty(excludePath) &&
                        string.Equals(Path.GetFullPath(f), Path.GetFullPath(excludePath),
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    string source;
                    try { source = File.ReadAllText(f); }
                    catch { continue; }

                    // Ask the rewriter for the transformed text; Count == 0 means no
                    // matching import in this file, so leave it untouched.
                    var result = HelixPythonNamespace.Workspace.Python.PythonIdentifierRewriter
                        .RewriteModuleImports(source, oldNs, newNs);
                    if (result.Count == 0) continue;

                    // Persist with the project's UTF-8-no-BOM encoding; a write failure
                    // on one file must not stop the rest of the rewrite.
                    try
                    {
                        File.WriteAllText(f, result.Text, MainForm.Utf8NoBomForNewFiles);
                        filesChanged++;
                        totalHits += result.Count;
                    }
                    catch { /* best-effort per-file */ }
                }
            }
            return (filesChanged, totalHits);
        }

        /// <summary>
        /// F12 — Go to Definition.
        ///
        /// Runs the caret through the same <see cref="HelixPythonNamespace.Workspace.Python.SymbolResolver"/>
        /// the context menu uses, so a function, a method or a class all resolve, and lands on the
        /// definition in SOURCE — which is what F12 means in every IDE the user has used.
        ///
        /// Before iter 347 this took only the identifier text, matched it against class names, and
        /// returned silently on a miss: F12 on a function did nothing, and nothing told the user
        /// why. Every exit path below now reports what happened in the status bar.
        /// </summary>
        internal void OnEditorGoToDefinitionRequested(object sender, HelixPythonNamespace.Editor.EditorGoToDefinitionArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.Token)) return;

            // CUDA-C files resolve against the CUDA symbol table, not the Python
            // resolver (which can't parse them and used to report a parse failure).
            string ext = string.IsNullOrEmpty(e.FilePath)
                ? string.Empty : Path.GetExtension(e.FilePath).ToLowerInvariant();
            if (ext == ".cu" || ext == ".cuh")
            {
                if (GoToCudaDefinition(e)) e.Handled = true;
                return;
            }

            if (host._currentSolution == null) { Status("Go to Definition needs an open solution."); return; }

            HelixPythonNamespace.Workspace.Python.SymbolResolver resolver = GetSymbolResolver();
            if (resolver == null) { Status("Go to Definition needs an open solution."); return; }

            HelixPythonNamespace.Python.Syntax.ModuleSyntax module;
            try
            {
                string src = (e.FilePath != null && File.Exists(e.FilePath))
                    ? File.ReadAllText(e.FilePath) : string.Empty;
                module = global::HelixPythonNamespace.Python.Python.ParseModule(src, e.FilePath).Module;
            }
            catch
            {
                Status("Couldn't parse this file, so '" + e.Token + "' can't be resolved.");
                return;
            }

            HelixPythonNamespace.Workspace.Python.ResolvedSymbol resolved =
                resolver.Resolve(module, e.FilePath, e.Line, e.Column, e.Token);

            // Functions and methods: jump to the body. This is the case the old bare-word
            // implementation could never handle.
            if (resolved != null && resolved.Function != null
                && !string.IsNullOrEmpty(resolved.Function.SourceFilePath))
            {
                GoToFunction(resolved.Function);
                e.Handled = true;
                return;
            }

            if (resolved != null && resolved.Variable != null
                && !string.IsNullOrEmpty(resolved.Variable.SourceFilePath))
            {
                GoToVariable(resolved.Variable);
                e.Handled = true;
                return;
            }

            TypeModel target = resolved != null ? resolved.Target : null;

            // Several classes share the name — ask rather than guessing, matching the context menu.
            if (target == null && resolved != null && resolved.IsAmbiguous)
            {
                List<TypeModel> candidates = new List<TypeModel>(resolved.Candidates);
                using (HelixPythonNamespace.App.Dialogs.GoToTypePickerForm picker =
                    new HelixPythonNamespace.App.Dialogs.GoToTypePickerForm(resolved.Token, candidates))
                {
                    if (picker.ShowDialog(host) == DialogResult.OK) target = picker.Selected;
                    else return;   // the user cancelled; that is not a failure worth reporting
                }
            }

            // Last resort for a token the resolver can't place from context (e.g. the file no
            // longer parses cleanly): a unique class of that name across the solution.
            if (target == null) target = UniqueTypeNamed(e.Token);

            if (target == null)
            {
                Status("No definition found for '" + e.Token + "'.");
                return;
            }

            GoToSource(target);
            e.Handled = true;
        }

        /// <summary>
        /// F12 in a .cu/.cuh file: symbol-table-backed lookup (SRS §10 IntelliSense).
        /// Searches the current file first, then sibling .cu/.cuh files in the same
        /// directory (a native workspace's TUs live together). Returns true when it
        /// navigated; every miss lands a status-bar explanation, like the Python path.
        /// </summary>
        private bool GoToCudaDefinition(HelixPythonNamespace.Editor.EditorGoToDefinitionArgs e)
        {
            string source;
            try
            {
                source = File.Exists(e.FilePath) ? File.ReadAllText(e.FilePath) : null;
            }
            catch { source = null; }
            if (source == null)
            {
                Status("Couldn't read this file, so '" + e.Token + "' can't be resolved.");
                return false;
            }

            var loc = HelixPythonNamespace.CudaVirtualCompiler.CudaDefinitionLocator.Locate(source, e.FilePath, e.Token);
            if (loc == null)
            {
                // Not in this TU — try the sibling CUDA files of the same directory.
                string dir = null;
                try { dir = Path.GetDirectoryName(Path.GetFullPath(e.FilePath)); } catch { }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string pattern in new[] { "*.cu", "*.cuh" })
                    {
                        string[] siblings;
                        try { siblings = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
                        catch { continue; }
                        foreach (string sibling in siblings)
                        {
                            if (string.Equals(Path.GetFullPath(sibling), Path.GetFullPath(e.FilePath),
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            string siblingSource;
                            try { siblingSource = File.ReadAllText(sibling); } catch { continue; }
                            loc = HelixPythonNamespace.CudaVirtualCompiler.CudaDefinitionLocator
                                .Locate(siblingSource, sibling, e.Token);
                            if (loc != null) break;
                        }
                        if (loc != null) break;
                    }
                }
            }

            if (loc == null)
            {
                Status("No definition found for '" + e.Token + "'.");
                return false;
            }
            host.OpenInEditor(loc.FilePath, loc.Line);
            Status("Definition of " + loc.SymbolKind + " '" + e.Token + "' — line " + loc.Line + ".");
            return true;
        }

        /// <summary>The one solution class with this simple name, or null if there are none or many.</summary>
        private TypeModel UniqueTypeNamed(string name)
        {
            TypeModel found = null;
            foreach (TypeModel t in host._currentSolution.GetAllTypes())
            {
                if (!string.Equals(t.Name, name, StringComparison.Ordinal)) continue;
                if (found != null) return null;   // ambiguous — don't pick arbitrarily
                found = t;
            }
            return found;
        }

        private void Status(string message)
        {
            if (host._statusLabel != null) host._statusLabel.Text = message;
        }

        // Navigate to a type on the "All" designer tab (the diagram view).
        public void GoToDesigner(TypeModel type)
        {
            if (type == null) return;
            host.NavigateToTypeOnAllTab(type);
        }

        // Open the type's underlying .py source in the text editor.
        public void GoToSource(TypeModel type)
        {
            if (type == null) return;
            host.OpenSourceForType(type);
        }

        // Build a fresh resolver bound to the current solution; null when nothing is loaded.
        internal HelixPythonNamespace.Workspace.Python.SymbolResolver GetSymbolResolver()
        {
            if (host._currentSolution == null) return null;
            return new HelixPythonNamespace.Workspace.Python.SymbolResolver(host._currentSolution);
        }

        // Parse the file under the caret and resolve the token to a TypeModel.
        // Returns null on any parse failure so navigation silently no-ops.
        private TypeModel ResolveEditorSymbol(string filePath, int line, int column, string token)
        {
            HelixPythonNamespace.Workspace.Python.SymbolResolver resolver = GetSymbolResolver();
            if (resolver == null) return null;
            HelixPythonNamespace.Python.Syntax.ModuleSyntax module;
            // Parse from disk (empty source if the path is gone); swallow parse errors.
            try
            {
                string source = (filePath != null && File.Exists(filePath)) ? File.ReadAllText(filePath) : string.Empty;
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath).Module;
            }
            catch { return null; }
            HelixPythonNamespace.Workspace.Python.ResolvedSymbol resolved =
                resolver.Resolve(module, filePath, line, column, token);
            if (resolved == null) return null;
            // Unambiguous hit — return it directly.
            if (resolved.Target != null) return resolved.Target;
            // Multiple candidates share the name — let the user pick one.
            if (resolved.IsAmbiguous)
            {
                List<TypeModel> candidatesList = new List<TypeModel>(resolved.Candidates);
                using (HelixPythonNamespace.App.Dialogs.GoToTypePickerForm picker =
                    new HelixPythonNamespace.App.Dialogs.GoToTypePickerForm(resolved.Token, candidatesList))
                {
                    if (picker.ShowDialog(host) == DialogResult.OK) return picker.Selected;
                }
            }
            return null;
        }

        // Editor context-menu builder: the editor raises this to ask which navigation /
        // refactor actions apply to the symbol under the caret. We resolve the symbol and
        // append EditorContextMenuItem entries to e.Items; the editor renders them.
        internal void OnEditorResolveSymbolRequested(object sender, HelixPythonNamespace.Editor.EditorSymbolRequestArgs e)
        {
            if (e == null) return;

            // File-level refactoring: Remove Unused Imports is offered on any Python file.
            if (!string.IsNullOrEmpty(e.FilePath)
                && e.FilePath.EndsWith(".py", System.StringComparison.OrdinalIgnoreCase))
            {
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Remove Unused Imports",
                    Callback = delegate { host.OrganizeImportsInActiveEditor(); }
                });
            }

            // Selection-based refactoring: offer Extract Variable whenever text is selected, regardless
            // of whether a symbol resolves under the caret.
            if (host.ActiveEditorHasSelection())
            {
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Extract Variable…",
                    Callback = delegate { host.PromptExtractVariable(); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Extract Constant…",
                    Callback = delegate { host.PromptExtractConstant(); }
                });
            }

            if (host._currentSolution == null) return;

            HelixPythonNamespace.Workspace.Python.SymbolResolver resolver = GetSymbolResolver();
            if (resolver == null) return;

            HelixPythonNamespace.Python.Syntax.ModuleSyntax module;
            // Parse the file the request came from; bail quietly if it won't parse.
            try
            {
                string src = (e.FilePath != null && File.Exists(e.FilePath))
                    ? File.ReadAllText(e.FilePath) : string.Empty;
                module = global::HelixPythonNamespace.Python.Python.ParseModule(src, e.FilePath).Module;
            }
            catch { return; }

            HelixPythonNamespace.Workspace.Python.ResolvedSymbol resolved =
                resolver.Resolve(module, e.FilePath, e.Line, e.Column, e.Token);
            if (resolved == null) return;

            if (resolved.Variable != null)
            {
                HelixPythonNamespace.Workspace.Python.VariableSymbol variable = resolved.Variable;
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Go to variable declaration",
                    Callback = delegate { GoToVariable(variable); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Rename Symbolâ€¦",
                    Callback = delegate { PromptRenameVariable(variable); }
                });
                return;
            }

            // Function target — offer navigation + refactor actions.
            // fn is captured into each delegate below so the menu item acts on this
            // resolved function even after the resolver state is gone.
            if (resolved.Function != null)
            {
                FunctionModel fn = resolved.Function;
                // Only offer "Goto Function Body" when we actually know where it lives.
                if (!string.IsNullOrEmpty(fn.SourceFilePath))
                {
                    e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                    {
                        Label = "Go to Function Body",
                        Callback = delegate { GoToFunction(fn); }
                    });
                }
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Find All References",
                    Callback = delegate { host.ShowFindAllReferences(fn); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Call Hierarchy (Incoming Calls)",
                    Callback = delegate { host.ShowCallHierarchy(fn); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Rename Symbol…",
                    Callback = delegate { PromptRenameSymbol(null, fn); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Edit Signature…",
                    Callback = delegate { host.EditFunctionSignature(fn); }
                });
                e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
                {
                    Label = "Rename Containing Package…",
                    Callback = delegate { PromptRenamePackageForSymbol(fn.SourceFilePath); }
                });
                // Function and type branches are mutually exclusive — done here.
                return;
            }

            // Type target — picker only in the type branch.
            TypeModel type = resolved.Target;
            // Ambiguous name: prompt the user to disambiguate before building the menu.
            if (type == null && resolved.IsAmbiguous)
            {
                List<TypeModel> candidatesList = new List<TypeModel>(resolved.Candidates);
                using (HelixPythonNamespace.App.Dialogs.GoToTypePickerForm picker =
                    new HelixPythonNamespace.App.Dialogs.GoToTypePickerForm(resolved.Token, candidatesList))
                {
                    if (picker.ShowDialog(host) == DialogResult.OK) type = picker.Selected;
                }
            }
            if (type == null) return;

            // Capture into a local so every delegate below closes over the same instance.
            TypeModel captured = type;
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Goto Designer",
                Callback = delegate { GoToDesigner(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Goto Source",
                Callback = delegate { GoToSource(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Go To Implementation",
                Callback = delegate { host.GoToImplementation(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Go To Base Type",
                Callback = delegate { host.GoToBaseType(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Go To Derived Types",
                Callback = delegate { host.GoToDerivedType(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Find All References",
                Callback = delegate { host.ShowFindAllReferences(captured); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Rename Symbol…",
                Callback = delegate { PromptRenameSymbol(captured, null); }
            });
            e.Items.Add(new HelixPythonNamespace.Editor.EditorContextMenuItem
            {
                Label = "Rename Containing Package…",
                Callback = delegate { PromptRenamePackageForSymbol(captured.SourceFilePath); }
            });
        }

        // Prompt for a new name for either a type or a function (exactly one is non-null),
        // then hand the actual rename off to the host with the user-chosen scope.
        private void PromptRenameSymbol(TypeModel type, FunctionModel fn)
        {
            if (type == null && fn == null) return;
            string oldName = type != null ? type.Name : fn.Name;
            string kind = type != null ? "Type" : "Function";

            using (var dlg = new HelixPythonNamespace.App.Dialogs.RenameSymbolDialog(kind, oldName))
            {
                // Cancelled, blank, or unchanged name — nothing to do.
                if (dlg.ShowDialog(host) != DialogResult.OK) return;
                string newName = dlg.NewName;
                if (string.IsNullOrEmpty(newName) || newName == oldName) return;
                host.ExecuteScopedRename(type, fn, newName, dlg.SelectedScope);
            }
        }

        private void PromptRenameVariable(HelixPythonNamespace.Workspace.Python.VariableSymbol variable)
        {
            if (variable == null) return;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.RenameSymbolDialog("Variable", variable.Name))
            {
                if (dlg.ShowDialog(host) != DialogResult.OK) return;
                string newName = dlg.NewName;
                if (string.IsNullOrEmpty(newName) || newName == variable.Name) return;
                host.ExecuteScopedVariableRename(variable, newName, dlg.SelectedScope);
            }
        }

        private void GoToVariable(HelixPythonNamespace.Workspace.Python.VariableSymbol variable)
        {
            if (variable == null || string.IsNullOrEmpty(variable.SourceFilePath)) return;
            host.OpenInEditor(variable.SourceFilePath, variable.SourceLineNumber);
        }

        // Rename the package (directory) that declares a symbol. Rather than duplicate the
        // tree's package-rename flow, we reuse it by synthesizing the event it expects.
        private void PromptRenamePackageForSymbol(string declaringFilePath)
        {
            if (string.IsNullOrEmpty(declaringFilePath)) return;
            string packageDir = Path.GetDirectoryName(Path.GetFullPath(declaringFilePath));
            // Reuse the same flow as the tree menu by synthesizing a FileGroup-like tag.
            // The tree handler only needs a directory — we already have one.
            host.OnTreeRenamePackage(host, new HelixPythonNamespace.UI.Controls.TreeOperationEventArgs(
                null, new HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup
                {
                    Project = null, FilePath = declaringFilePath
                }));
        }

        // Open the editor at the function's declaration line.
        public void GoToFunction(FunctionModel fn)
        {
            if (fn == null || string.IsNullOrEmpty(fn.SourceFilePath)) return;
            host.OpenInEditor(fn.SourceFilePath, fn.SourceLineNumber);
        }

        // Tree "Go To Designer" context action: the tagged item is a TypeModel.
        internal void OnTreeGoToDesigner(object sender, HelixPythonNamespace.UI.Controls.TreeOperationEventArgs e)
        {
            TypeModel type = e?.Item as TypeModel;
            if (type != null) GoToDesigner(type);
        }
    }
}
