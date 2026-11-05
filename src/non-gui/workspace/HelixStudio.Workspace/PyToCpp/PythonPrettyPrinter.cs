using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    /// <summary>
    /// Walks a <see cref="HelixPythonNamespace.Python.Syntax"/> tree and emits valid, re-parseable,
    /// re-runnable Python 3 source text. Used by Pass 1 (the strict type checker/rewriter)
    /// to materialize its typed-Python intermediate — the file gets literally run by
    /// python.exe and re-parsed by Pass 2, so round-trip validity matters more than
    /// matching the original formatting.
    ///
    /// Correctness strategy for operator precedence: rather than reproducing Python's full
    /// precedence table, compound sub-expressions (binary/unary/comparison/conditional/
    /// lambda/named/yield) are defensively parenthesized wherever ambiguity is possible.
    /// The output is machine-generated intermediate code, not hand-edited source, so a few
    /// extra parens are a fine trade for not having to get precedence exactly right.
    /// </summary>
    public sealed class PythonPrettyPrinter
    {
        private const int IndentWidth = 4;

        public string Print(ModuleSyntax module)
        {
            var sb = new StringBuilder();
            if (module?.Statements != null)
                foreach (StatementSyntax s in module.Statements) PrintStatement(s, sb, 0);
            return sb.ToString();
        }

        /// <summary>Print a single statement (used by Pass 1 for targeted re-emission / diagnostics).</summary>
        public string PrintStatement(StatementSyntax s)
        {
            var sb = new StringBuilder();
            PrintStatement(s, sb, 0);
            return sb.ToString();
        }

        public string PrintExpression(ExpressionSyntax e) => PrintExpr(e);

        // ================================================================
        // Statements
        // ================================================================

        private static string Ind(int level) => new string(' ', level * IndentWidth);

        private void PrintSuite(SuiteSyntax suite, StringBuilder sb, int indent)
        {
            if (suite == null || suite.Statements.Count == 0)
            {
                sb.Append(Ind(indent)).Append("pass\n");
                return;
            }
            foreach (StatementSyntax s in suite.Statements) PrintStatement(s, sb, indent);
        }

        private void PrintDecorators(IReadOnlyList<DecoratorSyntax> decorators, StringBuilder sb, int indent)
        {
            if (decorators == null) return;
            foreach (DecoratorSyntax d in decorators)
                sb.Append(Ind(indent)).Append('@').Append(PrintExpr(d.Expression)).Append('\n');
        }

        private void PrintStatement(StatementSyntax s, StringBuilder sb, int indent)
        {
            switch (s)
            {
                case FunctionDefSyntax fd: PrintFunctionDef(fd, sb, indent); return;
                case ClassDefSyntax cd: PrintClassDef(cd, sb, indent); return;
                case AssignmentStatement asn: PrintAssignment(asn, sb, indent); return;
                case AugmentedAssignmentStatement aug: PrintAugmentedAssignment(aug, sb, indent); return;
                case ExpressionStatement es:
                    sb.Append(Ind(indent)).Append(PrintExpr(es.Expression)).Append('\n');
                    return;
                case PassStatement _: sb.Append(Ind(indent)).Append("pass\n"); return;
                case BreakStatement _: sb.Append(Ind(indent)).Append("break\n"); return;
                case ContinueStatement _: sb.Append(Ind(indent)).Append("continue\n"); return;
                case ReturnStatement rs:
                    sb.Append(Ind(indent)).Append("return");
                    if (rs.Value != null) sb.Append(' ').Append(PrintExpr(rs.Value));
                    sb.Append('\n');
                    return;
                case RaiseStatement raise:
                    sb.Append(Ind(indent)).Append("raise");
                    if (raise.Exception != null) sb.Append(' ').Append(PrintExpr(raise.Exception));
                    if (raise.Cause != null) sb.Append(" from ").Append(PrintExpr(raise.Cause));
                    sb.Append('\n');
                    return;
                case AssertStatement assertS:
                    sb.Append(Ind(indent)).Append("assert ").Append(PrintExpr(assertS.Test));
                    if (assertS.Message != null) sb.Append(", ").Append(PrintExpr(assertS.Message));
                    sb.Append('\n');
                    return;
                case DelStatement del:
                    sb.Append(Ind(indent)).Append("del ").Append(JoinExpr(del.Targets)).Append('\n');
                    return;
                case GlobalStatement glob:
                    sb.Append(Ind(indent)).Append("global ").Append(string.Join(", ", glob.Names)).Append('\n');
                    return;
                case NonlocalStatement nl:
                    sb.Append(Ind(indent)).Append("nonlocal ").Append(string.Join(", ", nl.Names)).Append('\n');
                    return;
                case YieldStatement ys:
                    sb.Append(Ind(indent)).Append(PrintExpr(ys.Expression)).Append('\n');
                    return;
                case IfStatement ifs: PrintIf(ifs, sb, indent); return;
                case WhileStatement ws: PrintWhile(ws, sb, indent); return;
                case ForStatement fs: PrintFor(fs, sb, indent); return;
                case TryStatement trys: PrintTry(trys, sb, indent); return;
                case WithStatement withS: PrintWith(withS, sb, indent); return;
                case ImportStatement imp: PrintImport(imp, sb, indent); return;
                case FromImportStatement fim: PrintFromImport(fim, sb, indent); return;
                case UnparsedStatement up:
                    // Best-effort verbatim round-trip. Pass 1 rejects UnparsedStatement outright
                    // (it can't type-check something it can't structurally analyze), so this path
                    // should not be exercised on Pass-1 output in practice.
                    sb.Append(Ind(indent));
                    foreach (Token t in up.Tokens) sb.Append(t.Text);
                    sb.Append('\n');
                    return;
                default:
                    throw new NotSupportedException("PythonPrettyPrinter: unsupported statement type " + s?.GetType().Name);
            }
        }

        private void PrintFunctionDef(FunctionDefSyntax fd, StringBuilder sb, int indent)
        {
            PrintDecorators(fd.Decorators, sb, indent);
            sb.Append(Ind(indent)).Append(fd.IsAsync ? "async def " : "def ").Append(fd.Name);
            PrintTypeParameters(fd.TypeParameters, sb);
            sb.Append('(').Append(PrintParameters(fd.Parameters)).Append(')');
            if (fd.ReturnAnnotation != null) sb.Append(" -> ").Append(PrintExpr(fd.ReturnAnnotation));
            sb.Append(":\n");
            PrintSuite(fd.Body, sb, indent + 1);
        }

        private void PrintClassDef(ClassDefSyntax cd, StringBuilder sb, int indent)
        {
            PrintDecorators(cd.Decorators, sb, indent);
            sb.Append(Ind(indent)).Append("class ").Append(cd.Name);
            PrintTypeParameters(cd.TypeParameters, sb);
            bool hasBases = cd.BaseExpressions.Count > 0;
            bool hasKwargs = cd.KeywordArguments.Count > 0;
            if (hasBases || hasKwargs)
            {
                sb.Append('(');
                bool first = true;
                foreach (ExpressionSyntax b in cd.BaseExpressions) { if (!first) sb.Append(", "); first = false; sb.Append(PrintExpr(b)); }
                foreach (var kv in cd.KeywordArguments)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append('=').Append(PrintExpr(kv.Value));
                }
                sb.Append(')');
            }
            sb.Append(":\n");
            PrintSuite(cd.Body, sb, indent + 1);
        }

        private void PrintTypeParameters(IReadOnlyList<TypeParameterSyntax> tps, StringBuilder sb)
        {
            if (tps == null || tps.Count == 0) return;
            sb.Append('[');
            for (int i = 0; i < tps.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                TypeParameterSyntax tp = tps[i];
                if (tp.Kind == TypeParameterSyntax.TypeParamKind.TypeVarTuple) sb.Append('*');
                else if (tp.Kind == TypeParameterSyntax.TypeParamKind.ParamSpec) sb.Append("**");
                sb.Append(tp.Name);
                if (tp.Bound != null) sb.Append(": ").Append(PrintExpr(tp.Bound));
            }
            sb.Append(']');
        }

        private string PrintParameters(IReadOnlyList<ParameterSyntax> parameters)
        {
            var sb = new StringBuilder();
            bool sawKeywordOnlyMarker = false;
            bool sawPositionalOnlyMarker = false;
            // Positional-only marker '/' goes right after the last PositionalOnly param;
            // detect the boundary by scanning ahead.
            for (int i = 0; i < parameters.Count; i++)
            {
                ParameterSyntax p = parameters[i];
                bool isLastPositionalOnly = p.Kind == ParameterKind.PositionalOnly &&
                    (i + 1 >= parameters.Count || parameters[i + 1].Kind != ParameterKind.PositionalOnly);

                if (p.Kind == ParameterKind.KeywordOnly && !sawKeywordOnlyMarker)
                {
                    bool precededByVarPositional = i > 0 && parameters[i - 1].Kind == ParameterKind.VarPositional;
                    if (!precededByVarPositional) { if (sb.Length > 0) sb.Append(", "); sb.Append('*'); }
                    sawKeywordOnlyMarker = true;
                }

                if (sb.Length > 0) sb.Append(", ");
                if (p.Kind == ParameterKind.VarPositional) sb.Append('*');
                else if (p.Kind == ParameterKind.VarKeyword) sb.Append("**");
                sb.Append(p.Name);
                if (p.Annotation != null) sb.Append(": ").Append(PrintExpr(p.Annotation));
                if (p.DefaultValue != null) sb.Append(p.Annotation != null ? " = " : "=").Append(PrintExpr(p.DefaultValue));

                if (isLastPositionalOnly && !sawPositionalOnlyMarker) { sb.Append(", /"); sawPositionalOnlyMarker = true; }
            }
            return sb.ToString();
        }

        private void PrintAssignment(AssignmentStatement asn, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent));
            if (asn.Annotation != null)
            {
                // Annotated assignment always has exactly one target: `x: T = value` or `x: T`.
                sb.Append(PrintExpr(asn.Targets[0])).Append(": ").Append(PrintExpr(asn.Annotation));
                if (asn.Value != null) sb.Append(" = ").Append(PrintExpr(asn.Value));
            }
            else
            {
                foreach (ExpressionSyntax target in asn.Targets) sb.Append(PrintExpr(target)).Append(" = ");
                sb.Append(PrintExpr(asn.Value)); // grammar guarantees Value != null when Annotation == null
            }
            sb.Append('\n');
        }

        private void PrintAugmentedAssignment(AugmentedAssignmentStatement aug, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append(PrintExpr(aug.Target)).Append(' ')
              .Append(BinaryOpText(aug.Operator)).Append("= ").Append(PrintExpr(aug.Value)).Append('\n');
        }

        private void PrintIf(IfStatement ifs, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append("if ").Append(PrintExpr(ifs.Test)).Append(":\n");
            PrintSuite(ifs.Body, sb, indent + 1);
            foreach (IfStatement elif in ifs.Elifs)
            {
                sb.Append(Ind(indent)).Append("elif ").Append(PrintExpr(elif.Test)).Append(":\n");
                PrintSuite(elif.Body, sb, indent + 1);
            }
            if (ifs.Else != null)
            {
                sb.Append(Ind(indent)).Append("else:\n");
                PrintSuite(ifs.Else, sb, indent + 1);
            }
        }

        private void PrintWhile(WhileStatement ws, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append("while ").Append(PrintExpr(ws.Test)).Append(":\n");
            PrintSuite(ws.Body, sb, indent + 1);
            if (ws.Else != null) { sb.Append(Ind(indent)).Append("else:\n"); PrintSuite(ws.Else, sb, indent + 1); }
        }

        private void PrintFor(ForStatement fs, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append(fs.IsAsync ? "async for " : "for ")
              .Append(PrintExpr(fs.Target)).Append(" in ").Append(PrintExpr(fs.Iter)).Append(":\n");
            PrintSuite(fs.Body, sb, indent + 1);
            if (fs.Else != null) { sb.Append(Ind(indent)).Append("else:\n"); PrintSuite(fs.Else, sb, indent + 1); }
        }

        private void PrintTry(TryStatement trys, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append("try:\n");
            PrintSuite(trys.Body, sb, indent + 1);
            foreach (ExceptHandler h in trys.Handlers)
            {
                sb.Append(Ind(indent)).Append(h.IsStar ? "except*" : "except");
                if (h.ExceptionType != null)
                {
                    sb.Append(' ').Append(PrintExpr(h.ExceptionType));
                    if (!string.IsNullOrEmpty(h.Name)) sb.Append(" as ").Append(h.Name);
                }
                sb.Append(":\n");
                PrintSuite(h.Body, sb, indent + 1);
            }
            if (trys.Else != null) { sb.Append(Ind(indent)).Append("else:\n"); PrintSuite(trys.Else, sb, indent + 1); }
            if (trys.Finally != null) { sb.Append(Ind(indent)).Append("finally:\n"); PrintSuite(trys.Finally, sb, indent + 1); }
        }

        private void PrintWith(WithStatement withS, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append(withS.IsAsync ? "async with " : "with ");
            for (int i = 0; i < withS.Items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                WithItem item = withS.Items[i];
                sb.Append(PrintExpr(item.ContextExpression));
                if (item.OptionalVars != null) sb.Append(" as ").Append(PrintExpr(item.OptionalVars));
            }
            sb.Append(":\n");
            PrintSuite(withS.Body, sb, indent + 1);
        }

        private void PrintImport(ImportStatement imp, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append("import ");
            for (int i = 0; i < imp.Names.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var n = imp.Names[i];
                sb.Append(n.Name.AsString);
                if (!string.IsNullOrEmpty(n.Alias)) sb.Append(" as ").Append(n.Alias);
            }
            sb.Append('\n');
        }

        private void PrintFromImport(FromImportStatement fim, StringBuilder sb, int indent)
        {
            sb.Append(Ind(indent)).Append("from ").Append(new string('.', fim.RelativeDots));
            if (fim.Module != null) sb.Append(fim.Module.AsString);
            sb.Append(" import ");
            if (fim.IsStar) { sb.Append('*'); sb.Append('\n'); return; }
            for (int i = 0; i < fim.Names.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(fim.Names[i].Key);
                if (!string.IsNullOrEmpty(fim.Names[i].Value)) sb.Append(" as ").Append(fim.Names[i].Value);
            }
            sb.Append('\n');
        }

        private string JoinExpr(IReadOnlyList<ExpressionSyntax> exprs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < exprs.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(PrintExpr(exprs[i])); }
            return sb.ToString();
        }

        // ================================================================
        // Expressions
        // ================================================================

        private string PrintExpr(ExpressionSyntax e)
        {
            switch (e)
            {
                case null: return "";
                case NameExpression n: return n.Name;
                case DottedName dn: return dn.AsString;
                case LiteralExpression lit: return lit.Token?.Text ?? "None";
                case EllipsisExpression _: return "...";
                case UnparsedExpression up:
                {
                    var sb = new StringBuilder();
                    foreach (Token t in up.Tokens) sb.Append(t.Text);
                    return sb.ToString();
                }
                case ParenthesizedExpression pe: return "(" + PrintExpr(pe.Inner) + ")";
                case BinaryExpression be:
                    return Paren(be.Left) + " " + BinaryOpText(be.Operator) + " " + Paren(be.Right);
                case UnaryExpression ue:
                    return UnaryOpText(ue.Operator) + Paren(ue.Operand);
                case ComparisonExpression cmp:
                {
                    var sb = new StringBuilder();
                    sb.Append(Paren(cmp.Left));
                    for (int i = 0; i < cmp.Operators.Count; i++)
                        sb.Append(' ').Append(ComparisonOpText(cmp.Operators[i])).Append(' ').Append(Paren(cmp.Comparators[i]));
                    return sb.ToString();
                }
                case ConditionalExpression ce:
                    return Paren(ce.IfTrue) + " if " + Paren(ce.Condition) + " else " + Paren(ce.IfFalse);
                case NamedExpression ne:
                    return "(" + ne.Target.Name + " := " + PrintExpr(ne.Value) + ")";
                case LambdaExpression le:
                    return "lambda " + PrintParameters(le.Parameters) + ": " + PrintExpr(le.Body);
                case YieldExpression ye:
                    return ye.IsFrom ? "yield from " + PrintExpr(ye.Value) : "yield" + (ye.Value != null ? " " + PrintExpr(ye.Value) : "");
                case CallExpression call: return PrintCall(call);
                case SubscriptExpression sub: return PrintExpr(sub.Target) + "[" + PrintExpr(sub.Index) + "]";
                case SliceExpression sl:
                    return (sl.Lower != null ? PrintExpr(sl.Lower) : "") + ":" +
                           (sl.Upper != null ? PrintExpr(sl.Upper) : "") +
                           (sl.Step != null ? ":" + PrintExpr(sl.Step) : "");
                case AttributeExpression attr: return PrintExpr(attr.Target) + "." + attr.Name;
                case StarredExpression star: return (star.IsDouble ? "**" : "*") + PrintExpr(star.Value);
                case TupleExpression tup:
                    return tup.Elements.Count == 1 ? "(" + PrintExpr(tup.Elements[0]) + ",)" : "(" + JoinExpr(tup.Elements) + ")";
                case ListExpression list: return "[" + JoinExpr(list.Elements) + "]";
                case SetExpression set: return set.Elements.Count == 0 ? "set()" : "{" + JoinExpr(set.Elements) + "}";
                case DictExpression dict: return PrintDict(dict);
                case ComprehensionExpression comp: return PrintComprehension(comp);
                case FStringExpression fstr: return PrintFString(fstr);
                default:
                    throw new NotSupportedException("PythonPrettyPrinter: unsupported expression type " + e.GetType().Name);
            }
        }

        // Wraps a sub-expression in parens when it's a compound form that could otherwise
        // bind incorrectly once embedded in a binary/unary/comparison/conditional context.
        private string Paren(ExpressionSyntax e)
        {
            string text = PrintExpr(e);
            bool needsParens = e is BinaryExpression || e is UnaryExpression || e is ComparisonExpression ||
                                e is ConditionalExpression || e is LambdaExpression || e is YieldExpression ||
                                e is NamedExpression || e is TupleExpression;
            return needsParens ? "(" + text + ")" : text;
        }

        private string PrintCall(CallExpression call)
        {
            var sb = new StringBuilder();
            sb.Append(PrintExpr(call.Callee)).Append('(');
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                CallArgument a = call.Arguments[i];
                switch (a.Kind)
                {
                    case CallArgument.ArgKind.Positional: sb.Append(PrintExpr(a.Value)); break;
                    case CallArgument.ArgKind.Keyword: sb.Append(a.Name).Append('=').Append(PrintExpr(a.Value)); break;
                    case CallArgument.ArgKind.Star: sb.Append('*').Append(PrintExpr(a.Value)); break;
                    case CallArgument.ArgKind.DoubleStar: sb.Append("**").Append(PrintExpr(a.Value)); break;
                }
            }
            sb.Append(')');
            return sb.ToString();
        }

        private string PrintDict(DictExpression dict)
        {
            if (dict.Entries.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            for (int i = 0; i < dict.Entries.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                DictEntry entry = dict.Entries[i];
                if (entry.Key == null) sb.Append("**").Append(PrintExpr(entry.Value));
                else sb.Append(PrintExpr(entry.Key)).Append(": ").Append(PrintExpr(entry.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private string PrintComprehension(ComprehensionExpression comp)
        {
            string element = comp.Kind == ComprehensionKind.Dict
                ? PrintExpr(comp.Element) + ": " + PrintExpr(comp.ValueElement)
                : PrintExpr(comp.Element);

            var sb = new StringBuilder(element);
            foreach (ComprehensionClause clause in comp.Clauses)
            {
                sb.Append(clause.IsAsync ? " async for " : " for ")
                  .Append(PrintExpr(clause.Target)).Append(" in ").Append(PrintExpr(clause.Iter));
                foreach (ExpressionSyntax ifExpr in clause.Ifs) sb.Append(" if ").Append(PrintExpr(ifExpr));
            }

            switch (comp.Kind)
            {
                case ComprehensionKind.List: return "[" + sb + "]";
                case ComprehensionKind.Set: return "{" + sb + "}";
                case ComprehensionKind.Dict: return "{" + sb + "}";
                default: return "(" + sb + ")"; // Generator
            }
        }

        private string PrintFString(FStringExpression fstr)
        {
            // Re-emit as a single double-quoted f-string; literal-text parts are escaped for
            // '"', '\\', and brace-doubling ('{' -> '{{', '}' -> '}}') so re-parsing produces
            // the same literal text and field boundaries.
            var sb = new StringBuilder("f\"");
            for (int i = 0; i < fstr.Parts.Count; i++)
            {
                SyntaxNode part = fstr.Parts[i];
                if (part is LiteralExpression lit)
                {
                    sb.Append(EscapeFStringLiteralSegment(RawFStringSegmentText(lit)));
                }
                else if (part is ExpressionSyntax expr)
                {
                    sb.Append('{').Append(PrintExpr(expr));
                    char? conv = i < fstr.Conversions.Count ? fstr.Conversions[i] : null;
                    string spec = i < fstr.FormatSpecs.Count ? fstr.FormatSpecs[i] : null;
                    if (conv.HasValue) sb.Append('!').Append(conv.Value);
                    if (!string.IsNullOrEmpty(spec)) sb.Append(':').Append(spec);
                    sb.Append('}');
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // FStringMiddle tokens (the common case — a literal text run inside `f"..."`) carry the
        // raw text as-lexed, already un-escaped of {{ / }} doubling, in Token.Value.
        // A plain TokenKind.String token can also land here: implicit string concatenation
        // (`"a" f"b{x}"`) flattens BOTH literals' parts into one FStringExpression, and a
        // regular string token's Text is the FULL quoted literal (prefix + quotes) — that has
        // to be unquoted before it can be re-embedded as literal f-string body text, or the
        // stray quote characters corrupt the output.
        private static string RawFStringSegmentText(LiteralExpression lit)
        {
            if (lit.Token == null) return "";
            if (lit.Token.Kind == TokenKind.FStringMiddle) return (lit.Token.Value as string) ?? lit.Token.Text;
            return StripStringLiteralQuotes(lit.Token.Text);
        }

        private static string StripStringLiteralQuotes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int i = 0;
            while (i < raw.Length && char.IsLetter(raw[i])) i++; // skip r/b/u/f prefix (and combos)
            string body = raw.Substring(i);
            if (body.Length >= 6 && (body.StartsWith("\"\"\"", StringComparison.Ordinal) || body.StartsWith("'''", StringComparison.Ordinal)))
                return body.Substring(3, body.Length - 6);
            if (body.Length >= 2) return body.Substring(1, body.Length - 2);
            return body;
        }

        private static string EscapeFStringLiteralSegment(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                switch (c)
                {
                    case '{': sb.Append("{{"); break;
                    case '}': sb.Append("}}"); break;
                    case '"': sb.Append("\\\""); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // ================================================================
        // Operator text
        // ================================================================

        private static string BinaryOpText(BinaryOperator op)
        {
            switch (op)
            {
                case BinaryOperator.Or: return "or";
                case BinaryOperator.And: return "and";
                case BinaryOperator.BitOr: return "|";
                case BinaryOperator.BitXor: return "^";
                case BinaryOperator.BitAnd: return "&";
                case BinaryOperator.LShift: return "<<";
                case BinaryOperator.RShift: return ">>";
                case BinaryOperator.Add: return "+";
                case BinaryOperator.Subtract: return "-";
                case BinaryOperator.Multiply: return "*";
                case BinaryOperator.MatMul: return "@";
                case BinaryOperator.Divide: return "/";
                case BinaryOperator.FloorDivide: return "//";
                case BinaryOperator.Modulo: return "%";
                case BinaryOperator.Power: return "**";
                default: throw new NotSupportedException("Unknown BinaryOperator " + op);
            }
        }

        private static string UnaryOpText(UnaryOperator op)
        {
            switch (op)
            {
                case UnaryOperator.Plus: return "+";
                case UnaryOperator.Minus: return "-";
                case UnaryOperator.BitNot: return "~";
                case UnaryOperator.Not: return "not ";
                case UnaryOperator.Await: return "await ";
                default: throw new NotSupportedException("Unknown UnaryOperator " + op);
            }
        }

        private static string ComparisonOpText(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Eq: return "==";
                case ComparisonOperator.NotEq: return "!=";
                case ComparisonOperator.Less: return "<";
                case ComparisonOperator.LessEq: return "<=";
                case ComparisonOperator.Greater: return ">";
                case ComparisonOperator.GreaterEq: return ">=";
                case ComparisonOperator.In: return "in";
                case ComparisonOperator.NotIn: return "not in";
                case ComparisonOperator.Is: return "is";
                case ComparisonOperator.IsNot: return "is not";
                default: throw new NotSupportedException("Unknown ComparisonOperator " + op);
            }
        }
    }
}
