using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// AST- and scope-aware identifier rewriter for Python source. Replaces every
    /// occurrence of a module-level name with a new name, correctly skipping:
    ///
    /// <list type="bullet">
    ///   <item>Local bindings in any enclosing function / lambda (parameters,
    ///         assignments, for-targets, with-as, walrus, nested defs/classes).</item>
    ///   <item>Attribute access positions — <c>obj.name</c> is not touched, because
    ///         the attribute name is a plain string on <see cref="AttributeExpression"/>
    ///         rather than a <see cref="NameExpression"/>.</item>
    ///   <item>String literals and comments (we only walk the AST, not source text).</item>
    ///   <item>Nested <c>def</c>/<c>class</c> declarations that redefine the name at a
    ///         non-module scope.</item>
    /// </list>
    ///
    /// Additionally rewrites the declaration site itself and any
    /// <c>from X import oldName</c> binding in the imports list, since those names
    /// live on AST nodes as raw strings (not NameExpression children).
    /// </summary>
    public static class AstIdentifierRewriter
    {
        public readonly struct RewriteResult
        {
            public readonly string Text;
            public readonly int Count;
            public RewriteResult(string text, int count) { Text = text; Count = count; }
        }

        public static RewriteResult Rewrite(string source, string oldName, string newName)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(oldName) || newName == null || oldName == newName)
                return new RewriteResult(source, 0);

            IReadOnlyList<(int start, int end)> hits = FindOccurrences(source, oldName);
            if (hits.Count == 0) return new RewriteResult(source, 0);

            StringBuilder sb = new StringBuilder(source.Length + hits.Count * Math.Max(0, newName.Length - oldName.Length));
            int cursor = 0;
            int appliedCount = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue; // overlap / duplicate
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(newName);
                cursor = h.end;
                appliedCount++;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);

            return new RewriteResult(sb.ToString(), appliedCount);
        }

        /// <summary>
        /// Scope-aware occurrence finder: returns the character spans (deduped, sorted ascending by
        /// start) of every reference to <paramref name="name"/> in <paramref name="source"/> — the
        /// same binding-resolved set the rename rewriter edits, so "Find All References" and "Rename"
        /// agree exactly. Covers NameExpression uses, module-scope <c>def</c>/<c>class</c> declaration
        /// sites, and <c>from X import name</c> lines.
        /// </summary>
        public static IReadOnlyList<(int start, int end)> FindOccurrences(string source, string name)
        {
            List<(int start, int end)> result = new List<(int, int)>();
            if (source == null || string.IsNullOrEmpty(name)) return result;

            ModuleSyntax module;
            IReadOnlyList<Token> tokens;
            try
            {
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module;
                tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source);
            }
            catch { return result; }
            if (module == null) return result;

            // Per-scope binding sets. Key = scope-defining node (FunctionDefSyntax or
            // LambdaExpression or ModuleSyntax); value = names bound directly in that
            // scope (not in nested functions).
            Dictionary<SyntaxNode, HashSet<string>> scopeBindings = CollectScopeBindings(module);

            List<(int start, int end)> hits = new List<(int, int)>();

            // ---- 1. NameExpression references. Walk the AST while maintaining our
            //         own enclosing-scope stack (we can't rely on SyntaxNode.Parent
            //         because several statement types skip AdoptChildren). ----
            var scopeStack = new List<SyntaxNode>();
            var compTargets = new List<string>();
            CollectNameHits(module, name, scopeBindings, scopeStack, compTargets, hits);

            // ---- 2. Declaration sites at module scope (`def X` / `class X` where
            //         Parent is ModuleSyntax). ----
            foreach (StatementSyntax stmt in module.Statements)
            {
                if (stmt is FunctionDefSyntax fd && string.Equals(fd.Name, name, StringComparison.Ordinal))
                {
                    SpliceDeclarationNameSpan(tokens, fd.Span, "def", hits);
                }
                else if (stmt is ClassDefSyntax cd && string.Equals(cd.Name, name, StringComparison.Ordinal))
                {
                    SpliceDeclarationNameSpan(tokens, cd.Span, "class", hits);
                }
            }

            // ---- 3. `from X import name [as alias]` lines. The name-token lives
            //         as a raw string in FromImportStatement.Names — recover its span
            //         from the statement's tokens. ----
            foreach (SyntaxNode n in EnumerateAll(module))
            {
                if (!(n is FromImportStatement fi)) continue;
                bool imports = false;
                foreach (KeyValuePair<string, string> kv in fi.Names)
                {
                    if (string.Equals(kv.Key, name, StringComparison.Ordinal)) { imports = true; break; }
                }
                if (!imports) continue;
                SpliceFromImportNameSpan(tokens, fi.Span, name, hits);
            }

            if (hits.Count == 0) return result;

            // Dedup + sort ascending by start (drop overlaps, same rule as the rewriter's splice loop).
            hits.Sort((a, b) => a.start.CompareTo(b.start));
            int cursor = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue;                     // overlap / duplicate
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                result.Add(h);
                cursor = h.end;
            }
            return result;
        }

        // ------------------------------------------------------------------ scope

        /// <summary>
        /// For every FunctionDefSyntax and LambdaExpression (plus the module itself),
        /// collect the set of names bound directly within that scope — not including
        /// nested function / lambda bodies.
        /// </summary>
        private static Dictionary<SyntaxNode, HashSet<string>> CollectScopeBindings(ModuleSyntax module)
        {
            var result = new Dictionary<SyntaxNode, HashSet<string>>();

            // Seed all function/lambda scopes with empty sets, then fill.
            foreach (SyntaxNode n in EnumerateAll(module))
            {
                if (n is FunctionDefSyntax || n is LambdaExpression)
                    result[n] = new HashSet<string>(StringComparer.Ordinal);
            }

            // Fill function-def scopes.
            foreach (SyntaxNode n in EnumerateAll(module))
            {
                if (n is FunctionDefSyntax fd)
                {
                    var set = result[fd];
                    foreach (ParameterSyntax p in fd.Parameters)
                        if (!string.IsNullOrEmpty(p.Name)) set.Add(p.Name);
                    if (fd.Body != null) CollectBodyBindings(fd.Body, fd, set);
                }
                else if (n is LambdaExpression lam)
                {
                    var set = result[lam];
                    foreach (ParameterSyntax p in lam.Parameters)
                        if (!string.IsNullOrEmpty(p.Name)) set.Add(p.Name);
                    // Lambdas don't have a body with statements; their body is an expression.
                }
            }

            return result;
        }

        private static void CollectBodyBindings(SuiteSyntax body, SyntaxNode scopeOwner, HashSet<string> set)
        {
            foreach (StatementSyntax s in body.Statements) CollectStmtBindings(s, scopeOwner, set);
        }

        private static void CollectStmtBindings(StatementSyntax s, SyntaxNode scopeOwner, HashSet<string> set)
        {
            if (s == null) return;

            // Nested defs/classes bind their name in this scope (but their bodies belong
            // to their own scopes, handled by the outer walker).
            if (s is FunctionDefSyntax fd)
            {
                if (!string.IsNullOrEmpty(fd.Name)) set.Add(fd.Name);
                return;
            }
            if (s is ClassDefSyntax cd)
            {
                if (!string.IsNullOrEmpty(cd.Name)) set.Add(cd.Name);
                return;
            }

            if (s is AssignmentStatement asn)
            {
                foreach (ExpressionSyntax tgt in asn.Targets) CollectTargetName(tgt, set);
                return;
            }
            if (s is AugmentedAssignmentStatement aug)
            {
                CollectTargetName(aug.Target, set);
                return;
            }

            if (s is ForStatement fs)
            {
                CollectTargetName(fs.Target, set);
                // Descend into body/else for more bindings (still in the same scope).
                if (fs.Body != null) foreach (StatementSyntax inner in fs.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                if (fs.Else != null) foreach (StatementSyntax inner in fs.Else.Statements) CollectStmtBindings(inner, scopeOwner, set);
                return;
            }
            if (s is WhileStatement ws)
            {
                if (ws.Body != null) foreach (StatementSyntax inner in ws.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                if (ws.Else != null) foreach (StatementSyntax inner in ws.Else.Statements) CollectStmtBindings(inner, scopeOwner, set);
                return;
            }
            if (s is IfStatement ifs)
            {
                if (ifs.Body != null) foreach (StatementSyntax inner in ifs.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                foreach (IfStatement elif in ifs.Elifs)
                {
                    if (elif.Body != null) foreach (StatementSyntax inner in elif.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                }
                if (ifs.Else != null) foreach (StatementSyntax inner in ifs.Else.Statements) CollectStmtBindings(inner, scopeOwner, set);
                return;
            }
            if (s is TryStatement ts)
            {
                if (ts.Body != null) foreach (StatementSyntax inner in ts.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                foreach (ExceptHandler h in ts.Handlers)
                {
                    // `except X as e:` binds `e` in the enclosing scope (Python 3 scopes it
                    // to the handler block only, but for safety we treat it as bound).
                    if (!string.IsNullOrEmpty(h.Name)) set.Add(h.Name);
                    if (h.Body != null) foreach (StatementSyntax inner in h.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                }
                if (ts.Else != null) foreach (StatementSyntax inner in ts.Else.Statements) CollectStmtBindings(inner, scopeOwner, set);
                if (ts.Finally != null) foreach (StatementSyntax inner in ts.Finally.Statements) CollectStmtBindings(inner, scopeOwner, set);
                return;
            }
            if (s is WithStatement wts)
            {
                foreach (WithItem item in wts.Items)
                {
                    if (item.OptionalVars != null) CollectTargetName(item.OptionalVars, set);
                }
                if (wts.Body != null) foreach (StatementSyntax inner in wts.Body.Statements) CollectStmtBindings(inner, scopeOwner, set);
                return;
            }
            if (s is ImportStatement imp)
            {
                foreach (ImportStatement.ImportedName n in imp.Names)
                {
                    string local = !string.IsNullOrEmpty(n.Alias)
                        ? n.Alias
                        : (n.Name != null && n.Name.Parts.Count > 0 ? n.Name.Parts[0] : null);
                    if (!string.IsNullOrEmpty(local)) set.Add(local);
                }
                return;
            }
            if (s is FromImportStatement fi)
            {
                if (!fi.IsStar)
                {
                    foreach (KeyValuePair<string, string> kv in fi.Names)
                    {
                        string local = string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value;
                        if (!string.IsNullOrEmpty(local)) set.Add(local);
                    }
                }
                return;
            }
        }

        private static void CollectTargetName(ExpressionSyntax expr, HashSet<string> set)
        {
            if (expr is NameExpression ne) { set.Add(ne.Name); return; }
            if (expr is TupleExpression te)
            {
                foreach (ExpressionSyntax el in te.Elements) CollectTargetName(el, set);
                return;
            }
            if (expr is ListExpression le)
            {
                foreach (ExpressionSyntax el in le.Elements) CollectTargetName(el, set);
                return;
            }
            if (expr is StarredExpression se) { CollectTargetName(se.Value, set); return; }
            // Attribute / subscript targets don't create new local bindings.
        }

        /// <summary>
        /// Walk the AST depth-first while maintaining our own scope stack. For every
        /// NameExpression matching <paramref name="oldName"/>, decide whether it's
        /// shadowed by an enclosing function, lambda, or comprehension target — and
        /// if not, record its span. We don't rely on <see cref="SyntaxNode.Parent"/>
        /// because several statement types skip AdoptChildren.
        /// </summary>
        private static void CollectNameHits(
            SyntaxNode node, string oldName,
            Dictionary<SyntaxNode, HashSet<string>> scopeBindings,
            List<SyntaxNode> scopeStack,
            List<string> compTargets,
            List<(int start, int end)> hits)
        {
            if (node == null) return;

            if (node is NameExpression ne && string.Equals(ne.Name, oldName, StringComparison.Ordinal))
            {
                bool shadowed = false;
                for (int i = scopeStack.Count - 1; i >= 0; i--)
                {
                    SyntaxNode s = scopeStack[i];
                    if (scopeBindings.TryGetValue(s, out var set) && set.Contains(oldName))
                    { shadowed = true; break; }
                }
                if (!shadowed)
                {
                    foreach (string tgt in compTargets)
                    {
                        if (string.Equals(tgt, oldName, StringComparison.Ordinal)) { shadowed = true; break; }
                    }
                }
                if (!shadowed)
                {
                    int s = ne.Span.Start.Offset;
                    int e = ne.Span.End.Offset;
                    if (e > s) hits.Add((s, e));
                }
                return;
            }

            bool pushedScope = false;
            int pushedComp = 0;

            if (node is FunctionDefSyntax || node is LambdaExpression)
            {
                scopeStack.Add(node);
                pushedScope = true;
            }

            // Comprehensions bind their clause targets over the WHOLE comprehension,
            // including the element expression — which is a sibling of the clauses
            // under ComprehensionExpression. Push the target names BEFORE we descend.
            if (node is ComprehensionExpression ce)
            {
                foreach (ComprehensionClause clause in ce.Clauses)
                {
                    if (clause.Target != null)
                    {
                        int added;
                        CollectCompTargets(clause.Target, compTargets, out added);
                        pushedComp += added;
                    }
                }
            }
            else if (node is ComprehensionClause cc && cc.Target != null)
            {
                // Parent ComprehensionExpression already pushed these targets. No-op here.
            }

            foreach (SyntaxNode child in ExplicitChildren(node))
            {
                CollectNameHits(child, oldName, scopeBindings, scopeStack, compTargets, hits);
            }

            if (pushedComp > 0) compTargets.RemoveRange(compTargets.Count - pushedComp, pushedComp);
            if (pushedScope) scopeStack.RemoveAt(scopeStack.Count - 1);
        }

        private static void CollectCompTargets(ExpressionSyntax expr, List<string> list, out int pushedCount)
        {
            int before = list.Count;
            CollectCompTargetsCore(expr, list);
            pushedCount = list.Count - before;
        }

        private static void CollectCompTargetsCore(ExpressionSyntax expr, List<string> list)
        {
            if (expr is NameExpression ne) { list.Add(ne.Name); return; }
            if (expr is TupleExpression te)
            {
                foreach (ExpressionSyntax el in te.Elements) CollectCompTargetsCore(el, list);
                return;
            }
            if (expr is ListExpression le)
            {
                foreach (ExpressionSyntax el in le.Elements) CollectCompTargetsCore(el, list);
                return;
            }
            if (expr is StarredExpression se) { CollectCompTargetsCore(se.Value, list); return; }
        }

        private static bool TargetContainsName(ExpressionSyntax expr, string name)
        {
            if (expr is NameExpression ne) return string.Equals(ne.Name, name, StringComparison.Ordinal);
            if (expr is TupleExpression te)
            {
                foreach (ExpressionSyntax el in te.Elements)
                    if (TargetContainsName(el, name)) return true;
                return false;
            }
            if (expr is ListExpression le)
            {
                foreach (ExpressionSyntax el in le.Elements)
                    if (TargetContainsName(el, name)) return true;
                return false;
            }
            if (expr is StarredExpression se) return TargetContainsName(se.Value, name);
            return false;
        }

        /// <summary>
        /// Enumerate every node reachable from <paramref name="root"/> — not just the
        /// ones exposed via <see cref="SyntaxNode.Children"/>. Several statement types
        /// (ExpressionStatement, AssignmentStatement, ImportStatement, …) omit their
        /// Children override, so <see cref="SyntaxNode.Descendants"/> alone skips the
        /// expressions inside them. We explicitly walk the relevant properties here.
        /// </summary>
        /// <summary>
        /// Internal (not private) so <see cref="PythonMemberRewriter"/> can reuse this exact
        /// traversal rather than risk a subtly incomplete duplicate — several statement types omit
        /// or under-report <see cref="SyntaxNode.Children"/> (see <see cref="ExplicitChildren"/>),
        /// so a plain <c>SyntaxNode.Descendants()</c> silently misses expressions inside them.
        /// </summary>
        internal static IEnumerable<SyntaxNode> EnumerateAll(SyntaxNode root)
        {
            if (root == null) yield break;
            Stack<SyntaxNode> stack = new Stack<SyntaxNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                SyntaxNode n = stack.Pop();
                yield return n;
                foreach (SyntaxNode child in ExplicitChildren(n))
                {
                    if (child != null) stack.Push(child);
                }
            }
        }

        internal static IEnumerable<SyntaxNode> ExplicitChildren(SyntaxNode node)
        {
            // Prefer the node's declared Children where present and non-empty.
            if (node.Children != null && node.Children.Count > 0)
            {
                foreach (SyntaxNode c in node.Children) yield return c;
            }
            // Fill in omissions for statement types that don't override Children.
            switch (node)
            {
                case ExpressionStatement es:
                    if (es.Expression != null) yield return es.Expression;
                    break;
                case AssignmentStatement asn:
                    foreach (ExpressionSyntax t in asn.Targets) yield return t;
                    if (asn.Value != null) yield return asn.Value;
                    if (asn.Annotation != null) yield return asn.Annotation;
                    break;
                case ImportStatement imp:
                    foreach (var n2 in imp.Names)
                        if (n2?.Name != null) yield return n2.Name;
                    break;
                case FromImportStatement fi:
                    if (fi.Module != null) yield return fi.Module;
                    break;
                // Other statement types either override Children correctly or have no
                // children worth walking (pass/break/continue/global/nonlocal).
            }
        }

        // ------------------------------------------------------------------ token span helpers

        /// <summary>
        /// Within <paramref name="stmtSpan"/>, find the Name token immediately following
        /// the <c>def</c> or <c>class</c> keyword and record its span.
        /// </summary>
        private static void SpliceDeclarationNameSpan(
            IReadOnlyList<Token> tokens, SourceSpan stmtSpan, string keyword,
            List<(int start, int end)> hits)
        {
            int startOff = stmtSpan.Start.Offset;
            int endOff = stmtSpan.End.Offset;
            TokenKind kw = keyword == "def" ? TokenKind.KwDef : TokenKind.KwClass;

            bool seenKeyword = false;
            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                if (tStart >= endOff) break;

                if (!seenKeyword)
                {
                    if (t.Kind == kw) seenKeyword = true;
                    continue;
                }
                if (t.Kind == TokenKind.Name)
                {
                    hits.Add((t.Span.Start.Offset, t.Span.End.Offset));
                    return;
                }
            }
        }

        /// <summary>
        /// Within a <c>from X import a, b, c</c> statement span, find Name tokens after
        /// the <c>import</c> keyword (and after each comma) that match <paramref name="oldName"/>
        /// and record their spans. The name right after <c>as</c> is an alias (local
        /// binding) — we rewrite the imported-name, not the alias.
        /// </summary>
        private static void SpliceFromImportNameSpan(
            IReadOnlyList<Token> tokens, SourceSpan stmtSpan, string oldName,
            List<(int start, int end)> hits)
        {
            int startOff = stmtSpan.Start.Offset;
            int endOff = stmtSpan.End.Offset;

            // States: 0 = before `import`, 1 = expecting name (imported), 2 = after name (expect `,` / `as` / EOL).
            int state = 0;
            bool expectAlias = false;

            foreach (Token t in tokens)
            {
                if (t == null || t.IsTrivia) continue;
                int tStart = t.Span.Start.Offset;
                if (tStart < startOff) continue;
                if (tStart >= endOff) break;

                if (state == 0)
                {
                    if (t.Kind == TokenKind.KwImport) state = 1;
                    continue;
                }

                if (t.Kind == TokenKind.KwAs) { expectAlias = true; continue; }
                if (t.Kind == TokenKind.Comma) { expectAlias = false; state = 1; continue; }

                if (t.Kind == TokenKind.Name)
                {
                    if (!expectAlias && state == 1 &&
                        string.Equals(t.Text, oldName, StringComparison.Ordinal))
                    {
                        hits.Add((t.Span.Start.Offset, t.Span.End.Offset));
                    }
                    expectAlias = false;
                    state = 2;
                }
            }
        }
    }
}
