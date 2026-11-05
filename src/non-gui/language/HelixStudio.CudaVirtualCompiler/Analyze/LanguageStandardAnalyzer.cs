namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// Rejects recognized language features that post-date the selected dialect.
    /// This is deliberately additive: it provides precise version diagnostics while
    /// the parser remains responsible for the syntax/semantic validity of the feature.
    /// </summary>
    public static class LanguageStandardAnalyzer
    {
        private sealed class Feature
        {
            public string First;
            public string Second;
            public int Minimum;
            public string Name;
        }

        private static readonly Feature[] Features =
        {
            new Feature { First = "nullptr", Minimum = 11, Name = "nullptr" },
            new Feature { First = "constexpr", Minimum = 11, Name = "constexpr" },
            new Feature { First = "decltype", Minimum = 11, Name = "decltype" },
            new Feature { First = "thread_local", Minimum = 11, Name = "thread_local" },
            new Feature { First = "alignas", Minimum = 11, Name = "alignas" },
            new Feature { First = "alignof", Minimum = 11, Name = "alignof" },
            new Feature { First = "static_assert", Minimum = 11, Name = "static_assert" },
            new Feature { First = "if", Second = "constexpr", Minimum = 17, Name = "if constexpr" },
            new Feature { First = "concept", Minimum = 20, Name = "concepts" },
            new Feature { First = "requires", Minimum = 20, Name = "requires expressions/clauses" },
            new Feature { First = "consteval", Minimum = 20, Name = "consteval" },
            new Feature { First = "constinit", Minimum = 20, Name = "constinit" },
            new Feature { First = "co_await", Minimum = 20, Name = "coroutines" },
            new Feature { First = "co_yield", Minimum = 20, Name = "coroutines" },
            new Feature { First = "co_return", Minimum = 20, Name = "coroutines" },
            new Feature { First = "if", Second = "consteval", Minimum = 23, Name = "if consteval" },
            new Feature { First = "contract_assert", Minimum = 26, Name = "contract_assert" },
        };

        public static List<CudaCompileDiagnostic> Analyze(
            string source, string filePath, string selectedStandard)
        {
            int selected = ParseStandard(selectedStandard);
            string selectedDisplay = string.IsNullOrWhiteSpace(selectedStandard)
                ? "20" : selectedStandard.Trim();
            var diagnostics = new List<CudaCompileDiagnostic>();
            var lexer = new CudaLexer(source ?? string.Empty, filePath);
            List<CudaToken> tokens = lexer.Tokenize();
            for (int f = 0; f < Features.Length; f++)
            {
                Feature feature = Features[f];
                if (selected >= feature.Minimum) continue;
                for (int i = 0; i < tokens.Count; i++)
                {
                    CudaToken token = tokens[i];
                    if (token.IsTrivia || token.IsEnd || token.Text != feature.First) continue;
                    if (feature.Second != null)
                    {
                        int next = NextSignificant(tokens, i + 1);
                        if (next < 0 || tokens[next].Text != feature.Second) continue;
                    }
                    diagnostics.Add(new CudaCompileDiagnostic(filePath,
                        CudaDiagnosticCodes.StandardFeatureUnavailable, ParseSeverity.Error,
                        token.Span, "'" + feature.Name + "' requires C++" +
                        Display(feature.Minimum) + " or later; selected dialect is C++" +
                        selectedDisplay));
                    break;
                }
            }
            // Digit separators (1'000'000, C++14): a per-token text predicate, not a
            // keyword — the lexer folds the separators into the literal's text.
            if (selected < 14)
            {
                for (int i = 0; i < tokens.Count; i++)
                {
                    CudaToken token = tokens[i];
                    if (token.IsTrivia || token.IsEnd) continue;
                    if (token.Kind != CudaTokenKind.IntLiteral &&
                        token.Kind != CudaTokenKind.FloatLiteral) continue;
                    if (token.Text.IndexOf('\'') < 0) continue;
                    diagnostics.Add(new CudaCompileDiagnostic(filePath,
                        CudaDiagnosticCodes.StandardFeatureUnavailable, ParseSeverity.Error,
                        token.Span, "'digit separators' requires C++14 or later; selected " +
                        "dialect is C++" + selectedDisplay));
                    break;
                }
            }
            return diagnostics;
        }

        /// <summary>
        /// AST-driven checks (SRS §10): features a token scan cannot recognize
        /// without syntax context. Lambdas ('[' is ambiguous with indexing) and
        /// auto type deduction ('auto' was a C++98 storage-class specifier) only
        /// exist once the parser has decided what the tokens mean. One diagnostic
        /// per feature, like the token pass. Runs after the parse phase.
        /// </summary>
        public static List<CudaCompileDiagnostic> AnalyzeAst(
            TranslationUnit unit, string filePath, string selectedStandard)
        {
            var diagnostics = new List<CudaCompileDiagnostic>();
            int selected = ParseStandard(selectedStandard);
            if (unit == null || selected >= 11) return diagnostics;
            string selectedDisplay = string.IsNullOrWhiteSpace(selectedStandard)
                ? "20" : selectedStandard.Trim();

            bool lambdaReported = false;
            bool autoReported = false;
            AstWalk.ForEachFunction(unit, delegate (FunctionDecl fn)
            {
                if (fn == null || !fn.HasBody || (lambdaReported && autoReported)) return;
                if (!lambdaReported)
                {
                    AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
                    {
                        if (lambdaReported || !(e is LambdaExpr)) return;
                        lambdaReported = true;
                        diagnostics.Add(new CudaCompileDiagnostic(filePath,
                            CudaDiagnosticCodes.StandardFeatureUnavailable, ParseSeverity.Error,
                            e.Span, "'lambda expressions' requires C++11 or later; selected " +
                            "dialect is C++" + selectedDisplay));
                    });
                }
                if (!autoReported)
                {
                    VarDecl autoVar = FirstAutoVar(fn.Body);
                    if (autoVar != null)
                    {
                        autoReported = true;
                        diagnostics.Add(new CudaCompileDiagnostic(filePath,
                            CudaDiagnosticCodes.StandardFeatureUnavailable, ParseSeverity.Error,
                            autoVar.Span, "'auto type deduction' requires C++11 or later; " +
                            "selected dialect is C++" + selectedDisplay));
                    }
                }
            });
            return diagnostics;
        }

        // First locally declared variable whose declared type is `auto`.
        private static VarDecl FirstAutoVar(Stmt s)
        {
            if (s == null) return null;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i)
                {
                    VarDecl v = FirstAutoVar(c.Body[i]);
                    if (v != null) return v;
                }
                return null;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && v.Type != null && v.Type.Base == "auto") return v;
                return null;
            }
            IfStmt i2 = s as IfStmt;
            if (i2 != null) return FirstAutoVar(i2.Then) ?? FirstAutoVar(i2.Else);
            ForStmt f = s as ForStmt;
            if (f != null) return FirstAutoVar(f.Init) ?? FirstAutoVar(f.Body);
            WhileStmt w = s as WhileStmt;
            if (w != null) return FirstAutoVar(w.Body);
            DoStmt dw = s as DoStmt;
            if (dw != null) return FirstAutoVar(dw.Body);
            SwitchStmt sw = s as SwitchStmt;
            if (sw != null) return FirstAutoVar(sw.Body);
            return null;
        }

        private static int NextSignificant(List<CudaToken> tokens, int start)
        {
            for (int i = start; i < tokens.Count; i++)
                if (!tokens[i].IsTrivia && !tokens[i].IsEnd) return i;
            return -1;
        }

        private static int ParseStandard(string value)
        {
            int parsed;
            if (!int.TryParse(value, out parsed)) return 20;
            return parsed == 98 || parsed == 3 ? 3 : parsed;
        }

        private static string Display(int standard)
        {
            return standard < 10 ? "0" + standard : standard.ToString();
        }
    }
}
