using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Services
{
    public enum ParamKind
    {
        Regular,
        PositionalOnly,
        KeywordOnly,
        VarPositional,  // *args — editable name/annotation only
        VarKeyword,     // **kwargs — editable name/annotation only
    }

    public struct ParameterEdit
    {
        public string OldName;       // null for a newly-added parameter
        public string NewName;
        public string Annotation;    // may be null/empty
        public string DefaultValue;  // may be null/empty
        public ParamKind Kind;
    }

    public struct SignatureEditResult
    {
        public int FilesChanged;
        public int CallsRewritten;
        public List<string> Warnings;
    }

    /// <summary>
    /// Rewrites a Python function's signature (def site) and, across the chosen
    /// scope, rewrites every matching call site's argument list to match the new
    /// parameter order / names / removals.
    ///
    /// Known limitations (v1):
    /// - Methods (functions inside a class body) are supported syntactically but
    ///   override-cascading across subclasses is not performed.
    /// - Calls with <c>*args</c> / <c>**kwargs</c> splats get a warning and are
    ///   left untouched.
    /// - Structural markers (<c>/</c>, <c>*</c>) between parameter-kind groups are
    ///   re-derived from the edited kind sequence.
    /// </summary>
    public sealed class SignatureEditService
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public struct ComputedSignatureEdit
        {
            public List<FileTextEdit> Edits;
            public int CallsRewritten;
            public List<string> Warnings;
        }

        public ComputedSignatureEdit Compute(
            SolutionModel solution, ProjectModel owningProject,
            string declaringFile, int defLine, string functionName,
            IReadOnlyList<ParameterEdit> newParams, string newReturnAnnotation,
            RenameScope scope)
        {
            ComputedSignatureEdit result = default;
            result.Edits = new List<FileTextEdit>();
            result.Warnings = new List<string>();

            if (solution == null || string.IsNullOrEmpty(declaringFile) ||
                !File.Exists(declaringFile) || string.IsNullOrEmpty(functionName) ||
                newParams == null)
                return result;

            string source;
            try { source = File.ReadAllText(declaringFile); }
            catch { return result; }

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source, declaringFile).Module; }
            catch { return result; }
            if (module == null) return result;

            FunctionDefSyntax target = FindFunctionAt(module, defLine, functionName);
            if (target == null) return result;

            // --- Def-site rewrite ---
            List<ParameterSyntax> oldParams = new List<ParameterSyntax>(target.Parameters);
            string newHeader = BuildHeader(target, newParams, newReturnAnnotation);
            int headerStart = target.Span.Start.Offset;
            int headerEnd = FindHeaderEnd(source, target);
            if (headerEnd <= headerStart) return result;

            StringBuilder sb = new StringBuilder(source.Length);
            sb.Append(source, 0, headerStart);
            sb.Append(newHeader);
            sb.Append(source, headerEnd, source.Length - headerEnd);
            string defRewritten = sb.ToString();

            result.Edits.Add(new FileTextEdit(declaringFile, source, defRewritten));

            // --- Call-site rewrite across scope ---
            string fullDeclaringPath = Path.GetFullPath(declaringFile);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullDeclaringPath };

            foreach (string f in EnumerateScopeFiles(solution, owningProject, declaringFile, scope))
            {
                string full = Path.GetFullPath(f);
                if (!seen.Add(full)) continue;

                string fileSrc;
                try { fileSrc = File.ReadAllText(full); }
                catch { continue; }

                var rewritten = RewriteCallsInFile(fileSrc, functionName, oldParams, newParams, full, result.Warnings, ref result.CallsRewritten);
                if (rewritten == null || rewritten == fileSrc) continue;
                result.Edits.Add(new FileTextEdit(full, fileSrc, rewritten));
            }

            // Also rewrite calls within the declaring file (the def-rewrite text is our starting point now).
            var declRewritten = RewriteCallsInFile(defRewritten, functionName, oldParams, newParams,
                fullDeclaringPath, result.Warnings, ref result.CallsRewritten);
            if (declRewritten != null && declRewritten != defRewritten)
            {
                // Replace the already-added declaring-file edit with one that also has call rewrites.
                for (int i = 0; i < result.Edits.Count; i++)
                {
                    if (string.Equals(result.Edits[i].Path, declaringFile, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Edits[i] = new FileTextEdit(declaringFile, source, declRewritten);
                        break;
                    }
                }
            }

            return result;
        }

        public SignatureEditResult Apply(ComputedSignatureEdit c)
        {
            SignatureEditResult r = default;
            r.Warnings = c.Warnings;
            foreach (FileTextEdit e in c.Edits)
            {
                try { File.WriteAllText(e.Path, e.NewText, Utf8NoBom); r.FilesChanged++; } catch { }
            }
            r.CallsRewritten = c.CallsRewritten;
            return r;
        }

        // ---- def-site helpers ------------------------------------------------

        private static FunctionDefSyntax FindFunctionAt(ModuleSyntax module, int defLine, string name)
        {
            FunctionDefSyntax bestMatch = null;
            foreach (SyntaxNode n in EnumerateAll(module))
            {
                if (!(n is FunctionDefSyntax fd)) continue;
                if (!string.Equals(fd.Name, name, StringComparison.Ordinal)) continue;
                int line = fd.Span.Start.Line;
                if (defLine <= 0 || line == defLine || line + 1 == defLine || line - 1 == defLine)
                    return fd;
                if (bestMatch == null) bestMatch = fd;
            }
            return bestMatch;
        }

        /// <summary>
        /// Find the byte offset just AFTER the <c>:</c> that ends the function header,
        /// so we can splice everything from <c>def</c>…<c>)</c>:...<c>:</c> in one shot.
        /// </summary>
        private static int FindHeaderEnd(string source, FunctionDefSyntax fd)
        {
            // Walk tokens from fd.Span.Start to find the first ':' at bracket depth 0.
            IReadOnlyList<Token> tokens;
            try { tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source); }
            catch { return -1; }
            int startOff = fd.Span.Start.Offset;
            int depth = 0;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                switch (t.Kind)
                {
                    case TokenKind.LeftParen:
                    case TokenKind.LeftBracket:
                    case TokenKind.LeftBrace:
                        depth++; break;
                    case TokenKind.RightParen:
                    case TokenKind.RightBracket:
                    case TokenKind.RightBrace:
                        if (depth > 0) depth--;
                        break;
                    case TokenKind.Colon:
                        if (depth == 0) return t.Span.End.Offset;
                        break;
                }
            }
            return -1;
        }

        private static string BuildHeader(
            FunctionDefSyntax target,
            IReadOnlyList<ParameterEdit> newParams,
            string newReturnAnnotation)
        {
            StringBuilder sb = new StringBuilder();
            if (target.IsAsync) sb.Append("async ");
            sb.Append("def ").Append(target.Name).Append('(');

            bool needSlash = false;
            bool needStar = false;
            bool starInserted = false;
            bool slashInserted = false;

            // Track whether we saw any PositionalOnly to decide if a '/' belongs.
            bool sawPositionalOnly = false;
            foreach (ParameterEdit pe in newParams)
                if (pe.Kind == ParamKind.PositionalOnly) { sawPositionalOnly = true; break; }
            needSlash = sawPositionalOnly;

            bool sawKeywordOnly = false;
            foreach (ParameterEdit pe in newParams)
                if (pe.Kind == ParamKind.KeywordOnly) { sawKeywordOnly = true; break; }
            // `*` is only needed when there's no VarPositional — otherwise *args serves the role.
            bool hasVarPositional = false;
            foreach (ParameterEdit pe in newParams)
                if (pe.Kind == ParamKind.VarPositional) { hasVarPositional = true; break; }
            needStar = sawKeywordOnly && !hasVarPositional;

            bool first = true;
            for (int i = 0; i < newParams.Count; i++)
            {
                ParameterEdit pe = newParams[i];

                // Emit `/` when transitioning out of PositionalOnly.
                if (!slashInserted && needSlash && pe.Kind != ParamKind.PositionalOnly)
                {
                    if (!first) sb.Append(", ");
                    sb.Append('/');
                    first = false;
                    slashInserted = true;
                }
                // Emit `*` when transitioning into KeywordOnly without a VarPositional.
                if (!starInserted && needStar && pe.Kind == ParamKind.KeywordOnly)
                {
                    if (!first) sb.Append(", ");
                    sb.Append('*');
                    first = false;
                    starInserted = true;
                }

                if (!first) sb.Append(", ");
                first = false;

                if (pe.Kind == ParamKind.VarPositional) sb.Append('*');
                else if (pe.Kind == ParamKind.VarKeyword) sb.Append("**");

                sb.Append(pe.NewName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(pe.Annotation)) sb.Append(": ").Append(pe.Annotation);
                if (!string.IsNullOrWhiteSpace(pe.DefaultValue))
                {
                    sb.Append(!string.IsNullOrWhiteSpace(pe.Annotation) ? " = " : "=");
                    sb.Append(pe.DefaultValue);
                }
            }

            // Final `/` if every visible parameter was PositionalOnly.
            if (!slashInserted && needSlash)
            {
                if (newParams.Count > 0) sb.Append(", ");
                sb.Append('/');
            }

            sb.Append(')');
            if (!string.IsNullOrWhiteSpace(newReturnAnnotation))
                sb.Append(" -> ").Append(newReturnAnnotation);
            sb.Append(':');
            return sb.ToString();
        }

        // ---- call-site rewriting ---------------------------------------------

        private string RewriteCallsInFile(
            string source, string functionName,
            IReadOnlyList<ParameterSyntax> oldParams, IReadOnlyList<ParameterEdit> newParams,
            string filePath, List<string> warnings, ref int callsRewritten)
        {
            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath).Module; }
            catch { return null; }
            if (module == null) return null;

            // Collect every CallExpression whose callee resolves to functionName.
            List<CallExpression> calls = new List<CallExpression>();
            foreach (SyntaxNode n in EnumerateAll(module))
            {
                if (!(n is CallExpression ce)) continue;
                string cName;
                if (ce.Callee is NameExpression ne) cName = ne.Name;
                else if (ce.Callee is AttributeExpression ae) cName = ae.Name;
                else continue;
                if (string.Equals(cName, functionName, StringComparison.Ordinal))
                    calls.Add(ce);
            }
            if (calls.Count == 0) return null;

            // Map old-name → new-name / kind / default for quick lookup.
            Dictionary<string, ParameterEdit> oldToNew = new Dictionary<string, ParameterEdit>(StringComparer.Ordinal);
            HashSet<string> keptOldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ParameterEdit pe in newParams)
            {
                if (!string.IsNullOrEmpty(pe.OldName))
                {
                    oldToNew[pe.OldName] = pe;
                    keptOldNames.Add(pe.OldName);
                }
            }

            // Process calls from LAST to FIRST (splicing preserves earlier offsets).
            calls.Sort((a, b) => b.Span.Start.Offset.CompareTo(a.Span.Start.Offset));

            StringBuilder output = new StringBuilder(source);
            foreach (CallExpression ce in calls)
            {
                bool hasSplat = false;
                foreach (CallArgument a in ce.Arguments)
                {
                    if (a.Kind == CallArgument.ArgKind.Star || a.Kind == CallArgument.ArgKind.DoubleStar)
                    { hasSplat = true; break; }
                }
                if (hasSplat)
                {
                    warnings.Add(filePath + ":" + ce.Span.Start.Line + ": call uses *args/**kwargs; not rewritten");
                    continue;
                }

                // Gather old args keyed by old param name (positional by index).
                Dictionary<string, string> providedByName = new Dictionary<string, string>(StringComparer.Ordinal);
                int posIndex = 0;
                foreach (CallArgument a in ce.Arguments)
                {
                    string valueText = SliceSpan(source, a.Value?.Span);
                    if (valueText == null) continue;
                    if (a.Kind == CallArgument.ArgKind.Keyword && !string.IsNullOrEmpty(a.Name))
                    {
                        providedByName[a.Name] = valueText;
                    }
                    else if (a.Kind == CallArgument.ArgKind.Positional)
                    {
                        if (posIndex < oldParams.Count)
                            providedByName[oldParams[posIndex].Name] = valueText;
                        posIndex++;
                    }
                }

                // Build the new argument list in new-param order.
                StringBuilder rebuilt = new StringBuilder();
                bool firstArg = true;
                bool anyKeyword = false; // once we start emitting kwargs we must keep using kwargs
                for (int i = 0; i < newParams.Count; i++)
                {
                    ParameterEdit pe = newParams[i];
                    if (pe.Kind == ParamKind.VarPositional || pe.Kind == ParamKind.VarKeyword) continue; // splat params — no explicit arg

                    string provided = null;
                    if (!string.IsNullOrEmpty(pe.OldName) && providedByName.TryGetValue(pe.OldName, out var v))
                        provided = v;

                    if (provided == null)
                    {
                        // No value to emit; if the new param has no default, warn.
                        if (string.IsNullOrWhiteSpace(pe.DefaultValue) && !string.IsNullOrEmpty(pe.NewName))
                        {
                            warnings.Add(filePath + ":" + ce.Span.Start.Line + ": new required parameter '" + pe.NewName + "' has no default; call may break");
                        }
                        continue;
                    }

                    if (!firstArg) rebuilt.Append(", ");
                    // Use keyword form when the new param is keyword-only or once we switched to keywords.
                    bool emitAsKw = anyKeyword || pe.Kind == ParamKind.KeywordOnly;
                    if (emitAsKw)
                    {
                        rebuilt.Append(pe.NewName).Append('=').Append(provided);
                        anyKeyword = true;
                    }
                    else
                    {
                        rebuilt.Append(provided);
                    }
                    firstArg = false;
                }

                // Splice: replace everything inside the parentheses of the call.
                int openParen = FindParenAfter(source, ce.Callee?.Span.End.Offset ?? ce.Span.Start.Offset);
                int closeParen = ce.Span.End.Offset - 1; // ')' is the last char of CallExpression span
                if (openParen < 0 || closeParen <= openParen)
                {
                    warnings.Add(filePath + ":" + ce.Span.Start.Line + ": could not locate argument parentheses");
                    continue;
                }

                // Replace between openParen+1 ... closeParen exclusive.
                output.Remove(openParen + 1, closeParen - openParen - 1);
                output.Insert(openParen + 1, rebuilt.ToString());
                callsRewritten++;
            }

            string newSource = output.ToString();
            return newSource == source ? null : newSource;
        }

        private static int FindParenAfter(string source, int startOffset)
        {
            for (int i = startOffset; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '(') return i;
                if (c == '\n' || c == '\r') return -1; // defensive: no '(' on this line
            }
            return -1;
        }

        private static string SliceSpan(string source, SourceSpan? span)
        {
            if (!span.HasValue) return null;
            SourceSpan s = span.Value;
            int start = s.Start.Offset, end = s.End.Offset;
            if (start < 0 || end > source.Length || end <= start) return null;
            return source.Substring(start, end - start);
        }

        // ---- scope enumeration (shared with SymbolRenameService, duplicated here for simplicity) ----

        private static IEnumerable<string> EnumerateScopeFiles(
            SolutionModel solution, ProjectModel owningProject,
            string declaringFile, RenameScope scope)
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
            if (scope == RenameScope.Project)
            {
                if (owningProject != null && !string.IsNullOrEmpty(owningProject.ProjectDirectory))
                {
                    string[] files;
                    try { files = Directory.GetFiles(owningProject.ProjectDirectory, "*.py", SearchOption.AllDirectories); }
                    catch { yield break; }
                    foreach (string f in files) { string full = Path.GetFullPath(f); if (seen.Add(full)) yield return full; }
                }
                yield break;
            }
            if (scope == RenameScope.Package)
            {
                string pkgDir = !string.IsNullOrEmpty(declaringFile) ? Path.GetDirectoryName(declaringFile) : null;
                if (!string.IsNullOrEmpty(pkgDir) && Directory.Exists(pkgDir))
                {
                    string[] files;
                    try { files = Directory.GetFiles(pkgDir, "*.py", SearchOption.AllDirectories); }
                    catch { yield break; }
                    foreach (string f in files) { string full = Path.GetFullPath(f); if (seen.Add(full)) yield return full; }
                }
                yield break;
            }
            // Solution
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.ProjectDirectory)) continue;
                string[] files;
                try { files = Directory.GetFiles(p.ProjectDirectory, "*.py", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string f in files) { string full = Path.GetFullPath(f); if (seen.Add(full)) yield return full; }
            }
        }

        // ---- AST traversal (same omission-aware walker as AstIdentifierRewriter) ----

        private static IEnumerable<SyntaxNode> EnumerateAll(SyntaxNode root)
        {
            if (root == null) yield break;
            Stack<SyntaxNode> stack = new Stack<SyntaxNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                SyntaxNode n = stack.Pop();
                yield return n;
                // Default children
                if (n.Children != null)
                {
                    foreach (SyntaxNode c in n.Children) if (c != null) stack.Push(c);
                }
                // Fill omissions.
                switch (n)
                {
                    case ExpressionStatement es:
                        if (es.Expression != null) stack.Push(es.Expression); break;
                    case AssignmentStatement asn:
                        foreach (ExpressionSyntax t in asn.Targets) if (t != null) stack.Push(t);
                        if (asn.Value != null) stack.Push(asn.Value);
                        if (asn.Annotation != null) stack.Push(asn.Annotation);
                        break;
                }
            }
        }
    }
}
