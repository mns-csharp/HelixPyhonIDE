using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Which files a C++ rename is allowed to touch — the counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Services.RenameScope"/>, with three members instead of four.
    ///
    /// Python's <c>Package</c> scope has no honest C++ equivalent: it exists because a Python
    /// package IS a directory whose name is the first segment of the symbol's dotted namespace, so
    /// "the package this symbol lives in" is a directory that can be derived from the symbol alone.
    /// C++ namespaces have no such convention — <c>namespace lib_core</c> may be declared across a
    /// dozen files in unrelated folders, and a folder may hold several namespaces — so a
    /// namespace-derived directory would be a guess, not a scope. A CMake target's source list is
    /// the real intermediate grouping in this codebase, and that is exactly what
    /// <see cref="CppRenameScope.Project"/> already means here (a CMake-backed
    /// <see cref="ProjectModel"/> is one target). Adding a fourth member that silently aliased
    /// Project would be worse than not offering it.
    ///
    /// Named <c>CppRenameScope</c> rather than reusing the name <c>RenameScope</c> so a UI file that
    /// imports both the Python and the C++ rename namespaces is never ambiguous.
    /// </summary>
    public enum CppRenameScope
    {
        Solution,
        Project,
        File,
    }

    /// <summary>
    /// Scoped rename of a C++/CUDA class, struct, free function or class member. Rides on
    /// <see cref="CppIdentifierRewriter"/>, so strings, comments, preprocessor lines, member-access
    /// names and locally shadowed names are all preserved; scope controls which files are candidates.
    /// The C++ counterpart to <see cref="SymbolRenameService"/> and deliberately the same shape:
    /// <c>Compute*</c> methods return the edits without writing (so a caller can wrap them in an
    /// undoable command), and the <c>Rename*</c> methods apply them in place.
    ///
    /// It reuses <see cref="FileTextEdit"/>, <see cref="FileMove"/>, <see cref="RenameResult"/> and
    /// <see cref="ReferenceHit"/> from the Python-side service rather than cloning them, so a single
    /// UI command surface can drive either language.
    /// </summary>
    public sealed class CppSymbolRenameService
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Native source extensions considered part of a C++ project. <c>.cu</c>/<c>.cuh</c> are
        /// included because this front end is the CUDA one and CMake targets in this workspace mix
        /// CUDA and host translation units in a single target.
        /// </summary>
        private static readonly string[] NativeExtensions =
        {
            ".cpp", ".cxx", ".cc", ".c", ".hpp", ".hh", ".hxx", ".h", ".cu", ".cuh", ".inl",
        };

        public struct ComputedRename
        {
            public List<FileTextEdit> Edits;

            /// <summary>Non-null when the declaring file's stem matched the old symbol name and the
            /// file itself will be renamed too.</summary>
            public FileMove? DeclaringFileMove;

            public int TotalHits;

            /// <summary>Set only when a rename could not proceed (missing declaring file, unreadable
            /// source, member not found) — distinct from a legitimate "nothing to do" no-op, which
            /// leaves this null with empty Edits. Mirrors
            /// <see cref="SymbolRenameService.ComputedRename.ErrorMessage"/>.</summary>
            public string ErrorMessage;
        }

        // ---- Compute-only API ------------------------------------------------

        /// <summary>
        /// Rename a class/struct/union/enum. Constructors and destructors of the renamed class are
        /// rewritten along with it — see <see cref="CppIdentifierRewriter"/>; C++ requires their
        /// spelling to match the class exactly, so this is a correctness requirement, not a nicety.
        /// </summary>
        public ComputedRename ComputeRenameType(SolutionModel solution, TypeModel type, string newName, CppRenameScope scope)
        {
            if (solution == null || type == null || string.IsNullOrWhiteSpace(newName) || newName == type.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            return Compute(
                ScopeFiles(solution, type.Project, type.SourceFilePath, scope),
                type.Name, newName, type.SourceFilePath);
        }

        /// <summary>Rename a namespace-scope (free) function.</summary>
        public ComputedRename ComputeRenameFunction(SolutionModel solution, FunctionModel fn, string newName, CppRenameScope scope)
        {
            if (solution == null || fn == null || string.IsNullOrWhiteSpace(newName) || newName == fn.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            return Compute(
                ScopeFiles(solution, fn.Project, fn.SourceFilePath, scope),
                fn.Name, newName, fn.SourceFilePath);
        }

        /// <summary>
        /// Rename a class member (method / field / constructor). Deliberately offers no
        /// <see cref="CppRenameScope"/> parameter, exactly as
        /// <see cref="SymbolRenameService.ComputeRenameMember"/> does not: member names collide
        /// across unrelated classes far more often than type names, so the rewriter only ever
        /// touches the owning class's body and <c>Owner::member</c> qualified names.
        ///
        /// Unlike the Python member rename this does look at more than one file, because it has to:
        /// this codebase's C++ convention (see <c>tests/fixtures/cpp/CppTestSample</c>) declares
        /// a class in <c>include/&lt;lib&gt;/x.hpp</c> and defines its members out-of-line in
        /// <c>src/x.cpp</c> as <c>std::string Parrot::speak() const { ... }</c>. Renaming only the
        /// header would leave the definition orphaned and the target failing to link, so the search
        /// runs over the owning project's native files; in every file other than the header the only
        /// pattern that can match is <c>Owner::member</c>, which is precisely the out-of-line
        /// definition (or a static access). Nothing wider than the owning project is considered.
        /// </summary>
        public ComputedRename ComputeRenameMember(SolutionModel solution, TypeModel owner, MemberModel member, string newName)
        {
            if (solution == null || owner == null || member == null ||
                string.IsNullOrWhiteSpace(newName) || newName == member.Name)
                return new ComputedRename { Edits = new List<FileTextEdit>() };

            if (string.IsNullOrEmpty(owner.SourceFilePath) || !File.Exists(owner.SourceFilePath))
                return new ComputedRename { Edits = new List<FileTextEdit>(), ErrorMessage = "could not find the declaring source file" };

            List<string> files = new List<string>(
                ScopeFiles(solution, owner.Project, owner.SourceFilePath, CppRenameScope.Project));
            if (files.Count == 0) files.Add(Path.GetFullPath(owner.SourceFilePath));

            List<FileTextEdit> edits = new List<FileTextEdit>();
            int totalHits = 0;
            RewriteFiles(files, delegate (string src)
            {
                return CppIdentifierRewriter.RewriteMember(src, owner.Name, member.Name, newName);
            }, member.Name, edits, ref totalHits);

            if (totalHits == 0)
            {
                return new ComputedRename
                {
                    Edits = new List<FileTextEdit>(),
                    ErrorMessage = "'" + member.Name + "' was not found in '" + owner.Name + "'",
                };
            }
            return new ComputedRename { Edits = edits, TotalHits = totalHits };
        }

        // ---- In-place API ----------------------------------------------------

        public RenameResult RenameType(SolutionModel solution, TypeModel type, string newName, CppRenameScope scope)
        {
            ComputedRename c = ComputeRenameType(solution, type, newName, scope);
            RenameResult r = ApplyAndCount(c);
            if (c.DeclaringFileMove.HasValue)
            {
                try
                {
                    FileMove mv = c.DeclaringFileMove.Value;
                    if (!File.Exists(mv.NewPath)) File.Move(mv.OldPath, mv.NewPath);
                    if (type != null) type.SourceFilePath = mv.NewPath;
                }
                catch { }
            }
            if (type != null) type.Name = newName;
            return r;
        }

        public RenameResult RenameFunction(SolutionModel solution, FunctionModel fn, string newName, CppRenameScope scope)
        {
            RenameResult r = ApplyAndCount(ComputeRenameFunction(solution, fn, newName, scope));
            if (fn != null) fn.Name = newName;
            return r;
        }

        public RenameResult RenameMember(SolutionModel solution, TypeModel owner, MemberModel member, string newName)
        {
            RenameResult r = ApplyAndCount(ComputeRenameMember(solution, owner, member, newName));
            if (member != null && r.TotalHits > 0) member.Name = newName;
            return r;
        }

        // ---- Find All References ---------------------------------------------

        /// <summary>All references to a class/struct across the given scope, resolved with the same
        /// logic the rename uses, so the results list matches exactly what a rename would touch.</summary>
        public List<ReferenceHit> FindReferencesType(SolutionModel solution, TypeModel type, CppRenameScope scope)
        {
            if (solution == null || type == null) return new List<ReferenceHit>();
            return FindReferences(ScopeFiles(solution, type.Project, type.SourceFilePath, scope), type.Name);
        }

        /// <summary>All references to a free function across the given scope.</summary>
        public List<ReferenceHit> FindReferencesFunction(SolutionModel solution, FunctionModel fn, CppRenameScope scope)
        {
            if (solution == null || fn == null) return new List<ReferenceHit>();
            return FindReferences(ScopeFiles(solution, fn.Project, fn.SourceFilePath, scope), fn.Name);
        }

        private static List<ReferenceHit> FindReferences(IEnumerable<string> files, string name)
        {
            List<ReferenceHit> hits = new List<ReferenceHit>();
            if (string.IsNullOrEmpty(name)) return hits;

            List<string> paths = new List<string>(files);
            List<ReferenceHit>[] perFile = new List<ReferenceHit>[paths.Count];

            System.Threading.Tasks.Parallel.For(0, paths.Count, i =>
            {
                perFile[i] = ScanFileForReferences(paths[i], name);
            });

            // Flattened in the ORIGINAL file order so the list is identical run to run —
            // parallelism must not make the output depend on thread scheduling.
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

            // Same cheap textual pre-filter the Python service uses: an identifier occurrence
            // requires the name to appear as a substring, so skipping files without it cannot change
            // the result — only how long it takes to lex and parse the ones that could not match.
            if (source.IndexOf(name, StringComparison.Ordinal) < 0) return hits;

            foreach (var span in CppIdentifierRewriter.FindOccurrences(source, name))
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

        private static void OffsetToLineColumn(string source, int offset, out int line, out int column)
        {
            line = 1;
            int lineStart = 0;
            int n = Math.Min(offset, source.Length);
            for (int i = 0; i < n; i++)
                if (source[i] == '\n') { line++; lineStart = i + 1; }
            column = (n - lineStart) + 1;
        }

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
            RewriteFiles(new List<string>(files), delegate (string src)
            {
                return CppIdentifierRewriter.Rewrite(src, oldName, newName);
            }, oldName, edits, ref totalHits);

            // Filename convention only, exactly as the Python service does it: when the declaring
            // file is named after the symbol, the file is renamed with it. The original extension is
            // preserved (.hpp stays .hpp). Note that #include lines naming the old file are NOT
            // fixed up — the same limitation the Python service has with `import old_module`.
            FileMove? move = null;
            if (!string.IsNullOrEmpty(declaringFile))
            {
                string dir = Path.GetDirectoryName(declaringFile);
                string stem = Path.GetFileNameWithoutExtension(declaringFile);
                string ext = Path.GetExtension(declaringFile);
                if (string.Equals(stem, oldName, StringComparison.Ordinal) && !string.IsNullOrEmpty(dir))
                {
                    string renamed = Path.Combine(dir, newName + ext);
                    if (!File.Exists(renamed)) move = new FileMove(declaringFile, renamed);
                }
            }

            return new ComputedRename { Edits = edits, DeclaringFileMove = move, TotalHits = totalHits };
        }

        /// <summary>
        /// Rewrite each candidate file with <paramref name="rewrite"/> and collect the non-empty
        /// edits. Parallelised per file — a C++ translation unit costs a full lex + parse to
        /// evaluate, several times what a Python module costs, and solution-scope renames run over
        /// every native file in every project. Results are gathered per file and flattened IN THE
        /// ORIGINAL FILE ORDER so the edit list (and therefore any undo record built from it) is
        /// identical run to run.
        /// </summary>
        private static void RewriteFiles(
            List<string> files,
            Func<string, CppIdentifierRewriter.RewriteResult> rewrite,
            string oldName,
            List<FileTextEdit> edits,
            ref int totalHits)
        {
            FileTextEdit?[] perFile = new FileTextEdit?[files.Count];
            int[] perFileHits = new int[files.Count];

            System.Threading.Tasks.Parallel.For(0, files.Count, i =>
            {
                string source;
                try { source = File.ReadAllText(files[i]); }
                catch { return; }

                // A rename cannot touch a file that never mentions the old name, so do not pay to
                // lex and parse it.
                if (source.IndexOf(oldName, StringComparison.Ordinal) < 0) return;

                CppIdentifierRewriter.RewriteResult r = rewrite(source);
                if (r.Count == 0 || r.Text == null || r.Text == source) return;

                perFile[i] = new FileTextEdit(files[i], source, r.Text);
                perFileHits[i] = r.Count;
            });

            for (int i = 0; i < perFile.Length; i++)
            {
                if (!perFile[i].HasValue) continue;
                edits.Add(perFile[i].Value);
                totalHits += perFileHits[i];
            }
        }

        /// <summary>
        /// Writes every computed edit. All edits are computed before anything is written, so a file
        /// that fails to rewrite cannot leave the earlier ones half-applied against a stale
        /// computation; an I/O failure on one file still leaves the others written, matching the
        /// Python service (the undoable-command path is the caller's answer to that, not this one).
        /// </summary>
        private static RenameResult ApplyAndCount(ComputedRename c)
        {
            RenameResult r = default(RenameResult);
            if (c.Edits == null) return r;
            foreach (FileTextEdit e in c.Edits)
            {
                try { File.WriteAllText(e.Path, e.NewText, Utf8NoBom); r.FilesChanged++; }
                catch { }
            }
            r.TotalHits = c.TotalHits;
            return r;
        }

        /// <summary>
        /// The candidate files for a rename. For a CMake-backed <see cref="ProjectModel"/> the
        /// <c>FilePath</c> is a synthetic <c>&lt;folder&gt;/&lt;target&gt;.cmake-target</c> marker, so
        /// <see cref="ProjectModel.ProjectDirectory"/> still resolves to the real folder on disk and
        /// needs no special handling here.
        /// </summary>
        private static IEnumerable<string> ScopeFiles(
            SolutionModel solution, ProjectModel owningProject, string declaringFile, CppRenameScope scope)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (scope == CppRenameScope.File)
            {
                if (!string.IsNullOrEmpty(declaringFile) && File.Exists(declaringFile))
                {
                    string full = Path.GetFullPath(declaringFile);
                    if (seen.Add(full)) yield return full;
                }
                yield break;
            }

            if (scope == CppRenameScope.Project)
            {
                if (owningProject != null && !string.IsNullOrEmpty(owningProject.ProjectDirectory) &&
                    Directory.Exists(owningProject.ProjectDirectory))
                {
                    foreach (string f in EnumerateNative(owningProject.ProjectDirectory))
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

            // Solution.
            if (solution != null)
            {
                foreach (ProjectModel p in solution.Projects)
                {
                    if (p == null) continue;
                    string dir = p.ProjectDirectory;
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (string f in EnumerateNative(dir))
                    {
                        string full = Path.GetFullPath(f);
                        if (seen.Add(full)) yield return full;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateNative(string dir)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch { yield break; }

            foreach (string f in files)
            {
                if (!HasNativeExtension(f)) continue;
                if (IsGeneratedBuildArtifact(f)) continue;
                yield return f;
            }
        }

        private static bool HasNativeExtension(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < NativeExtensions.Length; i++)
                if (string.Equals(ext, NativeExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// CMake writes real, compilable C++ into its build tree (compiler-id probes, try-compile
        /// scratch files). Those are not project sources and rewriting them is at best pointless and
        /// at worst a corrupted configure cache, so any path under a <c>CMakeFiles</c> directory or a
        /// <c>cmake-build*</c> directory is skipped. The Python service needs no equivalent because
        /// Python has no build tree.
        /// </summary>
        private static bool IsGeneratedBuildArtifact(string path)
        {
            string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int i = 0; i < parts.Length - 1; i++)   // directory segments only
            {
                string seg = parts[i];
                if (string.Equals(seg, "CMakeFiles", StringComparison.OrdinalIgnoreCase)) return true;
                if (seg.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
