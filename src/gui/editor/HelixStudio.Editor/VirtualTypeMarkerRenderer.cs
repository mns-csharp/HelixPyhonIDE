using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HelixTextEditorNET;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// SRS solution_explorer_rules §3.3: virtual display of inferred static types
    /// for Python assignments — rendered as faint inlay markers (`: int`) after the
    /// line's text, never written to the file (§3.5). Inference is AST-driven over
    /// the assignment's right-hand side: primitives, containers, tuples, f-strings,
    /// user-defined classes via constructor calls, and callables via lambda.
    /// Explicitly annotated assignments are skipped (the real annotation wins).
    /// </summary>
    public sealed class VirtualTypeMarkerRenderer : IDisposable
    {
        private readonly Scintilla _editor;
        private readonly Timer _debounce;
        private Dictionary<int, string> _markersByDocLine = new Dictionary<int, string>();
        private bool _attached;
        private bool _disposed;

        private static readonly Color MarkerColor = Color.FromArgb(150, 120, 144, 156);
        private const int MaxMarkers = 500;

        public VirtualTypeMarkerRenderer(Scintilla editor)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _debounce = new Timer { Interval = 500 };
            _debounce.Tick += OnDebounceTick;
        }

        public void Attach()
        {
            if (_attached || _editor.IsDisposed) return;
            _editor.TextChanged += OnTextChanged;
            _attached = true;
            Recompute();
        }

        public void Detach()
        {
            if (!_attached) return;
            try { _editor.TextChanged -= OnTextChanged; } catch { }
            _markersByDocLine = new Dictionary<int, string>();
            _attached = false;
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed || _editor.IsDisposed) return;
            Recompute();
            try { _editor.Invalidate(); } catch { }
        }

        private void Recompute()
        {
            if (_editor.IsDisposed) return;
            var pythonEditor = _editor as PythonEditorControl;
            _markersByDocLine = ComputeMarkers(_editor.Text,
                pythonEditor != null ? pythonEditor.CurrentFilePath : null,
                pythonEditor != null ? pythonEditor.ModuleSearchPaths : null);
        }

        internal void RefreshFileContext()
        {
            if (_disposed || _editor.IsDisposed) return;
            Recompute();
            try { _editor.Invalidate(); } catch { }
        }

        /// <summary>
        /// Parses the module and maps 0-based document lines to inferred-type marker
        /// text. Static and pure for tests.
        /// </summary>
        public static Dictionary<int, string> ComputeMarkers(string text)
        {
            return ComputeMarkers(text, null);
        }

        internal static Dictionary<int, string> ComputeMarkers(string text, string filePath)
        {
            return ComputeMarkers(text, filePath, null);
        }

        /// <summary>
        /// The overload a notebook uses. <paramref name="moduleRoots"/> are the directories the
        /// user registered in the Modules pane — the same list the kernel is sent as its
        /// <c>sys_path</c> request.
        ///
        /// <para>
        /// Without them this engine resolves an import purely by walking the disk around
        /// <paramref name="filePath"/>, and a notebook is by definition nowhere near the code it
        /// imports. The driving case: a notebook under the user's Documents folder importing a
        /// book's example package from another drive. That folder is its own git repository, so
        /// the walk is confined to it and cannot reach the package at any depth or budget. The
        /// kernel ran the cell correctly while the inlay three inches above it read "couldn't
        /// infer type" — the one fact the user had explicitly supplied reached the kernel and not
        /// the analyzer. A registered root is the user saying where the code is; declining to look
        /// there is not caution, it is ignoring the answer.
        /// </para>
        /// </summary>
        internal static Dictionary<int, string> ComputeMarkers(string text, string filePath,
            IList<string> moduleRoots)
        {
            var markers = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(text)) return markers;
            ParseResult parsed;
            try { parsed = global::HelixPythonNamespace.Python.Python.ParseModule(text); }
            catch { return markers; }
            if (parsed == null || parsed.Module == null) return markers;

            // A qualified call such as `Factory.create()` cannot be classified from the callee's
            // spelling alone. Index return annotations in this module first so assignments can use
            // the same explicit type information the author and IntelliSense already trust.
            var annotatedReturns = new Dictionary<string, string>(StringComparer.Ordinal);
            var variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SyntaxNode node in parsed.Module.Descendants())
            {
                var function = node as FunctionDefSyntax;
                if (function == null || function.ReturnAnnotation == null) continue;
                string type = TypeExpressionName(function.ReturnAnnotation);
                if (!string.IsNullOrEmpty(type)) annotatedReturns[function.Name] = type;
            }
            BeginImportSearchBudget();
            try { AddImportedReturnAnnotations(parsed.Module, filePath, annotatedReturns, moduleRoots); }
            finally { if (_importSearchClock != null) _importSearchClock.Stop(); }

            // Collect constraints from operations whose contracts require tensors. This is the
            // backward half of inference: in `x = normalize(a); torch.stack([x, y])`, stack proves
            // x/y are tensors even when normalize has no annotation. Iterate through assignments
            // so that proof can flow back through small unannotated helper methods and arithmetic.
            CollectTensorRequirements(parsed.Module, variableTypes);
            CollectRegisteredBufferTypes(parsed.Module, annotatedReturns, variableTypes);
            bool changed;
            do
            {
                changed = false;
                foreach (SyntaxNode node in parsed.Module.Descendants())
                {
                    var assign = node as AssignmentStatement;
                    if (assign == null || assign.Value == null || assign.Targets.Count != 1) continue;
                    var target = assign.Targets[0] as NameExpression;
                    string required;
                    if (target == null || !variableTypes.TryGetValue(target.Name, out required) || required != "Tensor") continue;
                    changed |= BindNamesInExpression(assign.Value, "Tensor", variableTypes);
                }
            } while (changed);

            InferLocalReturnAnnotations(parsed.Module, annotatedReturns, variableTypes);

            foreach (SyntaxNode node in parsed.Module.Descendants())
            {
                if (markers.Count >= MaxMarkers) break;
                var assign = node as AssignmentStatement;
                if (assign == null || assign.Value == null) continue;
                // The real annotation wins. A destructuring target gets one aggregate marker on
                // its physical line while each bound name is retained for subsequent inference.
                if (assign.Annotation != null) continue;
                if (assign.Targets == null || assign.Targets.Count != 1) continue;

                IDictionary<string, string> contextualTypes = ContextualTypes(assign, variableTypes);
                string inferred = InferTypeName(assign.Value, annotatedReturns, contextualTypes);
                var targetName = assign.Targets[0] as NameExpression;
                if (targetName != null)
                {
                    string required;
                    if ((inferred == null || inferred == "couldn't infer type") &&
                        variableTypes.TryGetValue(targetName.Name, out required)) inferred = required;
                    if (inferred != null) variableTypes[targetName.Name] = inferred;
                }
                else if (assign.Targets[0] is AttributeExpression)
                {
                    string qualified = ExpressionName(assign.Targets[0]);
                    if (qualified != null && inferred != null) variableTypes[qualified] = inferred;
                }
                else if (assign.Targets[0] is TupleExpression)
                {
                    inferred = InferDestructuringAssignment(assign.Targets[0], assign.Value,
                        annotatedReturns, contextualTypes);
                    CommitBoundTargetTypes(assign.Targets[0], contextualTypes, variableTypes);
                }
                if (inferred == null) continue;
                int docLine = assign.Span.Start.Line - 1;
                if (docLine < 0) continue;
                if (!markers.ContainsKey(docLine)) markers[docLine] = inferred;
            }

            foreach (SyntaxNode node in parsed.Module.Descendants())
            {
                if (markers.Count >= MaxMarkers) break;
                var ret = node as ReturnStatement;
                if (ret == null || ret.Value == null) continue;
                string inferred = InferTypeName(ret.Value, annotatedReturns, variableTypes);
                string declared = DeclaredReturnTypeAt(parsed.Module, ret.Span.Start.Line);
                if (!string.IsNullOrEmpty(declared)) inferred = declared;
                if (inferred == null) inferred = "couldn't infer type";
                int docLine = ret.Span.Start.Line - 1;
                if (docLine >= 0 && !markers.ContainsKey(docLine)) markers[docLine] = inferred;
            }
            return markers;
        }

        private static void CommitBoundTargetTypes(ExpressionSyntax target,
            IDictionary<string, string> contextual, IDictionary<string, string> accumulated)
        {
            var name = target as NameExpression;
            if (name != null)
            {
                string type;
                if (contextual.TryGetValue(name.Name, out type)) accumulated[name.Name] = type;
                return;
            }
            var attribute = target as AttributeExpression;
            if (attribute != null)
            {
                string qualified = ExpressionName(attribute);
                string type;
                if (qualified != null && contextual.TryGetValue(qualified, out type))
                    accumulated[qualified] = type;
                return;
            }
            var tuple = target as TupleExpression;
            if (tuple == null) return;
            foreach (ExpressionSyntax element in tuple.Elements)
                CommitBoundTargetTypes(element, contextual, accumulated);
        }

        private static IDictionary<string, string> ContextualTypes(SyntaxNode node,
            IDictionary<string, string> accumulated)
        {
            var result = new Dictionary<string, string>(accumulated, StringComparer.Ordinal);
            FunctionDefSyntax function = node.FirstAncestor<FunctionDefSyntax>();
            if (function != null)
                foreach (ParameterSyntax parameter in function.Parameters)
                {
                    string annotation = TypeExpressionName(parameter.Annotation);
                    if (!string.IsNullOrEmpty(annotation)) result[parameter.Name] = annotation;
                }

            // Flow-sensitive narrowing for the common and statically sound
            // `if isinstance(value, Type):` contract.
            IfStatement branch = node.FirstAncestor<IfStatement>();
            while (branch != null)
            {
                var testCall = branch.Test as CallExpression;
                var testName = testCall != null ? testCall.Callee as NameExpression : null;
                if (testName != null && testName.Name == "isinstance" && testCall.Arguments.Count >= 2 &&
                    IsWithin(node, branch.Body))
                {
                    var subject = testCall.Arguments[0].Value as NameExpression;
                    string narrowed = TypeExpressionName(testCall.Arguments[1].Value);
                    if (subject != null && !string.IsNullOrEmpty(narrowed)) result[subject.Name] = narrowed;
                }
                branch = branch.FirstAncestor<IfStatement>();
            }
            return result;
        }

        private static bool IsWithin(SyntaxNode node, SyntaxNode ancestor)
        {
            for (SyntaxNode current = node; current != null; current = current.Parent)
                if (ReferenceEquals(current, ancestor)) return true;
            return false;
        }

        private static string DeclaredReturnTypeAt(ModuleSyntax module, int line)
        {
            FunctionDefSyntax closest = null;
            foreach (SyntaxNode node in module.Descendants())
            {
                var function = node as FunctionDefSyntax;
                if (function == null || function.ReturnAnnotation == null) continue;
                if (line < function.Span.Start.Line || line > function.Span.End.Line) continue;
                if (closest == null || function.Span.Start.Line >= closest.Span.Start.Line) closest = function;
            }
            return closest != null ? TypeExpressionName(closest.ReturnAnnotation) : null;
        }

        private static readonly object ImportCacheLock = new object();
        private static readonly Dictionary<string, KeyValuePair<DateTime, Dictionary<string, string>>> ImportCache =
            new Dictionary<string, KeyValuePair<DateTime, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ImportResolutionCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How long one <see cref="ComputeMarkers"/> call may spend hunting for imported modules on
        /// disk, and how many directories a single hunt may open.
        ///
        /// <para>
        /// <b>This is what stops the IDE freezing on a file opened from outside the solution.</b>
        /// The recursive fallback searches from the nearest enclosing `.git`, and that root is
        /// chosen by walking up twelve levels — so opening a book's example package from
        /// <c>...\AuthoredBooks\...\code\cms</c> picked <c>AuthoredBooks</c>, a 54,169-directory
        /// library of unrelated documents. Every import that did not resolve locally — `math`,
        /// `torch`, `typing`, once per name in each of the package's 54 modules, all of which get
        /// parsed on the way in — walked all of it. Measured: the UI thread never came back.
        /// </para>
        ///
        /// <para>
        /// Bounding it is the surgical fix, not the deep one; the deep one is to compute markers
        /// off the UI thread and invalidate when they arrive. This work is already cached and
        /// already best-effort, so a search that runs out of budget and reports "not found" costs a
        /// few inlay hints, while a search that never returns costs the whole application.
        /// </para>
        /// </summary>
        private const int ImportSearchBudgetMilliseconds = 400;

        private const int ImportSearchDirectoryCap = 3000;

        [ThreadStatic] private static System.Diagnostics.Stopwatch _importSearchClock;

        /// <summary>
        /// Top-level names that belong to the standard library.
        ///
        /// <para>
        /// A recursive hunt for `math` through the user's documents can only fail, and paying tens
        /// of thousands of directory reads to discover that is the bulk of the cost this removes.
        /// Skipping only the RECURSIVE fallback is safe: a local <c>math.py</c> beside the file, or
        /// in a parent package, is still found by the direct path checks that run first. What is
        /// skipped is the "a sibling project might own it" search, and no sibling project owns the
        /// standard library.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> StandardLibraryModules = new HashSet<string>(StringComparer.Ordinal)
        {
            "abc", "argparse", "array", "ast", "asyncio", "base64", "binascii", "bisect", "builtins",
            "bz2", "calendar", "cmath", "cmd", "codecs", "collections", "colorsys", "concurrent",
            "configparser", "contextlib", "copy", "csv", "ctypes", "dataclasses", "datetime",
            "decimal", "difflib", "dis", "email", "enum", "errno", "faulthandler", "filecmp",
            "fileinput", "fnmatch", "fractions", "functools", "gc", "getpass", "glob", "gzip",
            "hashlib", "heapq", "hmac", "html", "http", "importlib", "inspect", "io", "ipaddress",
            "itertools", "json", "keyword", "linecache", "locale", "logging", "lzma", "mailbox",
            "math", "mimetypes", "multiprocessing", "numbers", "operator", "os", "pathlib", "pdb",
            "pickle", "pkgutil", "platform", "plistlib", "pprint", "profile", "pstats", "queue",
            "random", "re", "reprlib", "sched", "secrets", "select", "selectors", "shelve", "shlex",
            "shutil", "signal", "site", "smtplib", "socket", "sqlite3", "ssl", "stat", "statistics",
            "string", "struct", "subprocess", "sys", "sysconfig", "tarfile", "tempfile", "textwrap",
            "threading", "time", "timeit", "tkinter", "token", "tokenize", "trace", "traceback",
            "types", "typing", "unicodedata", "unittest", "urllib", "uuid", "venv", "warnings",
            "wave", "weakref", "webbrowser", "xml", "xmlrpc", "zipfile", "zipimport", "zlib",
        };

        /// <summary>Start this call's search budget. A nested call keeps the outermost one.</summary>
        private static void BeginImportSearchBudget()
        {
            if (_importSearchClock == null) _importSearchClock = new System.Diagnostics.Stopwatch();
            if (!_importSearchClock.IsRunning) { _importSearchClock.Reset(); _importSearchClock.Start(); }
        }

        private static bool ImportSearchBudgetSpent()
        {
            return _importSearchClock != null && _importSearchClock.IsRunning
                && _importSearchClock.ElapsedMilliseconds > ImportSearchBudgetMilliseconds;
        }

        private static void AddImportedReturnAnnotations(ModuleSyntax module, string filePath,
            IDictionary<string, string> annotatedReturns)
        {
            AddImportedReturnAnnotations(module, filePath, annotatedReturns, null);
        }

        private static void AddImportedReturnAnnotations(ModuleSyntax module, string filePath,
            IDictionary<string, string> annotatedReturns, IList<string> moduleRoots)
        {
            if (module == null) return;

            // A registered root is absolute and independent of where the document sits, so having
            // one is on its own enough to resolve an import. Requiring a real file on disk as well
            // would deny the feature to exactly the document that needs it most: a notebook the
            // user has not saved anywhere yet.
            bool anchored = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
            if (!anchored && !HasModuleRoot(moduleRoots)) return;
            string documentDirectory = anchored ? Path.GetDirectoryName(filePath) : null;

            foreach (SyntaxNode node in module.Descendants())
            {
                var import = node as FromImportStatement;
                if (import != null && import.Module != null)
                {
                    string start = documentDirectory;
                    for (int level = 1; level < import.RelativeDots && !string.IsNullOrEmpty(start); level++)
                        start = Path.GetDirectoryName(start);
                    AddImportedAnnotations(import.Module.AsString, start, annotatedReturns, moduleRoots);
                    continue;
                }
                var plainImport = node as ImportStatement;
                if (plainImport == null) continue;
                foreach (ImportStatement.ImportedName name in plainImport.Names)
                    AddImportedAnnotations(name.Name.AsString, documentDirectory, annotatedReturns, moduleRoots);
            }
        }

        private static void AddImportedAnnotations(string module, string startDirectory,
            IDictionary<string, string> annotatedReturns, IList<string> moduleRoots)
        {
            string importedFile = FindImportedModuleFile(startDirectory, module, moduleRoots);
            if (importedFile == null) return;
            foreach (KeyValuePair<string, string> pair in ImportedReturnAnnotations(importedFile))
                if (!annotatedReturns.ContainsKey(pair.Key)) annotatedReturns[pair.Key] = pair.Value;
        }

        private static string FindImportedModuleFile(string startDirectory, string module)
        {
            return FindImportedModuleFile(startDirectory, module, null);
        }

        /// <summary>True when at least one usable registered root was supplied.</summary>
        private static bool HasModuleRoot(IList<string> moduleRoots)
        {
            if (moduleRoots == null) return false;
            for (int i = 0; i < moduleRoots.Count; i++)
                if (!string.IsNullOrEmpty(moduleRoots[i])) return true;
            return false;
        }

        /// <summary>
        /// A registered root answers the import the way Python does — the root IS a
        /// <c>sys.path</c> entry, so the module sits directly under it — which is why this is a
        /// bounded pair of <c>File.Exists</c> probes per root and never a recursive hunt. It runs
        /// BEFORE the disk walk for the same reason: an explicit answer outranks a search.
        /// </summary>
        private static string ProbeModuleRoots(IList<string> moduleRoots, string suffix,
            string packageSuffix)
        {
            if (moduleRoots == null) return null;
            for (int i = 0; i < moduleRoots.Count; i++)
            {
                string root = moduleRoots[i];
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    string direct = Path.Combine(root, suffix);
                    if (File.Exists(direct)) return direct;
                    string package = Path.Combine(root, packageSuffix);
                    if (File.Exists(package)) return package;
                }
                catch { }   // a root can be malformed or on a drive that is not mounted
            }
            return null;
        }

        private static string FindImportedModuleFile(string startDirectory, string module,
            IList<string> moduleRoots)
        {
            if (string.IsNullOrEmpty(module)) return null;
            bool hasRoots = HasModuleRoot(moduleRoots);
            if (string.IsNullOrEmpty(startDirectory) && !hasRoots) return null;

            // The roots belong in the key. Two notebooks can register different roots and ask the
            // same question from the same directory, and a miss is cached as aggressively as a
            // hit — so a key that ignored them would answer the second notebook with the first
            // one's "not found" and never look again.
            string rootKey = hasRoots ? string.Join(";", moduleRoots) : string.Empty;
            string cacheKey = (startDirectory ?? string.Empty) + "|" + module + "|" + rootKey;
            lock (ImportCacheLock)
            {
                string cached;
                if (ImportResolutionCache.TryGetValue(cacheKey, out cached))
                    return cached.Length == 0 ? null : cached;
            }
            string suffix = module.Replace('.', Path.DirectorySeparatorChar) + ".py";
            string packageSuffix = module.Replace('.', Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar + "__init__.py";

            string registered = ProbeModuleRoots(moduleRoots, suffix, packageSuffix);
            if (registered != null) return CacheImportResolution(cacheKey, registered);

            // Nothing below can run without a document to walk from. An unsaved notebook that
            // registered a root gets the probe above and stops here, rather than resolving
            // against whatever directory the process happens to be sitting in.
            if (string.IsNullOrEmpty(startDirectory)) return CacheImportResolution(cacheKey, string.Empty);

            DirectoryInfo start = new DirectoryInfo(startDirectory);
            DirectoryInfo repositoryRoot = FindRepositoryRoot(start) ?? FindPythonWorkspaceRoot(start);
            DirectoryInfo directory = start;
            string searchRoot = repositoryRoot != null ? repositoryRoot.FullName : null;
            for (int level = 0; directory != null &&
                (repositoryRoot != null ? IsAtOrBelow(directory, repositoryRoot) : level < 4);
                level++, directory = directory.Parent)
            {
                if (repositoryRoot == null) searchRoot = directory.FullName;
                string direct = Path.Combine(directory.FullName, suffix);
                if (File.Exists(direct)) return CacheImportResolution(cacheKey, direct);
                string package = Path.Combine(directory.FullName, packageSuffix);
                if (File.Exists(package)) return CacheImportResolution(cacheKey, package);
            }

            // A sibling project may own the imported package, so one recursive fallback is still
            // required. Run it only at the highest search root, and share its result across every
            // file below that root. The old loop searched four overlapping trees per miss; on the
            // 244-file BugMap corpus that made a read-only overlay audit take minutes.
            string recursiveKey = "recursive|" + searchRoot + "|" + module;
            lock (ImportCacheLock)
            {
                string cached;
                if (ImportResolutionCache.TryGetValue(recursiveKey, out cached))
                    return CacheImportResolution(cacheKey, cached.Length == 0 ? null : cached);
            }
            // The standard library is never in a sibling project, and searching a document tree
            // for it can only fail expensively.
            string topLevel = module.Split('.')[0];
            if (StandardLibraryModules.Contains(topLevel)) return CacheImportResolution(cacheKey, string.Empty);

            // Out of budget: report "not found" and remember it, so this call ends in bounded time
            // and the next one does not pay for the same miss again.
            if (ImportSearchBudgetSpent())
            {
                CacheImportResolution(recursiveKey, string.Empty);
                return CacheImportResolution(cacheKey, string.Empty);
            }

            string found = FindModuleUnder(searchRoot, suffix, packageSuffix);
            CacheImportResolution(recursiveKey, found ?? string.Empty);
            if (found != null) return CacheImportResolution(cacheKey, found);
            CacheImportResolution(cacheKey, string.Empty);
            return null;
        }

        private static string FindModuleUnder(string root, string suffix, string packageSuffix)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string leaf = Path.GetFileName(suffix);
            var pending = new Stack<string>();
            pending.Push(root);
            int visited = 0;
            while (pending.Count > 0)
            {
                // Two independent stops. The directory cap bounds one pathological tree; the clock
                // bounds a whole file-open across many misses. Either firing means this hunt gives
                // up and says "not found" — which is what it would have said anyway for an import
                // that is not in this tree, just without spending the rest of the minute on it.
                if (++visited > ImportSearchDirectoryCap || ImportSearchBudgetSpent()) return null;

                string directory = pending.Pop();
                try
                {
                    foreach (string candidate in Directory.GetFiles(directory, leaf, SearchOption.TopDirectoryOnly))
                        if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return candidate;
                    foreach (string candidate in Directory.GetFiles(directory, "__init__.py", SearchOption.TopDirectoryOnly))
                        if (candidate.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase)) return candidate;
                    foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                        if (!IgnoredImportSearchDirectory(Path.GetFileName(child))) pending.Push(child);
                }
                catch { }
            }
            return null;
        }

        private static DirectoryInfo FindRepositoryRoot(DirectoryInfo start)
        {
            DirectoryInfo current = start;
            for (int level = 0; current != null && level < 12; level++, current = current.Parent)
                if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current;
            return null;
        }

        private static DirectoryInfo FindPythonWorkspaceRoot(DirectoryInfo start)
        {
            DirectoryInfo current = start;
            for (int level = 0; current != null && level < 12; level++, current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")) ||
                    File.Exists(Path.Combine(current.FullName, "setup.py")) ||
                    File.Exists(Path.Combine(current.FullName, "setup.cfg")) ||
                    Directory.Exists(Path.Combine(current.FullName, ".helixstudio"))) return current;
            return null;
        }

        private static bool IsAtOrBelow(DirectoryInfo current, DirectoryInfo root)
        {
            if (current == null || root == null) return false;
            return current.FullName.StartsWith(root.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IgnoredImportSearchDirectory(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == ".git" || name == ".vs" || name == ".pytest_cache" ||
                name == "__pycache__" || name == "artifacts" || name == "bin" || name == "obj";
        }

        private static string CacheImportResolution(string key, string path)
        {
            lock (ImportCacheLock) ImportResolutionCache[key] = path ?? string.Empty;
            return path;
        }

        private static Dictionary<string, string> ImportedReturnAnnotations(string path)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); } catch { return new Dictionary<string, string>(); }
            lock (ImportCacheLock)
            {
                KeyValuePair<DateTime, Dictionary<string, string>> cached;
                if (ImportCache.TryGetValue(path, out cached) && cached.Key == stamp) return cached.Value;
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                ParseResult parsed = global::HelixPythonNamespace.Python.Python.ParseModule(File.ReadAllText(path), path);
                // A recoverable parse diagnostic elsewhere in a large scientific module must not
                // discard valid function annotations already present in its syntax tree.
                if (parsed != null && parsed.Module != null)
                {
                    // Publish the in-progress map before following re-exports so circular package
                    // imports terminate while still sharing annotations collected so far.
                    lock (ImportCacheLock)
                        ImportCache[path] = new KeyValuePair<DateTime, Dictionary<string, string>>(stamp, result);
                    foreach (SyntaxNode node in parsed.Module.Descendants())
                    {
                        var function = node as FunctionDefSyntax;
                        if (function == null || function.ReturnAnnotation == null) continue;
                        string type = TypeExpressionName(function.ReturnAnnotation);
                        if (!string.IsNullOrEmpty(type)) result[function.Name] = type;
                    }
                    var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                    CollectTensorRequirements(parsed.Module, variables);
                    CollectRegisteredBufferTypes(parsed.Module, result, variables);
                    InferLocalReturnAnnotations(parsed.Module, result, variables);
                    AddImportedReturnAnnotations(parsed.Module, path, result);
                }
            }
            catch { }
            lock (ImportCacheLock) ImportCache[path] = new KeyValuePair<DateTime, Dictionary<string, string>>(stamp, result);
            return result;
        }

        private static string InferDestructuringAssignment(ExpressionSyntax target, ExpressionSyntax value,
            IDictionary<string, string> annotatedReturns, IDictionary<string, string> variableTypes)
        {
            var targetTuple = target as TupleExpression;
            var valueTuple = value as TupleExpression;
            if (targetTuple == null) return "couldn't infer type";

            var types = new List<string>();
            for (int i = 0; i < targetTuple.Elements.Count; i++)
            {
                string type = null;
                var shape = value as AttributeExpression;
                if (shape != null && shape.Name == "shape") type = "int";
                else if (valueTuple != null && valueTuple.Elements.Count == targetTuple.Elements.Count)
                    type = InferTypeName(valueTuple.Elements[i], annotatedReturns, variableTypes);
                else
                {
                    string aggregate = InferTypeName(value, annotatedReturns, variableTypes);
                    IList<string> elements = TupleElementTypes(aggregate);
                    if (elements == null && aggregate != null &&
                        aggregate.StartsWith("list[", StringComparison.Ordinal))
                    {
                        elements = new List<string>();
                        string element = GenericArguments(aggregate);
                        for (int repeated = 0; repeated < targetTuple.Elements.Count; repeated++)
                            elements.Add(element);
                    }
                    if (elements != null && elements.Count == targetTuple.Elements.Count) type = elements[i];
                }
                var name = targetTuple.Elements[i] as NameExpression;
                if ((type == null || type == "couldn't infer type") && name != null)
                {
                    string required;
                    if (variableTypes.TryGetValue(name.Name, out required)) type = required;
                }
                if (type == null) type = "couldn't infer type";
                if (name != null) variableTypes[name.Name] = type;
                types.Add(type);
            }
            return "tuple[" + string.Join(", ", types) + "]";
        }

        private static void InferLocalReturnAnnotations(ModuleSyntax module,
            IDictionary<string, string> returns, IDictionary<string, string> variableTypes)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (SyntaxNode node in module.Descendants())
                {
                    var function = node as FunctionDefSyntax;
                    if (function == null || function.ReturnAnnotation != null || returns.ContainsKey(function.Name))
                        continue;
                    string inferred = null;
                    foreach (SyntaxNode descendant in function.Body.Descendants())
                    {
                        var statement = descendant as ReturnStatement;
                        if (statement == null || statement.Value == null ||
                            !ReferenceEquals(statement.FirstAncestor<FunctionDefSyntax>(), function)) continue;
                        string candidate = InferTypeName(statement.Value, returns,
                            ContextualTypes(statement, variableTypes));
                        if (string.IsNullOrEmpty(candidate) || candidate.Contains("couldn't infer type")) continue;
                        inferred = inferred == null ? candidate : UnionType(inferred, candidate);
                    }
                    if (inferred != null)
                    {
                        returns[function.Name] = inferred;
                        changed = true;
                    }
                }
            } while (changed);
        }

        private static IList<string> TupleElementTypes(string type)
        {
            if (string.IsNullOrEmpty(type) || !type.StartsWith("tuple[", StringComparison.Ordinal) ||
                !type.EndsWith("]", StringComparison.Ordinal)) return null;
            return SplitTopLevel(GenericArguments(type));
        }

        private static IList<string> SplitTopLevel(string text)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']') depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    parts.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(text.Substring(start).Trim());
            return parts;
        }

        private static void CollectTensorRequirements(SyntaxNode module, IDictionary<string, string> variableTypes)
        {
            foreach (SyntaxNode node in module.Descendants())
            {
                var call = node as CallExpression;
                if (call == null) continue;
                var attribute = call.Callee as AttributeExpression;
                if (attribute == null) continue;
                string root = RootName(attribute.Target);
                if (root == "torch" && TensorArgumentCalls.Contains(attribute.Name))
                {
                    foreach (CallArgument argument in call.Arguments)
                        BindNamesInExpression(argument.Value, "Tensor", variableTypes);
                }
                else if (TensorPreservingMethods.Contains(attribute.Name))
                {
                    BindNamesInExpression(attribute.Target, "Tensor", variableTypes);
                }
            }
        }

        private static void CollectRegisteredBufferTypes(SyntaxNode module,
            IDictionary<string, string> annotatedReturns, IDictionary<string, string> variableTypes)
        {
            foreach (SyntaxNode node in module.Descendants())
            {
                var call = node as CallExpression;
                var method = call != null ? call.Callee as AttributeExpression : null;
                if (method == null || method.Name != "register_buffer" || call.Arguments.Count < 2) continue;
                string owner = ExpressionName(method.Target);
                var fieldLiteral = call.Arguments[0].Value as LiteralExpression;
                if (string.IsNullOrEmpty(owner) || fieldLiteral == null) continue;
                string field = Unquote(fieldLiteral.Token.Text);
                if (string.IsNullOrEmpty(field)) continue;
                string type = InferTypeName(call.Arguments[1].Value, annotatedReturns, variableTypes);
                if (!string.IsNullOrEmpty(type) && type != "couldn't infer type")
                    variableTypes[owner + "." + field] = type;
            }
        }

        private static string ExpressionName(ExpressionSyntax expression)
        {
            var name = expression as NameExpression;
            if (name != null) return name.Name;
            var attribute = expression as AttributeExpression;
            if (attribute == null) return null;
            string target = ExpressionName(attribute.Target);
            return target == null ? null : target + "." + attribute.Name;
        }

        private static string Unquote(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2) return null;
            char first = text[0];
            char last = text[text.Length - 1];
            return first == last && (first == '\'' || first == '"')
                ? text.Substring(1, text.Length - 2)
                : null;
        }

        private static bool BindNamesInExpression(ExpressionSyntax expression, string type,
            IDictionary<string, string> variableTypes)
        {
            if (expression == null) return false;
            bool changed = false;
            var direct = expression as NameExpression;
            if (direct != null) changed |= BindIfAbsent(direct.Name, type, variableTypes);
            foreach (SyntaxNode node in expression.Descendants())
            {
                var name = node as NameExpression;
                if (name != null) changed |= BindIfAbsent(name.Name, type, variableTypes);
            }
            return changed;
        }

        private static bool BindIfAbsent(string name, string type, IDictionary<string, string> variableTypes)
        {
            if (string.IsNullOrEmpty(name) || variableTypes.ContainsKey(name)) return false;
            variableTypes[name] = type;
            return true;
        }

        /// <summary>
        /// The inferred Python type name for an expression, or null when unknown.
        /// Deliberately conservative: a wrong marker is worse than no marker.
        /// </summary>
        public static string InferTypeName(ExpressionSyntax value)
        {
            return InferTypeName(value, null);
        }

        private static string InferTypeName(ExpressionSyntax value, IDictionary<string, string> annotatedReturns)
        {
            return InferTypeName(value, annotatedReturns, null);
        }

        private static string InferTypeName(ExpressionSyntax value, IDictionary<string, string> annotatedReturns,
            IDictionary<string, string> variableTypes)
        {
            switch (value)
            {
                case NameExpression name:
                {
                    string known;
                    return variableTypes != null && variableTypes.TryGetValue(name.Name, out known) ? known : null;
                }
                case AttributeExpression attributeValue:
                {
                    string known;
                    string qualified = ExpressionName(attributeValue);
                    if (variableTypes != null && qualified != null &&
                        variableTypes.TryGetValue(qualified, out known)) return known;
                    string owner = InferTypeName(attributeValue.Target, annotatedReturns, variableTypes);
                    if (attributeValue.Name == "shape" &&
                        (owner == "Tensor" || owner == "ndarray")) return "tuple[int]";
                    if (owner == "Path")
                    {
                        if (attributeValue.Name == "parent") return "Path";
                        if (attributeValue.Name == "parents") return "tuple[Path]";
                        if (attributeValue.Name == "name" || attributeValue.Name == "stem" ||
                            attributeValue.Name == "suffix") return "str";
                    }
                    var staticOwner = attributeValue.Target as NameExpression;
                    if (staticOwner != null && !string.IsNullOrEmpty(staticOwner.Name) &&
                        char.IsUpper(staticOwner.Name[0])) return "Callable";
                    return owner == "Any" ? "Any" : null;
                }
                case ParenthesizedExpression parenthesized:
                    return InferTypeName(parenthesized.Inner, annotatedReturns, variableTypes);
                case SubscriptExpression subscript:
                {
                    string container = InferTypeName(subscript.Target, annotatedReturns, variableTypes);
                    if (container == "Tensor" || container == "ndarray") return container;
                    return InferContainerElementType(container);
                }
                case LiteralExpression lit:
                    return InferLiteral(lit);
                case ListExpression list: return InferList(list, annotatedReturns, variableTypes);
                case DictExpression _: return "dict";
                case SetExpression _: return "set";
                case TupleExpression tuple: return InferTuple(tuple, annotatedReturns, variableTypes);
                case ComprehensionExpression comprehension:
                {
                    if (comprehension.Kind == ComprehensionKind.Dict) return "dict";
                    string element = InferTypeName(comprehension.Element, annotatedReturns, variableTypes);
                    string container = comprehension.Kind == ComprehensionKind.Set ? "set" :
                        comprehension.Kind == ComprehensionKind.Generator ? "Generator" : "list";
                    return element == null ? container : container + "[" + element + "]";
                }
                case FStringExpression _: return "str";
                case LambdaExpression lambda:
                    return "Callable[[" + string.Join(", ",
                        RepeatAny(lambda.Parameters != null ? lambda.Parameters.Count : 0)) + "], Any]";
                case UnaryExpression unary:
                    if (unary.Operator == UnaryOperator.Not) return "bool";
                    return InferTypeName(unary.Operand, annotatedReturns, variableTypes);
                case ComparisonExpression _: return "bool";
                case ConditionalExpression cond:
                {
                    string a = InferTypeName(cond.IfTrue, annotatedReturns, variableTypes);
                    string b = InferTypeName(cond.IfFalse, annotatedReturns, variableTypes);
                    if (a == null || b == null) return null;
                    return a == b ? a : UnionType(a, b);
                }
                case BinaryExpression bin:
                    return InferBinary(bin, annotatedReturns, variableTypes);
                case CallExpression call:
                    return InferCall(call, annotatedReturns, variableTypes);
                default:
                    return null;
            }
        }

        private static string InferTuple(TupleExpression tuple, IDictionary<string, string> annotatedReturns,
            IDictionary<string, string> variableTypes)
        {
            var types = new List<string>();
            foreach (ExpressionSyntax element in tuple.Elements)
                types.Add(InferTypeName(element, annotatedReturns, variableTypes) ?? "couldn't infer type");
            return types.Count == 0 ? "tuple" : "tuple[" + string.Join(", ", types) + "]";
        }

        private static string InferList(ListExpression list, IDictionary<string, string> annotatedReturns,
            IDictionary<string, string> variableTypes)
        {
            if (list.Elements.Count == 0) return "list";
            string elementType = null;
            foreach (ExpressionSyntax element in list.Elements)
            {
                string current = InferTypeName(element, annotatedReturns, variableTypes);
                if (current == null) return "list";
                elementType = elementType == null ? current : UnionType(elementType, current);
            }
            return "list[" + elementType + "]";
        }

        private static string InferLiteral(LiteralExpression lit)
        {
            string text = lit.Token.Text ?? string.Empty;
            switch (text)
            {
                case "True":
                case "False": return "bool";
                case "None": return "None";
            }
            if (text.Length == 0) return null;
            char first = text[0];
            if (char.IsDigit(first) || (first == '.' && text.Length > 1))
            {
                string lower = text.ToLowerInvariant();
                if (lower.EndsWith("j", StringComparison.Ordinal)) return "complex";
                bool isFloat = lower.Contains(".") ||
                               (!lower.StartsWith("0x", StringComparison.Ordinal) && lower.Contains("e"));
                return isFloat ? "float" : "int";
            }
            if (first == '"' || first == '\'') return "str";
            char lowerFirst = char.ToLowerInvariant(first);
            if (lowerFirst == 'b') return "bytes";
            if (lowerFirst == 'r' || lowerFirst == 'u' || lowerFirst == 'f') return "str";
            return null;
        }

        private static string InferBinary(BinaryExpression bin, IDictionary<string, string> annotatedReturns = null,
            IDictionary<string, string> variableTypes = null)
        {
            string left = InferTypeName(bin.Left, annotatedReturns, variableTypes);
            string right = InferTypeName(bin.Right, annotatedReturns, variableTypes);
            if (bin.Operator == BinaryOperator.Or || bin.Operator == BinaryOperator.And)
            {
                if (left == right) return left;
                if (left == "None") return right;
                if (right == "None") return left;
                return left == null || right == null ? null : UnionType(left, right);
            }
            if (left == "Tensor" || right == "Tensor") return "Tensor";
            if (left == "ndarray" || right == "ndarray") return "ndarray";
            if (left == "Path" && bin.Operator == BinaryOperator.Divide && right == "str") return "Path";
            if (bin.Operator == BinaryOperator.Divide)
                return left != null && right != null ? "float" : null;
            if (left == null || right == null) return null;
            if (left == "str" && right == "str" && bin.Operator == BinaryOperator.Add) return "str";
            if ((left == "list" || left.StartsWith("list[", StringComparison.Ordinal)) &&
                right == "int" && bin.Operator == BinaryOperator.Multiply) return left;
            if ((left == "int" || left == "float") && (right == "int" || right == "float"))
                return left == "float" || right == "float" ? "float" : "int";
            if (left == right && (left == "list" || left == "tuple") && bin.Operator == BinaryOperator.Add)
                return left;
            return null;
        }

        // Builtin constructor calls map to their type; a Capitalized callee reads as
        // a user-defined class instantiation (Python convention, PEP 8).
        private static readonly Dictionary<string, string> BuiltinConstructors =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "int", "int" }, { "float", "float" }, { "str", "str" }, { "bool", "bool" },
            { "list", "list" }, { "dict", "dict" }, { "set", "set" }, { "tuple", "tuple" },
            { "bytes", "bytes" }, { "bytearray", "bytearray" }, { "frozenset", "frozenset" },
            { "complex", "complex" }, { "len", "int" }, { "abs", "int" }, { "sorted", "list" },
            { "range", "range" }, { "enumerate", "enumerate" }, { "zip", "zip" },
            { "open", "TextIO" }, { "repr", "str" }, { "format", "str" },
        };

        private static string InferCall(CallExpression call, IDictionary<string, string> annotatedReturns,
            IDictionary<string, string> variableTypes)
        {
            var name = call.Callee as NameExpression;
            if (name != null && !string.IsNullOrEmpty(name.Name))
            {
                string mapped;
                if (name.Name == "next" && call.Arguments.Count > 1)
                    return InferTypeName(call.Arguments[1].Value, annotatedReturns, variableTypes) ?? "Any";
                if (name.Name == "any" || name.Name == "all") return "bool";
                if (name.Name == "sum") return "float";
                if ((name.Name == "max" || name.Name == "min") && call.Arguments.Count > 0)
                    return InferTypeName(call.Arguments[0].Value, annotatedReturns, variableTypes) ?? "Any";
                if (name.Name == "round" && call.Arguments.Count > 0)
                    return InferTypeName(call.Arguments[0].Value, annotatedReturns, variableTypes) ?? "float";
                if (name.Name == "getattr" && call.Arguments.Count > 2)
                    return InferTypeName(call.Arguments[2].Value, annotatedReturns, variableTypes) ?? "Any";
                if (BuiltinConstructors.TryGetValue(name.Name, out mapped)) return mapped;
                if (annotatedReturns != null && annotatedReturns.TryGetValue(name.Name, out mapped)) return mapped;
                mapped = InferStandardCall(name.Name, call, annotatedReturns, variableTypes);
                if (mapped != null) return mapped;
                if (char.IsUpper(name.Name[0])) return name.Name;   // user class instance
                return null;
            }

            var attribute = call.Callee as AttributeExpression;
            if (attribute == null || string.IsNullOrEmpty(attribute.Name)) return "couldn't infer type";

            string annotated;
            if (annotatedReturns != null && annotatedReturns.TryGetValue(attribute.Name, out annotated))
                return annotated;

            // Qualified constructors retain the constructed object's name (`pd.DataFrame()`,
            // `models.Widget()`). This is no less reliable than the existing Rectangle() rule.
            if (char.IsUpper(attribute.Name[0])) return attribute.Name;

            string qualifier = RootName(attribute.Target);
            string qualifiedCall = ExpressionName(call.Callee);
            string standardType = InferStandardCall(qualifiedCall, call, annotatedReturns, variableTypes);
            if (standardType != null) return standardType;
            if (qualifier == "json" && attribute.Name == "loads") return "Any";
            if (qualifier == "torch" && !TorchNonTensorCalls.Contains(attribute.Name)) return "Tensor";
            if ((qualifier == "np" || qualifier == "numpy") && NumpyArrayFactories.Contains(attribute.Name))
                return "ndarray";
            string targetType = InferTypeName(attribute.Target, annotatedReturns, variableTypes);
            if (targetType == "Any") return "Any";
            if (targetType == "Tensor")
            {
                if (attribute.Name == "item") return "float";
                if (attribute.Name == "tolist") return "list";
                return "Tensor";
            }
            if (targetType == "ndarray")
            {
                if (attribute.Name == "tolist") return "list";
                return "ndarray";
            }
            string builtinMethodType = InferBuiltinMethodReturn(targetType, attribute.Name);
            if (builtinMethodType != null) return builtinMethodType;
            return "couldn't infer type";
        }

        private static string InferStandardCall(string qualifiedCall, CallExpression call,
            IDictionary<string, string> annotatedReturns, IDictionary<string, string> variableTypes)
        {
            if (string.IsNullOrEmpty(qualifiedCall)) return null;
            if (qualifiedCall == "math.exp" || qualifiedCall == "math.sqrt" || qualifiedCall == "math.log" ||
                qualifiedCall == "math.sin" || qualifiedCall == "math.cos" || qualifiedCall == "math.atan2" ||
                qualifiedCall == "time.time" || qualifiedCall == "time.perf_counter" ||
                qualifiedCall == "time.process_time" || qualifiedCall == "time.monotonic") return "float";
            if (qualifiedCall == "os.path.join" || qualifiedCall == "os.path.abspath" ||
                qualifiedCall == "os.path.dirname" || qualifiedCall == "os.path.basename") return "str";
            if (qualifiedCall == "os.path.splitext") return "tuple[str, str]";
            if (qualifiedCall == "os.path.isabs" || qualifiedCall == "os.path.exists" ||
                qualifiedCall == "os.path.isfile" || qualifiedCall == "os.path.isdir") return "bool";
            if (qualifiedCall == "logging.getLogger") return "Logger";
            if (qualifiedCall == "re.compile") return "Pattern";
            if (qualifiedCall == "re.sub" || qualifiedCall == "re.escape") return "str";
            if (qualifiedCall == "re.search" || qualifiedCall == "re.match" ||
                qualifiedCall == "re.fullmatch") return "Match | None";
            if (qualifiedCall == "csv.writer") return "CsvWriter";
            if (qualifiedCall == "hashlib.sha1") return "Hash";
            if (qualifiedCall == "os.stat") return "stat_result";
            if (qualifiedCall == "np.linalg.norm" || qualifiedCall == "numpy.linalg.norm") return "float";
            if (qualifiedCall == "np.linalg.svd" || qualifiedCall == "numpy.linalg.svd")
                return "tuple[ndarray, ndarray, ndarray]";
            if (qualifiedCall == "np.linalg.qr" || qualifiedCall == "numpy.linalg.qr")
                return "tuple[ndarray, ndarray]";
            if (qualifiedCall == "np.random.randn" || qualifiedCall == "numpy.random.randn" ||
                qualifiedCall == "np.random.standard_normal" || qualifiedCall == "numpy.random.standard_normal")
                return "ndarray";
            if (qualifiedCall == "np.random.default_rng" || qualifiedCall == "numpy.random.default_rng")
                return "Generator";
            if (qualifiedCall == "cp.from_dlpack" || qualifiedCall == "cupy.from_dlpack" ||
                qualifiedCall == "cp.fromDlpack" || qualifiedCall == "cupy.fromDlpack")
                return "cupy.ndarray";
            if (qualifiedCall == "wilcoxon" || qualifiedCall.EndsWith(".wilcoxon", StringComparison.Ordinal))
                return "tuple[float, float]";
            if (qualifiedCall == "os.environ.get")
            {
                if (call.Arguments.Count > 1)
                {
                    string fallback = InferTypeName(call.Arguments[1].Value, annotatedReturns, variableTypes);
                    if (fallback == "str") return "str";
                }
                return "str | None";
            }
            return null;
        }

        private static string InferContainerElementType(string containerType)
        {
            if (string.IsNullOrEmpty(containerType)) return null;
            if (containerType == "Any") return "Any";
            if (containerType == "str") return "str";
            if (containerType == "bytes") return "int";

            // An unsubscripted collection annotation is equivalent to using Any for its
            // arguments. Preserve that information instead of turning every subsequent lookup
            // into an inference failure.
            if (containerType == "list" || containerType == "set" || containerType == "tuple" ||
                containerType == "dict") return "Any";

            if (containerType.StartsWith("list[", StringComparison.Ordinal) ||
                containerType.StartsWith("set[", StringComparison.Ordinal))
                return GenericArguments(containerType);
            if (containerType.StartsWith("dict[", StringComparison.Ordinal))
            {
                string arguments = GenericArguments(containerType);
                int comma = TopLevelComma(arguments);
                return comma >= 0 ? arguments.Substring(comma + 1).Trim() : "Any";
            }
            if (containerType.StartsWith("tuple[", StringComparison.Ordinal))
            {
                string arguments = GenericArguments(containerType);
                int comma = TopLevelComma(arguments);
                return comma < 0 ? arguments : "Any";
            }
            return null;
        }

        private static string GenericArguments(string type)
        {
            int open = type.IndexOf('[');
            return open >= 0 && type.EndsWith("]", StringComparison.Ordinal)
                ? type.Substring(open + 1, type.Length - open - 2)
                : string.Empty;
        }

        private static int TopLevelComma(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']') depth--;
                else if (text[i] == ',' && depth == 0) return i;
            }
            return -1;
        }

        private static string InferBuiltinMethodReturn(string receiverType, string method)
        {
            if (string.IsNullOrEmpty(receiverType) || string.IsNullOrEmpty(method)) return null;
            if (receiverType == "str")
            {
                if (StringReturningMethods.Contains(method)) return "str";
                if (StringBooleanMethods.Contains(method)) return "bool";
                if (StringIntegerMethods.Contains(method)) return "int";
                if (method == "encode") return "bytes";
                if (method == "split" || method == "rsplit" || method == "splitlines") return "list[str]";
                if (method == "partition" || method == "rpartition") return "tuple[str, str, str]";
            }
            if (receiverType == "bytes")
            {
                if (method == "decode") return "str";
                if (method == "replace" || method == "strip" || method == "lstrip" || method == "rstrip")
                    return "bytes";
            }
            if (receiverType == "Path")
            {
                if (method == "read_text") return "str";
                if (method == "read_bytes") return "bytes";
                if (method == "exists" || method == "is_file" || method == "is_dir") return "bool";
                if (method == "resolve" || method == "absolute" || method == "expanduser") return "Path";
                if (method == "with_name" || method == "with_suffix" || method == "joinpath") return "Path";
            }
            if (receiverType == "Hash" && method == "hexdigest") return "str";
            if (receiverType == "Generator")
            {
                if (method == "normal" || method == "gamma" || method == "uniform" ||
                    method == "integers" || method == "choice" || method == "permutation" ||
                    method == "standard_normal" || method == "random") return "ndarray";
                if (method == "shuffle") return "None";
            }
            if (receiverType == "Pattern")
            {
                if (method == "search" || method == "match" || method == "fullmatch") return "Match | None";
                if (method == "sub") return "str";
                if (method == "split" || method == "findall") return "list[str]";
            }
            if (receiverType == "Match" || receiverType == "Match | None")
            {
                if (method == "group" || method == "expand") return "str";
                if (method == "groups") return "tuple[str | None]";
                if (method == "start" || method == "end") return "int";
                if (method == "span") return "tuple[int, int]";
            }
            if (method == "to" || method == "eval" || method == "train") return receiverType;
            if (receiverType == "list" || receiverType.StartsWith("list[", StringComparison.Ordinal))
            {
                if (method == "copy") return receiverType;
                if (method == "count" || method == "index") return "int";
            }
            if (receiverType == "dict" || receiverType.StartsWith("dict[", StringComparison.Ordinal))
            {
                if (method == "copy") return receiverType;
                if (method == "get" || method == "pop" || method == "setdefault")
                    return InferContainerElementType(receiverType);
                if (method == "keys" || method == "values" || method == "items") return "view";
            }
            return null;
        }

        private static readonly HashSet<string> StringReturningMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "capitalize", "casefold", "center", "expandtabs", "format", "format_map", "join",
            "ljust", "lower", "lstrip", "removeprefix", "removesuffix", "replace", "rjust",
            "rstrip", "strip", "swapcase", "title", "translate", "upper", "zfill"
        };

        private static readonly HashSet<string> StringBooleanMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "endswith", "isalnum", "isalpha", "isascii", "isdecimal", "isdigit", "isidentifier",
            "islower", "isnumeric", "isprintable", "isspace", "istitle", "isupper", "startswith"
        };

        private static readonly HashSet<string> StringIntegerMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "count", "find", "index", "rfind", "rindex"
        };

        private static readonly HashSet<string> TorchTensorFactories = new HashSet<string>(StringComparer.Ordinal)
        {
            "tensor", "as_tensor", "stack", "cat", "zeros", "ones", "empty", "full",
            "arange", "linspace", "eye", "rand", "randn", "randint", "cross", "matmul"
        };

        private static readonly HashSet<string> TorchNonTensorCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "is_tensor", "numel", "save", "set_default_dtype", "set_default_device", "manual_seed"
        };

        private static readonly HashSet<string> TensorArgumentCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "cos", "sin", "tan", "exp", "log", "sqrt", "abs", "ones_like", "zeros_like",
            "empty_like", "full_like", "stack", "cat", "concat", "cross", "matmul", "einsum"
        };

        private static readonly HashSet<string> TensorPreservingMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "sum", "mean", "norm", "reshape", "view", "unsqueeze", "squeeze", "transpose",
            "permute", "flatten", "clone", "detach", "to", "cpu", "cuda", "contiguous"
        };

        private static readonly HashSet<string> NumpyArrayFactories = new HashSet<string>(StringComparer.Ordinal)
        {
            "array", "asarray", "zeros", "ones", "empty", "full", "arange", "linspace",
            "eye", "stack", "concatenate", "reshape"
        };

        private static string RootName(ExpressionSyntax expression)
        {
            while (expression is AttributeExpression attribute) expression = attribute.Target;
            var name = expression as NameExpression;
            return name != null ? name.Name : null;
        }

        private static string TypeExpressionName(ExpressionSyntax annotation)
        {
            if (annotation == null) return null;
            var name = annotation as NameExpression;
            if (name != null) return name.Name;
            var attribute = annotation as AttributeExpression;
            if (attribute != null) return attribute.Name;
            var literal = annotation as LiteralExpression;
            if (literal != null)
            {
                string forward = Unquote(literal.Token.Text);
                return string.IsNullOrEmpty(forward) ? null : forward;
            }
            var tuple = annotation as TupleExpression;
            if (tuple != null)
            {
                var elements = new List<string>();
                foreach (ExpressionSyntax element in tuple.Elements)
                {
                    string item = TypeExpressionName(element);
                    if (item == null) return null;
                    elements.Add(item);
                }
                return string.Join(", ", elements);
            }
            var binary = annotation as BinaryExpression;
            if (binary != null && binary.Operator == BinaryOperator.BitOr)
            {
                string left = TypeExpressionName(binary.Left);
                string right = TypeExpressionName(binary.Right);
                return left != null && right != null ? UnionType(left, right) : null;
            }
            var subscript = annotation as SubscriptExpression;
            if (subscript != null)
            {
                string outer = TypeExpressionName(subscript.Target);
                string inner = TypeExpressionName(subscript.Index);
                if (outer == null) return null;
                if (outer == "Optional") return inner != null ? UnionType(inner, "None") : null;
                if (outer == "Tuple") outer = "tuple";
                else if (outer == "List") outer = "list";
                else if (outer == "Dict") outer = "dict";
                else if (outer == "Set") outer = "set";
                return inner != null ? outer + "[" + inner + "]" : outer;
            }
            return null;
        }

        private static string UnionType(string left, string right)
        {
            if (left == right) return left;
            var parts = new List<string>();
            foreach (string part in (left + " | " + right).Split(new[] { " | " }, StringSplitOptions.None))
                if (!parts.Contains(part)) parts.Add(part);
            return string.Join(" | ", parts);
        }

        private static IEnumerable<string> RepeatAny(int count)
        {
            for (int i = 0; i < count; i++) yield return "Any";
        }

        /// <summary>Draws the visible markers; called from the editor's WM_PAINT hook.</summary>
        public void DrawOverlay(Graphics g)
        {
            if (g == null || _disposed || _editor.IsDisposed) return;
            var markers = _markersByDocLine;
            if (markers == null || markers.Count == 0) return;

            int totalLines = _editor.Lines.Count;
            int firstVisible = _editor.FirstVisibleLine;
            int linesOnScreen = _editor.LinesOnScreen;
            if (totalLines <= 0 || linesOnScreen <= 0) return;
            int firstDoc, lastDoc;
            try
            {
                firstDoc = _editor.DocLineFromVisible(firstVisible);
                lastDoc = _editor.DocLineFromVisible(firstVisible + linesOnScreen);
            }
            catch { return; }

            string fontName = _editor.Styles[Style.Default].Font;
            if (string.IsNullOrWhiteSpace(fontName)) fontName = _editor.Font.FontFamily.Name;
            using (var font = new Font(fontName, MarkerFontSizeFor(_editor), FontStyle.Italic))
            {
                foreach (KeyValuePair<int, string> kv in markers)
                {
                    int docLine = kv.Key;
                    if (docLine < firstDoc || docLine > lastDoc || docLine >= totalLines) continue;
                    Point at;
                    if (!TryGetMarkerPoint(_editor, docLine, out at)) continue;
                    TextRenderer.DrawText(g, ": " + kv.Value, font, at, MarkerColor,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
        }

        /// <summary>The gap between the end of a line's text and the start of its marker.</summary>
        private const int MarkerGap = 14;

        /// <summary>
        /// Where a line's inlay marker is painted: just past the last character of that line's text.
        ///
        /// Both coordinates MUST come from the SAME position. With word wrap on,
        /// <see cref="Scintilla.PointXFromPosition"/> returns the X of a position *within its own
        /// wrapped row*, and <see cref="Scintilla.PointYFromPosition"/> returns that row's Y. Taking
        /// X from the line's END position while taking Y from the line's START position therefore
        /// paired the last row's (short) X with the FIRST row's Y, painting the marker straight
        /// through the source text — reported during testing as the inferred-type inlays being mangled
        /// into the code, and only in word-wrap view. With wrap off a line is a single row and both
        /// positions land on it, which is exactly why the defect was invisible there.
        ///
        /// Returns false when the line's metrics cannot be read (a transient failure mid-edit).
        /// </summary>
        internal static bool TryGetMarkerPoint(Scintilla editor, int docLine, out Point point)
        {
            point = Point.Empty;
            if (editor == null || editor.IsDisposed) return false;
            try
            {
                if (docLine < 0 || docLine >= editor.Lines.Count) return false;
                var line = editor.Lines[docLine];
                // EndPosition includes the line terminator; back over it so the marker sits after
                // the last visible character instead of a row further down.
                int endPos = line.EndPosition;
                while (endPos > line.Position)
                {
                    int c = editor.GetCharAt(endPos - 1);
                    if (c == '\n' || c == '\r') endPos--;
                    else break;
                }
                point = new Point(editor.PointXFromPosition(endPos) + MarkerGap,
                                  editor.PointYFromPosition(endPos));
                return true;
            }
            catch { return false; }
        }

        /// <summary>The font size of the style that actually paints source text.</summary>
        internal static float MarkerFontSizeFor(Scintilla editor)
        {
            if (editor == null) return 11f;
            int styled = editor.Styles[Style.Default].Size;
            return styled > 0 ? styled : editor.Font.Size;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _debounce.Stop(); _debounce.Dispose(); } catch { }
            Detach();
        }
    }
}
