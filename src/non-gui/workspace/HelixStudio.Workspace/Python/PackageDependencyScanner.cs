using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.VirtualCompiler.Imports;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Works out what a package actually depends on, by reading its own <c>import</c> statements.
    ///
    /// Deliberately NOT the dependency list in pyproject.toml: that says what the package declares,
    /// which is a different (and frequently wrong) answer to "what does this code use". The
    /// Solution Explorer shows what the source really pulls in, split three ways — CPython's own
    /// library, third-party packages, and the other members of this solution — because those three
    /// have completely different consequences when something goes missing.
    ///
    /// Accumulates across the files of one package: call <see cref="AddImportsFrom"/> once per
    /// parsed module (the loader already has the AST), then <see cref="Build"/> once at the end.
    /// </summary>
    public sealed class PackageDependencyScanner
    {
        // module name -> the names imported from it, in first-seen order with duplicates dropped.
        private readonly Dictionary<string, List<string>> _imports =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _seenNames =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        private readonly string _ownPackageName;

        /// <param name="ownPackageName">
        /// The package being scanned. Its own modules are not dependencies of itself, so
        /// <c>from lib_utils.geometry import Point</c> inside lib_utils is skipped.
        /// </param>
        public PackageDependencyScanner(string ownPackageName)
        {
            _ownPackageName = ownPackageName ?? string.Empty;
        }

        /// <summary>Record every import statement in one parsed module.</summary>
        public void AddImportsFrom(ModuleSyntax module)
        {
            if (module == null) return;
            foreach (SyntaxNode node in Walk(module))
            {
                ImportStatement imp = node as ImportStatement;
                if (imp != null && imp.Names != null)
                {
                    foreach (var n in imp.Names)
                    {
                        if (n.Name == null || n.Name.Parts == null || n.Name.Parts.Count == 0) continue;
                        Record(string.Join(".", n.Name.Parts), null);
                    }
                    continue;
                }

                FromImportStatement fi = node as FromImportStatement;
                if (fi != null && fi.Module != null && fi.Module.Parts != null && fi.Module.Parts.Count > 0)
                {
                    // `from . import x` / `from .sibling import y` are intra-package and carry no
                    // module name worth listing — a leading dot means "inside me".
                    if (fi.RelativeDots > 0) continue;
                    string moduleName = string.Join(".", fi.Module.Parts);
                    if (fi.IsStar) { Record(moduleName, "*"); continue; }
                    if (fi.Names == null) { Record(moduleName, null); continue; }
                    foreach (var kv in fi.Names) Record(moduleName, kv.Key);
                }
            }
        }

        private void Record(string moduleName, string importedName)
        {
            if (string.IsNullOrEmpty(moduleName)) return;
            // A package importing its own submodules is not depending on anything external.
            if (!string.IsNullOrEmpty(_ownPackageName)
                && string.Equals(KnownExternalModules.TopLevelOf(moduleName), _ownPackageName, StringComparison.Ordinal))
                return;

            List<string> names;
            if (!_imports.TryGetValue(moduleName, out names))
            {
                names = new List<string>();
                _imports[moduleName] = names;
                _seenNames[moduleName] = new HashSet<string>(StringComparer.Ordinal);
            }
            if (string.IsNullOrEmpty(importedName)) return;
            if (_seenNames[moduleName].Add(importedName)) names.Add(importedName);
        }

        /// <summary>
        /// The classified dependency list, ordered by origin (standard library, third-party,
        /// solution packages) and then by module name.
        /// </summary>
        /// <param name="solutionPackageNames">
        /// Top-level import names of the solution's other workspace members. Anything matching one
        /// of these is a solution dependency, whatever else it might look like — a local package
        /// that shadows a PyPI name is still the local one at run time.
        /// </param>
        public List<ModuleDependency> Build(IEnumerable<string> solutionPackageNames)
        {
            var local = new HashSet<string>(StringComparer.Ordinal);
            if (solutionPackageNames != null)
                foreach (string n in solutionPackageNames)
                    if (!string.IsNullOrEmpty(n)) local.Add(n);

            var result = new List<ModuleDependency>();
            foreach (var kv in _imports)
            {
                var dep = new ModuleDependency(kv.Key, Classify(kv.Key, local));
                List<string> names = new List<string>(kv.Value);
                names.Sort(StringComparer.Ordinal);
                dep.ImportedNames.AddRange(names);
                result.Add(dep);
            }

            result.Sort(delegate (ModuleDependency a, ModuleDependency b)
            {
                if (a.Origin != b.Origin) return ((int)a.Origin).CompareTo((int)b.Origin);
                return string.Compare(a.ModuleName, b.ModuleName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// Where a module comes from. Solution members win over everything (a local package that
        /// shadows an installed one IS what gets imported), then CPython's own library, and
        /// everything left over is third-party — including packages no list has heard of, which is
        /// why the fallback has to be ThirdParty rather than "unknown".
        /// </summary>
        public static DependencyOrigin Classify(string moduleName, HashSet<string> solutionPackages)
        {
            string top = KnownExternalModules.TopLevelOf(moduleName);
            if (solutionPackages != null && solutionPackages.Contains(top)) return DependencyOrigin.SolutionPackage;
            if (KnownExternalModules.StandardLibrary.Contains(top)) return DependencyOrigin.StandardLibrary;
            return DependencyOrigin.ThirdParty;
        }

        private static IEnumerable<SyntaxNode> Walk(SyntaxNode root)
        {
            if (root == null) yield break;
            var stack = new Stack<SyntaxNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                SyntaxNode n = stack.Pop();
                yield return n;
                if (n.Children != null)
                    foreach (SyntaxNode c in n.Children) if (c != null) stack.Push(c);
            }
        }
    }

    /// <summary>
    /// The non-source files that ship inside a package — data, assets, templates. The Solution
    /// Explorer lists only Python sources, so without this they are invisible in the IDE even
    /// though they are part of what gets installed.
    /// </summary>
    public static class PackageResourceScanner
    {
        // Not resources: the manifest (the project row itself opens that), lock files, caches,
        // build output, and compiled artifacts.
        private static readonly string[] SkippedNames =
        {
            "pyproject.toml", "uv.lock", "poetry.lock", "Pipfile.lock", ".gitignore",
        };

        private static readonly string[] SkippedExtensions = { ".py", ".pyc", ".pyo", ".pyd" };

        private static readonly string[] SkippedFolders =
        {
            "__pycache__", ".venv", "venv", "env", ".git", ".pytest_cache", ".mypy_cache",
            ".tox", ".nox", "build", "dist", ".eggs", "node_modules", ".idea", ".vs",
            // IDE-private state is neither package content nor a Solution Explorer resource.
            // The parity disk scanner excludes these same folders; including one only on the
            // model side manufactures a Phantom for every repair journal the IDE writes.
            ".helixstudio", ".vscode",
        };

        /// <summary>How many resource files to list before giving up — a data directory with ten
        /// thousand samples in it must not turn the Solution Explorer into a wall of rows.</summary>
        public const int MaxResources = 200;

        /// <summary>
        /// Package-relative paths of the package's non-source files, sorted, capped at
        /// <see cref="MaxResources"/>. Empty when there are none — the caller then shows no node
        /// at all rather than an empty folder.
        /// </summary>
        public static List<string> Scan(string packageDirectory)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(packageDirectory) || !Directory.Exists(packageDirectory)) return found;

            string root;
            try { root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return found; }

            Collect(root, root, found, 0);
            found.Sort(StringComparer.OrdinalIgnoreCase);
            if (found.Count > MaxResources) found.RemoveRange(MaxResources, found.Count - MaxResources);
            return found;
        }

        private static void Collect(string dir, string root, List<string> into, int depth)
        {
            if (depth > 6 || into.Count > MaxResources) return;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }
            foreach (string f in files)
            {
                if (into.Count > MaxResources) return;
                if (IsSkippedFile(f)) continue;
                into.Add(Relative(root, f));
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string sub in subdirs)
            {
                if (IsSkippedFolder(Path.GetFileName(sub))) continue;
                Collect(sub, root, into, depth + 1);
            }
        }

        private static bool IsSkippedFile(string path)
        {
            string name = Path.GetFileName(path);
            foreach (string s in SkippedNames)
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
            string ext = Path.GetExtension(path);
            foreach (string s in SkippedExtensions)
                if (string.Equals(ext, s, StringComparison.OrdinalIgnoreCase)) return true;
            return name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSkippedFolder(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (string s in SkippedFolders)
                if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return true;
            return name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase);
        }

        private static string Relative(string root, string full)
        {
            string prefix = root + Path.DirectorySeparatorChar;
            string rel = full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length)
                : Path.GetFileName(full);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
