namespace HelixPythonNamespace.CudaVirtualCompiler.Parsing
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Recursive-descent parser for the C++/CUDA subset. Consumes the significant
    /// token stream from CudaLexer and produces an ast.TranslationUnit directly
    /// (there is no separate CST step — the coarse AST is the parser's output). The
    /// parser is deliberately permissive: constructs it does not model are skipped
    /// with balanced-token recovery rather than cascading errors, so the downstream
    /// CUDA analyses see as much structure as possible. Parse problems are reported
    /// as CU2xxx ParseDiagnostic. Port of the C++
    /// helix::cuda_vcompiler::CudaParser (cuda_vcompiler/parse/cuda_parser.{h,cpp}).
    /// </summary>
    public sealed class CudaParser
    {
        private readonly List<CudaToken> tokens_;  // trivia already filtered out; ends with EndOfFile
        private readonly string fileName_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private int pos_;
        private int errorBudget_;  // stop emitting after a cascade

        // Per-function goto/label bookkeeping (`goto nowhere;` with no such label is an
        // error every compiler rejects). Non-null only while a function body parses;
        // saved/restored around nested function parses.
        private HashSet<string> fnLabels_;
        private List<KeyValuePair<string, SourceSpan>> fnGotos_;

        public CudaParser(List<CudaToken> tokens, string fileName = null)
        {
            fileName_ = fileName ?? string.Empty;
            diagnostics_ = new List<ParseDiagnostic>();
            pos_ = 0;
            errorBudget_ = 200;
            tokens_ = new List<CudaToken>();
            if (tokens != null)
            {
                for (int i = 0; i < tokens.Count; ++i)
                {
                    if (!tokens[i].IsTrivia) tokens_.Add(tokens[i]);
                }
            }
            if (tokens_.Count == 0 || tokens_[tokens_.Count - 1].Kind != CudaTokenKind.EndOfFile)
            {
                tokens_.Add(new CudaToken(CudaTokenKind.EndOfFile, string.Empty, SourceSpan.Empty));
            }
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        // --- token cursor ---
        // >0 while the `<<<grid, block, shared, stream>>>` configuration is being parsed, so a
        // `>>` there is read as the launch's closer rather than as a right-shift operator.
        private int launchConfig_;

        private CudaToken Cur() { return tokens_[pos_]; }

        private CudaToken Peek(int k = 1)
        {
            int i = pos_ + k;
            if (i >= tokens_.Count) return tokens_[tokens_.Count - 1];
            return tokens_[i];
        }

        private CudaTokenKind Kind() { return tokens_[pos_].Kind; }
        private bool AtEnd() { return tokens_[pos_].Kind == CudaTokenKind.EndOfFile; }
        private bool Check(CudaTokenKind k) { return Kind() == k; }

        private CudaToken Advance()
        {
            CudaToken t = tokens_[pos_];
            if (!AtEnd()) ++pos_;
            return t;
        }

        private bool Match(CudaTokenKind k)
        {
            if (Check(k)) { Advance(); return true; }
            return false;
        }

        private bool Expect(CudaTokenKind k, string what)
        {
            if (Check(k)) { Advance(); return true; }
            if (Cur().Kind == CudaTokenKind.EndOfFile)
                Report(CudaDiagnosticCodes.ParseUnexpectedEof, Cur().Span,
                       "Unexpected end of file: expected " + what);
            else
                Report(CudaDiagnosticCodes.ParseExpectedToken, Cur().Span, "Expected " + what);
            return false;
        }

        private void Report(string code, SourceSpan span, string message)
        {
            if (errorBudget_ <= 0) return;
            --errorBudget_;
            diagnostics_.Add(new ParseDiagnostic(code, ParseSeverity.Error, span, message));
        }

        private SourceSpan SpanFrom(int startPos)
        {
            SourcePosition s = tokens_[startPos].Span.Start;
            int endIdx = pos_ > startPos ? pos_ - 1 : startPos;
            if (endIdx >= tokens_.Count) endIdx = tokens_.Count - 1;
            SourcePosition e = tokens_[endIdx].Span.End;
            if (e.Offset < s.Offset) e = s;
            return new SourceSpan(s, e);
        }

        private void Synchronize()
        {
            // Skip tokens until a likely statement / declaration boundary.
            while (!AtEnd())
            {
                if (Match(CudaTokenKind.Semicolon)) return;
                if (Check(CudaTokenKind.RightBrace)) return;
                switch (Kind())
                {
                    case CudaTokenKind.KwNamespace:
                    case CudaTokenKind.KwTemplate:
                    case CudaTokenKind.KwStruct:
                    case CudaTokenKind.KwClass:
                    case CudaTokenKind.KwGlobal:
                    case CudaTokenKind.KwDevice:
                    case CudaTokenKind.KwHost:
                        return;
                    default: break;
                }
                Advance();
            }
        }

        private CudaTokenKind KindAt(int idx)
        {
            return idx < tokens_.Count ? tokens_[idx].Kind : CudaTokenKind.EndOfFile;
        }

        private string TextAt(int idx)
        {
            return idx >= 0 && idx < tokens_.Count ? tokens_[idx].Text : string.Empty;
        }

        // True if, starting at the current struct/class/union token, this is a record
        // DEFINITION (tag [name] [': ' base-clause] '{') rather than a use of the tag as a
        // type (`struct Foo x;`), a forward declaration (`struct Foo;`), or an
        // elaborated-type-specifier. Scans past an optional base-clause so
        // `class Derived : public Base { ... }` is recognized — a plain lookahead for '{'
        // immediately after the name would miss every class that has a base list at all.
        private bool LooksLikeRecordDefinition()
        {
            int p = pos_ + 1;  // past struct/class/union
            if (p < tokens_.Count && tokens_[p].Kind == CudaTokenKind.Identifier) ++p;
            // Optional `final` on the class-head (contextual keyword, lexes as an Identifier).
            if (p < tokens_.Count && tokens_[p].Kind == CudaTokenKind.Identifier && tokens_[p].Text == "final") ++p;
            if (p >= tokens_.Count) return false;
            if (tokens_[p].Kind == CudaTokenKind.LeftBrace) return true;
            if (tokens_[p].Kind != CudaTokenKind.Colon) return false;
            ++p;  // ':'
            int angleDepth = 0;
            while (p < tokens_.Count)
            {
                CudaTokenKind k = tokens_[p].Kind;
                if (k == CudaTokenKind.LeftBrace && angleDepth == 0) return true;
                if (k == CudaTokenKind.Semicolon && angleDepth == 0) return false;
                if (k == CudaTokenKind.EndOfFile) return false;
                else if (k == CudaTokenKind.Less) ++angleDepth;
                else if (k == CudaTokenKind.Greater && angleDepth > 0) --angleDepth;
                else if (k == CudaTokenKind.RightShift && angleDepth > 0) angleDepth -= angleDepth >= 2 ? 2 : 1;
                else if (k == CudaTokenKind.TripleGreater && angleDepth > 0) angleDepth -= angleDepth >= 3 ? 3 : angleDepth;
                ++p;
            }
            return false;
        }

        // ===================== Top level =====================

        public TranslationUnit Parse()
        {
            List<Decl> decls = new List<Decl>();
            while (!AtEnd())
            {
                int before = pos_;
                Decl d = ParseTopLevelDecl();
                if (d != null) decls.Add(d);
                if (pos_ == before) Advance();  // guarantee forward progress
            }
            return new TranslationUnit(decls);
        }

        private Decl ParseTopLevelDecl()
        {
            // Stray semicolons.
            while (Match(CudaTokenKind.Semicolon)) { }
            if (AtEnd()) return null;

            // C++20 `concept Name = requires(...) { ... };` isn't modeled by this parser (no
            // constraint / requires-expression semantics — well beyond the CUDA-analysis scope
            // this parser was built for) and "concept" lexes as a plain identifier, not a
            // keyword, so without this check it falls through to the generic declaration path
            // and its `requires(...)` body reliably fails to parse as an expression. Recovering
            // by skipping just this one statement (this parser's usual "skip what it doesn't
            // model" philosophy) is what matters: left unhandled, the failed parse doesn't just
            // lose the concept — the recovery previously mis-tracked brace depth badly enough to
            // corrupt every declaration after it in the file (a real bug this fixes, not the
            // concept feature itself).
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "concept")
            {
                Advance();  // 'concept'
                if (Check(CudaTokenKind.Identifier)) Advance();  // name
                Match(CudaTokenKind.Assign);
                int depth = 0;
                while (!AtEnd())
                {
                    if (Check(CudaTokenKind.LeftBrace)) { depth++; Advance(); continue; }
                    if (Check(CudaTokenKind.RightBrace))
                    {
                        Advance();
                        if (--depth <= 0) { Match(CudaTokenKind.Semicolon); break; }
                        continue;
                    }
                    if (depth == 0 && Check(CudaTokenKind.Semicolon)) { Advance(); break; }
                    Advance();
                }
                return null;
            }

            // `static_assert(...)` — a declaration-with-no-entity; consume it cleanly (the
            // generic path used to silently drop it, and the parenthesized-declarator
            // support would otherwise misread its qualified first argument).
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "static_assert" &&
                Peek().Kind == CudaTokenKind.LeftParen)
            {
                Advance();  // static_assert
                int saDepth = 0;
                do
                {
                    if (Check(CudaTokenKind.LeftParen)) saDepth++;
                    else if (Check(CudaTokenKind.RightParen)) saDepth--;
                    Advance();
                } while (!AtEnd() && saDepth > 0);
                Match(CudaTokenKind.Semicolon);
                return null;
            }

            // Catch2 / GoogleTest test-definition macros — TEST_CASE("name", "[tag]") { ... },
            // TEST(Suite, Name) { ... }, SCENARIO / TEST_F / TEST_P / TYPED_TEST — are macros,
            // not C++ function grammar, so the generic declaration path reliably failed on
            // them. Depending on the body, recovery either swallowed the whole test silently
            // or resynced MID-BODY, spilling the remaining locals to FILE scope where the
            // same-named locals of sibling tests collided into bogus "Redefinition" errors
            // (red squiggles all over perfectly valid test files). Parse them as real host
            // functions instead: the macro arguments yield the display name, the body is an
            // ordinary compound statement, and locals land in their own function scope.
            if (Check(CudaTokenKind.Identifier) && IsTestMacroName(Cur().Text) &&
                Peek().Kind == CudaTokenKind.LeftParen && TestMacroIsFollowedByBody())
            {
                return ParseTestMacroFunction();
            }

            switch (Kind())
            {
                case CudaTokenKind.KwNamespace: return ParseNamespace();
                case CudaTokenKind.KwTemplate: return ParseTemplate();
                case CudaTokenKind.KwTypedef: return ParseTypedef();
                case CudaTokenKind.KwUsing: return ParseUsing();
                case CudaTokenKind.KwEnum: return ParseEnum();
                case CudaTokenKind.KwExtern:
                    // Linkage spec: extern "C" { ... } or extern "C" decl.
                    if (Peek().Kind == CudaTokenKind.StringLiteral)
                    {
                        Advance();  // extern
                        Advance();  // "C"
                        if (Match(CudaTokenKind.LeftBrace))
                        {
                            List<Decl> inner = new List<Decl>();
                            while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
                            {
                                int before = pos_;
                                Decl d = ParseTopLevelDecl();
                                if (d != null) inner.Add(d);
                                if (pos_ == before) Advance();
                            }
                            Match(CudaTokenKind.RightBrace);
                            // Flatten: wrap inner decls in an anonymous namespace node so
                            // the symbol builder still walks them.
                            return new NamespaceDecl("", inner, SourceSpan.Empty);
                        }
                        return ParseTopLevelDecl();
                    }
                    break;
                default: break;
            }

            // struct/class/union: definition (followed by '{' or, after name, '{') or a
            // variable of that type. Disambiguate by lookahead for '{'.
            if (Check(CudaTokenKind.KwStruct) || Check(CudaTokenKind.KwClass) ||
                Check(CudaTokenKind.KwUnion))
            {
                // tag [name] [: base-clause] '{'  => record definition; otherwise fall
                // through to decl (elaborated-type-specifier use, forward declaration, ...).
                if (LooksLikeRecordDefinition())
                {
                    return ParseRecord();
                }
            }

            List<Decl> group = ParseDeclaration();
            if (group.Count == 0)
            {
                Synchronize();
                return null;
            }
            if (group.Count == 1) return group[0];
            // Multiple declarators at top level: wrap in an anonymous namespace so the
            // single Decl return contract holds while keeping every VarDecl walkable.
            return new NamespaceDecl("", group, SourceSpan.Empty);
        }

        private static bool IsTestMacroName(string name)
        {
            switch (name)
            {
                case "TEST_CASE":
                case "TEMPLATE_TEST_CASE":
                case "SCENARIO":
                case "TEST":
                case "TEST_F":
                case "TEST_P":
                case "TYPED_TEST":
                    return true;
                default:
                    return false;
            }
        }

        // Lookahead only (no cursor movement): true when the macro's balanced argument list
        // is directly followed by '{' — i.e. this is a test DEFINITION, not some other use
        // of the name. Anything else falls through to the generic declaration path.
        private bool TestMacroIsFollowedByBody()
        {
            int i = 1;                       // Peek(1) is the '('
            int depth = 0;
            while (true)
            {
                CudaToken t = Peek(i);
                if (t.Kind == CudaTokenKind.EndOfFile) return false;
                if (t.Kind == CudaTokenKind.LeftParen) depth++;
                else if (t.Kind == CudaTokenKind.RightParen)
                {
                    if (--depth == 0) return Peek(i + 1).Kind == CudaTokenKind.LeftBrace;
                }
                i++;
            }
        }

        private Decl ParseTestMacroFunction()
        {
            int start = pos_;
            string macro = Advance().Text;   // TEST_CASE / TEST / ...
            Expect(CudaTokenKind.LeftParen, "'('");

            // Harvest a display name from the macro arguments: Catch2's first string literal
            // ("BoundingBox dimensions"), or GTest's Suite.Name identifier pair.
            string firstString = null;
            List<string> idents = new List<string>();
            int depth = 1;
            while (!AtEnd())
            {
                if (Check(CudaTokenKind.LeftParen)) { depth++; Advance(); continue; }
                if (Check(CudaTokenKind.RightParen))
                {
                    Advance();
                    if (--depth == 0) break;
                    continue;
                }
                if (depth == 1 && firstString == null && Check(CudaTokenKind.StringLiteral))
                    firstString = Cur().Text.Trim('"');
                else if (depth == 1 && Check(CudaTokenKind.Identifier))
                    idents.Add(Cur().Text);
                Advance();
            }
            string name = firstString != null ? firstString
                : idents.Count > 0 ? string.Join(".", idents)
                : macro;

            Stmt body = Check(CudaTokenKind.LeftBrace) ? ParseBodyTrackingLabels() : null;
            return new FunctionDecl(name, new CxxType("void"), new List<Parameter>(),
                FuncSpace.Host, body, SpanFrom(start));
        }

        private Decl ParseNamespace()
        {
            int start = pos_;
            Advance();  // namespace
            string name = string.Empty;
            if (Check(CudaTokenKind.Identifier)) name = Advance().Text;
            // C++17 nested namespace definition: `namespace a::b::c { ... }`. Folding the
            // qualified name into one namespace node keeps the symbol tree simple; without
            // this, every file using the syntax failed at its FIRST line ("Expected '{'")
            // and the recovery corrupted the parse of the whole namespace body after it.
            while (Check(CudaTokenKind.Scope) && Peek().Kind == CudaTokenKind.Identifier)
            {
                Advance();                       // '::'
                name += "::" + Advance().Text;   // next segment
            }
            // Namespace ALIAS: `namespace cg = cooperative_groups;` — no body follows. Every
            // CUDA file that abbreviates cooperative_groups opens with one of these.
            if (Check(CudaTokenKind.Assign))
            {
                Advance();                       // '='
                var target = new System.Text.StringBuilder();
                while (!AtEnd() && !Check(CudaTokenKind.Semicolon))
                {
                    if (Check(CudaTokenKind.Identifier) || Check(CudaTokenKind.Scope)) target.Append(Advance().Text);
                    else Advance();
                }
                Match(CudaTokenKind.Semicolon);
                return new NamespaceAliasDecl(name, target.ToString(), SpanFrom(start));
            }
            if (!Expect(CudaTokenKind.LeftBrace, "'{'")) return null;
            List<Decl> decls = new List<Decl>();
            while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
            {
                int before = pos_;
                Decl d = ParseTopLevelDecl();
                if (d != null) decls.Add(d);
                if (pos_ == before) Advance();
            }
            Expect(CudaTokenKind.RightBrace, "'}'");
            return new NamespaceDecl(name, decls, SpanFrom(start));
        }

        private Decl ParseTemplate()
        {
            int start = pos_;
            Advance();  // template
            List<string> parameters = new List<string>();
            if (Match(CudaTokenKind.Less))
            {
                // Parse template parameter list shallowly: capture type-param names.
                int depth = 1;
                bool expectName = false;
                while (!AtEnd() && depth > 0)
                {
                    CudaTokenKind k = Kind();
                    if (k == CudaTokenKind.Less) ++depth;
                    else if (k == CudaTokenKind.Greater) { --depth; }
                    else if (k == CudaTokenKind.RightShift) { depth -= 2; }
                    else if (k == CudaTokenKind.TripleGreater) { depth -= 3; }  // '>>>' in nested args
                    else if (k == CudaTokenKind.KwTypename || k == CudaTokenKind.KwClass)
                    {
                        expectName = true; Advance(); continue;
                    }
                    else if (expectName && k == CudaTokenKind.Identifier)
                    {
                        parameters.Add(Cur().Text);
                        expectName = false;
                    }
                    else if (k == CudaTokenKind.Comma)
                    {
                        // next parameter
                    }
                    Advance();
                }
            }
            // C++20 requires-CLAUSE between the parameter list and the entity:
            // `template <typename S> requires Shape<S> && std::derived_from<S, IShape> ...`.
            // Constraints aren't modeled; skip the clause so the entity still parses
            // (previously the clause derailed into a bogus declaration parse, spilling
            // "Redefinition"/undefined-name errors over valid concept-constrained code).
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "requires")
                SkipRequiresClause();

            // The templated entity follows.
            Decl inner = ParseTopLevelDecl();
            if (inner == null) return null;
            return new TemplateDecl(parameters, inner, SpanFrom(start));
        }

        // Skips `requires <constraint-expr>`: a sequence of constraint primaries
        // (qualified-id with optional template args, or a parenthesized/braced group)
        // joined by '&&' / '||'. Stops at the first token that is neither — which is
        // where the constrained declaration begins.
        private void SkipRequiresClause()
        {
            Advance();  // 'requires'
            while (!AtEnd())
            {
                if (Check(CudaTokenKind.LeftParen) || Check(CudaTokenKind.LeftBrace))
                {
                    CudaTokenKind open = Kind();
                    CudaTokenKind close = open == CudaTokenKind.LeftParen
                        ? CudaTokenKind.RightParen : CudaTokenKind.RightBrace;
                    int depth = 0;
                    do
                    {
                        if (Kind() == open) depth++;
                        else if (Kind() == close) depth--;
                        Advance();
                    } while (!AtEnd() && depth > 0);
                }
                else if (Check(CudaTokenKind.Identifier) || Check(CudaTokenKind.Scope))
                {
                    while (Check(CudaTokenKind.Identifier) || Check(CudaTokenKind.Scope)) Advance();
                    if (Check(CudaTokenKind.Less))
                    {
                        int past = SkipAnglesAt(pos_);
                        while (pos_ < past) Advance();
                    }
                }
                else
                {
                    break;   // not a constraint primary
                }
                if (Check(CudaTokenKind.AmpAmp) || Check(CudaTokenKind.PipePipe)) { Advance(); continue; }
                break;       // constraint ended — the declaration follows
            }
        }

        private Decl ParseRecord()
        {
            int start = pos_;
            CudaTokenKind tag = Kind();
            Advance();  // struct/class/union
            RecordKind rk = tag == CudaTokenKind.KwClass ? RecordKind.Class
                          : tag == CudaTokenKind.KwUnion ? RecordKind.Union
                                                         : RecordKind.Struct;
            string name = string.Empty;
            if (Check(CudaTokenKind.Identifier)) name = Advance().Text;

            // Optional `final` on the class-head itself (`class Circle final : public Base {`).
            // "final" is a contextual keyword, not a real one, so it lexes as a plain
            // Identifier — without this check it was left sitting where the base-clause/`{`
            // check below expects to find ':' or '{' next, so NEITHER matched and ParseRecord
            // returned an empty record without consuming any of `final : public Base { ... };`
            // at all, corrupting everything parsed afterward via cascading recovery.
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "final") Advance();

            // Optional base-clause: ': public Base1, private Base2, ...'. Coarse per this
            // parser's documented posture (no template-arg modeling on the base itself,
            // just enough to balance <...> so it doesn't confuse the ',' separator).
            List<BaseClassRef> baseClasses = new List<BaseClassRef>();
            AccessSpec defaultMemberAccess = rk == RecordKind.Class ? AccessSpec.Private : AccessSpec.Public;
            if (Match(CudaTokenKind.Colon))
            {
                do
                {
                    bool isVirtual = Match(CudaTokenKind.KwVirtual);
                    AccessSpec baseAccess = AccessSpec.Default;
                    if (Check(CudaTokenKind.KwPublic)) { baseAccess = AccessSpec.Public; Advance(); }
                    else if (Check(CudaTokenKind.KwProtected)) { baseAccess = AccessSpec.Protected; Advance(); }
                    else if (Check(CudaTokenKind.KwPrivate)) { baseAccess = AccessSpec.Private; Advance(); }
                    if (!isVirtual) isVirtual = Match(CudaTokenKind.KwVirtual);

                    var baseName = new System.Text.StringBuilder();
                    while (Check(CudaTokenKind.Identifier) || Check(CudaTokenKind.Scope))
                        baseName.Append(Advance().Text);

                    // Skip a template-argument list on the base, if any: Base<T, U>.
                    if (Check(CudaTokenKind.Less))
                    {
                        int depth = 0;
                        do
                        {
                            if (Check(CudaTokenKind.Less)) depth++;
                            else if (Check(CudaTokenKind.RightShift)) depth -= 2;
                            else if (Check(CudaTokenKind.TripleGreater)) depth -= 3;  // '>>>'
                            else if (Check(CudaTokenKind.Greater)) depth--;
                            Advance();
                        } while (!AtEnd() && depth > 0 && !Check(CudaTokenKind.LeftBrace));
                    }

                    if (baseName.Length > 0)
                        baseClasses.Add(new BaseClassRef(baseName.ToString(),
                            baseAccess == AccessSpec.Default ? defaultMemberAccess : baseAccess, isVirtual));
                } while (Match(CudaTokenKind.Comma));

                // Safety net: if the base-clause contained something this loop didn't
                // recognize, don't get stuck — skip forward to '{'/';' like before.
                while (!AtEnd() && !Check(CudaTokenKind.LeftBrace) && !Check(CudaTokenKind.Semicolon))
                    Advance();
            }
            List<Decl> members = new List<Decl>();
            if (Match(CudaTokenKind.LeftBrace))
            {
                AccessSpec currentAccess = defaultMemberAccess;
                while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
                {
                    // access specifiers.
                    if ((Check(CudaTokenKind.KwPublic) || Check(CudaTokenKind.KwPrivate) ||
                         Check(CudaTokenKind.KwProtected)) && Peek().Kind == CudaTokenKind.Colon)
                    {
                        if (Check(CudaTokenKind.KwPublic)) currentAccess = AccessSpec.Public;
                        else if (Check(CudaTokenKind.KwProtected)) currentAccess = AccessSpec.Protected;
                        else currentAccess = AccessSpec.Private;
                        Advance(); Advance();
                        continue;
                    }
                    while (Match(CudaTokenKind.Semicolon)) { }
                    if (Check(CudaTokenKind.RightBrace)) break;
                    int before = pos_;
                    if ((Check(CudaTokenKind.KwStruct) || Check(CudaTokenKind.KwClass) ||
                         Check(CudaTokenKind.KwUnion)) && LooksLikeRecordDefinition())
                    {
                        Decl d = ParseRecord();
                        if (d != null) { d.Access = currentAccess; members.Add(d); }
                        Match(CudaTokenKind.Semicolon);
                        if (pos_ == before) Advance();
                        continue;
                    }
                    // Member templates / member aliases / member enums have dedicated
                    // parsers the generic declaration path can't substitute for — route
                    // them the way the top level does, or the member (and the rest of the
                    // class body after the failed parse) is lost.
                    if (Check(CudaTokenKind.KwTemplate))
                    {
                        Decl d = ParseTemplate();
                        if (d != null) { d.Access = currentAccess; members.Add(d); }
                        if (pos_ == before) Advance();
                        continue;
                    }
                    if (Check(CudaTokenKind.KwUsing))
                    {
                        Decl d = ParseUsing();
                        if (d != null) { d.Access = currentAccess; members.Add(d); }
                        if (pos_ == before) Advance();
                        continue;
                    }
                    if (Check(CudaTokenKind.KwEnum))
                    {
                        Decl d = ParseEnum();
                        if (d != null) { d.Access = currentAccess; members.Add(d); }
                        if (pos_ == before) Advance();
                        continue;
                    }
                    List<Decl> grp = ParseDeclaration(name);
                    for (int gi = 0; gi < grp.Count; ++gi) { grp[gi].Access = currentAccess; members.Add(grp[gi]); }
                    if (pos_ == before) Advance();
                }
                Expect(CudaTokenKind.RightBrace, "'}'");
            }
            Match(CudaTokenKind.Semicolon);
            return new RecordDecl(rk, name, members, baseClasses, SpanFrom(start));
        }

        private Decl ParseEnum()
        {
            int start = pos_;
            Advance();  // enum
            if (!Match(CudaTokenKind.KwClass)) Match(CudaTokenKind.KwStruct);  // enum class
            string name = string.Empty;
            if (Check(CudaTokenKind.Identifier)) name = Advance().Text;
            // optional ': type'
            if (Match(CudaTokenKind.Colon)) { ParseType(); }
            List<string> enumerators = new List<string>();
            if (Match(CudaTokenKind.LeftBrace))
            {
                while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
                {
                    if (Check(CudaTokenKind.Identifier))
                    {
                        enumerators.Add(Advance().Text);
                        // optional '= value'
                        if (Match(CudaTokenKind.Assign))
                        {
                            while (!AtEnd() && !Check(CudaTokenKind.Comma) &&
                                   !Check(CudaTokenKind.RightBrace)) Advance();
                        }
                        Match(CudaTokenKind.Comma);
                    }
                    else
                    {
                        Advance();
                    }
                }
                Expect(CudaTokenKind.RightBrace, "'}'");
            }
            Match(CudaTokenKind.Semicolon);
            return new EnumDecl(name, enumerators, SpanFrom(start));
        }

        private Decl ParseTypedef()
        {
            int start = pos_;
            Advance();  // typedef
            CxxType t = ParseType();
            string name = string.Empty;
            if (Check(CudaTokenKind.Identifier)) name = Advance().Text;
            while (!AtEnd() && !Check(CudaTokenKind.Semicolon)) Advance();
            Match(CudaTokenKind.Semicolon);
            return new TypeAliasDecl(name, t, SpanFrom(start));
        }

        private Decl ParseUsing()
        {
            int start = pos_;
            Advance();  // using
            if (Match(CudaTokenKind.KwNamespace))
            {
                // `using namespace a::b;` — record the opened namespace so the
                // undefined-name pass can resolve its members used unqualified
                // (returning null here left e.g. Polygon/Point3D unresolvable in
                // every file written in the using-directive style).
                var nsName = new System.Text.StringBuilder();
                while (!AtEnd() && !Check(CudaTokenKind.Semicolon))
                {
                    if (Check(CudaTokenKind.Identifier)) nsName.Append(Cur().Text);
                    else if (Check(CudaTokenKind.Scope)) nsName.Append("::");
                    Advance();
                }
                Match(CudaTokenKind.Semicolon);
                return nsName.Length > 0
                    ? new UsingNamespaceDecl(nsName.ToString(), SpanFrom(start))
                    : (Decl)null;
            }
            string name = string.Empty;
            if (Check(CudaTokenKind.Identifier)) name = Advance().Text;
            // Qualified using-declaration: `using lib_core::errors::HelixError;` introduces
            // the LAST segment into the current scope — recording "lib_core" instead left
            // HelixError unresolvable for the undefined-name pass.
            while (Check(CudaTokenKind.Scope) && Peek().Kind == CudaTokenKind.Identifier)
            {
                Advance();               // '::'
                name = Advance().Text;   // keep the final segment
            }
            CxxType t = new CxxType();
            if (Match(CudaTokenKind.Assign)) t = ParseType();
            while (!AtEnd() && !Check(CudaTokenKind.Semicolon)) Advance();
            Match(CudaTokenKind.Semicolon);
            if (string.IsNullOrEmpty(name)) return null;
            return new TypeAliasDecl(name, t, SpanFrom(start));
        }

        // ===================== Specifiers & types =====================

        private sealed class Specifiers
        {
            public FuncSpace Space = FuncSpace.Host;
            public bool SawHost;
            public bool SawDevice;
            public bool SawGlobal;
            public StorageClass Storage = StorageClass.None;
            public bool IsConst;
            public bool IsConstexpr;
            public bool IsStatic;
            public bool IsExtern;
            public bool IsInline;
            public int LaunchBoundsMaxThreads;
            public bool Valid = true;
        }

        private void FinalizeSpace(Specifiers s)
        {
            if (s.SawGlobal) s.Space = FuncSpace.Global;
            else if (s.SawHost && s.SawDevice) s.Space = FuncSpace.HostDevice;
            else if (s.SawDevice) s.Space = FuncSpace.Device;
            else s.Space = FuncSpace.Host;
        }

        private Specifiers ParseSpecifiers()
        {
            Specifiers s = new Specifiers();
            ConsumeSpecifiers(s);
            FinalizeSpace(s);
            return s;
        }

        // Consume a run of declaration specifiers into an existing Specifiers. C++ allows
        // specifiers on either side of the type (`static Matrix f()` and `Matrix static f()`
        // are both legal), so this is called once before ParseType and again after it —
        // otherwise a post-type `static` strands the declarator parser and the member body
        // is misparsed as class-scope declarations (spurious CU2102 redefinitions).
        private void ConsumeSpecifiers(Specifiers s)
        {
            bool consuming = true;
            while (consuming && !AtEnd())
            {
                switch (Kind())
                {
                    case CudaTokenKind.KwGlobal: s.SawGlobal = true; Advance(); break;
                    case CudaTokenKind.KwDevice:
                        s.SawDevice = true; s.Storage = StorageClass.Device; Advance(); break;
                    case CudaTokenKind.KwHost: s.SawHost = true; Advance(); break;
                    case CudaTokenKind.KwConstant: s.Storage = StorageClass.Constant; Advance(); break;
                    case CudaTokenKind.KwShared: s.Storage = StorageClass.Shared; Advance(); break;
                    case CudaTokenKind.KwManaged: s.Storage = StorageClass.Managed; Advance(); break;
                    case CudaTokenKind.KwForceInline:
                    case CudaTokenKind.KwNoInline:
                        Advance(); break;
                    case CudaTokenKind.KwLaunchBounds:
                        {
                            Advance();
                            if (Match(CudaTokenKind.LeftParen))
                            {
                                if (Check(CudaTokenKind.IntLiteral))
                                {
                                    s.LaunchBoundsMaxThreads = Atoi(Cur().Text);
                                }
                                int depth = 1;
                                while (!AtEnd() && depth > 0)
                                {
                                    if (Check(CudaTokenKind.LeftParen)) ++depth;
                                    else if (Check(CudaTokenKind.RightParen)) --depth;
                                    Advance();
                                }
                            }
                            break;
                        }
                    case CudaTokenKind.KwStatic: s.IsStatic = true; s.Storage = StorageClass.Static; Advance(); break;
                    case CudaTokenKind.KwExtern: s.IsExtern = true; s.Storage = StorageClass.Extern; Advance(); break;
                    case CudaTokenKind.KwInline: s.IsInline = true; Advance(); break;
                    case CudaTokenKind.KwConstexpr: s.IsConstexpr = true; s.IsConst = true; Advance(); break;
                    case CudaTokenKind.KwVirtual:
                    case CudaTokenKind.KwFriend: Advance(); break;
                    case CudaTokenKind.Identifier:
                        // Specifiers with no dedicated keyword token: consuming them here
                        // keeps `mutable std::mutex mtx_;` / `explicit Foo(...)` parseable.
                        if (Cur().Text == "mutable" || Cur().Text == "thread_local" ||
                            // C++20/23 declaration specifiers.
                            Cur().Text == "consteval" || Cur().Text == "constinit" ||
                            // CUDA function/parameter annotations.
                            Cur().Text == "__grid_constant__" || Cur().Text == "__restrict__" ||
                            Cur().Text == "__device_builtin__")
                        { Advance(); break; }
                        // `explicit` and `explicit(bool)` (C++20), plus the CUDA annotations
                        // that carry an argument list: `__cluster_dims__(2,1,1)`, `__maxnreg__(32)`.
                        if (Cur().Text == "explicit" || Cur().Text == "__cluster_dims__" ||
                            Cur().Text == "__maxnreg__" || Cur().Text == "__launch_bounds__")
                        {
                            Advance();
                            if (Check(CudaTokenKind.LeftParen))
                                SkipBalanced(CudaTokenKind.LeftParen, CudaTokenKind.RightParen);
                            break;
                        }
                        consuming = false; break;
                    default: consuming = false; break;
                }
            }
        }

        // Consume a balanced group starting at the current token (which must be `open`).
        private void SkipBalanced(CudaTokenKind open, CudaTokenKind close)
        {
            if (!Check(open)) return;
            int depth = 0;
            while (!AtEnd())
            {
                if (Check(open)) ++depth;
                else if (Check(close)) { --depth; if (depth == 0) { Advance(); return; } }
                Advance();
            }
        }

        private void SkipBalancedAngles()
        {
            // Assumes Cur() is '<'. Consume to matching '>'.
            if (!Check(CudaTokenKind.Less)) return;
            Advance();
            int depth = 1;
            while (!AtEnd() && depth > 0)
            {
                switch (Kind())
                {
                    case CudaTokenKind.Less: ++depth; break;
                    case CudaTokenKind.Greater: --depth; break;
                    case CudaTokenKind.RightShift: depth -= 2; break;
                    case CudaTokenKind.TripleGreater: depth -= 3; break;  // '>>>' in nested args
                    case CudaTokenKind.LeftShift: depth += 2; break;
                    case CudaTokenKind.Semicolon: return;  // safety
                    default: break;
                }
                Advance();
            }
        }

        private bool ParseBaseType(CxxType outType)
        {
            // Leading cv.
            while (true)
            {
                if (Match(CudaTokenKind.KwConst)) { outType.IsConst = true; }
                else if (Match(CudaTokenKind.KwVolatile)) { outType.IsVolatile = true; }
                else break;
            }
            string baseName = string.Empty;
            if (IsBuiltinTypeKeyword(Kind()))
            {
                // Combine a run of builtin type keywords (unsigned long int, ...).
                bool first = true;
                while (IsBuiltinTypeKeyword(Kind()))
                {
                    if (!first) baseName += " ";
                    baseName += Advance().Text;
                    first = false;
                }
            }
            else if (Check(CudaTokenKind.KwStruct) || Check(CudaTokenKind.KwClass) ||
                     Check(CudaTokenKind.KwUnion) || Check(CudaTokenKind.KwEnum))
            {
                Advance();  // elaborated type specifier tag
                if (Check(CudaTokenKind.Identifier)) baseName = Advance().Text;
            }
            else if (Check(CudaTokenKind.KwTypename))
            {
                Advance();
                if (Check(CudaTokenKind.Identifier)) baseName = Advance().Text;
            }
            else if (Check(CudaTokenKind.Identifier))
            {
                baseName = Advance().Text;
                // qualified name a::b::c — with the C++ `template` disambiguator allowed on
                // any dependent segment (`T::template rebind<int>`).
                while (Check(CudaTokenKind.Scope) &&
                       (Peek().Kind == CudaTokenKind.Identifier || Peek().Kind == CudaTokenKind.KwTemplate))
                {
                    Advance();  // ::
                    if (Check(CudaTokenKind.KwTemplate)) Advance();
                    if (!Check(CudaTokenKind.Identifier)) break;
                    baseName += "::";
                    baseName += Advance().Text;
                    if (Check(CudaTokenKind.Less)) SkipBalancedAngles();
                }
            }
            else
            {
                return false;
            }
            outType.Base = baseName;
            // Template arguments: capture shallowly (skip), don't model.
            if (Check(CudaTokenKind.Less))
            {
                SkipBalancedAngles();
            }
            // Trailing cv.
            while (true)
            {
                if (Match(CudaTokenKind.KwConst)) { outType.IsConst = true; }
                else if (Match(CudaTokenKind.KwVolatile)) { outType.IsVolatile = true; }
                else break;
            }
            return true;
        }

        private void ParsePointerAndRef(CxxType type)
        {
            while (true)
            {
                if (Match(CudaTokenKind.Star))
                {
                    ++type.PointerDepth;
                    while (Match(CudaTokenKind.KwConst) || Match(CudaTokenKind.KwVolatile)) { }
                }
                else if (Match(CudaTokenKind.KwRestrict))
                {
                    type.IsRestrict = true;
                }
                else if (Check(CudaTokenKind.Amp) || Check(CudaTokenKind.AmpAmp))
                {
                    Advance();
                    type.IsReference = true;
                }
                else
                {
                    break;
                }
            }
        }

        private CxxType ParseType()
        {
            CxxType t = new CxxType();
            if (!ParseBaseType(t)) return new CxxType();
            ParsePointerAndRef(t);
            return t;
        }

        // C++ out-params id / qualified are returned via a small holder.
        private sealed class DeclaratorName
        {
            public string Id = string.Empty;
            public string Qualified = string.Empty;
        }

        private bool ParseDeclaratorName(DeclaratorName outName)
        {
            // Destructor or operator at start (rare at our granularity).
            if (Check(CudaTokenKind.KwOperator))
            {
                Advance();
                string op = "operator";
                // Consume the operator symbol token(s) until '(' .
                while (!AtEnd() && !Check(CudaTokenKind.LeftParen)) { op += Advance().Text; }
                outName.Id = op; outName.Qualified = op; return true;
            }
            if (!Check(CudaTokenKind.Identifier)) return false;
            string q = Advance().Text;
            string last = q;
            // An out-of-line member of a CLASS TEMPLATE names the template arguments:
            // `template<class T> void A<T>::g() {}`. Without skipping `<T>` the declarator
            // name came out as `A`, which then looked like a redefinition of the template.
            if (Check(CudaTokenKind.Less))
            {
                int after = SkipAnglesAt(pos_);
                if (after > pos_ && KindAt(after) == CudaTokenKind.Scope) { q += "<...>"; pos_ = after; }
            }
            while (Check(CudaTokenKind.Scope))
            {
                Advance();  // ::
                q += "::";
                if (Check(CudaTokenKind.KwTemplate)) Advance();   // dependent-name disambiguator
                if (Check(CudaTokenKind.Identifier))
                {
                    last = Advance().Text; q += last;
                    if (Check(CudaTokenKind.Less))
                    {
                        int a2 = SkipAnglesAt(pos_);
                        if (a2 > pos_ && KindAt(a2) == CudaTokenKind.Scope) pos_ = a2;
                    }
                }
                else if (Check(CudaTokenKind.Tilde) && Peek().Kind == CudaTokenKind.Identifier)
                {
                    Advance(); string d = "~" + Advance().Text; q += d; last = d;
                }
                else break;
            }
            outName.Id = last;
            outName.Qualified = q;
            return true;
        }

        private List<Parameter> ParseParameterList()
        {
            List<Parameter> parameters = new List<Parameter>();
            // Assumes '(' already consumed.
            if (Check(CudaTokenKind.RightParen)) return parameters;
            // 'void' as the sole parameter == no parameters.
            if (Check(CudaTokenKind.KwVoid) && Peek().Kind == CudaTokenKind.RightParen)
            {
                Advance();
                return parameters;
            }
            while (!AtEnd() && !Check(CudaTokenKind.RightParen))
            {
                if (Match(CudaTokenKind.Ellipsis)) break;  // C-variadic
                int pstart = pos_;
                Parameter p = new Parameter();
                // C++23 explicit object parameter, "deducing this" (P0847):
                // `int f(this S& self)` / `auto g(this Self&& self)`. The `this` is a
                // parameter *prefix*, not a type — consume it and parse the rest normally.
                bool explicitObject = Match(CudaTokenKind.KwThis);
                // A parameter attribute: `__grid_constant__ const int p`, `[[maybe_unused]] int x`.
                while (Check(CudaTokenKind.LeftBracket) && KindAt(pos_ + 1) == CudaTokenKind.LeftBracket)
                    SkipBalanced(CudaTokenKind.LeftBracket, CudaTokenKind.RightBracket);
                while (Check(CudaTokenKind.Identifier) &&
                       (Cur().Text == "__grid_constant__" || Cur().Text == "__restrict__")) Advance();
                p.Type = ParseType();
                if (explicitObject && p.Type.IsEmpty()) p.Type = new CxxType("auto");
                // Pack expansion: `Args... rest` / `Args&&... rest`.
                Match(CudaTokenKind.Ellipsis);

                // Function-pointer / function-reference / pointer-to-member parameter:
                //   RetType ( * | & | T::* [name] ) ( paramtypes )
                // e.g. void (&kernel)(int*, int)  |  void (*fp)(int)  |  void (T::*method)(const EV&)
                // Standard C++ that nvcc accepts; without this the declarator's inner
                // '(' derails ParseType's caller into a bogus CU2003 "Expected ')'".
                if (Check(CudaTokenKind.LeftParen) &&
                    (Peek().Kind == CudaTokenKind.Star || Peek().Kind == CudaTokenKind.Amp ||
                     (Peek().Kind == CudaTokenKind.Identifier && Peek(2).Kind == CudaTokenKind.Scope)))
                {
                    Advance();  // '('
                    // Pointer-to-member qualifier segments: `T::` / `ns::T::` before the '*'.
                    while (Check(CudaTokenKind.Identifier) && Peek().Kind == CudaTokenKind.Scope)
                    {
                        Advance();  // segment
                        Advance();  // '::'
                    }
                    // pointer/reference sigils (and any cv-qualifiers) before the name.
                    while (Match(CudaTokenKind.Star) || Match(CudaTokenKind.Amp) ||
                           Match(CudaTokenKind.KwConst) || Match(CudaTokenKind.KwVolatile)) { }
                    p.Type.PointerDepth += 1;  // a callable pointer/reference
                    if (Check(CudaTokenKind.Identifier)) p.Name = Advance().Text;
                    Expect(CudaTokenKind.RightParen, "')'");
                    // The function signature '(' paramtypes ')' — consume it.
                    if (Match(CudaTokenKind.LeftParen))
                    {
                        ParseParameterList();
                        Expect(CudaTokenKind.RightParen, "')'");
                    }
                    if (Match(CudaTokenKind.Assign)) p.DefaultValue = ParseAssignment();
                    p.Span = SpanFrom(pstart);
                    parameters.Add(p);
                    if (!Match(CudaTokenKind.Comma)) break;
                    continue;
                }

                if (Check(CudaTokenKind.Identifier)) p.Name = Advance().Text;
                // array parameter
                while (Match(CudaTokenKind.LeftBracket))
                {
                    p.Type.IsArray = true;
                    while (!AtEnd() && !Check(CudaTokenKind.RightBracket)) Advance();
                    Match(CudaTokenKind.RightBracket);
                }
                if (Match(CudaTokenKind.Assign))
                {
                    p.DefaultValue = ParseAssignment();
                }
                p.Span = SpanFrom(pstart);
                parameters.Add(p);
                if (!Match(CudaTokenKind.Comma)) break;
            }
            return parameters;
        }

        // ===================== Declarations =====================

        // Constructor/destructor declarations (`RecordName(...)`, `~RecordName(...)`) have no
        // return type, so the generic ParseType()-then-declarator-name flow below can't see
        // them: ParseType() would greedily consume the record name itself as if it were a type,
        // leaving no separate declarator-name token to find afterward — the whole statement was
        // previously dropped by the "not a declaration, let caller recover" bail-out. Recognized
        // only when parsing members of a record (enclosingRecordName supplied by ParseRecord).
        private List<Decl> ParseDeclaration(string enclosingRecordName = null)
        {
            List<Decl> outDecls = new List<Decl>();
            int start = pos_;

            // Specifiers FIRST, then the ctor/dtor check: a constructor commonly carries
            // leading specifiers (`__host__ __device__ Point3D() {}`, `explicit Foo(...)`)
            // and the old current-token-only check never matched them — the whole member
            // was dropped and the rest of the record parsed as free declarations.
            Specifiers spec = ParseSpecifiers();

            if (!string.IsNullOrEmpty(enclosingRecordName))
            {
                bool isDtor = Check(CudaTokenKind.Tilde) && Peek().Kind == CudaTokenKind.Identifier
                              && Peek().Text == enclosingRecordName
                              && pos_ + 2 < tokens_.Count && tokens_[pos_ + 2].Kind == CudaTokenKind.LeftParen;
                bool isCtor = !isDtor && Check(CudaTokenKind.Identifier) && Cur().Text == enclosingRecordName
                              && Peek().Kind == CudaTokenKind.LeftParen;
                if (isCtor || isDtor)
                {
                    if (isDtor) Advance();  // '~'
                    Advance();              // the identifier
                    string id = (isDtor ? "~" : string.Empty) + enclosingRecordName;
                    Decl ctorDtor = ParseFunctionDeclaratorTail(id, new CxxType(), spec.Space, start);
                    if (ctorDtor != null) outDecls.Add(ctorDtor);
                    return outDecls;
                }
            }

            CxxType baseType = ParseType();
            // Specifiers may also follow the type (`Matrix static f()` is legal C++).
            ConsumeSpecifiers(spec);
            FinalizeSpace(spec);
            // nvcc: `__global__` marks the device entry point and cannot also be
            // `__host__`/`__device__` — the combination is always rejected.
            if (spec.SawGlobal && (spec.SawHost || spec.SawDevice))
                Report(CudaDiagnosticCodes.ParseExpectedToken, SpanFrom(start),
                    "'__global__' cannot be combined with '__host__' or '__device__'");
            if (baseType.IsEmpty() && spec.Space == FuncSpace.Host &&
                spec.Storage == StorageClass.None)
            {
                // Not a declaration we can parse — let caller recover.
                pos_ = start;
                return outDecls;
            }

            bool first = true;
            while (true)
            {
                // Each declarator may add its own pointer/ref layers on top of baseType.
                CxxType type = baseType.Clone();
                ParsePointerAndRef(type);

                // Function-pointer / pointer-to-member VARIABLE or FIELD declarator:
                // `void (*fp)(int);`, `void (T::*method_)(const EV&);`. The parenthesized
                // declarator otherwise fails ParseDeclaratorName and the member is dropped
                // (its later uses then read as undefined names). The lookahead REQUIRES a
                // '*'/'&' after the optional qualifier — `static_assert(std::is_base_of_v...`
                // and out-of-line constructors also reach here with '(' + qualified name and
                // must fall through to the old recovery instead of erroring.
                if (IsParenthesizedDeclaratorAhead())
                {
                    Advance();  // '('
                    while (Check(CudaTokenKind.Identifier) && Peek().Kind == CudaTokenKind.Scope)
                    { Advance(); Advance(); }   // pointer-to-member qualifier segments
                    while (Match(CudaTokenKind.Star) || Match(CudaTokenKind.Amp) ||
                           Match(CudaTokenKind.KwConst) || Match(CudaTokenKind.KwVolatile)) { }
                    string fpName = Check(CudaTokenKind.Identifier) ? Advance().Text : string.Empty;
                    Expect(CudaTokenKind.RightParen, "')'");
                    if (Match(CudaTokenKind.LeftParen))
                    {
                        ParseParameterList();
                        Expect(CudaTokenKind.RightParen, "')'");
                    }
                    // Trailing cv/noexcept/initializer — consume to the declarator boundary.
                    while (!AtEnd() && !Check(CudaTokenKind.Comma) && !Check(CudaTokenKind.Semicolon) &&
                           !Check(CudaTokenKind.RightBrace)) Advance();
                    if (!string.IsNullOrEmpty(fpName))
                    {
                        CxxType fpType = baseType.Clone();
                        fpType.PointerDepth += 1;
                        outDecls.Add(new VarDecl(fpName, fpType, spec.Storage, null, SpanFrom(start)));
                    }
                    first = false;
                    if (Match(CudaTokenKind.Comma)) continue;
                    break;
                }

                DeclaratorName dn = new DeclaratorName();
                if (!ParseDeclaratorName(dn))
                {
                    // No name — could be an anonymous struct var or junk. Recover.
                    if (first) { Synchronize(); return outDecls; }
                    break;
                }
                string id = dn.Id;

                if (Check(CudaTokenKind.LeftParen) && LooksLikeParameterList())
                {
                    FunctionDecl fn = ParseFunctionDeclaratorTail(id, type, spec.Space, start);
                    fn.QualifiedName = dn.Qualified;   // "Class::method" for out-of-line members
                    if (spec.LaunchBoundsMaxThreads > 0)
                        fn.LaunchBoundsMaxThreads = spec.LaunchBoundsMaxThreads;
                    outDecls.Add(fn);
                    return outDecls;  // functions are not part of a comma declarator list
                }

                // Variable declarator. Array dimensions (capture the first dim expr so
                // the emulator can size local / __shared__ arrays).
                Expr arraySize = null;
                while (Match(CudaTokenKind.LeftBracket))
                {
                    type.IsArray = true;
                    if (!Check(CudaTokenKind.RightBracket))
                    {
                        Expr dim = ParseAssignment();
                        if (arraySize == null) arraySize = dim;
                        // `int arr[-5];` — a negative constant bound is always an error.
                        UnaryExpr negDim = dim as UnaryExpr;
                        if (negDim != null && negDim.Op == UnaryOpKind.Minus && negDim.Operand is LiteralExpr)
                            Report(CudaDiagnosticCodes.ParseExpectedToken, SpanFrom(start),
                                "Array size is negative");
                    }
                    Expect(CudaTokenKind.RightBracket, "']'");
                }
                Expr init = null;
                if (Match(CudaTokenKind.Assign))
                {
                    init = ParseAssignment();
                }
                else if (Check(CudaTokenKind.LeftBrace))
                {
                    init = ParsePrimary();  // brace-init list
                }
                else if (Check(CudaTokenKind.LeftParen))
                {
                    // direct-init: Type name(args)
                    Advance();
                    List<Expr> args = ParseCallArgs();
                    Expect(CudaTokenKind.RightParen, "')'");
                    // Represent as an InitListExpr of the args for analysis purposes.
                    init = new InitListExpr(args, SpanFrom(start));
                }
                // `auto x;` — auto deduces from the initializer, so its absence is an error.
                // Only at a real declaration terminator: a range-for head (`for (const
                // auto& item : c)`) legitimately has no '=' — its declarator is followed
                // by ':', not ';'.
                if (init == null && arraySize == null && type.PointerDepth == 0 &&
                    string.Equals(type.Base, "auto", System.StringComparison.Ordinal) &&
                    (Check(CudaTokenKind.Semicolon) || Check(CudaTokenKind.Comma)))
                    Report(CudaDiagnosticCodes.ParseExpectedToken, SpanFrom(start),
                        "Declaration of 'auto " + id + "' has no initializer");
                if (spec.IsConst) type.IsConst = true;
                VarDecl var = new VarDecl(id, type, spec.Storage, init, SpanFrom(start));
                if (arraySize != null) var.ArraySize = arraySize;
                outDecls.Add(var);
                first = false;
                if (Match(CudaTokenKind.Comma)) continue;
                break;
            }
            Match(CudaTokenKind.Semicolon);
            return outDecls;
        }

        // ===================== Statements =====================

        // Shared tail of a function declarator, from the '(' onward: parameter list, trailing
        // qualifiers (const/noexcept/override/final/trailing-return), '= 0'/'= default'/'= delete',
        // then either a body or a prototype ';'. Used both by the normal Type Name(...) path in
        // ParseDeclaration and by the constructor/destructor special-case above it (which has no
        // return type to parse and jumps straight here once it has consumed the name).
        private FunctionDecl ParseFunctionDeclaratorTail(string id, CxxType returnType, FuncSpace space, int start)
        {
            Advance();  // '('
            List<Parameter> parameters = ParseParameterList();
            Expect(CudaTokenKind.RightParen, "')'");
            // Trailing function qualifiers / specifiers.
            while (true)
            {
                if (Match(CudaTokenKind.KwConst) || Match(CudaTokenKind.KwVolatile)) continue;
                if (Check(CudaTokenKind.Identifier) &&
                    (Cur().Text == "noexcept" || Cur().Text == "override" ||
                     Cur().Text == "final"))
                {
                    Advance();
                    if (Check(CudaTokenKind.LeftParen))
                    {  // noexcept(expr)
                        int d = 0;
                        do
                        {
                            if (Check(CudaTokenKind.LeftParen)) ++d;
                            else if (Check(CudaTokenKind.RightParen)) --d;
                            Advance();
                        } while (!AtEnd() && d > 0);
                    }
                    continue;
                }
                if (Check(CudaTokenKind.Arrow)) { Advance(); ParseType(); continue; }  // trailing return
                break;
            }
            // '= 0' / '= default' / '= delete'
            if (Match(CudaTokenKind.Assign))
            {
                Advance();  // 0 / default / delete token
            }
            // Constructor member-initializer list: ': mem1(expr), mem2{expr}, Base(args) {'.
            // Without this, a ctor written as `Circle(double r) : radius_(r) {}` left the
            // parser sitting at ':' unable to match either the body or the prototype ';'
            // branch below, corrupting everything parsed after it via cascading recovery.
            if (Check(CudaTokenKind.Colon))
            {
                Advance();
                do
                {
                    while (Check(CudaTokenKind.Identifier) || Check(CudaTokenKind.Scope)) Advance();
                    // Templated base initializer: `: Step<T>(std::move(name))` — skip the
                    // template arguments so the '(' below is reachable.
                    if (Check(CudaTokenKind.Less))
                    {
                        int past = SkipAnglesAt(pos_);
                        while (pos_ < past) Advance();
                    }
                    if (Check(CudaTokenKind.LeftParen) || Check(CudaTokenKind.LeftBrace))
                    {
                        CudaTokenKind open = Kind();
                        CudaTokenKind close = open == CudaTokenKind.LeftParen
                            ? CudaTokenKind.RightParen : CudaTokenKind.RightBrace;
                        int depth = 0;
                        do
                        {
                            if (Kind() == open) depth++;
                            else if (Kind() == close) depth--;
                            Advance();
                        } while (!AtEnd() && depth > 0);
                    }
                } while (Match(CudaTokenKind.Comma));
            }
            Stmt body = null;
            if (Check(CudaTokenKind.LeftBrace))
            {
                body = ParseBodyTrackingLabels();
            }
            else
            {
                Match(CudaTokenKind.Semicolon);  // prototype
            }
            return new FunctionDecl(id, returnType, parameters, space, body, SpanFrom(start));
        }

        // Parses a function body while tracking goto targets vs defined labels, and
        // reports any `goto missing;` once the body closes. Save/restore keeps nested
        // function parses (local records with methods, test-macro bodies) independent.
        private Stmt ParseBodyTrackingLabels()
        {
            HashSet<string> savedLabels = fnLabels_;
            List<KeyValuePair<string, SourceSpan>> savedGotos = fnGotos_;
            fnLabels_ = new HashSet<string>(System.StringComparer.Ordinal);
            fnGotos_ = new List<KeyValuePair<string, SourceSpan>>();
            Stmt body;
            try
            {
                body = ParseCompound();
                for (int i = 0; i < fnGotos_.Count; ++i)
                {
                    if (!fnLabels_.Contains(fnGotos_[i].Key))
                        Report(CudaDiagnosticCodes.ParseExpectedToken, fnGotos_[i].Value,
                            "Label '" + fnGotos_[i].Key + "' used but not defined in this function");
                }
            }
            finally
            {
                fnLabels_ = savedLabels;
                fnGotos_ = savedGotos;
            }
            return body;
        }

        private Stmt ParseCompound()
        {
            int start = pos_;
            Expect(CudaTokenKind.LeftBrace, "'{'");
            List<Stmt> body = new List<Stmt>();
            while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
            {
                int before = pos_;
                Stmt s = ParseStatement();
                if (s != null) body.Add(s);
                if (pos_ == before) Advance();  // forward progress
            }
            Expect(CudaTokenKind.RightBrace, "'}'");
            return new CompoundStmt(body, SpanFrom(start));
        }

        private Stmt ParseStatement()
        {
            int start = pos_;
            switch (Kind())
            {
                case CudaTokenKind.LeftBrace: return ParseCompound();
                case CudaTokenKind.Semicolon: Advance(); return new EmptyStmt(SpanFrom(start));
                case CudaTokenKind.KwIf: return ParseIf();
                case CudaTokenKind.KwFor: return ParseFor();
                case CudaTokenKind.KwWhile: return ParseWhile();
                case CudaTokenKind.KwDo: return ParseDoWhile();
                case CudaTokenKind.KwSwitch: return ParseSwitch();
                case CudaTokenKind.KwReturn: return ParseReturn();
                case CudaTokenKind.KwBreak:
                    Advance(); Match(CudaTokenKind.Semicolon);
                    return new BreakStmt(SpanFrom(start));
                case CudaTokenKind.KwContinue:
                    Advance(); Match(CudaTokenKind.Semicolon);
                    return new ContinueStmt(SpanFrom(start));
                case CudaTokenKind.KwNamespace:
                    {
                        // `namespace cg = cooperative_groups;` inside a function body — a
                        // block-scope namespace alias, the idiom every CUDA kernel using
                        // cooperative groups opens with.
                        Decl nd = ParseNamespace();
                        return nd == null
                            ? (Stmt)new EmptyStmt(SpanFrom(start))
                            : new DeclStmt(nd, SpanFrom(start));
                    }
                case CudaTokenKind.KwUsing:
                    {
                        // `using namespace a::b;` / `using X = Y;` inside a function body —
                        // route to the same ParseUsing the top level uses instead of letting
                        // the declaration parser derail on it.
                        Decl u = ParseUsing();
                        return u != null ? (Stmt)new DeclStmt(u, SpanFrom(start))
                                         : new EmptyStmt(SpanFrom(start));
                    }
                case CudaTokenKind.KwGoto:
                    {
                        // `goto label;` — record the target so an undefined label can be
                        // reported once the enclosing function body finishes.
                        Advance();
                        if (Check(CudaTokenKind.Identifier))
                        {
                            if (fnGotos_ != null)
                                fnGotos_.Add(new KeyValuePair<string, SourceSpan>(Cur().Text, Cur().Span));
                            Advance();
                        }
                        Match(CudaTokenKind.Semicolon);
                        return new EmptyStmt(SpanFrom(start));
                    }
                case CudaTokenKind.KwCase:
                    Advance(); ParseExpression(); Match(CudaTokenKind.Colon);
                    return ParseStatement();  // label: the labeled statement is the real node
                case CudaTokenKind.KwDefault:
                    if (Peek().Kind == CudaTokenKind.Colon)
                    {
                        Advance(); Advance();
                        return ParseStatement();
                    }
                    break;
                default: break;
            }

            // `co_return [expr];` (C++20) — lexed as an identifier, so it must be handled
            // before LooksLikeDeclaration mistakes it for a declaration.
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "co_return")
            {
                Advance();
                Expr cv = null;
                if (!Check(CudaTokenKind.Semicolon)) cv = ParseExpression();
                Match(CudaTokenKind.Semicolon);
                return new ReturnStmt(cv, SpanFrom(start));
            }

            // `asm ("...")` / `__asm__ volatile ("..." : outs : ins : clobbers);` — GNU inline
            // assembly, ubiquitous in CUDA device code for PTX. Consumed wholesale.
            if (Check(CudaTokenKind.Identifier)
                && (Cur().Text == "asm" || Cur().Text == "__asm__" || Cur().Text == "__asm"))
            {
                Advance();
                while (Check(CudaTokenKind.Identifier) &&
                       (Cur().Text == "volatile" || Cur().Text == "__volatile__" || Cur().Text == "inline"
                        || Cur().Text == "goto")) Advance();
                if (Check(CudaTokenKind.KwVolatile)) Advance();
                if (Check(CudaTokenKind.LeftParen)) SkipBalanced(CudaTokenKind.LeftParen, CudaTokenKind.RightParen);
                Match(CudaTokenKind.Semicolon);
                return new CompoundStmt(new List<Stmt>(), SpanFrom(start));
            }

            // A label: `name:` — including C++23's label at the end of a compound statement
            // (P2324), where nothing follows before the '}'. The name is registered so the
            // per-function goto/label check still sees it.
            if (Check(CudaTokenKind.Identifier) && Peek().Kind == CudaTokenKind.Colon
                && KindAt(pos_ + 2) != CudaTokenKind.Colon)
            {
                if (fnLabels_ != null) fnLabels_.Add(Cur().Text);
                Advance();  // name
                Advance();  // ':'
                if (Check(CudaTokenKind.RightBrace) || AtEnd())
                    return new CompoundStmt(new List<Stmt>(), SpanFrom(start));
                return ParseStatement();
            }

            // 'try'/'catch' are lexed as identifiers (no dedicated keyword tokens).
            // Modeled as sequential blocks: a CompoundStmt of [try-body, handlers...],
            // each handler wrapped so its catch parameter is declared inside it. That
            // keeps every expression walkable and block scoping natural without a
            // dedicated TryStmt node the analyses would all need to learn.
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "try" &&
                Peek().Kind == CudaTokenKind.LeftBrace)
            {
                Advance();  // try
                List<Stmt> parts = new List<Stmt>();
                parts.Add(ParseCompound());
                while (Check(CudaTokenKind.Identifier) && Cur().Text == "catch")
                {
                    int hs = pos_;
                    Advance();  // catch
                    string paramName = null;
                    if (Match(CudaTokenKind.LeftParen))
                    {
                        // Skip the exception-declaration; remember the LAST identifier
                        // before ')' — that's the parameter name when one exists
                        // (`const std::exception& e` -> e; `...` -> none).
                        int depth = 1;
                        while (!AtEnd() && depth > 0)
                        {
                            CudaTokenKind k = Kind();
                            if (k == CudaTokenKind.LeftParen) depth++;
                            else if (k == CudaTokenKind.RightParen) depth--;
                            else if (k == CudaTokenKind.Identifier) paramName = Cur().Text;
                            Advance();
                        }
                    }
                    Stmt handler = Check(CudaTokenKind.LeftBrace) ? ParseCompound() : ParseStatement();
                    if (!string.IsNullOrEmpty(paramName))
                    {
                        List<Stmt> wrapped = new List<Stmt>();
                        wrapped.Add(new DeclStmt(
                            new VarDecl(paramName, new CxxType(), StorageClass.None, null, SpanFrom(hs)),
                            SpanFrom(hs)));
                        wrapped.Add(handler);
                        handler = new CompoundStmt(wrapped, SpanFrom(hs));
                    }
                    parts.Add(handler);
                }
                return new CompoundStmt(parts, SpanFrom(start));
            }

            // 'throw' is lexed as an identifier (there is no dedicated keyword token).
            // Handle it as a statement so `throw std::runtime_error("x");` is not
            // misparsed by LooksLikeDeclaration as a function declaration
            // (`throw` return type + `runtime_error(...)` declarator), which corrupts
            // local-scope tracking and yields spurious CU2102 redefinitions downstream.
            if (Check(CudaTokenKind.Identifier) && Cur().Text == "throw")
            {
                Advance();  // throw
                Expr thrown = null;
                if (!Check(CudaTokenKind.Semicolon)) thrown = ParseExpression();
                Match(CudaTokenKind.Semicolon);
                return thrown != null
                    ? (Stmt)new ExprStmt(thrown, SpanFrom(start))
                    : new EmptyStmt(SpanFrom(start));
            }

            // Local record definition — `struct Case { ... };` inside a function body
            // (Catch2's GENERATE idiom uses one per test). Only the TOP-level path routed
            // record definitions to ParseRecord; in statements they fell into the generic
            // declaration parser, whose failure on the brace body made recovery resync
            // MID-FUNCTION and spill the remaining locals to file scope.
            if ((Check(CudaTokenKind.KwStruct) || Check(CudaTokenKind.KwClass) ||
                 Check(CudaTokenKind.KwUnion)) && LooksLikeRecordDefinition())
            {
                Decl rec = ParseRecord();
                return rec != null ? (Stmt)new DeclStmt(rec, SpanFrom(start))
                                   : new EmptyStmt(SpanFrom(start));
            }

            // C++17 structured binding as a plain statement: `auto [it, inserted] = m.insert(...);`.
            if (IsStructuredBindingAhead())
            {
                while (!AtEnd() && !Check(CudaTokenKind.LeftBracket)) Advance();  // cv/auto/&
                Advance();  // '['
                List<Stmt> binds = new List<Stmt>();
                while (!AtEnd() && !Check(CudaTokenKind.RightBracket))
                {
                    if (Check(CudaTokenKind.Identifier))
                        binds.Add(new DeclStmt(
                            new VarDecl(Cur().Text, new CxxType(), StorageClass.None, null, SpanFrom(pos_)),
                            SpanFrom(pos_)));
                    Advance();
                }
                Match(CudaTokenKind.RightBracket);
                if (Match(CudaTokenKind.Assign)) ParseExpression();
                Match(CudaTokenKind.Semicolon);
                CompoundStmt sbGrp = new CompoundStmt(binds, SpanFrom(start));
                sbGrp.IsSyntheticGroup = true;
                return sbGrp;
            }

            if (LooksLikeDeclaration())
            {
                int declEnd = pos_;
                List<Decl> grp = ParseDeclaration();
                if (grp.Count == 0) return null;
                ReportMissingDeclarationSemicolon(declEnd);
                if (grp.Count == 1)
                    return new DeclStmt(grp[0], SpanFrom(start));
                // Multiple declarators -> a synthetic GROUP of DeclStmts (not a real block:
                // `double a, b;` declares into the enclosing scope).
                List<Stmt> stmts = new List<Stmt>();
                for (int gi = 0; gi < grp.Count; ++gi)
                    stmts.Add(new DeclStmt(grp[gi], SpanFrom(start)));
                CompoundStmt declGrp = new CompoundStmt(stmts, SpanFrom(start));
                declGrp.IsSyntheticGroup = true;
                return declGrp;
            }

            // Goto label: `retry:` — an identifier directly followed by ':' (a qualified
            // name lexes '::' as Scope, so this never fires for `std::x`).
            if (Check(CudaTokenKind.Identifier) && Peek().Kind == CudaTokenKind.Colon)
            {
                if (fnLabels_ != null) fnLabels_.Add(Cur().Text);
                Advance();  // label
                Advance();  // ':'
                return ParseStatement();
            }

            // Expression statement.
            Expr e = ParseExpression();
            // Catch2's SECTION("...") { ... } (and GIVEN/WHEN/THEN): a macro call directly
            // followed by a block is a statement WITH a body, not a missing semicolon.
            if (e is CallExpr && Check(CudaTokenKind.LeftBrace))
            {
                Stmt macroBody = ParseCompound();
                List<Stmt> pair = new List<Stmt>();
                pair.Add(new ExprStmt(e, SpanFrom(start)));
                pair.Add(macroBody);
                return new CompoundStmt(pair, SpanFrom(start));
            }
            if (e == null)
            {
                Match(CudaTokenKind.Semicolon);   // the failed expression already errored
                return new EmptyStmt(SpanFrom(start));
            }
            // A real syntax error nvcc/cl reject: an expression statement without its ';'
            // (e.g. a junk line like `x1x23444x123`). This used to be silently tolerated,
            // so typing garbage produced NO squiggle at all.
            Expect(CudaTokenKind.Semicolon, "';'");
            return new ExprStmt(e, SpanFrom(start));
        }

        private Stmt ParseIf()
        {
            int start = pos_;
            Advance();  // if
            // `if constexpr (...)` — the constexpr adds nothing to this parser's model.
            Match(CudaTokenKind.KwConstexpr);
            // C++23 `if consteval { … } else { … }` / `if !consteval { … }` (P1938): no
            // condition and no parentheses at all.
            bool bang = Check(CudaTokenKind.Bang);
            if ((bang && PeekIsIdent(1, "consteval")) || CheckIdent("consteval"))
            {
                if (bang) Advance();
                Advance();                                   // consteval
                Stmt cthen = ParseStatement();
                Stmt cels = null;
                if (Match(CudaTokenKind.KwElse)) cels = ParseStatement();
                return new IfStmt(null, cthen, cels, SpanFrom(start));
            }
            Expect(CudaTokenKind.LeftParen, "'('");
            // C++17 init-statement: `if (int n = g(); n > 0)`. Parse the declaration, consume
            // the ';', then fall through to the real condition.
            Stmt initStmt = null;
            if (LooksLikeDeclaration() && ConditionHasInitStatement())
            {
                int si = pos_;
                List<Decl> ig = ParseDeclaration();           // consumes the ';'
                if (ig.Count > 0) initStmt = new DeclStmt(ig[0], SpanFrom(si));
            }
            // Condition declaration: `if (const auto* c = dynamic_cast<const Circle*>(&s))`.
            // Parsed as a declaration + an if on the declared name, wrapped in a compound so
            // the local is scoped and walkable; a plain expression condition is unchanged
            // (LooksLikeDeclaration is conservative — a bare `x == y` never matches).
            Stmt condDecl = null;
            Expr cond = null;
            // The initializer requirement disambiguates: `use_new && q` also matches the
            // `Type&& name` declarator shape, but a condition DECLARATION must carry an
            // '='/braced initializer — a plain boolean expression never does.
            if (LooksLikeDeclaration() && ConditionHasInitializer())
            {
                int s2 = pos_;
                List<Decl> grp = ParseDeclaration();
                if (grp.Count > 0)
                {
                    condDecl = new DeclStmt(grp[0], SpanFrom(s2));
                    VarDecl vd = grp[0] as VarDecl;
                    if (vd != null) cond = new NameExpr(vd.Name, vd.Name, SpanFrom(s2));
                }
            }
            else
            {
                cond = ParseExpression();
            }
            Expect(CudaTokenKind.RightParen, "')'");
            Stmt then = ParseStatement();
            Stmt els = null;
            if (Match(CudaTokenKind.KwElse)) els = ParseStatement();
            Stmt ifs = new IfStmt(cond, then, els, SpanFrom(start));
            if (condDecl == null && initStmt == null) return ifs;
            List<Stmt> seq = new List<Stmt>();
            if (initStmt != null) seq.Add(initStmt);
            if (condDecl != null) seq.Add(condDecl);
            seq.Add(ifs);
            CompoundStmt wrapped = new CompoundStmt(seq, SpanFrom(start));
            wrapped.IsSyntheticGroup = true;   // the init-statement scopes to the if, not a block
            return wrapped;
        }

        // Lookahead only: the current condition contains a top-level ';' before its ')' — the
        // marker of a C++17 init-statement (`if (auto n = f(); n > 0)`).
        private bool ConditionHasInitStatement()
        {
            int depth = 0;
            for (int i = pos_; i < tokens_.Count; i++)
            {
                CudaTokenKind k = tokens_[i].Kind;
                if (k == CudaTokenKind.LeftParen || k == CudaTokenKind.LeftBracket) { depth++; continue; }
                if (k == CudaTokenKind.RightParen || k == CudaTokenKind.RightBracket)
                {
                    if (depth == 0) return false;
                    depth--; continue;
                }
                if (depth == 0 && k == CudaTokenKind.Semicolon) return true;
                if (k == CudaTokenKind.LeftBrace || k == CudaTokenKind.EndOfFile) return false;
            }
            return false;
        }

        private bool CheckIdent(string lexeme)
        {
            return Check(CudaTokenKind.Identifier) && Cur().Text == lexeme;
        }

        private bool PeekIsIdent(int offset, string lexeme)
        {
            return KindAt(pos_ + offset) == CudaTokenKind.Identifier
                && pos_ + offset < tokens_.Count && tokens_[pos_ + offset].Text == lexeme;
        }

        // Lookahead only: '(' then optional pointer-to-member qualifier segments then a
        // MANDATORY '*' or '&' — the shape of a parenthesized declarator. A '(' followed
        // by a plain qualified NAME (static_assert argument, constructor parameter list)
        // is not one.
        // Lookahead only: a `for` head that has BOTH a top-level ';' and, after it, a top-level
        // ':' before the closing ')' — the C++20 `for (init; decl : range)` shape. A classic
        // three-clause for has no such ':' (a ternary's ':' sits inside its own parens or after
        // a '?', which is checked too).
        // A declaration STATEMENT must end with ';'. ParseDeclaration stops without one when the
        // source omits it (`int i = threadIdx.x` then a new line), which used to pass silently —
        // nvcc rejects it. Reported only when the previous token is not a ';'/'}' and the next
        // token starts a LATER line, so a for-head (`;` inside parens), a range-for (':'), a
        // condition declaration and `int a = 1, b = 2;` are all untouched.
        private void ReportMissingDeclarationSemicolon(int declStartIndex)
        {
            if (pos_ <= declStartIndex || pos_ == 0) return;
            CudaTokenKind prev = KindAt(pos_ - 1);
            if (prev == CudaTokenKind.Semicolon || prev == CudaTokenKind.RightBrace) return;
            CudaTokenKind next = Kind();
            if (next == CudaTokenKind.EndOfFile || next == CudaTokenKind.RightBrace
                || next == CudaTokenKind.Semicolon || next == CudaTokenKind.Colon
                || next == CudaTokenKind.RightParen || next == CudaTokenKind.Comma) return;
            if (pos_ - 1 >= tokens_.Count) return;
            int prevLine = tokens_[pos_ - 1].Span.End.Line;
            int nextLine = tokens_[pos_].Span.Start.Line;
            if (nextLine <= prevLine) return;                 // same line: leave to other rules
            Report(CudaDiagnosticCodes.ParseExpectedToken, tokens_[pos_ - 1].Span,
                "Expected ';' at the end of the declaration");
        }

        private bool IsRangeForWithInitStatement()
        {
            // Called with the head's '(' ALREADY consumed, so the scan starts one level in.
            int depth = 1;
            bool sawSemi = false, sawQuestion = false;
            for (int i = pos_; i < tokens_.Count; i++)
            {
                CudaTokenKind k = tokens_[i].Kind;
                if (k == CudaTokenKind.LeftParen || k == CudaTokenKind.LeftBracket
                    || k == CudaTokenKind.LeftBrace) { depth++; continue; }
                if (k == CudaTokenKind.RightParen || k == CudaTokenKind.RightBracket
                    || k == CudaTokenKind.RightBrace)
                {
                    depth--;
                    if (depth == 0) return false;
                    continue;
                }
                if (depth != 1) continue;
                if (k == CudaTokenKind.Semicolon) { sawSemi = true; sawQuestion = false; continue; }
                if (k == CudaTokenKind.Question) { sawQuestion = true; continue; }
                if (k == CudaTokenKind.Colon && sawSemi && !sawQuestion) return true;
                if (k == CudaTokenKind.EndOfFile) return false;
            }
            return false;
        }

        private bool IsParenthesizedDeclaratorAhead()
        {
            if (!Check(CudaTokenKind.LeftParen)) return false;
            int i = pos_ + 1;
            while (KindAt(i) == CudaTokenKind.Identifier && KindAt(i + 1) == CudaTokenKind.Scope) i += 2;
            return KindAt(i) == CudaTokenKind.Star || KindAt(i) == CudaTokenKind.Amp;
        }

        // Lookahead only: true when the current if-condition (up to its closing ')') contains
        // a top-level '=' or '{' — the initializer a condition declaration must have.
        private bool ConditionHasInitializer()
        {
            int depth = 0;
            for (int i = pos_; i < tokens_.Count; i++)
            {
                CudaTokenKind k = tokens_[i].Kind;
                if (k == CudaTokenKind.LeftParen) { depth++; continue; }
                if (k == CudaTokenKind.RightParen)
                {
                    if (depth == 0) return false;
                    depth--; continue;
                }
                if (depth == 0 && (k == CudaTokenKind.Assign || k == CudaTokenKind.LeftBrace)) return true;
                if (k == CudaTokenKind.Semicolon || k == CudaTokenKind.EndOfFile) return false;
            }
            return false;
        }

        private Stmt ParseFor()
        {
            int start = pos_;
            Advance();  // for
            Expect(CudaTokenKind.LeftParen, "'('");
            // C++20 range-for with an init-statement: `for (int i = 0; auto x : v)`. The
            // init-statement is an ordinary declaration ending at its ';'; what follows is a
            // normal range-for head, so parse the init here and let the rest run unchanged.
            Stmt rangeInit = null;
            if (IsRangeForWithInitStatement())
            {
                int ri = pos_;
                List<Decl> ig = ParseDeclaration();       // consumes the ';'
                if (ig.Count > 0) rangeInit = new DeclStmt(ig[0], SpanFrom(ri));
                else Match(CudaTokenKind.Semicolon);
            }
            // init
            Stmt init = null;
            if (!Check(CudaTokenKind.Semicolon))
            {
                // C++17 structured binding over a range: `for (const auto& [k, v] : map_)`.
                // Each name binds as an untyped local; the pair/tuple decomposition itself
                // isn't modeled. Without this the declaration parser failed at '[' and the
                // recovery ate the rest of the enclosing function.
                if (IsStructuredBindingAhead())
                {
                    while (!AtEnd() && !Check(CudaTokenKind.LeftBracket)) Advance();  // cv/auto/&
                    Advance();  // '['
                    List<Stmt> bindings = new List<Stmt>();
                    while (!AtEnd() && !Check(CudaTokenKind.RightBracket))
                    {
                        if (Check(CudaTokenKind.Identifier))
                            bindings.Add(new DeclStmt(
                                new VarDecl(Cur().Text, new CxxType(), StorageClass.None, null, SpanFrom(pos_)),
                                SpanFrom(pos_)));
                        Advance();
                    }
                    Match(CudaTokenKind.RightBracket);
                    Expect(CudaTokenKind.Colon, "':'");
                    ParseExpression();  // range
                    Expect(CudaTokenKind.RightParen, "')'");
                    Stmt sbBody = ParseStatement();
                    CompoundStmt sbInit = new CompoundStmt(bindings, SpanFrom(start));
                    sbInit.IsSyntheticGroup = true;   // the names scope to the loop, not a sub-block
                    return new ForStmt(sbInit, null, null, sbBody, SpanFrom(start));
                }
                if (LooksLikeDeclaration())
                {
                    int s2 = pos_;
                    List<Decl> grp = ParseDeclaration();  // consumes trailing ';' if present
                    if (grp.Count != 0)
                        init = new DeclStmt(grp[0], SpanFrom(s2));
                    // Range-for: ParseDeclaration stops at ':' — detect & skip range.
                    if (Check(CudaTokenKind.Colon))
                    {
                        Advance();
                        ParseExpression();  // range
                        Expect(CudaTokenKind.RightParen, "')'");
                        Stmt rangeBody = ParseStatement();
                        Stmt loop = new ForStmt(init, null, null, rangeBody, SpanFrom(start));
                        if (rangeInit == null) return loop;
                        // The C++20 init-statement scopes to the loop, not to a sub-block.
                        CompoundStmt withInit = new CompoundStmt(new List<Stmt> { rangeInit, loop }, SpanFrom(start));
                        withInit.IsSyntheticGroup = true;
                        return withInit;
                    }
                }
                else
                {
                    int s2 = pos_;
                    Expr e = ParseExpression();
                    Match(CudaTokenKind.Semicolon);
                    if (e != null) init = new ExprStmt(e, SpanFrom(s2));
                }
            }
            else
            {
                Advance();  // ';'
            }
            // cond
            Expr cond = null;
            if (!Check(CudaTokenKind.Semicolon)) cond = ParseExpression();
            Match(CudaTokenKind.Semicolon);
            // inc
            Expr inc = null;
            if (!Check(CudaTokenKind.RightParen)) inc = ParseExpression();
            Expect(CudaTokenKind.RightParen, "')'");
            Stmt body = ParseStatement();
            return new ForStmt(init, cond, inc, body, SpanFrom(start));
        }

        private Stmt ParseWhile()
        {
            int start = pos_;
            Advance();  // while
            Expect(CudaTokenKind.LeftParen, "'('");
            Expr cond = ParseExpression();
            Expect(CudaTokenKind.RightParen, "')'");
            Stmt body = ParseStatement();
            return new WhileStmt(cond, body, SpanFrom(start));
        }

        private Stmt ParseDoWhile()
        {
            int start = pos_;
            Advance();  // do
            Stmt body = ParseStatement();
            Expect(CudaTokenKind.KwWhile, "'while'");
            Expect(CudaTokenKind.LeftParen, "'('");
            Expr cond = ParseExpression();
            Expect(CudaTokenKind.RightParen, "')'");
            Match(CudaTokenKind.Semicolon);
            return new DoStmt(body, cond, SpanFrom(start));
        }

        private Stmt ParseSwitch()
        {
            int start = pos_;
            Advance();  // switch
            Expect(CudaTokenKind.LeftParen, "'('");
            // C++17 init-statement: `switch (int k = h(); k)`.
            Stmt initStmt = null;
            if (LooksLikeDeclaration() && ConditionHasInitStatement())
            {
                int si = pos_;
                List<Decl> ig = ParseDeclaration();           // consumes the ';'
                if (ig.Count > 0) initStmt = new DeclStmt(ig[0], SpanFrom(si));
            }
            Expr cond = ParseExpression();
            Expect(CudaTokenKind.RightParen, "')'");
            Stmt body = ParseStatement();
            Stmt sw = new SwitchStmt(cond, body, SpanFrom(start));
            if (initStmt == null) return sw;
            CompoundStmt wrapped = new CompoundStmt(new List<Stmt> { initStmt, sw }, SpanFrom(start));
            wrapped.IsSyntheticGroup = true;
            return wrapped;
        }

        private Stmt ParseReturn()
        {
            int start = pos_;
            Advance();  // return
            Expr v = null;
            if (!Check(CudaTokenKind.Semicolon)) v = ParseExpression();
            Match(CudaTokenKind.Semicolon);
            return new ReturnStmt(v, SpanFrom(start));
        }

        // Lookahead only: cv-qualifiers / auto / '&'/'&&' then '[' — the shape of a
        // structured-binding declaration head. Anything else (an array index, a lambda
        // capture) has a non-qualifier token before the bracket and returns false.
        private bool IsStructuredBindingAhead()
        {
            for (int i = pos_; i < tokens_.Count; i++)
            {
                switch (tokens_[i].Kind)
                {
                    case CudaTokenKind.KwConst:
                    case CudaTokenKind.KwVolatile:
                    case CudaTokenKind.KwAuto:
                    case CudaTokenKind.Amp:
                    case CudaTokenKind.AmpAmp:
                        continue;
                    case CudaTokenKind.LeftBracket:
                        return i > pos_;   // at least one qualifier/auto before '['
                    default:
                        return false;
                }
            }
            return false;
        }

        private bool LooksLikeDeclaration()
        {
            CudaTokenKind k = Kind();
            if (IsBuiltinTypeKeyword(k)) return true;
            switch (k)
            {
                case CudaTokenKind.KwConst:
                case CudaTokenKind.KwConstexpr:
                case CudaTokenKind.KwVolatile:
                case CudaTokenKind.KwStatic:
                case CudaTokenKind.KwExtern:
                case CudaTokenKind.KwStruct:
                case CudaTokenKind.KwClass:
                case CudaTokenKind.KwUnion:
                case CudaTokenKind.KwEnum:
                case CudaTokenKind.KwTypedef:
                case CudaTokenKind.KwUsing:
                case CudaTokenKind.KwTemplate:
                case CudaTokenKind.KwShared:
                case CudaTokenKind.KwDevice:
                case CudaTokenKind.KwConstant:
                case CudaTokenKind.KwManaged:
                case CudaTokenKind.KwTypename:
                case CudaTokenKind.KwAuto:
                    return true;
                default: break;
            }
            // Identifier-led: scan a qualified (templated) name, then look for a
            // declarator. `Type name`, `Type* name`, `ns::Type name`.
            if (k != CudaTokenKind.Identifier) return false;
            int i = pos_;
            if (KindAt(i) != CudaTokenKind.Identifier) return false;
            ++i;
            while (KindAt(i) == CudaTokenKind.Scope
                   && (KindAt(i + 1) == CudaTokenKind.Identifier
                       // `T::template rebind<int> y;` — the disambiguator sits between the
                       // '::' and the dependent template name.
                       || (KindAt(i + 1) == CudaTokenKind.KwTemplate
                           && KindAt(i + 2) == CudaTokenKind.Identifier)))
                i += KindAt(i + 1) == CudaTokenKind.KwTemplate ? 3 : 2;
            // optional template args
            if (KindAt(i) == CudaTokenKind.Less)
            {
                int depth = 1; ++i;
                while (i < tokens_.Count && depth > 0)
                {
                    CudaTokenKind kk = KindAt(i);
                    if (kk == CudaTokenKind.Less) ++depth;
                    else if (kk == CudaTokenKind.Greater) --depth;
                    else if (kk == CudaTokenKind.RightShift) depth -= 2;
                    else if (kk == CudaTokenKind.TripleGreater) depth -= 3;  // '>>>' in nested args
                    else if (kk == CudaTokenKind.Semicolon || kk == CudaTokenKind.EndOfFile) return false;
                    ++i;
                }
            }
            // pointers / refs / cv
            while (KindAt(i) == CudaTokenKind.Star || KindAt(i) == CudaTokenKind.Amp ||
                   KindAt(i) == CudaTokenKind.AmpAmp || KindAt(i) == CudaTokenKind.KwConst ||
                   KindAt(i) == CudaTokenKind.KwRestrict)
                ++i;
            return KindAt(i) == CudaTokenKind.Identifier;
        }

        private bool LooksLikeParameterList()
        {
            // Cur() is '('. Decide whether the contents are a function parameter list
            // (=> function declarator) or constructor/expression arguments (=> a
            // direct-initialized variable, e.g. `dim3 block(256)`).
            int i = pos_ + 1;
            CudaTokenKind k = KindAt(i);
            if (k == CudaTokenKind.RightParen) return true;   // () => function proto
            if (k == CudaTokenKind.Ellipsis) return true;
            if (IsBuiltinTypeKeyword(k)) return true;
            // C++23 explicit object parameter: `int f(this S& self)` — only a parameter list
            // can begin with `this`.
            if (k == CudaTokenKind.KwThis) return true;
            // A parameter that opens with an attribute or a CUDA parameter annotation:
            // `void k(__grid_constant__ const int p)`, `void f([[maybe_unused]] int x)`.
            if (k == CudaTokenKind.Identifier
                && (TextAt(i) == "__grid_constant__" || TextAt(i) == "__restrict__")) return true;
            if (k == CudaTokenKind.LeftBracket && KindAt(i + 1) == CudaTokenKind.LeftBracket) return true;
            switch (k)
            {
                case CudaTokenKind.KwConst:
                case CudaTokenKind.KwVolatile:
                case CudaTokenKind.KwStruct:
                case CudaTokenKind.KwClass:
                case CudaTokenKind.KwEnum:
                case CudaTokenKind.KwTypename:
                case CudaTokenKind.KwUnsigned:
                case CudaTokenKind.KwSigned:
                    return true;
                default: break;
            }
            if (k == CudaTokenKind.Identifier)
            {
                // Walk the whole (possibly qualified, possibly templated) name so the decision
                // is made on what FOLLOWS it — deciding on the second token alone made
                // `txn(std::move(storage))` a "parameter list" because of the `::`.
                int q = i + 1;
                while (KindAt(q) == CudaTokenKind.Scope && KindAt(q + 1) == CudaTokenKind.Identifier) q += 2;
                bool qualifiedOrTemplated = q > i + 1;
                if (KindAt(q) == CudaTokenKind.Less)
                {
                    int j = SkipAnglesAt(q);
                    if (j > q) { q = j; qualifiedOrTemplated = true; }
                }
                CudaTokenKind after = KindAt(q);
                // A call follows the name => this is a direct-initializer ARGUMENT
                // (`Type txn(std::move(storage))`), never a parameter declaration.
                if (after == CudaTokenKind.LeftParen) return false;
                // Declarator continues => parameter list.
                if (after == CudaTokenKind.Identifier || after == CudaTokenKind.Star ||
                    after == CudaTokenKind.Amp || after == CudaTokenKind.AmpAmp)
                    return true;
                // A parameter PACK: `void f(Ts... xs)` — the ellipsis belongs to the type, and
                // only a parameter list can carry one.
                if (after == CudaTokenKind.Ellipsis) return true;
                // Type-only parameter slot: only trust it for qualified/templated names —
                // a bare `txn(storage)` stays a direct-init of a variable.
                if (qualifiedOrTemplated &&
                    (after == CudaTokenKind.Comma || after == CudaTokenKind.RightParen))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Index just past the '&gt;' matching the '&lt;' at <paramref name="index"/> (non-consuming;
        /// operates on the token list). Returns <paramref name="index"/> unchanged if the angles are
        /// unbalanced or a ';' / EOF is hit first (i.e. not a template argument list).
        /// </summary>
        private int SkipAnglesAt(int index)
        {
            if (KindAt(index) != CudaTokenKind.Less) return index;
            int i = index + 1;
            int depth = 1;
            int guard = 0;
            while (depth > 0 && guard++ < 4096)
            {
                CudaTokenKind k = KindAt(i);
                if (k == CudaTokenKind.EndOfFile || k == CudaTokenKind.Semicolon ||
                    k == CudaTokenKind.LeftBrace) return index;  // not a template-arg list
                if (k == CudaTokenKind.Less) ++depth;
                else if (k == CudaTokenKind.Greater) --depth;
                else if (k == CudaTokenKind.RightShift) depth -= 2;
                else if (k == CudaTokenKind.TripleGreater) depth -= 3;  // '>>>' in nested args
                else if (k == CudaTokenKind.LeftShift) depth += 2;
                ++i;
            }
            return depth <= 0 ? i : index;
        }

        // ===================== Expressions =====================

        private Expr ParseExpression()
        {
            Expr e = ParseAssignment();
            // comma operator
            while (Check(CudaTokenKind.Comma))
            {
                int start = pos_;
                Advance();
                Expr rhs = ParseAssignment();
                e = new BinaryExpr(BinOpKind.Comma, e, rhs, SpanFrom(start));
            }
            return e;
        }

        private Expr ParseAssignment()
        {
            int start = pos_;
            Expr lhs = ParseBinary(1);
            if (lhs == null) return lhs;
            BinOpKind aop;
            if (AssignOpFor(Kind(), out aop))
            {
                // `x = 5 = y;` — assigning INTO a literal: never an lvalue.
                if (lhs is LiteralExpr)
                    Report(CudaDiagnosticCodes.ParseExpectedToken, Cur().Span,
                        "Left operand of assignment must be an lvalue (a literal cannot be assigned to)");
                Advance();
                Expr rhs = ParseAssignment();  // right-assoc
                return new BinaryExpr(aop, lhs, rhs, SpanFrom(start));
            }
            if (Check(CudaTokenKind.Question))
            {
                Advance();
                Expr then = ParseAssignment();
                Expect(CudaTokenKind.Colon, "':'");
                Expr els = ParseAssignment();
                return new ConditionalExpr(lhs, then, els, SpanFrom(start));
            }
            return lhs;
        }

        private Expr ParseBinary(int minPrec)
        {
            Expr lhs = ParseUnary();
            if (lhs == null) return lhs;
            while (true)
            {
                int prec = BinPrec(Kind());
                if (prec == 0 || prec < minPrec) break;
                // Inside a launch configuration, `>>` closes the launch — it is not a shift.
                // (`k<<<n>>2, 256>>>(…)` is ambiguous for nvcc too and must be parenthesised.)
                if (launchConfig_ > 0 && Kind() == CudaTokenKind.RightShift) break;
                CudaTokenKind opTok = Kind();
                int start = pos_;
                Advance();
                Expr rhs = ParseBinary(prec + 1);
                lhs = new BinaryExpr(BinOpFor(opTok), lhs, rhs, SpanFrom(start));
            }
            return lhs;
        }

        private Expr ParseUnary()
        {
            int start = pos_;
            // Coroutine operands (C++20) and the C++23 requires-expression arrive as plain
            // identifiers from the lexer, so they are recognised by lexeme here.
            if (Check(CudaTokenKind.Identifier))
            {
                string lex = Cur().Text;
                if (lex == "co_await" || lex == "co_yield")
                {
                    Advance();
                    Expr operand = ParseUnary();
                    return new UnaryExpr(UnaryOpKind.Plus, operand, SpanFrom(start));
                }
                if (lex == "requires" && (KindAt(pos_ + 1) == CudaTokenKind.LeftBrace
                                          || KindAt(pos_ + 1) == CudaTokenKind.LeftParen))
                {
                    // requires-EXPRESSION used as a value: `constexpr bool ok = requires { … };`
                    // (the requires-CLAUSE form is handled by SkipRequiresClause).
                    Advance();
                    if (Check(CudaTokenKind.LeftParen)) SkipBalanced(CudaTokenKind.LeftParen, CudaTokenKind.RightParen);
                    if (Check(CudaTokenKind.LeftBrace)) SkipBalanced(CudaTokenKind.LeftBrace, CudaTokenKind.RightBrace);
                    return new LiteralExpr(LiteralKind.Bool, "true", SpanFrom(start));
                }
            }
            switch (Kind())
            {
                case CudaTokenKind.Plus: Advance(); return new UnaryExpr(UnaryOpKind.Plus, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.Minus: Advance(); return new UnaryExpr(UnaryOpKind.Minus, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.Bang: Advance(); return new UnaryExpr(UnaryOpKind.Not, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.Tilde: Advance(); return new UnaryExpr(UnaryOpKind.BitNot, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.Star: Advance(); return new UnaryExpr(UnaryOpKind.Deref, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.Amp: Advance(); return new UnaryExpr(UnaryOpKind.AddrOf, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.PlusPlus: Advance(); return new UnaryExpr(UnaryOpKind.PreInc, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.MinusMinus: Advance(); return new UnaryExpr(UnaryOpKind.PreDec, ParseUnary(), SpanFrom(start));
                case CudaTokenKind.KwSizeof:
                    {
                        Advance();
                        long size = 4;  // best-effort default
                        // `sizeof...(Ts)` — the pack-size operator (C++11 variadic templates).
                        if (Check(CudaTokenKind.Ellipsis))
                        {
                            Advance();
                            if (Match(CudaTokenKind.LeftParen))
                            {
                                while (!AtEnd() && !Check(CudaTokenKind.RightParen)) Advance();
                                Expect(CudaTokenKind.RightParen, "')'");
                            }
                            return new LiteralExpr(LiteralKind.Int, "0", SpanFrom(start));
                        }
                        if (Match(CudaTokenKind.LeftParen))
                        {
                            int save = pos_;
                            CxxType t = ParseType();
                            if (!t.IsEmpty() && Check(CudaTokenKind.RightParen))
                            {
                                size = SizeOfTypeName(t);
                            }
                            else
                            {
                                pos_ = save;  // it was an expression, not a type
                            }
                            int d = 1;
                            while (!AtEnd() && d > 0)
                            {
                                if (Check(CudaTokenKind.LeftParen)) ++d;
                                else if (Check(CudaTokenKind.RightParen)) --d;
                                Advance();
                            }
                        }
                        else
                        {
                            ParseUnary();
                        }
                        return new LiteralExpr(LiteralKind.Int, size.ToString(System.Globalization.CultureInfo.InvariantCulture), SpanFrom(start));
                    }
                case CudaTokenKind.KwNew:
                    {
                        Advance();
                        CxxType t = ParseType();
                        Expr sz = null;
                        if (Match(CudaTokenKind.LeftBracket))
                        {
                            if (!Check(CudaTokenKind.RightBracket)) sz = ParseExpression();
                            Expect(CudaTokenKind.RightBracket, "']'");
                        }
                        else if (Match(CudaTokenKind.LeftParen))
                        {
                            ParseCallArgs();
                            Expect(CudaTokenKind.RightParen, "')'");
                        }
                        return new NewExpr(t, sz, SpanFrom(start));
                    }
                case CudaTokenKind.KwDelete:
                    {
                        Advance();
                        bool arr = false;
                        if (Match(CudaTokenKind.LeftBracket)) { Expect(CudaTokenKind.RightBracket, "']'"); arr = true; }
                        Expr op = ParseUnary();
                        return new DeleteExpr(op, arr, SpanFrom(start));
                    }
                case CudaTokenKind.KwStaticCast:
                case CudaTokenKind.KwReinterpretCast:
                case CudaTokenKind.KwConstCast:
                case CudaTokenKind.KwDynamicCast:
                    {
                        Advance();
                        CxxType t = new CxxType();
                        if (Match(CudaTokenKind.Less))
                        {
                            t = ParseType();
                            // consume to matching '>'
                            while (!AtEnd() && !Check(CudaTokenKind.Greater) &&
                                   !Check(CudaTokenKind.RightShift)) Advance();
                            if (Check(CudaTokenKind.RightShift)) { /* leave one '>' */ }
                            else Match(CudaTokenKind.Greater);
                        }
                        Expr operand = null;
                        if (Match(CudaTokenKind.LeftParen))
                        {
                            operand = ParseExpression();
                            Expect(CudaTokenKind.RightParen, "')'");
                        }
                        return new CastExpr(t, operand, SpanFrom(start));
                    }
                default: break;
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            int start = pos_;
            Expr e = ParsePrimary();
            if (e == null) return e;
            while (true)
            {
                // The `template` disambiguator in a dependent member access —
                // `t.template get<int>()`, `p->template f<T>()`. It carries no meaning for
                // this parser; drop it so the member name behind it parses normally.
                if ((Check(CudaTokenKind.Dot) || Check(CudaTokenKind.Arrow))
                    && KindAt(pos_ + 1) == CudaTokenKind.KwTemplate)
                {
                    Advance();          // '.' / '->'
                    Advance();          // 'template'
                    if (Check(CudaTokenKind.Identifier))
                    {
                        string mname = Advance().Text;
                        e = new MemberExpr(e, mname, false, SpanFrom(start));
                        if (Check(CudaTokenKind.Less))
                        {
                            int after = TemplateArgListEnd();
                            if (after > 0) pos_ = after;
                        }
                        continue;
                    }
                    continue;
                }
                switch (Kind())
                {
                    case CudaTokenKind.LeftParen:
                        {
                            Advance();
                            List<Expr> args = ParseCallArgs();
                            Expect(CudaTokenKind.RightParen, "')'");
                            e = new CallExpr(e, args, SpanFrom(start));
                            break;
                        }
                    case CudaTokenKind.LeftBrace:
                        {
                            // Functional braced init on a type-ish name — `Case{2.0, 3.0}` as
                            // a call argument (Catch2 GENERATE), `Point{x, y}` as a value.
                            // Only after a plain name/member: `foo(){...}` or `a[i]{...}`
                            // stays unparsed, so statement-level '{' blocks are unaffected
                            // (ParseStatement claims those before any expression parse).
                            if (!(e is NameExpr || e is MemberExpr)) return e;
                            Advance();
                            List<Expr> elems = new List<Expr>();
                            while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
                            {
                                SkipDesignator();
                                Expr el = ParseAssignment();
                                if (el != null) elems.Add(el);
                                if (!Match(CudaTokenKind.Comma)) break;
                            }
                            Expect(CudaTokenKind.RightBrace, "'}'");
                            e = new CallExpr(e, elems, SpanFrom(start));
                            break;
                        }
                    case CudaTokenKind.Less:
                        {
                            // Template-id in an expression: `bus.subscribe_weak<A, B>(args)` or
                            // `std::vector<const IShape*>{}`. A '<' after a name is USUALLY a
                            // comparison, so commit only when a balanced '>' is found with no
                            // statement/logic boundary in between AND the next token opens a
                            // call or braced init — the classic parser disambiguation. The
                            // template arguments themselves aren't modeled; they're skipped and
                            // the following (...)/{...} binds to the plain callee name.
                            if (!(e is NameExpr || e is MemberExpr)) return e;
                            int after = TemplateArgListEnd();
                            if (after < 0) return e;
                            while (pos_ < after) Advance();   // consume '<' ... '>'
                            break;                            // loop re-enters on '(' or '{'
                        }
                    case CudaTokenKind.TripleLess:
                        e = ParseKernelLaunchTail(e, start);
                        break;
                    case CudaTokenKind.LeftShift:
                        // Visual Studio's formatter writes the launch as `k << <g, b >> > (a)`.
                        // nvcc accepts it, so this must too. `x << <…` has no other legal
                        // reading in C++, which is why splitting on the spacing is safe here.
                        if (Peek().Kind != CudaTokenKind.Less) return e;
                        e = ParseKernelLaunchTail(e, start);
                        break;
                    case CudaTokenKind.LeftBracket:
                        {
                            Advance();
                            Expr idx = ParseExpression();
                            Expect(CudaTokenKind.RightBracket, "']'");
                            e = new IndexExpr(e, idx, SpanFrom(start));
                            break;
                        }
                    case CudaTokenKind.Dot:
                        {
                            Advance();
                            string m = Check(CudaTokenKind.Identifier) ? Advance().Text : "";
                            e = new MemberExpr(e, m, false, SpanFrom(start));
                            break;
                        }
                    case CudaTokenKind.Arrow:
                        {
                            Advance();
                            string m = Check(CudaTokenKind.Identifier) ? Advance().Text : "";
                            e = new MemberExpr(e, m, true, SpanFrom(start));
                            break;
                        }
                    case CudaTokenKind.PlusPlus:
                        Advance();
                        e = new UnaryExpr(UnaryOpKind.PostInc, e, SpanFrom(start));
                        break;
                    case CudaTokenKind.MinusMinus:
                        Advance();
                        e = new UnaryExpr(UnaryOpKind.PostDec, e, SpanFrom(start));
                        break;
                    default:
                        return e;
                }
            }
        }

        private Expr ParseKernelLaunchTail(Expr callee, int startPos)
        {
            Advance();  // '<<<', or the '<<' of a spaced '<< <'
            if (Check(CudaTokenKind.Less)) Advance();
            Expr grid, block = null, shared = null, stream = null;
            launchConfig_++;
            try
            {
                grid = ParseAssignment();
                if (Match(CudaTokenKind.Comma)) block = ParseAssignment();
                if (Match(CudaTokenKind.Comma)) shared = ParseAssignment();
                if (Match(CudaTokenKind.Comma)) stream = ParseAssignment();
            }
            finally { launchConfig_--; }
            // The close is spelled '>>>', '>> >' or '> >>' depending on who formatted it.
            if (Match(CudaTokenKind.RightShift)) Expect(CudaTokenKind.Greater, "'>'");
            else if (Match(CudaTokenKind.Greater)) { if (!Match(CudaTokenKind.RightShift)) Expect(CudaTokenKind.TripleGreater, "'>>>'"); }
            else Expect(CudaTokenKind.TripleGreater, "'>>>'");
            List<Expr> args = new List<Expr>();
            if (Expect(CudaTokenKind.LeftParen, "'('"))
            {
                args = ParseCallArgs();
                Expect(CudaTokenKind.RightParen, "')'");
            }
            return new KernelLaunchExpr(callee, grid, block, shared, stream, args, SpanFrom(startPos));
        }

        // Lookahead for the template-id disambiguation above: starting at the current '<',
        // returns the absolute token index just PAST the matching '>' when the whole thing
        // reads as a template-id; -1 otherwise (leave the '<' as a comparison). Accepted:
        //   • '>' directly followed by '(' or '{' — a template call / braced temporary;
        //   • '>' followed by ',' or ')' when the arguments are unambiguously
        //     type/constant-ish (contain a '::', a literal, or a built-in type keyword and
        //     nothing expression-like) — `std::in_place_index<0>` as a call argument,
        //     `std::convertible_to<K, std::string>` as a condition. A plain
        //     `f(a < b, c > d)` has no such anchor and keeps its comparison reading.
        // Aborts on tokens a template argument list can't contain before its close —
        // ';', braces, '&&'/'||' (as in `a < b && c > (d)`).
        private int TemplateArgListEnd()
        {
            int depth = 0;
            bool anchor = false;       // saw '::', a literal, or a built-in type keyword
            bool typeishOnly = true;   // nothing that only makes sense as an expression
            for (int i = pos_; i < tokens_.Count; i++)
            {
                CudaTokenKind k = tokens_[i].Kind;
                if (k == CudaTokenKind.Less) { depth++; continue; }
                if (k == CudaTokenKind.Greater || k == CudaTokenKind.RightShift ||
                    k == CudaTokenKind.TripleGreater)
                {
                    depth -= k == CudaTokenKind.TripleGreater ? 3 : k == CudaTokenKind.RightShift ? 2 : 1;
                    if (depth > 0) continue;
                    if (depth < 0) return -1;
                    CudaTokenKind next = i + 1 < tokens_.Count ? tokens_[i + 1].Kind : CudaTokenKind.EndOfFile;
                    if (next == CudaTokenKind.LeftParen || next == CudaTokenKind.LeftBrace) return i + 1;
                    if ((next == CudaTokenKind.Comma || next == CudaTokenKind.RightParen) &&
                        typeishOnly && anchor)
                        return i + 1;
                    return -1;
                }
                if (k == CudaTokenKind.Semicolon || k == CudaTokenKind.LeftBrace ||
                    k == CudaTokenKind.RightBrace || k == CudaTokenKind.AmpAmp ||
                    k == CudaTokenKind.PipePipe || k == CudaTokenKind.EndOfFile)
                    return -1;
                if (i == pos_) continue;   // the opening '<' itself
                switch (k)
                {
                    case CudaTokenKind.Scope:
                    case CudaTokenKind.IntLiteral:
                    case CudaTokenKind.FloatLiteral:
                    case CudaTokenKind.KwTrue:
                    case CudaTokenKind.KwFalse:
                        anchor = true; break;
                    case CudaTokenKind.Identifier:
                        // `decltype` only occurs in type context — as good an anchor as '::'
                        // (covers std::is_void_v<decltype(fn(args...))> with no literal inside).
                        if (tokens_[i].Text == "decltype") anchor = true;
                        break;
                    case CudaTokenKind.Comma:
                    case CudaTokenKind.Star:
                    case CudaTokenKind.Amp:
                    case CudaTokenKind.KwConst:
                    case CudaTokenKind.KwTypename:
                    // decltype(fn(args...)) as a template argument (type-trait conditions
                    // like std::is_void_v<decltype(fn(args...))>): parens/ellipsis/dots are
                    // neutral — the '::' anchor requirement still gates acceptance.
                    case CudaTokenKind.LeftParen:
                    case CudaTokenKind.RightParen:
                    case CudaTokenKind.Ellipsis:
                    case CudaTokenKind.Dot:
                        break;
                    default:
                        if (IsBuiltinTypeKeyword(k)) anchor = true;
                        else typeishOnly = false;
                        break;
                }
            }
            return -1;
        }

        // A C++20 designated initializer prefix inside a braced init list: `.x =` (and the C
        // array form `[0] =`). The designator names a member the parser does not model; the
        // VALUE that follows is parsed normally.
        private void SkipDesignator()
        {
            if (Check(CudaTokenKind.Dot) && KindAt(pos_ + 1) == CudaTokenKind.Identifier
                && KindAt(pos_ + 2) == CudaTokenKind.Assign)
            {
                Advance(); Advance(); Advance();
                return;
            }
            if (Check(CudaTokenKind.LeftBracket))
            {
                int i = pos_, depth = 0;
                for (; i < tokens_.Count; i++)
                {
                    if (tokens_[i].Kind == CudaTokenKind.LeftBracket) depth++;
                    else if (tokens_[i].Kind == CudaTokenKind.RightBracket) { depth--; if (depth == 0) break; }
                }
                if (i + 1 < tokens_.Count && tokens_[i + 1].Kind == CudaTokenKind.Assign)
                {
                    SkipBalanced(CudaTokenKind.LeftBracket, CudaTokenKind.RightBracket);
                    Match(CudaTokenKind.Assign);
                }
            }
        }

        // Lookahead only: the parenthesized group starting here contains a top-level `...`,
        // which only a fold expression can. A pack EXPANSION (`f(xs...)`) has its ellipsis
        // inside the call's own parens, one level deeper.
        private bool IsFoldExpressionAhead()
        {
            if (!Check(CudaTokenKind.LeftParen)) return false;
            int depth = 0;
            for (int i = pos_; i < tokens_.Count; i++)
            {
                CudaTokenKind k = tokens_[i].Kind;
                if (k == CudaTokenKind.LeftParen || k == CudaTokenKind.LeftBracket
                    || k == CudaTokenKind.LeftBrace) { depth++; continue; }
                if (k == CudaTokenKind.RightParen || k == CudaTokenKind.RightBracket
                    || k == CudaTokenKind.RightBrace)
                {
                    depth--;
                    if (depth == 0) return false;
                    continue;
                }
                if (depth == 1 && k == CudaTokenKind.Ellipsis) return true;
                if (k == CudaTokenKind.Semicolon || k == CudaTokenKind.EndOfFile) return false;
            }
            return false;
        }

        private List<Expr> ParseCallArgs()
        {
            List<Expr> args = new List<Expr>();
            if (Check(CudaTokenKind.RightParen)) return args;
            while (!AtEnd())
            {
                Expr a = ParseAssignment();
                if (a != null) args.Add(a);
                // Pack expansion: `fn(std::forward<decltype(args)>(args)...)` — the
                // trailing ellipsis belongs to the argument, not the next one.
                Match(CudaTokenKind.Ellipsis);
                if (!Match(CudaTokenKind.Comma)) break;
            }
            return args;
        }

        private Expr ParsePrimary()
        {
            int start = pos_;
            switch (Kind())
            {
                case CudaTokenKind.IntLiteral:
                    return new LiteralExpr(LiteralKind.Int, Advance().Text, SpanFrom(start));
                case CudaTokenKind.FloatLiteral:
                    return new LiteralExpr(LiteralKind.Float, Advance().Text, SpanFrom(start));
                case CudaTokenKind.StringLiteral:
                    return new LiteralExpr(LiteralKind.String, Advance().Text, SpanFrom(start));
                case CudaTokenKind.CharLiteral:
                    return new LiteralExpr(LiteralKind.Char, Advance().Text, SpanFrom(start));
                case CudaTokenKind.KwTrue:
                case CudaTokenKind.KwFalse:
                    return new LiteralExpr(LiteralKind.Bool, Advance().Text, SpanFrom(start));
                case CudaTokenKind.KwNullptr:
                    return new LiteralExpr(LiteralKind.Nullptr, Advance().Text, SpanFrom(start));
                case CudaTokenKind.KwThis:
                    Advance();
                    return new NameExpr("this", "this", SpanFrom(start));
                case CudaTokenKind.LeftBrace:
                    {
                        Advance();
                        List<Expr> elems = new List<Expr>();
                        while (!AtEnd() && !Check(CudaTokenKind.RightBrace))
                        {
                            SkipDesignator();      // C++20 `.member =` / C `[index] =`
                            Expr el = ParseAssignment();
                            if (el != null) elems.Add(el);
                            if (!Match(CudaTokenKind.Comma)) break;
                        }
                        Expect(CudaTokenKind.RightBrace, "'}'");
                        return new InitListExpr(elems, SpanFrom(start));
                    }
                case CudaTokenKind.LeftBracket:
                    return ParseLambda();
                case CudaTokenKind.LeftParen:
                    {
                        // Fold expression (C++17): `(args + ...)`, `(... + args)`,
                        // `(init + ... + args)`, `(f(args), ...)`. The `...` is what marks it;
                        // the whole group is consumed as one value.
                        if (IsFoldExpressionAhead())
                        {
                            SkipBalanced(CudaTokenKind.LeftParen, CudaTokenKind.RightParen);
                            return new LiteralExpr(LiteralKind.Int, "0", SpanFrom(start));
                        }
                        // Possible C-style cast: '(' type ')' unary  — try, else paren-expr.
                        int save = pos_;
                        Advance();  // '('
                        CxxType t = new CxxType();
                        if (ParseBaseType(t))
                        {
                            ParsePointerAndRef(t);
                            if (Check(CudaTokenKind.RightParen) &&
                                (t.PointerDepth > 0 || IsBuiltinTypeName(t.Base)))
                            {
                                Advance();  // ')'
                                Expr operand = ParseUnary();
                                return new CastExpr(t, operand, SpanFrom(start));
                            }
                        }
                        // Not a cast — reparse as a parenthesized expression.
                        pos_ = save;
                        Advance();  // '('
                        Expr inner = ParseExpression();
                        Expect(CudaTokenKind.RightParen, "')'");
                        return new ParenExpr(inner, SpanFrom(start));
                    }
                default: break;
            }

            // Identifier (possibly qualified). Builtin type used as a functional cast
            // e.g. float(x) / dim3(a,b) is handled by postfix call on the NameExpr.
            if (Check(CudaTokenKind.Identifier) || IsBuiltinTypeKeyword(Kind()))
            {
                string q = Advance().Text;
                string last = q;
                while (true)
                {
                    // Template-id on the current segment: `Ident< ... >` used as a
                    // nested-name-specifier or template call, e.g.
                    // std::chrono::duration<double, std::milli>::zero() or Foo<int>::bar().
                    // Only treat '<' as a template-arg list (not less-than) when the
                    // matching '>' is followed by '::' or '(' — otherwise it's a comparison.
                    if (Check(CudaTokenKind.Less))
                    {
                        int j = SkipAnglesAt(pos_);
                        if (j > pos_ &&
                            (KindAt(j) == CudaTokenKind.Scope || KindAt(j) == CudaTokenKind.LeftParen))
                        {
                            SkipBalancedAngles();  // consume <...>; args not modeled
                        }
                    }
                    if (Check(CudaTokenKind.Scope) && Peek().Kind == CudaTokenKind.Identifier)
                    {
                        Advance();
                        q += "::";
                        last = Advance().Text;
                        q += last;
                        continue;
                    }
                    break;
                }
                return new NameExpr(last, q, SpanFrom(start));
            }

            // Unparseable primary — report once and stop.
            Report(CudaDiagnosticCodes.ParseUnexpectedToken, Cur().Span, "Unexpected token in expression");
            return null;
        }

        /// <summary>
        /// [captures](params) mutable? -&gt; type? { body }. Captures and types are
        /// skipped token-wise; only parameter names and the body survive into the
        /// LambdaExpr node. The body is parsed (so its syntax is still checked) but
        /// the analyses do not descend into it.
        /// </summary>
        private Expr ParseLambda()
        {
            int start = pos_;
            // Capture list: skip to the matching ']' (handles nested [] in captures).
            int bdepth = 0;
            do
            {
                CudaTokenKind k = Kind();
                if (k == CudaTokenKind.LeftBracket) bdepth++;
                else if (k == CudaTokenKind.RightBracket) bdepth--;
                Advance();
            } while (!AtEnd() && bdepth > 0);

            // Parameter list: remember the last identifier of each comma-separated
            // slot at depth 1 — that's the parameter's name.
            List<string> paramNames = new List<string>();
            if (Check(CudaTokenKind.LeftParen))
            {
                Advance();
                int depth = 1;
                string lastIdent = null;
                while (!AtEnd() && depth > 0)
                {
                    CudaTokenKind k = Kind();
                    if (k == CudaTokenKind.LeftParen) depth++;
                    else if (k == CudaTokenKind.RightParen)
                    {
                        depth--;
                        if (depth == 0 && lastIdent != null) { paramNames.Add(lastIdent); lastIdent = null; }
                    }
                    else if (k == CudaTokenKind.Comma && depth == 1)
                    {
                        if (lastIdent != null) { paramNames.Add(lastIdent); lastIdent = null; }
                    }
                    else if (k == CudaTokenKind.Identifier) lastIdent = Cur().Text;
                    Advance();
                }
            }

            // Specifiers / trailing return type: skip everything up to the body,
            // PARENTHESIS-AWARE — `mutable -> decltype(fn(args...))` contains ')' tokens
            // that must not be mistaken for the end of an enclosing call (that mistake
            // dropped the whole lambda body and cascaded dozens of expression errors).
            int tdepth = 0;
            while (!AtEnd())
            {
                CudaTokenKind tk = Kind();
                if (tk == CudaTokenKind.LeftParen) { tdepth++; Advance(); continue; }
                if (tk == CudaTokenKind.RightParen)
                {
                    if (tdepth == 0) break;   // an enclosing call really is ending
                    tdepth--; Advance(); continue;
                }
                if (tdepth == 0 && (tk == CudaTokenKind.LeftBrace ||
                                    tk == CudaTokenKind.Semicolon ||
                                    tk == CudaTokenKind.Comma))
                    break;
                Advance();
            }

            Stmt body = Check(CudaTokenKind.LeftBrace) ? ParseCompound() : null;
            return new LambdaExpr(paramNames, body, SpanFrom(start));
        }

        // ===================== Static helpers (anonymous-namespace equivalents) =====================

        private static bool IsBuiltinTypeKeyword(CudaTokenKind k)
        {
            switch (k)
            {
                case CudaTokenKind.KwVoid:
                case CudaTokenKind.KwBool:
                case CudaTokenKind.KwChar:
                case CudaTokenKind.KwShort:
                case CudaTokenKind.KwInt:
                case CudaTokenKind.KwLong:
                case CudaTokenKind.KwFloat:
                case CudaTokenKind.KwDouble:
                case CudaTokenKind.KwSigned:
                case CudaTokenKind.KwUnsigned:
                case CudaTokenKind.KwAuto:
                    return true;
                default: return false;
            }
        }

        private static long SizeOfTypeName(CxxType t)
        {
            if (t.IsPointer()) return 8;
            string b = t.Base;
            if (b == "char" || b == "bool" || b == "signed char" || b == "unsigned char") return 1;
            if (b == "short" || b == "unsigned short") return 2;
            if (b == "double" || b == "long" || b == "long long" || b == "size_t" ||
                b == "unsigned long" || b == "unsigned long long" || b == "int64_t" || b == "uint64_t")
                return 8;
            if (b == "float2" || b == "int2" || b == "uint2" || b == "double") return 8;
            if (b == "float4" || b == "int4" || b == "double2") return 16;
            if (b == "float3" || b == "int3") return 12;
            if (b == "dim3" || b == "uint3") return 12;
            return 4;  // int/float/unsigned/enum/unknown
        }

        private static bool IsBuiltinTypeName(string b)
        {
            if (b == null) return false;
            return b == "void" || b == "bool" || b == "char" || b == "short" ||
                   b == "int" || b == "long" || b == "float" || b == "double" ||
                   b == "unsigned" || b == "signed" || b == "size_t" || b == "auto" ||
                   b.StartsWith("unsigned ", System.StringComparison.Ordinal) ||
                   b.StartsWith("signed ", System.StringComparison.Ordinal) ||
                   b.StartsWith("long ", System.StringComparison.Ordinal);
        }

        // Binary operator precedence (higher binds tighter). 0 => not a binary op.
        private static int BinPrec(CudaTokenKind k)
        {
            switch (k)
            {
                case CudaTokenKind.Star:
                case CudaTokenKind.Slash:
                case CudaTokenKind.Percent: return 10;
                case CudaTokenKind.Plus:
                case CudaTokenKind.Minus: return 9;
                case CudaTokenKind.LeftShift:
                case CudaTokenKind.RightShift: return 8;
                // C++20 `<=>` binds tighter than the relational operators.
                case CudaTokenKind.Spaceship: return 8;
                case CudaTokenKind.Less:
                case CudaTokenKind.Greater:
                case CudaTokenKind.LessEqual:
                case CudaTokenKind.GreaterEqual: return 7;
                case CudaTokenKind.EqualEqual:
                case CudaTokenKind.NotEqual: return 6;
                case CudaTokenKind.Amp: return 5;
                case CudaTokenKind.Caret: return 4;
                case CudaTokenKind.Pipe: return 3;
                case CudaTokenKind.AmpAmp: return 2;
                case CudaTokenKind.PipePipe: return 1;
                default: return 0;
            }
        }

        private static BinOpKind BinOpFor(CudaTokenKind k)
        {
            switch (k)
            {
                case CudaTokenKind.Star: return BinOpKind.Mul;
                case CudaTokenKind.Slash: return BinOpKind.Div;
                case CudaTokenKind.Percent: return BinOpKind.Mod;
                case CudaTokenKind.Plus: return BinOpKind.Add;
                case CudaTokenKind.Minus: return BinOpKind.Sub;
                case CudaTokenKind.LeftShift: return BinOpKind.Shl;
                case CudaTokenKind.RightShift: return BinOpKind.Shr;
                case CudaTokenKind.Less: return BinOpKind.Lt;
                case CudaTokenKind.Greater: return BinOpKind.Gt;
                case CudaTokenKind.Spaceship: return BinOpKind.Le;   // modelled as a comparison
                case CudaTokenKind.LessEqual: return BinOpKind.Le;
                case CudaTokenKind.GreaterEqual: return BinOpKind.Ge;
                case CudaTokenKind.EqualEqual: return BinOpKind.Eq;
                case CudaTokenKind.NotEqual: return BinOpKind.Ne;
                case CudaTokenKind.Amp: return BinOpKind.BitAnd;
                case CudaTokenKind.Caret: return BinOpKind.BitXor;
                case CudaTokenKind.Pipe: return BinOpKind.BitOr;
                case CudaTokenKind.AmpAmp: return BinOpKind.LogAnd;
                case CudaTokenKind.PipePipe: return BinOpKind.LogOr;
                default: return BinOpKind.Comma;
            }
        }

        private static bool AssignOpFor(CudaTokenKind k, out BinOpKind result)
        {
            switch (k)
            {
                case CudaTokenKind.Assign: result = BinOpKind.Assign; return true;
                case CudaTokenKind.PlusAssign: result = BinOpKind.AddAssign; return true;
                case CudaTokenKind.MinusAssign: result = BinOpKind.SubAssign; return true;
                case CudaTokenKind.StarAssign: result = BinOpKind.MulAssign; return true;
                case CudaTokenKind.SlashAssign: result = BinOpKind.DivAssign; return true;
                case CudaTokenKind.PercentAssign: result = BinOpKind.ModAssign; return true;
                case CudaTokenKind.AmpAssign: result = BinOpKind.AndAssign; return true;
                case CudaTokenKind.PipeAssign: result = BinOpKind.OrAssign; return true;
                case CudaTokenKind.CaretAssign: result = BinOpKind.XorAssign; return true;
                case CudaTokenKind.LeftShiftAssign: result = BinOpKind.ShlAssign; return true;
                case CudaTokenKind.RightShiftAssign: result = BinOpKind.ShrAssign; return true;
                default: result = BinOpKind.Assign; return false;
            }
        }

        /// <summary>
        /// C-style atoi: parse an optional sign and leading decimal digits, stopping at
        /// the first non-digit; returns 0 if no digits are present. Mirrors std::atoi
        /// as used by the C++ parser on literal token text.
        /// </summary>
        private static int Atoi(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0;
            int n = s.Length;
            while (i < n && (s[i] == ' ' || s[i] == '\t')) ++i;
            int sign = 1;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                if (s[i] == '-') sign = -1;
                ++i;
            }
            long value = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9')
            {
                value = value * 10 + (s[i] - '0');
                if (value > int.MaxValue) return sign < 0 ? int.MinValue : int.MaxValue;
                ++i;
            }
            return (int)(sign * value);
        }
    }
}
