using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HelixPythonNamespace.VirtualCompiler.Ast;
using PyParser = HelixPythonNamespace.Python.Python;
using PyParseResult = HelixPythonNamespace.Python.ParseResult;

namespace HelixPythonNamespace.VirtualCompiler.Imports
{
    /// <summary>How a dotted name resolved against a <see cref="PythonSearchPath"/>.</summary>
    public enum ModuleKind
    {
        /// <summary>No portion of the name was found on the path.</summary>
        NotFound,
        /// <summary>A plain <c>.py</c> (or compiled extension) module.</summary>
        Module,
        /// <summary>A directory with an <c>__init__.py</c>.</summary>
        RegularPackage,
        /// <summary>
        /// A PEP 420 namespace package: one or more directories with no <c>__init__.py</c> and no
        /// same-named module anywhere later on the path. It has NO code and NO attributes — only
        /// submodules. CPython reports its origin as <c>unknown location</c>.
        /// </summary>
        NamespacePackage,
        /// <summary>Stdlib or site-packages: resolvable, but its contents are not visible here.</summary>
        ExternalOpaque,
    }

    /// <summary>The outcome of resolving one dotted module name.</summary>
    public sealed class ResolvedModule
    {
        public string Name { get; }
        public ModuleKind Kind { get; }
        /// <summary>Backing file, or null for namespace packages and external modules.</summary>
        public string Location { get; }
        /// <summary>Directories searched for submodules; empty for non-packages.</summary>
        public IReadOnlyList<string> SearchLocations { get; }
        /// <summary>
        /// For <see cref="ModuleKind.NotFound"/>: the longest dotted prefix that also failed —
        /// CPython names the first missing level, so <c>import a.b.c</c> with no <c>a</c> reports
        /// <c>No module named 'a'</c>, not <c>'a.b.c'</c>.
        /// </summary>
        public string FailedPrefix { get; }

        public ResolvedModule(string name, ModuleKind kind, string location,
            IReadOnlyList<string> searchLocations, string failedPrefix)
        {
            Name = name ?? string.Empty;
            Kind = kind;
            Location = location;
            SearchLocations = searchLocations ?? Array.Empty<string>();
            FailedPrefix = failedPrefix;
        }

        public bool Found { get { return Kind != ModuleKind.NotFound; } }
        public bool IsPackage
        {
            get { return Kind == ModuleKind.RegularPackage || Kind == ModuleKind.NamespacePackage; }
        }

        /// <summary>
        /// The origin string CPython puts in the parenthesis of an <c>ImportError</c>:
        /// the backing file, or the literal <c>unknown location</c> for a namespace package.
        /// </summary>
        public string LocationDisplay
        {
            get { return string.IsNullOrEmpty(Location) ? "unknown location" : Location; }
        }
    }

    /// <summary>The top-level names a module binds, as far as the compiler can tell.</summary>
    public sealed class ModuleExports
    {
        /// <summary>
        /// True when the compiler cannot enumerate the module's names — a star-import, a module-level
        /// <c>__getattr__</c> (PEP 562), an unreadable/unparseable file, or an external module. No
        /// "cannot import name" diagnostic may be reported against an opaque module.
        /// </summary>
        public bool Opaque { get; }
        public HashSet<string> Names { get; }

        public ModuleExports(bool opaque, HashSet<string> names)
        {
            Opaque = opaque;
            Names = names ?? new HashSet<string>(StringComparer.Ordinal);
        }

        public static readonly ModuleExports OpaqueExports =
            new ModuleExports(true, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Port of CPython's <c>importlib._bootstrap_external.PathFinder</c> / <c>FileFinder</c>: given
    /// a <see cref="PythonSearchPath"/>, resolve a dotted module name to the exact file the
    /// interpreter would load — including PEP 420 namespace packages, which is the case the
    /// compiler previously could not represent at all.
    ///
    /// The scan order is the part that matters and is easy to get wrong: for each path entry
    /// CPython prefers a regular package, then a module; a directory with no <c>__init__.py</c> is
    /// only *recorded* as a namespace portion and the scan CONTINUES. A regular package found at a
    /// later entry therefore beats a namespace portion found at an earlier one. Results are cached
    /// per resolver instance, so a whole-solution compile parses each package's <c>__init__.py</c>
    /// at most once.
    /// </summary>
    public sealed class ModuleResolver
    {
        private static readonly string[] SourceSuffixes = { ".py" };
        private static readonly string[] ExtensionSuffixes = { ".pyd", ".so", ".dll" };

        private readonly PythonSearchPath _path;
        private readonly Dictionary<string, ResolvedModule> _moduleCache =
            new Dictionary<string, ResolvedModule>(StringComparer.Ordinal);

        public ModuleResolver(PythonSearchPath path)
        {
            _path = path ?? PythonSearchPath.FromRoots(null);
        }

        public PythonSearchPath SearchPath { get { return _path; } }

        /// <summary>Resolve a dotted module name the way the interpreter would.</summary>
        public ResolvedModule Resolve(string dottedName, CancellationToken cancel = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(dottedName))
                return new ResolvedModule(string.Empty, ModuleKind.NotFound, null, null, string.Empty);

            ResolvedModule cached;
            if (_moduleCache.TryGetValue(dottedName, out cached)) return cached;

            ResolvedModule result = ResolveUncached(dottedName, cancel);
            _moduleCache[dottedName] = result;
            return result;
        }

        /// <summary>
        /// Resolve as <see cref="Resolve"/> does, but searching <paramref name="firstRoot"/> ahead
        /// of the configured path.
        ///
        /// This models the entry that is never in <c>PYTHONPATH</c> and yet is almost always
        /// present: <c>sys.path[0]</c>. CPython sets it to the running script's own directory, and
        /// pytest prepends a test file's directory for the same reason — which is what makes
        /// <c>from test_certify import …</c> work between two test modules that share a folder.
        /// Without this the checker reports every sibling import as a missing module.
        /// </summary>
        public ResolvedModule ResolveFrom(string firstRoot, string dottedName,
            CancellationToken cancel = default(CancellationToken))
        {
            if (!string.IsNullOrEmpty(firstRoot) && !string.IsNullOrEmpty(dottedName))
            {
                ResolvedModule local = ResolveWithin(new[] { firstRoot }, dottedName, cancel);
                // A namespace-only hit must NOT short-circuit: CPython records namespace portions
                // and keeps scanning, so a real package further down the path still wins.
                if (local.Found && local.Kind != ModuleKind.NamespacePackage) return local;
            }
            return Resolve(dottedName, cancel);
        }

        private ResolvedModule ResolveUncached(string dottedName, CancellationToken cancel)
        {
            ResolvedModule found = ResolveWithin(_path.Roots, dottedName, cancel);
            if (found.Found) return found;

            // Only the TOP-level name can be satisfied by the invisible stdlib/site-packages;
            // everything under it inherits that opacity.
            if (KnownExternalModules.ContainsTopLevelOf(dottedName))
                return new ResolvedModule(dottedName, ModuleKind.ExternalOpaque, null, null, null);

            return found;
        }

        private ResolvedModule ResolveWithin(IReadOnlyList<string> roots,
            string dottedName, CancellationToken cancel)
        {
            string[] segments = dottedName.Split('.');
            IReadOnlyList<string> searchDirs = roots;
            ResolvedModule current = null;
            string prefix = null;

            for (int i = 0; i < segments.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                string segment = segments[i];
                prefix = prefix == null ? segment : prefix + "." + segment;

                // A non-package cannot contain submodules; `import json.foo` where json is a module
                // fails at the descent, not at the leaf.
                if (i > 0 && (current == null || !current.IsPackage))
                    return NotFound(dottedName, prefix);

                ResolvedModule found = FindInDirectories(searchDirs, segment, prefix);
                if (found == null) return NotFound(dottedName, prefix);

                current = found;
                searchDirs = found.SearchLocations;
            }

            if (current == null) return NotFound(dottedName, dottedName);
            return new ResolvedModule(dottedName, current.Kind, current.Location,
                current.SearchLocations, null);
        }

        private static ResolvedModule NotFound(string dottedName, string failedPrefix)
        {
            return new ResolvedModule(dottedName, ModuleKind.NotFound, null, null, failedPrefix);
        }

        // One path-entry scan, in CPython's order. Namespace portions accumulate and never
        // short-circuit — see the class remarks.
        private ResolvedModule FindInDirectories(IReadOnlyList<string> dirs, string segment, string fqn)
        {
            if (dirs == null || dirs.Count == 0) return null;
            if (string.IsNullOrEmpty(segment)) return null;

            List<string> namespacePortions = null;

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidateDir = CombineSafe(dir, segment);
                if (candidateDir != null && DirectoryExistsSafe(candidateDir))
                {
                    string init = Path.Combine(candidateDir, "__init__.py");
                    if (FileExistsSafe(init))
                    {
                        return new ResolvedModule(fqn, ModuleKind.RegularPackage, init,
                            new[] { candidateDir }, null);
                    }
                    // No __init__.py: a namespace portion. Record and keep scanning.
                    if (namespacePortions == null) namespacePortions = new List<string>();
                    namespacePortions.Add(candidateDir);
                }

                foreach (string suffix in ExtensionSuffixes)
                {
                    string ext = CombineSafe(dir, segment + suffix);
                    if (ext != null && FileExistsSafe(ext))
                        return new ResolvedModule(fqn, ModuleKind.Module, ext, null, null);
                }
                foreach (string suffix in SourceSuffixes)
                {
                    string src = CombineSafe(dir, segment + suffix);
                    if (src != null && FileExistsSafe(src))
                        return new ResolvedModule(fqn, ModuleKind.Module, src, null, null);
                }
            }

            if (namespacePortions != null)
            {
                return new ResolvedModule(fqn, ModuleKind.NamespacePackage, null,
                    namespacePortions, null);
            }
            return null;
        }

        /// <summary>
        /// The names <paramref name="module"/> binds at module level. A namespace package binds
        /// nothing (its only reachable children are submodules); an external module is opaque.
        /// </summary>
        public ModuleExports ExportsOf(ResolvedModule module, CancellationToken cancel = default(CancellationToken))
        {
            ModuleFacts facts = FactsOf(module, cancel);
            return facts != null ? facts.Exports : ModuleExports.OpaqueExports;
        }

        /// <summary>
        /// True when importing <paramref name="dottedName"/> can rewrite <c>sys.path</c> before the
        /// importer's later imports run — either the module does it itself or something it imports
        /// does, within <paramref name="depth"/> hops.
        ///
        /// This is the escape hatch for the sys.path-bootstrap idiom, which is pervasive in
        /// research code: a <c>_paths.py</c> that inserts several directories, imported for its side
        /// effect by a package <c>__init__</c>, so every module downstream of it imports names that
        /// exist at a location the static path model cannot know. Once a file is downstream of one
        /// of these, the compiler has no ground truth and must stay silent rather than guess.
        /// </summary>
        /// <param name="firstRoot">
        /// The importing file's own directory, searched ahead of the path for the FIRST hop — a
        /// bootstrap module usually sits right next to the script that imports it.
        /// </param>
        public bool ReachesSearchPathMutation(string dottedName, int depth, string firstRoot = null,
            CancellationToken cancel = default(CancellationToken))
        {
            return ReachesSearchPathMutation(dottedName, depth, firstRoot,
                new HashSet<string>(StringComparer.Ordinal), cancel);
        }

        private bool ReachesSearchPathMutation(string dottedName, int depth, string firstRoot,
            HashSet<string> visiting, CancellationToken cancel)
        {
            if (depth < 0 || string.IsNullOrEmpty(dottedName)) return false;
            if (!visiting.Add(dottedName)) return false;

            ResolvedModule m = string.IsNullOrEmpty(firstRoot)
                ? Resolve(dottedName, cancel)
                : ResolveFrom(firstRoot, dottedName, cancel);
            if (!m.Found || m.Kind == ModuleKind.ExternalOpaque) return false;

            ModuleFacts facts = FactsOf(m, cancel);
            if (facts == null) return false;
            if (facts.TouchesSearchPath) return true;
            if (depth == 0) return false;

            foreach (string target in facts.ImportTargets)
            {
                // Relative targets are recorded with their leading dots; re-anchor them on the
                // importing module's package, one level up per extra dot (CPython _resolve_name).
                string absolute = target;
                if (target.Length > 0 && target[0] == '.')
                {
                    int level = 0;
                    while (level < target.Length && target[level] == '.') level++;
                    string basePkg = m.IsPackage ? dottedName : ParentOf(dottedName);
                    for (int up = 1; up < level && basePkg.Length > 0; up++) basePkg = ParentOf(basePkg);
                    string spec = target.Substring(level);
                    if (basePkg.Length == 0) absolute = spec;
                    else absolute = spec.Length == 0 ? basePkg : basePkg + "." + spec;
                    if (absolute.Length == 0) continue;
                }
                // Deeper hops resolve from the package they were written in, not the original
                // importer's directory, so firstRoot does not propagate.
                if (ReachesSearchPathMutation(absolute, depth - 1, null, visiting, cancel)) return true;
            }
            return false;
        }

        private static string ParentOf(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return string.Empty;
            int dot = dotted.LastIndexOf('.');
            return dot < 0 ? string.Empty : dotted.Substring(0, dot);
        }

        /// <summary>Everything the resolver needs to know about one parsed module file.</summary>
        private sealed class ModuleFacts
        {
            public readonly ModuleExports Exports;
            public readonly bool TouchesSearchPath;
            public readonly IReadOnlyList<string> ImportTargets;

            public ModuleFacts(ModuleExports exports, bool touchesSearchPath, IReadOnlyList<string> importTargets)
            {
                Exports = exports;
                TouchesSearchPath = touchesSearchPath;
                ImportTargets = importTargets ?? Array.Empty<string>();
            }

            public static readonly ModuleFacts Unknown =
                new ModuleFacts(ModuleExports.OpaqueExports, false, null);
        }

        private readonly Dictionary<string, ModuleFacts> _factsCache =
            new Dictionary<string, ModuleFacts>(StringComparer.OrdinalIgnoreCase);

        private ModuleFacts FactsOf(ResolvedModule module, CancellationToken cancel)
        {
            if (module == null || !module.Found) return ModuleFacts.Unknown;
            if (module.Kind == ModuleKind.ExternalOpaque) return ModuleFacts.Unknown;
            if (module.Kind == ModuleKind.NamespacePackage)
            {
                // A namespace package has no code: nothing to bind, nothing to mutate.
                return new ModuleFacts(new ModuleExports(false, new HashSet<string>(StringComparer.Ordinal)),
                    false, null);
            }
            if (string.IsNullOrEmpty(module.Location)) return ModuleFacts.Unknown;

            // A compiled extension has no readable source — never judge its names.
            string ext = Path.GetExtension(module.Location);
            if (!string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase)) return ModuleFacts.Unknown;

            ModuleFacts cached;
            if (_factsCache.TryGetValue(module.Location, out cached)) return cached;

            ModuleFacts computed = ComputeFacts(module.Location, cancel);
            _factsCache[module.Location] = computed;
            return computed;
        }

        private static ModuleFacts ComputeFacts(string filePath, CancellationToken cancel)
        {
            string source;
            try { source = File.ReadAllText(filePath); }
            catch { return ModuleFacts.Unknown; }
            return ComputeFacts(source, filePath, cancel);
        }

        /// <summary>Parse a module's source and extract everything the checker needs from it.</summary>
        internal static ModuleFactsView FactsOfSource(string source, string filePath,
            CancellationToken cancel = default(CancellationToken))
        {
            ModuleFacts f = ComputeFacts(source ?? string.Empty, filePath, cancel);
            return new ModuleFactsView(f.TouchesSearchPath, f.ImportTargets);
        }

        /// <summary>Public projection of <see cref="ModuleFacts"/> for the import checker.</summary>
        internal sealed class ModuleFactsView
        {
            public readonly bool TouchesSearchPath;
            public readonly IReadOnlyList<string> ImportTargets;
            public ModuleFactsView(bool touchesSearchPath, IReadOnlyList<string> importTargets)
            {
                TouchesSearchPath = touchesSearchPath;
                ImportTargets = importTargets ?? Array.Empty<string>();
            }
        }

        private static ModuleFacts ComputeFacts(string source, string filePath, CancellationToken cancel)
        {
            bool touchesPath = ReferencesSysPath(source, cancel);

            Module astModule;
            try
            {
                PyParseResult parsed = PyParser.ParseModule(source, filePath, cancel);
                if (parsed == null || parsed.Module == null)
                    return new ModuleFacts(ModuleExports.OpaqueExports, touchesPath, null);
                astModule = CstToAst.Lower(parsed.Module);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new ModuleFacts(ModuleExports.OpaqueExports, touchesPath, null); }

            if (astModule == null)
                return new ModuleFacts(ModuleExports.OpaqueExports, touchesPath, null);

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            bool opaque = false;
            CollectBindings(astModule.Body, names, ref opaque);

            // PEP 562: a module-level __getattr__ can synthesize any attribute on demand.
            if (names.Contains("__getattr__")) opaque = true;

            // Always present on a real module object.
            names.Add("__name__");
            names.Add("__file__");
            names.Add("__doc__");
            names.Add("__package__");
            names.Add("__spec__");
            names.Add("__loader__");
            names.Add("__dict__");
            names.Add("__builtins__");

            List<string> targets = new List<string>();
            CollectImportTargets(astModule.Body, targets);

            return new ModuleFacts(new ModuleExports(opaque, names), touchesPath, targets);
        }

        // Module-level import targets only — that is where a bootstrap's side effect happens.
        // Relative imports are recorded with their leading dots so the caller can re-anchor them.
        private static void CollectImportTargets(IReadOnlyList<Stmt> body, List<string> targets)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case Import imp:
                        foreach (Alias a in imp.Names)
                            if (!string.IsNullOrEmpty(a.Name)) targets.Add(a.Name);
                        break;
                    case ImportFrom imf:
                        // Both the module AND each imported name: `from . import _paths` names the
                        // bootstrap in the ALIAS list, not the module spec, and `from pkg import sub`
                        // executes sub's module body just the same.
                        string prefix = imf.Level > 0
                            ? new string('.', imf.Level) + (imf.Module ?? string.Empty)
                            : (imf.Module ?? string.Empty);
                        if (prefix.Length > 0) targets.Add(prefix);
                        foreach (Alias a in imf.Names)
                        {
                            if (string.IsNullOrEmpty(a.Name) || a.Name == "*") continue;
                            bool needsDot = prefix.Length > 0
                                && prefix[prefix.Length - 1] != '.';
                            targets.Add(prefix + (needsDot ? "." : string.Empty) + a.Name);
                        }
                        break;
                    case If iff:
                        CollectImportTargets(iff.Body, targets);
                        CollectImportTargets(iff.OrElse, targets);
                        break;
                    case Try tr:
                        CollectImportTargets(tr.Body, targets);
                        foreach (ExceptHandler h in tr.Handlers) CollectImportTargets(h.Body, targets);
                        CollectImportTargets(tr.OrElse, targets);
                        CollectImportTargets(tr.FinalBody, targets);
                        break;
                }
            }
        }

        /// <summary>
        /// True when the source so much as mentions <c>sys.path</c>. Detected on the LEXED token
        /// stream, never raw text, so the words inside a docstring or comment cannot trigger it.
        /// Deliberately broad: a file that reads <c>sys.path</c> is almost always about to change
        /// it, and the cost of one missed report is far below the cost of a wrong one.
        /// </summary>
        private static bool ReferencesSysPath(string source, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(source)) return false;
            // Cheap reject first: the token walk is only worth doing if the text could match.
            if (source.IndexOf("path", StringComparison.Ordinal) < 0) return false;

            IReadOnlyList<HelixPythonNamespace.Python.Syntax.Token> tokens;
            try { tokens = new HelixPythonNamespace.Python.Lexing.PythonLexer(source).Tokenize(cancel); }
            catch (OperationCanceledException) { throw; }
            catch { return false; }

            var significant = new List<HelixPythonNamespace.Python.Syntax.Token>();
            foreach (var t in tokens)
            {
                switch (t.Kind)
                {
                    case HelixPythonNamespace.Python.Syntax.TokenKind.Comment:
                    case HelixPythonNamespace.Python.Syntax.TokenKind.Whitespace:
                    case HelixPythonNamespace.Python.Syntax.TokenKind.NewLine:
                    case HelixPythonNamespace.Python.Syntax.TokenKind.Indent:
                    case HelixPythonNamespace.Python.Syntax.TokenKind.Dedent:
                    case HelixPythonNamespace.Python.Syntax.TokenKind.LineContinuation:
                        continue;
                }
                significant.Add(t);
            }

            for (int i = 0; i + 2 < significant.Count; i++)
            {
                var a = significant[i];
                var b = significant[i + 1];
                var c = significant[i + 2];

                // sys . path
                if (a.Kind == HelixPythonNamespace.Python.Syntax.TokenKind.Name && a.Text == "sys"
                    && b.Text == "." && c.Text == "path")
                    return true;

                // from sys import path[, ...]  — an alias that can mutate the list just as well.
                if (a.Kind == HelixPythonNamespace.Python.Syntax.TokenKind.KwFrom
                    && b.Kind == HelixPythonNamespace.Python.Syntax.TokenKind.Name && b.Text == "sys"
                    && c.Kind == HelixPythonNamespace.Python.Syntax.TokenKind.KwImport)
                {
                    for (int j = i + 3; j < significant.Count; j++)
                    {
                        if (significant[j].Text == "path") return true;
                        if (significant[j].Kind != HelixPythonNamespace.Python.Syntax.TokenKind.Name
                            && significant[j].Text != "," && significant[j].Text != "("
                            && significant[j].Text != ")" && significant[j].Kind
                                != HelixPythonNamespace.Python.Syntax.TokenKind.KwAs)
                            break;
                    }
                }
            }
            return false;
        }

        // Walks module-level statements, descending into the control-flow bodies that still execute
        // at module scope (if/try/with/for/while/match). Function and class BODIES are deliberately
        // not entered — their contents are not module attributes.
        private static void CollectBindings(IReadOnlyList<Stmt> body, HashSet<string> names, ref bool opaque)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                if (s == null) continue;
                switch (s)
                {
                    case FunctionDef fn: names.Add(fn.Name); break;
                    case AsyncFunctionDef afn: names.Add(afn.Name); break;
                    case ClassDef cls: names.Add(cls.Name); break;
                    case Assign asg:
                        foreach (Expr t in asg.Targets) AddTargetNames(t, names);
                        break;
                    case AnnAssign ann: AddTargetNames(ann.Target, names); break;
                    case AugAssign aug: AddTargetNames(aug.Target, names); break;
                    case TypeAlias ta: AddTargetNames(ta.Name, names); break;
                    case Import imp:
                        foreach (Alias a in imp.Names)
                        {
                            // `import pkg.sub` binds only the top-level `pkg`.
                            names.Add(!string.IsNullOrEmpty(a.AsName) ? a.AsName : TopDotted(a.Name));
                        }
                        break;
                    case ImportFrom imf:
                        foreach (Alias a in imf.Names)
                        {
                            if (a.Name == "*") { opaque = true; continue; }
                            names.Add(!string.IsNullOrEmpty(a.AsName) ? a.AsName : a.Name);
                        }
                        break;
                    case If iff:
                        CollectBindings(iff.Body, names, ref opaque);
                        CollectBindings(iff.OrElse, names, ref opaque);
                        break;
                    case Try tr:
                        CollectBindings(tr.Body, names, ref opaque);
                        foreach (ExceptHandler h in tr.Handlers)
                        {
                            CollectBindings(h.Body, names, ref opaque);
                        }
                        CollectBindings(tr.OrElse, names, ref opaque);
                        CollectBindings(tr.FinalBody, names, ref opaque);
                        break;
                    case TryStar trs:
                        CollectBindings(trs.Body, names, ref opaque);
                        foreach (ExceptHandler h in trs.Handlers)
                        {
                            CollectBindings(h.Body, names, ref opaque);
                        }
                        CollectBindings(trs.OrElse, names, ref opaque);
                        CollectBindings(trs.FinalBody, names, ref opaque);
                        break;
                    case For f:
                        AddTargetNames(f.Target, names);
                        CollectBindings(f.Body, names, ref opaque);
                        CollectBindings(f.OrElse, names, ref opaque);
                        break;
                    case AsyncFor af:
                        AddTargetNames(af.Target, names);
                        CollectBindings(af.Body, names, ref opaque);
                        CollectBindings(af.OrElse, names, ref opaque);
                        break;
                    case While w:
                        CollectBindings(w.Body, names, ref opaque);
                        CollectBindings(w.OrElse, names, ref opaque);
                        break;
                    case With wi:
                        foreach (WithItem item in wi.Items) AddTargetNames(item.OptionalVars, names);
                        CollectBindings(wi.Body, names, ref opaque);
                        break;
                    case AsyncWith aw:
                        foreach (WithItem item in aw.Items) AddTargetNames(item.OptionalVars, names);
                        CollectBindings(aw.Body, names, ref opaque);
                        break;
                    case Match m:
                        foreach (MatchCase mc in m.Cases) CollectBindings(mc.Body, names, ref opaque);
                        break;
                }
            }
        }

        private static void AddTargetNames(Expr target, HashSet<string> names)
        {
            if (target == null) return;
            if (target is Name n) { names.Add(n.Id); return; }
            if (target is Starred st) { AddTargetNames(st.Value, names); return; }
            if (target is TupleExpr tup)
            {
                foreach (Expr e in tup.Elts) AddTargetNames(e, names);
                return;
            }
            if (target is ListExpr lst)
            {
                foreach (Expr e in lst.Elts) AddTargetNames(e, names);
            }
            // Attribute/Subscript targets bind nothing new at module level.
        }

        private static string TopDotted(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int dot = dotted.IndexOf('.');
            return dot < 0 ? dotted : dotted.Substring(0, dot);
        }

        private static string CombineSafe(string dir, string leaf)
        {
            try { return Path.Combine(dir, leaf); }
            catch { return null; }
        }

        private static bool FileExistsSafe(string p)
        {
            try { return File.Exists(p); }
            catch { return false; }
        }

        private static bool DirectoryExistsSafe(string p)
        {
            try { return Directory.Exists(p); }
            catch { return false; }
        }
    }
}
