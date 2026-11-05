using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Emit
{
    /// <summary>
    /// Renders CST nodes back to Python source.
    /// This emitter is optimized for newly-generated code. Round-tripping a
    /// lossless CST (i.e. preserving original trivia verbatim) is future work;
    /// for now the writer re-formats using the <see cref="CodegenPolicy"/>.
    /// </summary>
    public sealed class PythonWriter
    {
        private readonly CodegenPolicy _policy;

        public PythonWriter(CodegenPolicy policy = null)
        {
            _policy = policy ?? CodegenPolicy.Default;
        }

        public string WriteModule(ModuleSyntax module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            StringBuilder sb = new StringBuilder();
            using (StringWriter w = new StringWriter(sb))
            {
                WriteModule(module, w);
            }
            return sb.ToString();
        }

        public void WriteModule(ModuleSyntax module, TextWriter writer)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            foreach (StatementSyntax stmt in module.Statements)
            {
                WriteStatement(stmt, writer, depth: 0);
            }
        }

        public string WriteClass(ClassDefSyntax cls)
        {
            if (cls == null) throw new ArgumentNullException(nameof(cls));
            StringBuilder sb = new StringBuilder();
            using (StringWriter w = new StringWriter(sb))
            {
                WriteStatement(cls, w, depth: 0);
            }
            return sb.ToString();
        }

        private void WriteStatement(StatementSyntax stmt, TextWriter w, int depth)
        {
            if (stmt == null) return;
            string indent = Indent(depth);

            switch (stmt)
            {
                case ClassDefSyntax cls:
                    WriteClassDef(cls, w, depth);
                    break;
                case FunctionDefSyntax fn:
                    WriteFunctionDef(fn, w, depth);
                    break;
                case ImportStatement imp:
                    w.Write(indent);
                    w.Write("import ");
                    bool first = true;
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        if (!first) w.Write(", ");
                        first = false;
                        w.Write(n.Name.AsString);
                        if (!string.IsNullOrEmpty(n.Alias)) { w.Write(" as "); w.Write(n.Alias); }
                    }
                    w.Write(_policy.LineEnding);
                    break;
                case FromImportStatement fi:
                    w.Write(indent);
                    w.Write("from ");
                    for (int i = 0; i < fi.RelativeDots; i++) w.Write('.');
                    if (fi.Module != null) w.Write(fi.Module.AsString);
                    w.Write(" import ");
                    if (fi.IsStar) w.Write("*");
                    else
                    {
                        bool f = true;
                        foreach (KeyValuePair<string, string> kv in fi.Names)
                        {
                            if (!f) w.Write(", ");
                            f = false;
                            w.Write(kv.Key);
                            if (!string.IsNullOrEmpty(kv.Value)) { w.Write(" as "); w.Write(kv.Value); }
                        }
                    }
                    w.Write(_policy.LineEnding);
                    break;
                case PassStatement _:
                    w.Write(indent);
                    w.Write("pass");
                    w.Write(_policy.LineEnding);
                    break;
                case UnparsedStatement us:
                    WriteTokens(us.Tokens, w, indent);
                    w.Write(_policy.LineEnding);
                    break;
                case ExpressionStatement es:
                    w.Write(indent);
                    WriteExpression(es.Expression, w);
                    w.Write(_policy.LineEnding);
                    break;
                case AssignmentStatement asg:
                    w.Write(indent);
                    for (int i = 0; i < asg.Targets.Count; i++)
                    {
                        if (i > 0) w.Write(" = ");
                        WriteExpression(asg.Targets[i], w);
                    }
                    if (asg.Annotation != null) { w.Write(": "); WriteExpression(asg.Annotation, w); }
                    if (asg.Value != null) { w.Write(" = "); WriteExpression(asg.Value, w); }
                    w.Write(_policy.LineEnding);
                    break;
                default:
                    w.Write(indent);
                    w.Write("# <unsupported statement: ");
                    w.Write(stmt.GetType().Name);
                    w.Write(">");
                    w.Write(_policy.LineEnding);
                    break;
            }
        }

        private void WriteClassDef(ClassDefSyntax cls, TextWriter w, int depth)
        {
            string indent = Indent(depth);
            foreach (DecoratorSyntax d in cls.Decorators)
            {
                w.Write(indent);
                w.Write('@');
                WriteExpression(d.Expression, w);
                w.Write(_policy.LineEnding);
            }
            w.Write(indent);
            w.Write("class ");
            w.Write(cls.Name);
            if (cls.TypeParameters.Count > 0)
            {
                w.Write('[');
                for (int i = 0; i < cls.TypeParameters.Count; i++)
                {
                    if (i > 0) w.Write(", ");
                    WriteTypeParameter(cls.TypeParameters[i], w);
                }
                w.Write(']');
            }
            if (cls.BaseExpressions.Count > 0 || cls.KeywordArguments.Count > 0)
            {
                w.Write('(');
                bool first = true;
                foreach (ExpressionSyntax b in cls.BaseExpressions)
                {
                    if (!first) w.Write(", ");
                    first = false;
                    WriteExpression(b, w);
                }
                foreach (KeyValuePair<string, ExpressionSyntax> kv in cls.KeywordArguments)
                {
                    if (!first) w.Write(", ");
                    first = false;
                    w.Write(kv.Key);
                    w.Write('=');
                    WriteExpression(kv.Value, w);
                }
                w.Write(')');
            }
            w.Write(':');
            w.Write(_policy.LineEnding);

            if (cls.Body == null || cls.Body.Statements.Count == 0)
            {
                w.Write(Indent(depth + 1));
                w.Write("pass");
                w.Write(_policy.LineEnding);
                return;
            }
            foreach (StatementSyntax s in cls.Body.Statements)
                WriteStatement(s, w, depth + 1);
        }

        private void WriteFunctionDef(FunctionDefSyntax fn, TextWriter w, int depth)
        {
            string indent = Indent(depth);
            foreach (DecoratorSyntax d in fn.Decorators)
            {
                w.Write(indent);
                w.Write('@');
                WriteExpression(d.Expression, w);
                w.Write(_policy.LineEnding);
            }
            w.Write(indent);
            if (fn.IsAsync) w.Write("async ");
            w.Write("def ");
            w.Write(fn.Name);
            if (fn.TypeParameters.Count > 0)
            {
                w.Write('[');
                for (int i = 0; i < fn.TypeParameters.Count; i++)
                {
                    if (i > 0) w.Write(", ");
                    WriteTypeParameter(fn.TypeParameters[i], w);
                }
                w.Write(']');
            }
            w.Write('(');
            bool seenKwOnly = false;
            bool seenPositionalOnly = false;
            int lastIndex = fn.Parameters.Count - 1;
            for (int i = 0; i < fn.Parameters.Count; i++)
            {
                ParameterSyntax p = fn.Parameters[i];
                if (i > 0) w.Write(", ");
                // Insert '/' marker after last positional-only parameter.
                if (p.Kind == ParameterKind.PositionalOnly && !seenPositionalOnly
                    && (i == lastIndex || fn.Parameters[i + 1].Kind != ParameterKind.PositionalOnly))
                {
                    WriteParameter(p, w);
                    w.Write(", /");
                    seenPositionalOnly = true;
                    continue;
                }
                if (p.Kind == ParameterKind.KeywordOnly && !seenKwOnly)
                {
                    w.Write("*, ");
                    seenKwOnly = true;
                }
                if (p.Kind == ParameterKind.VarPositional) { w.Write('*'); seenKwOnly = true; }
                else if (p.Kind == ParameterKind.VarKeyword) w.Write("**");
                WriteParameter(p, w);
            }
            w.Write(')');
            if (fn.ReturnAnnotation != null)
            {
                w.Write(" -> ");
                WriteExpression(fn.ReturnAnnotation, w);
            }
            w.Write(':');
            w.Write(_policy.LineEnding);

            if (fn.Body == null || fn.Body.Statements.Count == 0)
            {
                w.Write(Indent(depth + 1));
                w.Write("pass");
                w.Write(_policy.LineEnding);
                return;
            }
            foreach (StatementSyntax s in fn.Body.Statements)
                WriteStatement(s, w, depth + 1);
        }

        private void WriteParameter(ParameterSyntax p, TextWriter w)
        {
            w.Write(p.Name);
            if (p.Annotation != null) { w.Write(": "); WriteExpression(p.Annotation, w); }
            if (p.DefaultValue != null) { w.Write(p.Annotation != null ? " = " : "="); WriteExpression(p.DefaultValue, w); }
        }

        private void WriteTypeParameter(TypeParameterSyntax tp, TextWriter w)
        {
            if (tp.Kind == TypeParameterSyntax.TypeParamKind.TypeVarTuple) w.Write('*');
            else if (tp.Kind == TypeParameterSyntax.TypeParamKind.ParamSpec) w.Write("**");
            w.Write(tp.Name);
            if (tp.Bound != null) { w.Write(": "); WriteExpression(tp.Bound, w); }
        }

        private void WriteExpression(ExpressionSyntax expr, TextWriter w)
        {
            if (expr == null) return;
            switch (expr)
            {
                case NameExpression ne: w.Write(ne.Name); return;
                case DottedName dn: w.Write(dn.AsString); return;
                case LiteralExpression le: w.Write(le.Token?.Text ?? string.Empty); return;
                case UnparsedExpression ue: WriteTokens(ue.Tokens, w, string.Empty); return;
                case AttributeExpression ae:
                    WriteExpression(ae.Target, w); w.Write('.'); w.Write(ae.Name); return;
                case CallExpression ce:
                    WriteExpression(ce.Callee, w); w.Write('(');
                    for (int i = 0; i < ce.Arguments.Count; i++)
                    {
                        if (i > 0) w.Write(", ");
                        CallArgument a = ce.Arguments[i];
                        switch (a.Kind)
                        {
                            case CallArgument.ArgKind.Star: w.Write('*'); break;
                            case CallArgument.ArgKind.DoubleStar: w.Write("**"); break;
                            case CallArgument.ArgKind.Keyword: w.Write(a.Name); w.Write('='); break;
                        }
                        WriteExpression(a.Value, w);
                    }
                    w.Write(')'); return;
                case SubscriptExpression se:
                    WriteExpression(se.Target, w); w.Write('[');
                    WriteExpression(se.Index, w); w.Write(']'); return;
                case TupleExpression te:
                    for (int i = 0; i < te.Elements.Count; i++)
                    {
                        if (i > 0) w.Write(", ");
                        WriteExpression(te.Elements[i], w);
                    }
                    return;
                case ListExpression lsx:
                    w.Write('[');
                    for (int i = 0; i < lsx.Elements.Count; i++)
                    {
                        if (i > 0) w.Write(", ");
                        WriteExpression(lsx.Elements[i], w);
                    }
                    w.Write(']'); return;
                case ParenthesizedExpression pe:
                    w.Write('('); WriteExpression(pe.Inner, w); w.Write(')'); return;
                case EllipsisExpression _: w.Write("..."); return;
                case StarredExpression sxe:
                    w.Write(sxe.IsDouble ? "**" : "*"); WriteExpression(sxe.Value, w); return;
                case BinaryExpression be:
                    WriteExpression(be.Left, w);
                    w.Write(' '); w.Write(BinaryOperatorToString(be.Operator)); w.Write(' ');
                    WriteExpression(be.Right, w);
                    return;
                case UnaryExpression uxe:
                    switch (uxe.Operator)
                    {
                        case UnaryOperator.Plus: w.Write('+'); break;
                        case UnaryOperator.Minus: w.Write('-'); break;
                        case UnaryOperator.BitNot: w.Write('~'); break;
                        case UnaryOperator.Not: w.Write("not "); break;
                        case UnaryOperator.Await: w.Write("await "); break;
                    }
                    WriteExpression(uxe.Operand, w);
                    return;
                default: w.Write("<expr>"); return;
            }
        }

        private static string BinaryOperatorToString(BinaryOperator op)
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
            }
            return "?";
        }

        private void WriteTokens(IReadOnlyList<Token> tokens, TextWriter w, string indent)
        {
            if (tokens == null || tokens.Count == 0) return;
            w.Write(indent);
            TokenKind prev = TokenKind.Unknown;
            for (int i = 0; i < tokens.Count; i++)
            {
                Token t = tokens[i];
                if (i > 0 && NeedsSeparator(prev, t.Kind)) w.Write(' ');
                w.Write(t.Text);
                prev = t.Kind;
            }
        }

        private static bool NeedsSeparator(TokenKind a, TokenKind b)
        {
            if (a == TokenKind.Dot || b == TokenKind.Dot) return false;
            if (a == TokenKind.LeftParen || a == TokenKind.LeftBracket || a == TokenKind.LeftBrace) return false;
            if (b == TokenKind.RightParen || b == TokenKind.RightBracket || b == TokenKind.RightBrace) return false;
            if (a == TokenKind.Comma || a == TokenKind.Colon) return true;
            if (b == TokenKind.Comma || b == TokenKind.Colon || b == TokenKind.Semicolon) return false;
            // Generous default: separate whenever both sides are word-like or after operators.
            return true;
        }

        private string Indent(int depth)
        {
            if (depth <= 0) return string.Empty;
            StringBuilder sb = new StringBuilder(_policy.Indent.Length * depth);
            for (int i = 0; i < depth; i++) sb.Append(_policy.Indent);
            return sb.ToString();
        }
    }
}
