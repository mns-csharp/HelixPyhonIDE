using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;
using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
using HelixPythonNamespace.CudaVirtualCompiler.Parsing;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Scope-aware identifier rewriter for C++/CUDA source — the counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Python.AstIdentifierRewriter"/>, and deliberately the same
    /// shape (<c>Rewrite</c> + <c>FindOccurrences</c> returning the identical span set, so a rename
    /// and a "Find All References" can never disagree about what would be touched).
    ///
    /// Like the Python engine this is NOT type inference and NOT a cross-file symbol graph: it
    /// renames every syntactic reference to <c>oldName</c> in one file that is not hidden by a
    /// local binding, treating any un-shadowed occurrence of the exact same name as the same
    /// symbol. Which files are candidates is <see cref="CppSymbolRenameService"/>'s job.
    ///
    /// What is skipped, mirroring the Python rewriter:
    /// <list type="bullet">
    ///   <item>Comments, preprocessor directives, string and character literals — the CUDA lexer
    ///         classifies all of those as their own token kinds, so they are never candidates.</item>
    ///   <item>Member-access names: <c>obj.oldName</c> and <c>ptr-&gt;oldName</c>. This is the exact
    ///         analogue of Python's rule that <c>AttributeExpression</c> names are untouched — the
    ///         name after <c>.</c>/<c>-&gt;</c> is a member of some object's type, not a reference to
    ///         the entity being renamed, even when the spelling coincides.</item>
    ///   <item>Names shadowed by a local binding — a parameter, local variable, local type or local
    ///         function declaration of the same name anywhere in an enclosing function/lambda hides
    ///         the outer entity for that whole scope (same whole-scope granularity Python's
    ///         <c>CollectScopeBindings</c> uses, rather than statement-precise block scoping).</item>
    ///   <item>The declarator name of a member function of some OTHER record that coincidentally has
    ///         the same name (e.g. renaming class <c>Widget</c> must not rewrite
    ///         <c>class Other { void Widget(); };</c>). Python gets this for free by only rewriting
    ///         <c>def</c>/<c>class</c> declaration sites whose parent is the module; C++ needs it
    ///         spelled out because the declarator name lives on the token stream, not on a node.</item>
    /// </list>
    ///
    /// What is deliberately INCLUDED and has no Python equivalent:
    /// <list type="bullet">
    ///   <item>Constructors and destructors. C++ requires them to be spelled exactly like their
    ///         class, so renaming class <c>Foo</c> to <c>Bar</c> must rewrite <c>Foo(...)</c> and
    ///         <c>~Foo()</c> (and out-of-line <c>Foo::Foo</c> / <c>Foo::~Foo</c>) or the result does
    ///         not compile. That falls out of the rule "a bare identifier token equal to the old
    ///         name that is not member access and not shadowed", and is covered by tests rather
    ///         than being special-cased.</item>
    ///   <item>Every segment of a qualified name: <c>Foo::static_method()</c> is rewritten when
    ///         renaming <c>Foo</c>, and <c>ns::helper()</c> when renaming <c>helper</c>. Unlike
    ///         Python's <c>.</c>, C++'s <c>::</c> is compile-time scope resolution to a declared
    ///         entity, so both sides are genuine name references; leaving the trailing segment alone
    ///         would break every qualified call to a renamed namespace-scope function.</item>
    ///   <item>Base-clause names (<c>class Derived : public Foo</c>) and type-usage positions
    ///         (<c>Foo obj;</c>, <c>void f(const Foo&amp;)</c>, <c>new Foo()</c>,
    ///         <c>static_cast&lt;Foo&gt;(x)</c>). These are unavoidable for a usable class rename and
    ///         are the reason this rewriter is token-positioned rather than purely node-positioned:
    ///         <see cref="CxxType"/> carries only a <c>Base</c> string with no source span, and
    ///         <see cref="BaseClassRef"/> likewise, so there is no node whose span could be spliced.
    ///         The AST still decides WHICH positions are eligible (scopes, member-function
    ///         declarators); the token stream only supplies the exact character ranges.</item>
    /// </list>
    ///
    /// The file is lexed with macro expansion DISABLED (no <c>#define</c> table), unlike
    /// <see cref="HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource"/>: an expanded
    /// object-like macro splices the replacement's tokens in at the macro's use-site span, which
    /// would make token spans stop being exact substrings of the source — fatal for a rewriter that
    /// edits by character range. Every recorded span is additionally verified against the source
    /// text before it is accepted.
    /// </summary>
    public static class CppIdentifierRewriter
    {
        /// <summary>Same shape as <c>AstIdentifierRewriter.RewriteResult</c>.</summary>
        public readonly struct RewriteResult
        {
            public readonly string Text;
            public readonly int Count;
            public RewriteResult(string text, int count) { Text = text; Count = count; }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Rewrite every un-shadowed reference to <paramref name="oldName"/> in
        /// <paramref name="source"/>. Returns the original source and a count of 0 when there is
        /// nothing to do or the file cannot be lexed/parsed at all.
        /// </summary>
        public static RewriteResult Rewrite(string source, string oldName, string newName)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(oldName) || newName == null || oldName == newName)
                return new RewriteResult(source, 0);

            return Splice(source, FindOccurrences(source, oldName), newName);
        }

        /// <summary>
        /// Every reference to <paramref name="name"/> in <paramref name="source"/>, as character
        /// spans sorted ascending by start and free of overlaps — exactly the set
        /// <see cref="Rewrite"/> edits.
        /// </summary>
        public static IReadOnlyList<(int start, int end)> FindOccurrences(string source, string name)
        {
            List<(int start, int end)> result = new List<(int, int)>();
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(name)) return result;

            Lexed lx = LexAndParse(source);
            if (lx == null) return result;

            List<BindingScope> shadowing = ScopesBinding(lx.Unit, name);
            HashSet<int> excluded = ForeignMemberFunctionNameOffsets(lx, name);

            List<(int start, int end)> hits = new List<(int, int)>();
            for (int i = 0; i < lx.Sig.Count; i++)
            {
                CudaToken t = lx.Sig[i];
                if (t.Kind != CudaTokenKind.Identifier) continue;
                if (!string.Equals(t.Text, name, StringComparison.Ordinal)) continue;

                int s = t.Span.Start.Offset;
                int e = t.Span.End.Offset;
                if (!SpanIsLiterally(source, s, e, name)) continue;

                // `obj.name` / `ptr->name`: a member of some other type, never this symbol.
                if (i > 0)
                {
                    CudaTokenKind pk = lx.Sig[i - 1].Kind;
                    if (pk == CudaTokenKind.Dot || pk == CudaTokenKind.Arrow) continue;
                }
                if (excluded.Contains(s)) continue;
                if (IsShadowed(shadowing, s)) continue;

                hits.Add((s, e));
            }

            return Normalize(hits, source);
        }

        /// <summary>
        /// Rename a class member (method / field / constructor-adjacent name) — deliberately much
        /// narrower than <see cref="Rewrite"/>, exactly as
        /// <see cref="HelixPythonNamespace.Workspace.Python.PythonMemberRewriter"/> is narrower than the
        /// Python identifier rewriter: member names collide across unrelated classes far more often
        /// than type or namespace-scope function names do, so there is no wide-scope option.
        /// </summary>
        public static RewriteResult RewriteMember(string source, string className, string oldName, string newName)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(oldName) ||
                newName == null || oldName == newName)
                return new RewriteResult(source, 0);

            return Splice(source, FindMemberOccurrences(source, className, oldName), newName);
        }

        /// <summary>
        /// The spans <see cref="RewriteMember"/> edits: occurrences of <paramref name="name"/>
        /// inside the body of a record called <paramref name="className"/> (its declaration, and
        /// bare or <c>this-&gt;</c>-qualified uses from sibling members — the analogue of Python's
        /// <c>self.&lt;old&gt;</c> rule), plus <c>ClassName::name</c> anywhere in the file, which is
        /// how C++ spells an out-of-line definition (<c>void Foo::bar() { }</c>) or a static access.
        /// The latter is what makes member rename work across this codebase's header/source split.
        /// </summary>
        public static IReadOnlyList<(int start, int end)> FindMemberOccurrences(string source, string className, string name)
        {
            List<(int start, int end)> result = new List<(int, int)>();
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(className) || string.IsNullOrEmpty(name))
                return result;

            Lexed lx = LexAndParse(source);
            if (lx == null) return result;

            List<(int start, int end)> ownerSpans = new List<(int, int)>();
            CollectRecordSpans(lx.Unit.Decls, className, ownerSpans);
            List<BindingScope> shadowing = ScopesBinding(lx.Unit, name);

            List<(int start, int end)> hits = new List<(int, int)>();
            for (int i = 0; i < lx.Sig.Count; i++)
            {
                CudaToken t = lx.Sig[i];
                if (t.Kind != CudaTokenKind.Identifier) continue;
                if (!string.Equals(t.Text, name, StringComparison.Ordinal)) continue;

                int s = t.Span.Start.Offset;
                int e = t.Span.End.Offset;
                if (!SpanIsLiterally(source, s, e, name)) continue;

                CudaTokenKind pk = i > 0 ? lx.Sig[i - 1].Kind : CudaTokenKind.Unknown;
                CudaTokenKind ppk = i > 1 ? lx.Sig[i - 2].Kind : CudaTokenKind.Unknown;

                // `ClassName::name` — out-of-line definition or static access. Valid anywhere.
                if (pk == CudaTokenKind.Scope && ppk == CudaTokenKind.Identifier &&
                    string.Equals(lx.Sig[i - 2].Text, className, StringComparison.Ordinal))
                {
                    hits.Add((s, e));
                    continue;
                }

                if (!SpanListContains(ownerSpans, s)) continue;      // outside the owning class body
                if (pk == CudaTokenKind.Scope) continue;             // Other::name inside the body
                if (pk == CudaTokenKind.Dot || pk == CudaTokenKind.Arrow)
                {
                    // Only `this->name` / `this.name` is this member; `other.name` is not.
                    if (ppk != CudaTokenKind.KwThis) continue;
                }
                if (IsShadowed(shadowing, s)) continue;

                hits.Add((s, e));
            }

            return Normalize(hits, source);
        }

        // ------------------------------------------------------------------ splice

        /// <summary>
        /// Replace each span with <paramref name="newName"/>. Spans arrive sorted ascending and
        /// non-overlapping, and the source is copied forward between them, so no replacement can
        /// shift the offsets of a later one — the same construction the Python rewriter uses.
        /// </summary>
        private static RewriteResult Splice(string source, IReadOnlyList<(int start, int end)> hits, string newName)
        {
            if (hits.Count == 0) return new RewriteResult(source, 0);

            StringBuilder sb = new StringBuilder(source.Length + hits.Count * Math.Max(0, newName.Length));
            int cursor = 0;
            int applied = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue;                                   // overlap / duplicate
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(newName);
                cursor = h.end;
                applied++;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);
            return new RewriteResult(sb.ToString(), applied);
        }

        private static IReadOnlyList<(int start, int end)> Normalize(List<(int start, int end)> hits, string source)
        {
            List<(int start, int end)> result = new List<(int, int)>();
            if (hits.Count == 0) return result;
            hits.Sort((a, b) => a.start.CompareTo(b.start));
            int cursor = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue;
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                result.Add(h);
                cursor = h.end;
            }
            return result;
        }

        /// <summary>
        /// Guard that a token's span really is the literal name in the source. Cheap insurance
        /// against any future span synthesis (macro splicing does exactly this) silently turning a
        /// rename into corruption.
        /// </summary>
        private static bool SpanIsLiterally(string source, int start, int end, string name)
        {
            if (start < 0 || end > source.Length) return false;
            if (end - start != name.Length) return false;
            return string.CompareOrdinal(source, start, name, 0, name.Length) == 0;
        }

        // ------------------------------------------------------------------ lex + parse

        private sealed class Lexed
        {
            public List<CudaToken> Sig;     // significant tokens only, in source order
            public int[] Offsets;           // Sig[i].Span.Start.Offset, for binary search
            public TranslationUnit Unit;
        }

        private static Lexed LexAndParse(string source)
        {
            try
            {
                // defines: null — see the class remarks. Rename must see the source's own
                // identifiers, not a macro's replacement tokens re-spanned onto the use site.
                CudaLexer lexer = new CudaLexer(source, string.Empty, null);
                List<CudaToken> all = lexer.Tokenize();
                TranslationUnit unit = new CudaParser(all, string.Empty).Parse();
                if (unit == null) return null;

                List<CudaToken> sig = new List<CudaToken>(all.Count);
                for (int i = 0; i < all.Count; i++)
                {
                    CudaToken t = all[i];
                    if (t == null || t.IsTrivia || t.Kind == CudaTokenKind.EndOfFile) continue;
                    sig.Add(t);
                }
                int[] offsets = new int[sig.Count];
                for (int i = 0; i < sig.Count; i++) offsets[i] = sig[i].Span.Start.Offset;
                return new Lexed { Sig = sig, Offsets = offsets, Unit = unit };
            }
            catch
            {
                // A file the CUDA front end cannot even tokenize contributes nothing, exactly as a
                // Python file with a syntax error is skipped rather than aborting the rename.
                return null;
            }
        }

        /// <summary>Index of the first significant token starting at or after <paramref name="offset"/>.</summary>
        private static int FirstSigIndexAtOrAfter(Lexed lx, int offset)
        {
            int lo = 0, hi = lx.Offsets.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (lx.Offsets[mid] < offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------ scopes

        /// <summary>
        /// One function or lambda scope: the source range it covers and the names bound directly
        /// inside it. Whole-scope granularity, matching Python's <c>CollectScopeBindings</c> — a
        /// local declared halfway down a function is treated as bound for the entire function.
        /// </summary>
        private sealed class BindingScope
        {
            public int Start;
            public int End;
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>Only the scopes that actually bind <paramref name="name"/> — usually none, which
        /// makes the per-token shadow test free on the overwhelming majority of files.</summary>
        private static List<BindingScope> ScopesBinding(TranslationUnit unit, string name)
        {
            List<BindingScope> all = new List<BindingScope>();
            CollectScopes(unit.Decls, all);
            List<BindingScope> filtered = new List<BindingScope>();
            for (int i = 0; i < all.Count; i++)
                if (all[i].Names.Contains(name)) filtered.Add(all[i]);
            return filtered;
        }

        private static bool IsShadowed(List<BindingScope> scopes, int offset)
        {
            for (int i = 0; i < scopes.Count; i++)
            {
                BindingScope s = scopes[i];
                if (offset >= s.Start && offset < s.End) return true;
            }
            return false;
        }

        private static void CollectScopes(IReadOnlyList<Decl> decls, List<BindingScope> outScopes)
        {
            if (decls == null) return;
            for (int i = 0; i < decls.Count; i++) CollectScopesInDecl(decls[i], outScopes);
        }

        private static void CollectScopesInDecl(Decl d, List<BindingScope> outScopes)
        {
            if (d == null) return;
            if (d is NamespaceDecl ns) { CollectScopes(ns.Decls, outScopes); return; }
            if (d is TemplateDecl tmpl) { CollectScopesInDecl(tmpl.Inner, outScopes); return; }
            if (d is RecordDecl rec) { CollectScopes(rec.Members, outScopes); return; }
            if (d is FunctionDecl fn) { AddFunctionScope(fn, outScopes); return; }
            if (d is VarDecl v)
            {
                // A namespace/class-scope initializer can still contain a lambda with its own scope.
                CollectScopesInExpr(v.Initializer, outScopes);
                CollectScopesInExpr(v.ArraySize, outScopes);
            }
        }

        private static void AddFunctionScope(FunctionDecl fn, List<BindingScope> outScopes)
        {
            BindingScope scope = new BindingScope
            {
                // The whole declaration, so parameters (which live before the body's '{') are
                // covered too. That the return type is inside the range as well is harmless: a
                // function whose parameter or local is spelled exactly like its own return type is
                // not valid code to begin with.
                Start = fn.Span.Start.Offset,
                End = fn.Span.End.Offset,
            };
            for (int i = 0; i < fn.Params.Count; i++)
            {
                Parameter p = fn.Params[i];
                if (p != null && !string.IsNullOrEmpty(p.Name)) scope.Names.Add(p.Name);
                if (p != null) CollectScopesInExpr(p.DefaultValue, outScopes);
            }
            if (fn.Body != null) CollectStmtBindings(fn.Body, scope, outScopes);
            outScopes.Add(scope);
        }

        private static void CollectStmtBindings(Stmt s, BindingScope owner, List<BindingScope> outScopes)
        {
            if (s == null) return;

            if (s is CompoundStmt cs)
            {
                for (int i = 0; i < cs.Body.Count; i++) CollectStmtBindings(cs.Body[i], owner, outScopes);
                return;
            }
            if (s is DeclStmt ds)
            {
                Decl d = ds.Decl;
                if (d is VarDecl v)
                {
                    if (!string.IsNullOrEmpty(v.Name)) owner.Names.Add(v.Name);
                    CollectScopesInExpr(v.Initializer, outScopes);
                    CollectScopesInExpr(v.ArraySize, outScopes);
                }
                else if (d is RecordDecl rec)
                {
                    if (!string.IsNullOrEmpty(rec.Name)) owner.Names.Add(rec.Name);
                    CollectScopes(rec.Members, outScopes);
                }
                else if (d is EnumDecl en)
                {
                    if (!string.IsNullOrEmpty(en.Name)) owner.Names.Add(en.Name);
                }
                else if (d is TypeAliasDecl ta)
                {
                    if (!string.IsNullOrEmpty(ta.Name)) owner.Names.Add(ta.Name);
                }
                else if (d is FunctionDecl fn)
                {
                    // A block-local function declaration binds its name here; its own parameters
                    // and body form a nested scope (registered separately).
                    if (!string.IsNullOrEmpty(fn.Name)) owner.Names.Add(fn.Name);
                    AddFunctionScope(fn, outScopes);
                }
                else if (d is TemplateDecl tmpl)
                {
                    CollectScopesInDecl(tmpl.Inner, outScopes);
                }
                return;
            }
            if (s is ExprStmt es) { CollectScopesInExpr(es.Expr, outScopes); return; }
            if (s is IfStmt ifs)
            {
                CollectScopesInExpr(ifs.Cond, outScopes);
                CollectStmtBindings(ifs.Then, owner, outScopes);
                CollectStmtBindings(ifs.Else, owner, outScopes);
                return;
            }
            if (s is ForStmt fs)
            {
                // The init declaration binds the loop variable in the enclosing function scope,
                // matching how Python treats a `for` target.
                CollectStmtBindings(fs.Init, owner, outScopes);
                CollectScopesInExpr(fs.Cond, outScopes);
                CollectScopesInExpr(fs.Inc, outScopes);
                CollectStmtBindings(fs.Body, owner, outScopes);
                return;
            }
            if (s is WhileStmt ws)
            {
                CollectScopesInExpr(ws.Cond, outScopes);
                CollectStmtBindings(ws.Body, owner, outScopes);
                return;
            }
            if (s is DoStmt dos)
            {
                CollectStmtBindings(dos.Body, owner, outScopes);
                CollectScopesInExpr(dos.Cond, outScopes);
                return;
            }
            if (s is SwitchStmt sw)
            {
                CollectScopesInExpr(sw.Cond, outScopes);
                CollectStmtBindings(sw.Body, owner, outScopes);
                return;
            }
            if (s is ReturnStmt rs) { CollectScopesInExpr(rs.Value, outScopes); return; }
            // Break/Continue/Empty bind nothing.
        }

        /// <summary>
        /// Walk an expression looking for lambdas, each of which is its own binding scope. Captured
        /// and enclosing names still shadow inside a lambda because scopes are tested by range
        /// containment, so the enclosing function's scope also covers the lambda's tokens.
        /// </summary>
        private static void CollectScopesInExpr(Expr e, List<BindingScope> outScopes)
        {
            if (e == null) return;

            if (e is LambdaExpr lam)
            {
                BindingScope scope = new BindingScope
                {
                    Start = lam.Span.Start.Offset,
                    End = lam.Span.End.Offset,
                };
                for (int i = 0; i < lam.ParamNames.Count; i++)
                    if (!string.IsNullOrEmpty(lam.ParamNames[i])) scope.Names.Add(lam.ParamNames[i]);
                if (lam.Body != null) CollectStmtBindings(lam.Body, scope, outScopes);
                outScopes.Add(scope);
                return;
            }
            if (e is ParenExpr pe) { CollectScopesInExpr(pe.Inner, outScopes); return; }
            if (e is UnaryExpr ue) { CollectScopesInExpr(ue.Operand, outScopes); return; }
            if (e is BinaryExpr be)
            {
                CollectScopesInExpr(be.Lhs, outScopes);
                CollectScopesInExpr(be.Rhs, outScopes);
                return;
            }
            if (e is ConditionalExpr ce)
            {
                CollectScopesInExpr(ce.Cond, outScopes);
                CollectScopesInExpr(ce.Then, outScopes);
                CollectScopesInExpr(ce.Else, outScopes);
                return;
            }
            if (e is CallExpr call)
            {
                CollectScopesInExpr(call.Callee, outScopes);
                for (int i = 0; i < call.Args.Count; i++) CollectScopesInExpr(call.Args[i], outScopes);
                return;
            }
            if (e is KernelLaunchExpr kl)
            {
                CollectScopesInExpr(kl.Callee, outScopes);
                CollectScopesInExpr(kl.Grid, outScopes);
                CollectScopesInExpr(kl.Block, outScopes);
                CollectScopesInExpr(kl.SharedMem, outScopes);
                CollectScopesInExpr(kl.Stream, outScopes);
                for (int i = 0; i < kl.Args.Count; i++) CollectScopesInExpr(kl.Args[i], outScopes);
                return;
            }
            if (e is MemberExpr me) { CollectScopesInExpr(me.Object, outScopes); return; }
            if (e is IndexExpr ie)
            {
                CollectScopesInExpr(ie.Base, outScopes);
                CollectScopesInExpr(ie.Index, outScopes);
                return;
            }
            if (e is CastExpr cast) { CollectScopesInExpr(cast.Operand, outScopes); return; }
            if (e is NewExpr ne) { CollectScopesInExpr(ne.ArraySize, outScopes); return; }
            if (e is DeleteExpr de) { CollectScopesInExpr(de.Operand, outScopes); return; }
            if (e is InitListExpr il)
            {
                for (int i = 0; i < il.Elements.Count; i++) CollectScopesInExpr(il.Elements[i], outScopes);
                return;
            }
            // NameExpr / LiteralExpr have no children.
        }

        // ------------------------------------------------------------------ declaration-site exclusions

        /// <summary>
        /// Offsets of the declarator-name token of every member function called
        /// <paramref name="name"/> that belongs to a record NOT called <paramref name="name"/> —
        /// i.e. a method that merely happens to share the spelling of the symbol being renamed.
        /// Records that ARE called <paramref name="name"/> are skipped on purpose, because their
        /// same-named members are the constructors and destructor, which C++ requires to be renamed
        /// alongside the class.
        /// </summary>
        private static HashSet<int> ForeignMemberFunctionNameOffsets(Lexed lx, string name)
        {
            HashSet<int> result = new HashSet<int>();
            List<RecordDecl> records = new List<RecordDecl>();
            CollectRecords(lx.Unit.Decls, records);

            for (int r = 0; r < records.Count; r++)
            {
                RecordDecl rec = records[r];
                if (string.Equals(rec.Name, name, StringComparison.Ordinal)) continue;
                for (int m = 0; m < rec.Members.Count; m++)
                {
                    FunctionDecl fn = rec.Members[m] as FunctionDecl;
                    if (fn == null) continue;
                    if (!string.Equals(fn.Name, name, StringComparison.Ordinal)) continue;
                    int off = DeclaratorNameOffset(lx, fn, name);
                    if (off >= 0) result.Add(off);
                }
            }
            return result;
        }

        /// <summary>
        /// The offset of a function declaration's own name token: the identifier immediately before
        /// the '(' that opens its parameter list. Locating it from the token stream is unavoidable —
        /// <see cref="FunctionDecl"/> stores the name as a raw string and its span starts at the
        /// declaration's leading specifiers, exactly the situation Python's
        /// <c>SpliceDeclarationNameSpan</c> handles by scanning for the token after <c>def</c>.
        /// Returns -1 when the shape is not recognized (fail-open: the occurrence is treated as an
        /// ordinary reference rather than being silently dropped).
        /// </summary>
        private static int DeclaratorNameOffset(Lexed lx, FunctionDecl fn, string name)
        {
            int startOff = fn.Span.Start.Offset;
            int endOff = fn.Span.End.Offset;
            int i = FirstSigIndexAtOrAfter(lx, startOff);
            for (; i < lx.Sig.Count; i++)
            {
                CudaToken t = lx.Sig[i];
                if (t.Span.Start.Offset >= endOff) break;
                if (t.Kind != CudaTokenKind.LeftParen) continue;
                if (i == 0) return -1;
                CudaToken prev = lx.Sig[i - 1];
                if (prev.Kind == CudaTokenKind.Identifier &&
                    string.Equals(prev.Text, name, StringComparison.Ordinal))
                    return prev.Span.Start.Offset;
                return -1;   // first '(' was not preceded by the name (e.g. a function-typed return)
            }
            return -1;
        }

        // ------------------------------------------------------------------ record lookup

        private static void CollectRecords(IReadOnlyList<Decl> decls, List<RecordDecl> outRecords)
        {
            if (decls == null) return;
            for (int i = 0; i < decls.Count; i++)
            {
                Decl d = decls[i];
                if (d is NamespaceDecl ns) CollectRecords(ns.Decls, outRecords);
                else if (d is TemplateDecl tmpl) CollectRecords(new List<Decl> { tmpl.Inner }, outRecords);
                else if (d is RecordDecl rec)
                {
                    outRecords.Add(rec);
                    CollectRecords(rec.Members, outRecords);
                }
            }
        }

        private static void CollectRecordSpans(IReadOnlyList<Decl> decls, string className, List<(int start, int end)> outSpans)
        {
            List<RecordDecl> records = new List<RecordDecl>();
            CollectRecords(decls, records);
            for (int i = 0; i < records.Count; i++)
            {
                RecordDecl rec = records[i];
                if (!string.Equals(rec.Name, className, StringComparison.Ordinal)) continue;
                if (rec.Members.Count == 0) continue;   // forward declaration, not a body
                outSpans.Add((rec.Span.Start.Offset, rec.Span.End.Offset));
            }
        }

        private static bool SpanListContains(List<(int start, int end)> spans, int offset)
        {
            for (int i = 0; i < spans.Count; i++)
                if (offset >= spans[i].start && offset < spans[i].end) return true;
            return false;
        }
    }
}
