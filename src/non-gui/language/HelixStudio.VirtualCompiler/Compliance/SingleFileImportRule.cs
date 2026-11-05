using System;
using System.Collections.Generic;
using System.Threading;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;
using HelixPythonNamespace.VirtualCompiler.Imports;

namespace HelixPythonNamespace.VirtualCompiler.Compliance
{
    /// <summary>
    /// Rule 5 (<c>PY8005</c>): a file opened on its own — outside any solution — may import
    /// installed packages and nothing else. Importing a module the user wrote is prohibited
    /// outright; the way to do that is to create a solution.
    ///
    /// <para>
    /// This is rule 1 (<c>PY8001</c>) restated for the one context rule 1 cannot reach. Rule 1 says
    /// executable source must be a workspace member, so that the IDE can show, compile-check and
    /// breakpoint everything it runs. A lone script has no workspace to be a member of, so rule 1
    /// has nothing to test — and without this rule, single-file mode would be a hole straight
    /// through the policy: put the modules next to the script, open the script alone, and the IDE
    /// runs a whole program it never parsed. The remedy is the same one rule 1 names, and it is the
    /// reason solutions exist: a Helix solution is what makes the other files visible.
    /// </para>
    ///
    /// <para>
    /// There are exactly three ways a lone script can reach code the user wrote, and the rule has
    /// one limb per way. Anything else an <c>import</c> can name is either installed (fine, and not
    /// this rule's business) or missing (fine too — that is <c>PY5002</c>, which says something
    /// entirely different and has its own wording).
    /// </para>
    ///
    /// <list type="number">
    /// <item><b>A relative import.</b> <c>from . import helpers</c> asks for a sibling inside the
    /// script's own package, and a lone script is not in a package at all. CPython answers this one
    /// with <c>ImportError: attempted relative import with no known parent package</c>, so the file
    /// could not run either way; the rule reports it as policy rather than letting the user find out
    /// at the traceback, and names the remedy CPython cannot.</item>
    ///
    /// <item><b>An absolute import that resolves inside the script's own directory.</b> CPython puts
    /// the running script's directory at <c>sys.path[0]</c>, which is precisely what makes
    /// <c>import helpers</c> bind the <c>helpers.py</c> sitting beside it. Resolution is done with
    /// the real <see cref="ModuleResolver"/> over a path of exactly that one directory, so this limb
    /// agrees with the interpreter on packages, namespace packages and compiled extensions rather
    /// than approximating them with a file-exists check.</item>
    ///
    /// <item><b>A <c>sys.path</c> mutation.</b> <c>sys.path.insert(0, "...")</c> is not an import,
    /// but it is the mechanism by which one becomes possible, and it is the exact anti-pattern rule
    /// 1 exists to kill — "do not fix PY8001 by putting the directory on the path" is in that rule's
    /// own remedy text. Without this limb the other two are a formality: two lines of setup make any
    /// directory on the machine importable, and the IDE is back to running code it cannot show.
    /// Reported wherever it appears, including inside a function, because it is reachable from
    /// anywhere the script runs.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Imports are collected from the WHOLE file, not just module level.</b> The import checker
    /// walks only the top level, correctly — it models what happens at import time. This rule is
    /// about what the program is allowed to reach, and <c>def load(): import helpers</c> reaches it
    /// just as surely, one call later. That includes imports inside a <c>try</c>, which the import
    /// checker deliberately treats as guarded: an optional dependency is a legitimate shape for an
    /// installed package and an irrelevant one for a file the user wrote next door.
    /// </para>
    ///
    /// <para>
    /// The rule pairs with a launch guarantee it cannot make alone: a single-file run must be
    /// started with an EMPTY <c>PYTHONPATH</c>. The three limbs enumerate every route into
    /// user-written code <i>given</i> that <c>sys.path</c> is the script's directory plus the
    /// installation. An inherited <c>PYTHONPATH</c> would add a fourth route this rule cannot see,
    /// and the guarantee would be false. See the single-file launch path in the IDE host.
    /// </para>
    ///
    /// <para>
    /// <b>What this is not.</b> It is a static rule over the shapes user-written imports actually
    /// take, not a sandbox. <c>importlib.import_module(name)</c>, <c>__import__</c> and <c>exec</c>
    /// take a string computed at runtime, and no static check can say what that string will be
    /// without becoming a source of false blockers on correct code that loads an installed plugin
    /// by name. Rule 1 has the same boundary for the same reason and has always had it: the rules
    /// make the supported way the easy way and name the remedy, they do not make the unsupported
    /// way impossible. A user who reaches for <c>importlib</c> to dodge this has been told twice
    /// what to do instead.
    /// </para>
    /// </summary>
    public static class SingleFileImportRule
    {
        /// <summary>The one authority for the code, so callers do not spell it out.</summary>
        public const string DiagnosticCode = "PY8005";

        private const string Remedy =
            "A file opened on its own may import installed packages only. To use your own modules, "
            + "create a solution (File > New > Solution) and put them in a project — that is "
            + "what lets the IDE show, compile-check and debug the code it runs.";

        /// <summary>
        /// Every violation in <paramref name="module"/>, in source order.
        /// </summary>
        /// <param name="scriptPath">
        /// The file being checked. Its DIRECTORY is the whole of the search path limb 2 resolves
        /// against — that directory is what CPython puts at <c>sys.path[0]</c>, and modelling it as
        /// exactly one root is what keeps this rule's answer identical to the interpreter's.
        /// </param>
        public static IReadOnlyList<CompileDiagnostic> FindViolations(
            Module module, string scriptPath, CancellationToken cancel = default(CancellationToken))
        {
            var found = new List<CompileDiagnostic>();
            if (module == null) return found;

            string scriptDir = SafeDirectory(scriptPath);
            // An unsaved buffer has no directory, so limb 2 has nothing to resolve against and is
            // skipped — not failed. The other two limbs need no path at all and still apply, which
            // is what keeps a brand-new untitled file honest before its first save.
            ModuleResolver resolver = string.IsNullOrEmpty(scriptDir)
                ? null
                : new ModuleResolver(PythonSearchPath.FromRoots(new[] { scriptDir }));

            Walk(module.Body, scriptPath ?? string.Empty, scriptDir, resolver, found, cancel);
            return found;
        }

        // ---- Traversal --------------------------------------------------------

        private static void Walk(IReadOnlyList<Stmt> body, string filePath, string scriptDir,
            ModuleResolver resolver, List<CompileDiagnostic> into, CancellationToken cancel)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                cancel.ThrowIfCancellationRequested();
                switch (s)
                {
                    case Import imp:
                        foreach (Alias a in imp.Names)
                            CheckAbsolute(a.Name, imp.Span, filePath, scriptDir, resolver, into, cancel);
                        break;

                    case ImportFrom imf:
                        if (imf.Level > 0) ReportRelative(imf, filePath, into);
                        else CheckAbsolute(imf.Module, imf.Span, filePath, scriptDir, resolver, into, cancel);
                        break;

                    case ExprStmt expr:
                        CheckSysPathMutation(expr, filePath, into);
                        break;

                    case Assign assign:
                        CheckSysPathAssignment(assign, filePath, into);
                        break;

                    case AugAssign aug:
                        // `sys.path += [...]` — an assignment by another name, and the shape a
                        // reader is most likely to reach for once `.insert(...)` is rejected.
                        if (IsSysPath(aug.Target)) ReportPathMutation(aug.Span, filePath, into);
                        break;

                    // Everything below merely contains statements. An import nested inside any of
                    // them is still an import the running script can reach.
                    case FunctionDef fn: Walk(fn.Body, filePath, scriptDir, resolver, into, cancel); break;
                    case AsyncFunctionDef afn: Walk(afn.Body, filePath, scriptDir, resolver, into, cancel); break;
                    case ClassDef cls: Walk(cls.Body, filePath, scriptDir, resolver, into, cancel); break;
                    case If iff:
                        Walk(iff.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(iff.OrElse, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case For fr:
                        Walk(fr.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(fr.OrElse, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case AsyncFor afr:
                        Walk(afr.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(afr.OrElse, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case While wh:
                        Walk(wh.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(wh.OrElse, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case With wi: Walk(wi.Body, filePath, scriptDir, resolver, into, cancel); break;
                    case AsyncWith aw: Walk(aw.Body, filePath, scriptDir, resolver, into, cancel); break;
                    case Try tr:
                        Walk(tr.Body, filePath, scriptDir, resolver, into, cancel);
                        foreach (ExceptHandler h in tr.Handlers)
                            Walk(h.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(tr.OrElse, filePath, scriptDir, resolver, into, cancel);
                        Walk(tr.FinalBody, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case TryStar ts:
                        Walk(ts.Body, filePath, scriptDir, resolver, into, cancel);
                        foreach (ExceptHandler h in ts.Handlers)
                            Walk(h.Body, filePath, scriptDir, resolver, into, cancel);
                        Walk(ts.OrElse, filePath, scriptDir, resolver, into, cancel);
                        Walk(ts.FinalBody, filePath, scriptDir, resolver, into, cancel);
                        break;
                    case Match mt:
                        foreach (MatchCase c in mt.Cases)
                            Walk(c.Body, filePath, scriptDir, resolver, into, cancel);
                        break;
                }
            }
        }

        // ---- Limb 1: relative imports -----------------------------------------

        private static void ReportRelative(ImportFrom node, string filePath, List<CompileDiagnostic> into)
        {
            string spelled = new string('.', node.Level) + (node.Module ?? string.Empty);
            into.Add(Diagnostic(filePath, node.Span,
                "Relative import '" + spelled + "' is not available to a file opened on its own: "
                + "it names a module in this file's own package, and a lone file is not in a package. "
                + Remedy));
        }

        // ---- Limb 2: a module that lives beside the script ---------------------

        private static void CheckAbsolute(string dottedName, HelixPythonNamespace.Python.Text.SourceSpan span,
            string filePath, string scriptDir, ModuleResolver resolver,
            List<CompileDiagnostic> into, CancellationToken cancel)
        {
            if (resolver == null || string.IsNullOrEmpty(dottedName)) return;

            // Only the TOP-level name can be satisfied by the script's own directory; everything
            // under it comes along with whatever that name bound to. Reporting `pkg.sub.mod` would
            // also point the user at a name they cannot act on — `pkg` is the thing beside them.
            string top = KnownExternalModules.TopLevelOf(dottedName);
            if (string.IsNullOrEmpty(top)) return;

            ResolvedModule hit;
            try { hit = resolver.Resolve(top, cancel); }
            catch (OperationCanceledException) { throw; }
            catch { return; }

            // NotFound: nothing of that name sits beside the script, so this is an installed
            // package or a missing one. ExternalOpaque: the resolver fell through to the known
            // stdlib/ecosystem list, which is the same answer. Neither is this rule's business.
            if (!hit.Found || hit.Kind == ModuleKind.ExternalOpaque) return;

            into.Add(Diagnostic(filePath, span,
                "'" + top + "' is a module you wrote (" + Describe(hit, scriptDir) + "), and a file "
                + "opened on its own may not import one. " + Remedy));
        }

        /// <summary>Where the offending module was found, relative to the script, for the message.</summary>
        private static string Describe(ResolvedModule hit, string scriptDir)
        {
            if (hit.Kind == ModuleKind.NamespacePackage)
                return "a folder beside this file, with no __init__.py";

            string location = hit.Location;
            if (string.IsNullOrEmpty(location)) return "beside this file";
            try
            {
                string name = System.IO.Path.GetFileName(
                    HelixPythonNamespace.Core.IO.PathComparison.TrimTrailing(location));
                string parent = System.IO.Path.GetDirectoryName(location);
                bool sameFolder = !string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(scriptDir)
                    && HelixPythonNamespace.Core.IO.PathComparison.AreSame(parent, scriptDir);
                return sameFolder ? name + " beside this file" : location;
            }
            catch { return location; }
        }

        // ---- Limb 3: sys.path mutation ----------------------------------------

        private static readonly HashSet<string> MutatingMethods =
            new HashSet<string>(StringComparer.Ordinal) { "insert", "append", "extend", "__setitem__" };

        private static void CheckSysPathMutation(ExprStmt statement, string filePath,
            List<CompileDiagnostic> into)
        {
            if (!(statement.Value is Call call)) return;
            if (!(call.Func is AttributeExpr method)) return;
            if (!MutatingMethods.Contains(method.Attr ?? string.Empty)) return;
            if (!IsSysPath(method.Value)) return;
            ReportPathMutation(statement.Span, filePath, into);
        }

        private static void CheckSysPathAssignment(Assign assign, string filePath,
            List<CompileDiagnostic> into)
        {
            foreach (Expr target in assign.Targets)
            {
                // `sys.path = [...]` and `sys.path[0] = ...` are both replacements of the path.
                if (IsSysPath(target) || (target is Subscript sub && IsSysPath(sub.Value)))
                {
                    ReportPathMutation(assign.Span, filePath, into);
                    return;
                }
            }
        }

        /// <summary>
        /// True for the expression <c>sys.path</c>, spelled out.
        ///
        /// Deliberately NOT a bare <c>path</c> bound by <c>from sys import path</c>, even though
        /// that is the same list object. <c>path</c> is one of the most common local variable names
        /// in Python, and <c>path.insert(0, x)</c> on somebody's own list would then be a hard
        /// blocker on a correct line. Matching the spelled-out attribute is a judgement about which
        /// error is worse, not an oversight.
        /// </summary>
        private static bool IsSysPath(Expr expr)
        {
            if (expr is AttributeExpr attr)
            {
                return string.Equals(attr.Attr, "path", StringComparison.Ordinal)
                    && attr.Value is Name owner
                    && string.Equals(owner.Id, "sys", StringComparison.Ordinal);
            }
            return false;
        }

        private static void ReportPathMutation(HelixPythonNamespace.Python.Text.SourceSpan span,
            string filePath, List<CompileDiagnostic> into)
        {
            into.Add(Diagnostic(filePath, span,
                "Changing sys.path makes code the IDE cannot see importable, which is what this "
                + "rule exists to prevent — it is not a way around it. " + Remedy));
        }

        // ---- Shared -----------------------------------------------------------

        private static CompileDiagnostic Diagnostic(string filePath,
            HelixPythonNamespace.Python.Text.SourceSpan span, string message)
        {
            return new CompileDiagnostic(filePath, DiagnosticCode, ParseSeverity.Error, span, message);
        }

        private static string SafeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch { return null; }
        }
    }
}
