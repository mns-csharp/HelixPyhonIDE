using System;
using System.Collections.Generic;
using System.Threading;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Imports
{
    /// <summary>
    /// Compile-time import resolution: replays every <c>import</c> / <c>from … import …</c> against
    /// the <see cref="ModuleResolver"/> and reports the exact failure the interpreter would raise,
    /// with CPython's own wording, before the program is ever run.
    ///
    /// This closes the gap that let a solution compile clean and then die at runtime with
    /// <c>ImportError: cannot import name 'TsvPreparationWorkflow' from 'sample_common'
    /// (unknown location)</c>. The previous passes only ever checked names WITHIN a file; nothing
    /// modelled <c>sys.path</c>, so a packaging fault — a namespace package shadowing the real one,
    /// a project missing from the search path, a name that is not actually re-exported — was
    /// invisible until the process started.
    ///
    /// The pass is deliberately conservative: it stays silent whenever it cannot see the truth
    /// (stdlib/site-packages modules, star-imported or <c>__getattr__</c>-backed modules, compiled
    /// extensions, imports guarded by <c>try</c>/<c>except ImportError</c>, any import written
    /// inside a function or class body, and — most importantly — any file that is downstream of a
    /// runtime <c>sys.path</c> mutation). A false "no module named" on valid scientific code is far
    /// more costly than a missed report, so every uncertain case resolves in the code's favour.
    ///
    /// Measured on the repository's Python fixture corpus — 11 solutions, 360 files compiled with
    /// each solution's real workspace members as the project set — the pass reports zero
    /// diagnostics. It got there the hard way: the first version emitted 807 findings, every one a
    /// false positive, and each conservatism rule above corresponds to one class that measurement
    /// exposed. Re-run that sweep before loosening any of them.
    /// </summary>
    public sealed class ImportChecker
    {
        /// <summary>
        /// How far to chase a <c>sys.path</c> bootstrap through the import graph. Two hops covers
        /// the usual shape — <c>import pkg</c> → <c>pkg/__init__.py</c> → <c>from . import _paths</c>
        /// — without turning the check into a whole-graph traversal.
        /// </summary>
        private const int SysPathBootstrapDepth = 2;

        private readonly string _filePath;
        private readonly ModuleResolver _resolver;
        private readonly List<CompileDiagnostic> _diagnostics = new List<CompileDiagnostic>();
        private readonly string _importerFqn;
        private readonly bool _importerIsPackage;
        private readonly string _importerDir;
        private readonly CancellationToken _cancel;

        public ImportChecker(string filePath, ModuleResolver resolver,
            CancellationToken cancel = default(CancellationToken))
        {
            _filePath = filePath ?? string.Empty;
            _resolver = resolver;
            _cancel = cancel;
            _importerFqn = resolver != null ? resolver.SearchPath.ModuleFqnOf(_filePath) : string.Empty;
            _importerIsPackage = PythonSearchPath.IsPackageInit(_filePath);
            try
            {
                _importerDir = string.IsNullOrEmpty(_filePath)
                    ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_filePath));
            }
            catch { _importerDir = null; }
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        /// <param name="source">
        /// The importing file's own text, used only to detect that it rewrites <c>sys.path</c>
        /// itself. Optional; omitting it just costs the self-mutation check.
        /// </param>
        public void Check(Module module, string source = null)
        {
            if (module == null || _resolver == null) return;
            if (IsDownstreamOfSearchPathMutation(module, source)) return;
            // Only module-level imports are checked — see the class remarks on deferred imports.
            WalkTopLevel(module.Body, false);
        }

        // A file that rewrites sys.path — or imports anything that does — resolves its later
        // imports against a path this compiler cannot reconstruct. The honest answer is silence.
        private bool IsDownstreamOfSearchPathMutation(Module module, string source)
        {
            if (source != null)
            {
                ModuleResolver.ModuleFactsView self =
                    ModuleResolver.FactsOfSource(source, _filePath, _cancel);
                if (self.TouchesSearchPath) return true;
            }

            foreach (string target in ModuleLevelImportTargets(module))
            {
                string absolute = target;
                if (target.Length > 0 && target[0] == '.')
                {
                    bool escapes;
                    int level = 0;
                    while (level < target.Length && target[level] == '.') level++;
                    absolute = ResolveRelative(target.Substring(level), level, out escapes);
                    if (escapes || string.IsNullOrEmpty(absolute)) continue;
                }
                if (_resolver.ReachesSearchPathMutation(absolute, SysPathBootstrapDepth,
                        _importerDir, _cancel))
                    return true;
            }
            return false;
        }

        private static List<string> ModuleLevelImportTargets(Module module)
        {
            List<string> targets = new List<string>();
            Collect(module.Body, targets);
            return targets;

            void Collect(IReadOnlyList<Stmt> body, List<string> outList)
            {
                if (body == null) return;
                foreach (Stmt s in body)
                {
                    switch (s)
                    {
                        case Import imp:
                            foreach (Alias a in imp.Names)
                                if (!string.IsNullOrEmpty(a.Name)) outList.Add(a.Name);
                            break;
                        case ImportFrom imf:
                            // The imported NAMES matter as much as the module: `from . import _paths`
                            // puts the bootstrap module in the alias list, not the module spec.
                            string prefix = imf.Level > 0
                                ? new string('.', imf.Level) + (imf.Module ?? string.Empty)
                                : (imf.Module ?? string.Empty);
                            if (prefix.Length > 0) outList.Add(prefix);
                            foreach (Alias a in imf.Names)
                            {
                                if (string.IsNullOrEmpty(a.Name) || a.Name == "*") continue;
                                bool needsDot = prefix.Length > 0 && prefix[prefix.Length - 1] != '.';
                                outList.Add(prefix + (needsDot ? "." : string.Empty) + a.Name);
                            }
                            break;
                        case If iff: Collect(iff.Body, outList); Collect(iff.OrElse, outList); break;
                        case Try tr:
                            Collect(tr.Body, outList);
                            foreach (ExceptHandler h in tr.Handlers) Collect(h.Body, outList);
                            Collect(tr.OrElse, outList);
                            Collect(tr.FinalBody, outList);
                            break;
                    }
                }
            }
        }

        // `guarded` propagates through try/except bodies: an import inside one is an intentional
        // optional dependency (`try: import ujson as json / except ImportError: import json`).
        private void WalkTopLevel(IReadOnlyList<Stmt> body, bool guarded)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                _cancel.ThrowIfCancellationRequested();
                switch (s)
                {
                    case Import imp:
                        if (!guarded) CheckImport(imp);
                        break;
                    case ImportFrom imf:
                        if (!guarded) CheckImportFrom(imf);
                        break;
                    case If iff:
                        WalkTopLevel(iff.Body, guarded);
                        WalkTopLevel(iff.OrElse, guarded);
                        break;
                    case Try tr:
                        WalkTopLevel(tr.Body, true);
                        foreach (ExceptHandler h in tr.Handlers) WalkTopLevel(h.Body, true);
                        WalkTopLevel(tr.OrElse, true);
                        WalkTopLevel(tr.FinalBody, true);
                        break;
                    case TryStar trs:
                        WalkTopLevel(trs.Body, true);
                        foreach (ExceptHandler h in trs.Handlers) WalkTopLevel(h.Body, true);
                        WalkTopLevel(trs.OrElse, true);
                        WalkTopLevel(trs.FinalBody, true);
                        break;
                    case With wi:
                        WalkTopLevel(wi.Body, guarded);
                        break;
                    case AsyncWith aw:
                        WalkTopLevel(aw.Body, guarded);
                        break;
                }
            }
        }

        // Every lookup goes through the importer's own directory first — see
        // ModuleResolver.ResolveFrom on why sys.path[0] has to be modelled per file.
        private ResolvedModule ResolveForImporter(string dottedName)
        {
            return _resolver.ResolveFrom(_importerDir, dottedName, _cancel);
        }

        private void CheckImport(Import node)
        {
            foreach (Alias a in node.Names)
            {
                if (string.IsNullOrEmpty(a.Name)) continue;
                ResolvedModule m = ResolveForImporter(a.Name);
                if (!m.Found) ReportModuleNotFound(m, a.Span);
            }
        }

        private void CheckImportFrom(ImportFrom node)
        {
            string target;
            // A relative import resolves strictly inside its own package — sys.path[0] plays no
            // part in it, so the importer-directory shortcut must not apply.
            bool isRelative = node.Level > 0;
            if (isRelative)
            {
                bool escapes;
                target = ResolveRelative(node.Module ?? string.Empty, node.Level, out escapes);
                if (escapes)
                {
                    Report(VirtualCompilerDiagnosticCodes.ImportRelativeBeyondTopLevel, node.Span,
                        "attempted relative import beyond top-level package");
                    return;
                }
                // Outside any known root the importer's own package is unknown, so a relative
                // import cannot be judged at all.
                if (string.IsNullOrEmpty(target)) return;
            }
            else
            {
                target = node.Module;
                if (string.IsNullOrEmpty(target)) return;
            }

            ResolvedModule module = isRelative
                ? _resolver.Resolve(target, _cancel)
                : ResolveForImporter(target);
            if (!module.Found)
            {
                ReportModuleNotFound(module, node.Span);
                return;
            }

            ModuleExports exports = _resolver.ExportsOf(module, _cancel);
            if (exports.Opaque) return;

            foreach (Alias a in node.Names)
            {
                if (string.IsNullOrEmpty(a.Name) || a.Name == "*") continue;
                if (exports.Names.Contains(a.Name)) continue;

                // `from pkg import sub` also succeeds when `sub` is a submodule that the package's
                // __init__ never imports — CPython's _handle_fromlist falls back to importing it.
                if (module.IsPackage
                    && (isRelative
                        ? _resolver.Resolve(target + "." + a.Name, _cancel)
                        : ResolveForImporter(target + "." + a.Name)).Found) continue;

                Report(VirtualCompilerDiagnosticCodes.ImportNameNotFound, a.Span,
                    "cannot import name '" + a.Name + "' from '" + target + "' ("
                    + module.LocationDisplay + ")");
            }
        }

        // Reports the first missing level, the way CPython does: `import a.b.c` with no `a` on the
        // path raises "No module named 'a'", not "No module named 'a.b.c'".
        private void ReportModuleNotFound(ResolvedModule m, HelixPythonNamespace.Python.Text.SourceSpan span)
        {
            string missing = string.IsNullOrEmpty(m.FailedPrefix) ? m.Name : m.FailedPrefix;
            Report(VirtualCompilerDiagnosticCodes.ImportModuleNotFound, span,
                "No module named '" + missing + "'");
        }

        /// <summary>
        /// CPython's <c>importlib._bootstrap._resolve_name</c>: turn a relative import into an
        /// absolute dotted name, given the importing module's own position.
        /// </summary>
        private string ResolveRelative(string moduleSpec, int level, out bool escapes)
        {
            escapes = false;
            if (string.IsNullOrEmpty(_importerFqn)) return string.Empty;

            // `__package__` is the module's own FQN for a package's __init__, else its parent.
            string package = _importerFqn;
            if (!_importerIsPackage)
            {
                int lastDot = package.LastIndexOf('.');
                package = lastDot < 0 ? string.Empty : package.Substring(0, lastDot);
            }

            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(package)) parts.AddRange(package.Split('.'));
            if (parts.Count < level) { escapes = true; return string.Empty; }
            parts.RemoveRange(parts.Count - (level - 1), level - 1);

            string baseFqn = string.Join(".", parts);
            if (string.IsNullOrEmpty(moduleSpec)) return baseFqn;
            if (string.IsNullOrEmpty(baseFqn)) return moduleSpec;
            return baseFqn + "." + moduleSpec;
        }

        private void Report(string code, HelixPythonNamespace.Python.Text.SourceSpan span, string message)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, ParseSeverity.Error, span, message));
        }
    }
}
