using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    public enum RenameScope
    {
        Solution,
        Project,
        Package,
        File,
    }

    public struct RenameResult
    {
        public int FilesChanged;
        public int TotalHits;
    }

    public struct FileTextEdit
    {
        public string Path;
        public string OldText;
        public string NewText;
        public FileTextEdit(string path, string oldText, string newText)
        { Path = path; OldText = oldText; NewText = newText; }
    }

    /// <summary>One resolved reference to a symbol: file, 1-based line/column, and the trimmed source
    /// line for preview in the Find All References results list.</summary>
    public struct ReferenceHit
    {
        public string Path;
        public int Line;      // 1-based
        public int Column;    // 1-based
        public string Preview;
    }

    /// <summary>One incoming call in a Call Hierarchy: the calling function/method and the call site.</summary>
    public struct CallerHit
    {
        public string CallerLabel;   // enclosing function/method, e.g. "Class.method" or "<module>"
        public string Path;
        public int Line;             // 1-based
        public int Column;           // 1-based
        public string Preview;
    }

    public struct FileMove
    {
        public string OldPath;
        public string NewPath;
        public FileMove(string oldPath, string newPath) { OldPath = oldPath; NewPath = newPath; }
    }

    /// <summary>
    /// Scoped rename of a Python class or top-level function. Uses the AST-aware
    /// <see cref="AstIdentifierRewriter"/> so strings, comments, and shadowed locals
    /// are preserved. Scope controls which files are touched.
    ///
    /// The "Compute" methods return the set of edits without writing — the caller
    /// is responsible for wrapping them in an undoable command and writing.
    /// The legacy in-place <c>RenameType</c>/<c>RenameFunction</c> methods remain
    /// for callers that don't need undo.
    /// </summary>
    public sealed class SymbolRenameService
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---- Compute-only API ------------------------------------------------

        public struct ComputedRename
        {
            public List<FileTextEdit> Edits;
            public FileMove? DeclaringFileMove; // non-null when the declaring file stem matched and will be renamed.
            public int TotalHits;

            /// <summary>
            /// Set only by <see cref="ComputeRenameMember"/> when the rename could not proceed
            /// (unparsable source, declaring class not found, member not found) — distinct from a
            /// legitimate "nothing to do" no-op (empty Edits with this left null), so the caller can
            /// tell the user why instead of the rename silently doing nothing. Unused by
            /// ComputeRenameType/ComputeRenameFunction, which have no comparable failure mode.
            /// </summary>
            public string ErrorMessage;
        }

        public ComputedRename ComputeRenameType(SolutionModel solution, TypeModel type, string newName, RenameScope scope)
        {
            if (solution == null || type == null || string.IsNullOrWhiteSpace(newName) || newName == type.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            return Compute(
                ScopeFiles(solution, type.Project, type.Namespace, type.SourceFilePath, scope),
                type.Name, newName, type.SourceFilePath);
        }

        public ComputedRename ComputeRenameFunction(SolutionModel solution, FunctionModel fn, string newName, RenameScope scope)
        {
            if (solution == null || fn == null || string.IsNullOrWhiteSpace(newName) || newName == fn.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            return Compute(
                ScopeFiles(solution, fn.Project, fn.Namespace, fn.SourceFilePath, scope),
                fn.Name, newName, fn.SourceFilePath);
        }

        public ComputedRename ComputeRenameVariable(SolutionModel solution, VariableSymbol variable, string newName, RenameScope scope)
        {
            if (solution == null || variable == null || string.IsNullOrWhiteSpace(newName) || newName == variable.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            if (variable.OwnerType != null && variable.Member != null)
                return ComputeRenameMember(solution, variable.OwnerType, variable.Member, newName);

            var edits = new List<FileTextEdit>();
            int totalHits = 0;
            var resolver = new SymbolResolver(solution);
            foreach (string file in ScopeFiles(solution, variable.Project, variable.Namespace,
                variable.SourceFilePath, scope))
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch { continue; }
                string currentModule = resolver.DeriveModuleNamespace(file);
                string currentPackage = currentModule;
                if (!string.Equals(Path.GetFileName(file), "__init__.py", StringComparison.OrdinalIgnoreCase))
                {
                    int dot = currentPackage.LastIndexOf('.');
                    currentPackage = dot < 0 ? string.Empty : currentPackage.Substring(0, dot);
                }
                bool declaring = SamePath(file, variable.SourceFilePath);
                AstIdentifierRewriter.RewriteResult rewritten = PythonModuleSymbolRewriter.Rewrite(
                    source, currentPackage, variable.Namespace, variable.Name, newName, declaring);
                if (rewritten.Count == 0 || rewritten.Text == source) continue;
                edits.Add(new FileTextEdit(file, source, rewritten.Text));
                totalHits += rewritten.Count;
            }
            return new ComputedRename { Edits = edits, TotalHits = totalHits };
        }

        /// <summary>
        /// Renames a class member (method/property/constructor/field) — deliberately scoped to the
        /// OWNING CLASS BODY ONLY via <see cref="PythonMemberRewriter"/>, not solution/file-wide like
        /// the type/function renamers above: a member name collides far more often than a class or
        /// function name (many unrelated classes can share a method/field name), so there is no
        /// wider-scope option here the way <see cref="RenameScope"/> offers for types/functions.
        /// </summary>
        public ComputedRename ComputeRenameMember(SolutionModel solution, TypeModel owner, MemberModel member, string newName)
        {
            if (solution == null || owner == null || member == null ||
                string.IsNullOrWhiteSpace(newName) || newName == member.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            if (string.IsNullOrEmpty(owner.SourceFilePath) || !File.Exists(owner.SourceFilePath))
                return new ComputedRename { Edits = new List<FileTextEdit>(), ErrorMessage = "could not find the declaring source file" };

            string source;
            try { source = File.ReadAllText(owner.SourceFilePath); }
            catch (Exception ex)
            {
                return new ComputedRename { Edits = new List<FileTextEdit>(), ErrorMessage = ex.Message };
            }

            PythonMemberRewriter.RewriteResult result = PythonMemberRewriter.Rename(source, owner.Name, member.Name, newName);
            if (!result.Success)
                return new ComputedRename { Edits = new List<FileTextEdit>(), ErrorMessage = result.ErrorMessage };
            if (result.Count == 0)
            {
                return new ComputedRename
                {
                    Edits = new List<FileTextEdit>(),
                    ErrorMessage = "'" + member.Name + "' was not found in '" + owner.Name + "'"
                };
            }

            var edits = new List<FileTextEdit> { new FileTextEdit(owner.SourceFilePath, source, result.NewSource) };
            return new ComputedRename { Edits = edits, TotalHits = result.Count };
        }

        // ---- Legacy in-place API (no undo) -----------------------------------

        public RenameResult RenameType(SolutionModel solution, TypeModel type, string newName, RenameScope scope)
        {
            var c = ComputeRenameType(solution, type, newName, scope);
            RenameResult r = ApplyAndCount(c);
            if (c.DeclaringFileMove.HasValue)
            {
                try
                {
                    var mv = c.DeclaringFileMove.Value;
                    if (!File.Exists(mv.NewPath)) File.Move(mv.OldPath, mv.NewPath);
                    if (type != null) type.SourceFilePath = mv.NewPath;
                }
                catch { }
            }
            if (type != null) type.Name = newName;
            return r;
        }

        public RenameResult RenameFunction(SolutionModel solution, FunctionModel fn, string newName, RenameScope scope)
        {
            var c = ComputeRenameFunction(solution, fn, newName, scope);
            RenameResult r = ApplyAndCount(c);
            if (fn != null) fn.Name = newName;
            return r;
        }

        // ---- Find All References ---------------------------------------------

        /// <summary>All references to a class/type across the given scope, resolved with the same
        /// scope-aware binding logic the rename uses (so the results list matches exactly what a rename
        /// would touch).</summary>
        public List<ReferenceHit> FindReferencesType(SolutionModel solution, TypeModel type, RenameScope scope)
        {
            if (solution == null || type == null) return new List<ReferenceHit>();
            return FindReferences(
                ScopeFiles(solution, type.Project, type.Namespace, type.SourceFilePath, scope), type.Name);
        }

        /// <summary>All references to a top-level function across the given scope.</summary>
        public List<ReferenceHit> FindReferencesFunction(SolutionModel solution, FunctionModel fn, RenameScope scope)
        {
            if (solution == null || fn == null) return new List<ReferenceHit>();
            return FindReferences(
                ScopeFiles(solution, fn.Project, fn.Namespace, fn.SourceFilePath, scope), fn.Name);
        }

        /// <summary>
        /// Incoming calls (Call Hierarchy): every reference to <paramref name="fn"/> that is NOT its own
        /// declaration, annotated with the enclosing function/method (the caller). Reuses the same
        /// reference-finding as Find All References, then resolves each site's containing function.
        /// </summary>
        public List<CallerHit> FindCallers(SolutionModel solution, FunctionModel fn, RenameScope scope)
        {
            var result = new List<CallerHit>();
            if (solution == null || fn == null) return result;
            List<ReferenceHit> refs = FindReferencesFunction(solution, fn, scope);
            var seen = new HashSet<string>();
            foreach (ReferenceHit h in refs)
            {
                // Skip the declaration site itself (the `def fn` line in the declaring file).
                if (SamePath(h.Path, fn.SourceFilePath) && h.Line == fn.SourceLineNumber) continue;
                string key = h.Path + "|" + h.Line + "|" + h.Column;
                if (!seen.Add(key)) continue;
                string caller = CallHierarchyService.FindEnclosingFunctionInFile(h.Path, h.Line);
                result.Add(new CallerHit
                {
                    CallerLabel = string.IsNullOrEmpty(caller) ? "<module>" : caller,
                    Path = h.Path,
                    Line = h.Line,
                    Column = h.Column,
                    Preview = h.Preview,
                });
            }
            return result;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static List<ReferenceHit> FindReferences(IEnumerable<string> files, string name)
        {
            if (string.IsNullOrEmpty(name)) return new List<ReferenceHit>();

            List<string> paths = new List<string>(files);

            // Each file is scanned independently, so the work parallelises cleanly. Parsing is the
            // cost (see ScanFileForReferences) and a solution the size of SymPy has ~1600 files, so
            // this is the difference between a stall and a wait on any modern machine.
            //
            // Results are gathered per-file and then flattened IN THE ORIGINAL FILE ORDER, so the
            // list the user sees is identical run to run — parallelism must not make the output
            // depend on thread scheduling.
            List<ReferenceHit>[] perFile = new List<ReferenceHit>[paths.Count];

            System.Threading.Tasks.Parallel.For(0, paths.Count, i =>
            {
                perFile[i] = ScanFileForReferences(paths[i], name);
            });

            List<ReferenceHit> hits = new List<ReferenceHit>();
            for (int i = 0; i < perFile.Length; i++)
                if (perFile[i] != null) hits.AddRange(perFile[i]);

            return hits;
        }

        /// <summary>Scans one file. Pure and side-effect free, so it is safe to run concurrently.</summary>
        private static List<ReferenceHit> ScanFileForReferences(string path, string name)
        {
            List<ReferenceHit> hits = new List<ReferenceHit>();

            string source;
            try { source = File.ReadAllText(path); }
            catch { return hits; }

            // Cheap textual pre-filter. FindOccurrences PARSES AND TOKENIZES the whole file, and
            // on a real repository most files never mention the symbol at all: searching SymPy's
            // 1599 modules for "Poly" spent ~33 s parsing files that could not contain a hit.
            // An identifier occurrence requires the name to appear as a substring, so skipping
            // files without it cannot change the result — only how long it takes to get there.
            if (source.IndexOf(name, StringComparison.Ordinal) < 0) return hits;

            foreach (var span in AstIdentifierRewriter.FindOccurrences(source, name))
            {
                int line, col;
                OffsetToLineColumn(source, span.start, out line, out col);
                hits.Add(new ReferenceHit
                {
                    Path = path,
                    Line = line,
                    Column = col,
                    Preview = LineTextAt(source, span.start),
                });
            }
            return hits;
        }

        // Map a 0-based character offset to a 1-based (line, column).
        private static void OffsetToLineColumn(string source, int offset, out int line, out int column)
        {
            line = 1;
            int lineStart = 0;
            int n = Math.Min(offset, source.Length);
            for (int i = 0; i < n; i++)
            {
                if (source[i] == '\n') { line++; lineStart = i + 1; }
            }
            column = (n - lineStart) + 1;
        }

        // The full source line containing the given offset, trimmed for preview.
        private static string LineTextAt(string source, int offset)
        {
            int start = offset;
            if (start > source.Length) start = source.Length;
            int a = start;
            while (a > 0 && source[a - 1] != '\n') a--;
            int b = start;
            while (b < source.Length && source[b] != '\n') b++;
            return source.Substring(a, b - a).Trim();
        }

        // ---- Internals -------------------------------------------------------

        private ComputedRename Compute(
            IEnumerable<string> files, string oldName, string newName, string declaringFile)
        {
            List<FileTextEdit> edits = new List<FileTextEdit>();
            int totalHits = 0;

            foreach (string f in files)
            {
                string source;
                try { source = File.ReadAllText(f); }
                catch { continue; }

                // Same pre-filter as FindReferences: a rename cannot touch a file that never
                // mentions the old name, so do not pay to parse it.
                if (source.IndexOf(oldName, StringComparison.Ordinal) < 0) continue;

                var r = AstIdentifierRewriter.Rewrite(source, oldName, newName);
                if (r.Count == 0 || r.Text == source) continue;

                edits.Add(new FileTextEdit(f, source, r.Text));
                totalHits += r.Count;
            }

            FileMove? move = null;
            if (!string.IsNullOrEmpty(declaringFile))
            {
                string dir = Path.GetDirectoryName(declaringFile);
                string stem = Path.GetFileNameWithoutExtension(declaringFile);
                if (string.Equals(stem, oldName, StringComparison.Ordinal))
                {
                    string renamed = Path.Combine(dir, newName + ".py");
                    if (!File.Exists(renamed)) move = new FileMove(declaringFile, renamed);
                }
            }

            return new ComputedRename { Edits = edits, DeclaringFileMove = move, TotalHits = totalHits };
        }

        private static RenameResult ApplyAndCount(ComputedRename c)
        {
            RenameResult r = default;
            foreach (FileTextEdit e in c.Edits)
            {
                try { File.WriteAllText(e.Path, e.NewText, Utf8NoBom); r.FilesChanged++; }
                catch { }
            }
            r.TotalHits = c.TotalHits;
            return r;
        }

        private static IEnumerable<string> ScopeFiles(
            SolutionModel solution, ProjectModel owningProject,
            string symbolNamespace, string declaringFile, RenameScope scope)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (scope == RenameScope.File)
            {
                if (!string.IsNullOrEmpty(declaringFile) && File.Exists(declaringFile))
                {
                    string full = Path.GetFullPath(declaringFile);
                    if (seen.Add(full)) yield return full;
                }
                yield break;
            }

            if (scope == RenameScope.Package)
            {
                string pkgRoot = DerivePackageRoot(owningProject, symbolNamespace);
                if (pkgRoot != null && Directory.Exists(pkgRoot))
                {
                    foreach (string f in EnumeratePy(pkgRoot))
                    {
                        string full = Path.GetFullPath(f);
                        if (seen.Add(full)) yield return full;
                    }
                }
                else if (!string.IsNullOrEmpty(declaringFile) && File.Exists(declaringFile))
                {
                    string full = Path.GetFullPath(declaringFile);
                    if (seen.Add(full)) yield return full;
                }
                yield break;
            }

            if (scope == RenameScope.Project)
            {
                if (owningProject != null && !string.IsNullOrEmpty(owningProject.ProjectDirectory)
                    && Directory.Exists(owningProject.ProjectDirectory))
                {
                    foreach (string f in EnumeratePy(owningProject.ProjectDirectory))
                    {
                        string full = Path.GetFullPath(f);
                        if (seen.Add(full)) yield return full;
                    }
                }
                yield break;
            }

            // Solution
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                string dir = p.ProjectDirectory;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string f in EnumeratePy(dir))
                {
                    string full = Path.GetFullPath(f);
                    if (seen.Add(full)) yield return full;
                }
            }
        }

        private static IEnumerable<string> EnumeratePy(string dir)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (string f in files) yield return f;
        }

        private static string DerivePackageRoot(ProjectModel project, string symbolNamespace)
        {
            if (project == null || string.IsNullOrEmpty(project.ProjectDirectory)) return null;
            if (string.IsNullOrEmpty(symbolNamespace)) return null;

            int dot = symbolNamespace.IndexOf('.');
            string firstSegment = dot < 0 ? symbolNamespace : symbolNamespace.Substring(0, dot);
            if (string.IsNullOrEmpty(firstSegment)) return null;

            string candidate = Path.Combine(project.ProjectDirectory, firstSegment);
            if (Directory.Exists(candidate)) return candidate;
            string withSrc = Path.Combine(project.ProjectDirectory, "src", firstSegment);
            if (Directory.Exists(withSrc)) return withSrc;
            return null;
        }
    }
}
