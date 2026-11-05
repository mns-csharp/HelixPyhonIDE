using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;
using Cst = HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    /// <summary>
    /// Lowers the parser's CST (HelixPythonNamespace.Python.Syntax.*) into the CPython-shaped
    /// AST consumed by the virtual compiler. The AST is a strict subset of CPython's
    /// <c>Python/Python-ast.h</c> representation so the ported symtable.c and
    /// compile.c code reads like its C counterpart.
    /// </summary>
    public sealed class CstToAst
    {
        public static Module Lower(ModuleSyntax cst)
        {
            if (cst == null) throw new ArgumentNullException(nameof(cst));
            return new CstToAst().LowerModule(cst);
        }

        private Module LowerModule(ModuleSyntax cst)
        {
            List<Stmt> body = new List<Stmt>(cst.Statements.Count);
            foreach (StatementSyntax s in cst.Statements)
            {
                Stmt lowered = LowerStmt(s);
                if (lowered != null) body.Add(lowered);
            }
            return new Module(body, null, cst.Span);
        }

        // ---------- Statements ----------

        private Stmt LowerStmt(StatementSyntax s)
        {
            if (s == null) return null;
            switch (s)
            {
                case ClassDefSyntax c: return LowerClassDef(c);
                case FunctionDefSyntax f: return LowerFunctionDef(f);
                case ImportStatement i: return LowerImport(i);
                case FromImportStatement fi: return LowerFromImport(fi);
                case AssignmentStatement a: return LowerAssign(a);
                case AugmentedAssignmentStatement aa: return LowerAugAssign(aa);
                case ExpressionStatement es: return new ExprStmt(LowerExpr(es.Expression, ExprContext.Load), es.Span);
                case PassStatement _: return new Pass(s.Span);
                case BreakStatement _: return new Break(s.Span);
                case ContinueStatement _: return new Continue(s.Span);
                case ReturnStatement r:
                    return new Return(r.Value != null ? LowerExpr(r.Value, ExprContext.Load) : null, r.Span);
                case RaiseStatement ra:
                    return new Raise(
                        ra.Exception != null ? LowerExpr(ra.Exception, ExprContext.Load) : null,
                        ra.Cause != null ? LowerExpr(ra.Cause, ExprContext.Load) : null,
                        ra.Span);
                case AssertStatement ast:
                    return new Assert(
                        LowerExpr(ast.Test, ExprContext.Load),
                        ast.Message != null ? LowerExpr(ast.Message, ExprContext.Load) : null,
                        ast.Span);
                case DelStatement d:
                    return new Delete(LowerExprList(d.Targets, ExprContext.Del), d.Span);
                case GlobalStatement g: return new Global(g.Names, g.Span);
                case NonlocalStatement nl: return new Nonlocal(nl.Names, nl.Span);
                case YieldStatement y:
                    // CPython treats bare `yield` as an ExprStmt wrapping a Yield expression.
                    return new ExprStmt(LowerExpr(y.Expression, ExprContext.Load), y.Span);
                case IfStatement ifs: return LowerIf(ifs);
                case WhileStatement w: return LowerWhile(w);
                case ForStatement fs: return LowerFor(fs);
                case TryStatement t: return LowerTry(t);
                case WithStatement ws: return LowerWith(ws);
                case MatchStatement m: return LowerMatch(m);
                case UnparsedStatement _: return null; // parser recovery — drop silently
                default: return null;
            }
        }

        private Stmt LowerClassDef(ClassDefSyntax c)
        {
            List<Expr> bases = new List<Expr>();
            List<Keyword> kwds = new List<Keyword>();
            foreach (ExpressionSyntax b in c.BaseExpressions) bases.Add(LowerExpr(b, ExprContext.Load));
            foreach (KeyValuePair<string, ExpressionSyntax> kv in c.KeywordArguments)
            {
                kwds.Add(new Keyword(kv.Key, LowerExpr(kv.Value, ExprContext.Load), kv.Value != null ? kv.Value.Span : c.Span));
            }
            List<Expr> decorators = LowerDecorators(c.Decorators);
            List<TypeParam> typeParams = LowerTypeParams(c.TypeParameters);
            List<Stmt> body = LowerSuite(c.Body);
            return new ClassDef(c.Name, bases, kwds, body, decorators, typeParams, c.Span);
        }

        private Stmt LowerFunctionDef(FunctionDefSyntax f)
        {
            Arguments args = LowerParameters(f.Parameters, f.Span);
            List<Stmt> body = LowerSuite(f.Body);
            List<Expr> decorators = LowerDecorators(f.Decorators);
            List<TypeParam> typeParams = LowerTypeParams(f.TypeParameters);
            Expr returns = f.ReturnAnnotation != null ? LowerExpr(f.ReturnAnnotation, ExprContext.Load) : null;
            if (f.IsAsync)
                return new AsyncFunctionDef(f.Name, args, body, decorators, returns, null, typeParams, f.Span);
            return new FunctionDef(f.Name, args, body, decorators, returns, null, typeParams, f.Span);
        }

        private List<Expr> LowerDecorators(IReadOnlyList<DecoratorSyntax> decorators)
        {
            List<Expr> list = new List<Expr>(decorators.Count);
            foreach (DecoratorSyntax d in decorators)
            {
                if (d.Expression != null) list.Add(LowerExpr(d.Expression, ExprContext.Load));
            }
            return list;
        }

        private List<TypeParam> LowerTypeParams(IReadOnlyList<TypeParameterSyntax> cstParams)
        {
            List<TypeParam> list = new List<TypeParam>(cstParams.Count);
            foreach (TypeParameterSyntax tp in cstParams)
            {
                switch (tp.Kind)
                {
                    case TypeParameterSyntax.TypeParamKind.Type:
                        list.Add(new TypeVarParam(tp.Name,
                            tp.Bound != null ? LowerExpr(tp.Bound, ExprContext.Load) : null, tp.Span));
                        break;
                    case TypeParameterSyntax.TypeParamKind.ParamSpec:
                        list.Add(new ParamSpecParam(tp.Name, tp.Span));
                        break;
                    case TypeParameterSyntax.TypeParamKind.TypeVarTuple:
                        list.Add(new TypeVarTupleParam(tp.Name, tp.Span));
                        break;
                }
            }
            return list;
        }

        private Arguments LowerParameters(IReadOnlyList<ParameterSyntax> parameters, SourceSpan fallbackSpan)
        {
            List<Arg> posOnly = new List<Arg>();
            List<Arg> args = new List<Arg>();
            Arg varArg = null;
            List<Arg> kwOnly = new List<Arg>();
            List<Expr> kwDefaults = new List<Expr>();
            Arg kwArg = null;
            List<Expr> defaults = new List<Expr>();

            // Walk parameters in order. CPython stores defaults tail-aligned with (posOnly + args) and
            // a parallel null-padded list for kwOnly.
            foreach (ParameterSyntax p in parameters)
            {
                Expr ann = p.Annotation != null ? LowerExpr(p.Annotation, ExprContext.Load) : null;
                Arg a = new Arg(p.Name, ann, null, p.Span);
                switch (p.Kind)
                {
                    case ParameterKind.PositionalOnly:
                        posOnly.Add(a);
                        if (p.DefaultValue != null) defaults.Add(LowerExpr(p.DefaultValue, ExprContext.Load));
                        break;
                    case ParameterKind.Positional:
                        args.Add(a);
                        if (p.DefaultValue != null) defaults.Add(LowerExpr(p.DefaultValue, ExprContext.Load));
                        break;
                    case ParameterKind.KeywordOnly:
                        kwOnly.Add(a);
                        kwDefaults.Add(p.DefaultValue != null ? LowerExpr(p.DefaultValue, ExprContext.Load) : null);
                        break;
                    case ParameterKind.VarPositional:
                        varArg = a;
                        break;
                    case ParameterKind.VarKeyword:
                        kwArg = a;
                        break;
                }
            }

            return new Arguments(posOnly, args, varArg, kwOnly, kwDefaults, kwArg, defaults, fallbackSpan);
        }

        private Stmt LowerImport(ImportStatement i)
        {
            List<Alias> aliases = new List<Alias>(i.Names.Count);
            foreach (ImportStatement.ImportedName n in i.Names)
            {
                string name = n.Name != null ? n.Name.AsString : string.Empty;
                aliases.Add(new Alias(name, n.Alias, n.Name != null ? n.Name.Span : i.Span));
            }
            return new Import(aliases, i.Span);
        }

        private Stmt LowerFromImport(FromImportStatement fi)
        {
            List<Alias> aliases = new List<Alias>(fi.Names.Count);
            if (fi.IsStar)
            {
                aliases.Add(new Alias("*", null, fi.Span));
            }
            else
            {
                foreach (KeyValuePair<string, string> kv in fi.Names)
                {
                    aliases.Add(new Alias(kv.Key, kv.Value, fi.Span));
                }
            }
            string module = fi.Module != null ? fi.Module.AsString : null;
            return new ImportFrom(module, aliases, fi.RelativeDots, fi.Span);
        }

        private Stmt LowerAssign(AssignmentStatement a)
        {
            // Annotated single-target assignment → AnnAssign.
            if (a.Annotation != null && a.Targets.Count == 1)
            {
                Expr target = LowerExpr(a.Targets[0], ExprContext.Store);
                Expr annotation = LowerExpr(a.Annotation, ExprContext.Load);
                Expr value = a.Value != null ? LowerExpr(a.Value, ExprContext.Load) : null;
                bool simple = target is Name;
                return new AnnAssign(target, annotation, value, simple, a.Span);
            }
            List<Expr> targets = LowerExprList(a.Targets, ExprContext.Store);
            Expr val = a.Value != null ? LowerExpr(a.Value, ExprContext.Load) : null;
            return new Assign(targets, val, null, a.Span);
        }

        private Stmt LowerAugAssign(AugmentedAssignmentStatement aa)
        {
            BinOp op = MapBinOp(aa.Operator);
            Expr target = LowerExpr(aa.Target, ExprContext.Store);
            Expr value = LowerExpr(aa.Value, ExprContext.Load);
            return new AugAssign(target, op, value, aa.Span);
        }

        private Stmt LowerIf(IfStatement ifs)
        {
            // Flatten elifs: CPython represents `elif X: Y` as `orelse=[If(X, Y, ...)]`.
            Expr test = LowerExpr(ifs.Test, ExprContext.Load);
            List<Stmt> body = LowerSuite(ifs.Body);
            IReadOnlyList<Stmt> orElse = BuildIfElseChain(ifs.Elifs, 0, ifs.Else);
            return new If(test, body, orElse, ifs.Span);
        }

        private IReadOnlyList<Stmt> BuildIfElseChain(IReadOnlyList<IfStatement> elifs, int index, SuiteSyntax tail)
        {
            if (index >= elifs.Count)
            {
                return tail != null ? LowerSuite(tail) : (IReadOnlyList<Stmt>)Array.Empty<Stmt>();
            }
            IfStatement cur = elifs[index];
            Expr test = LowerExpr(cur.Test, ExprContext.Load);
            List<Stmt> body = LowerSuite(cur.Body);
            IReadOnlyList<Stmt> next = BuildIfElseChain(elifs, index + 1, tail);
            return new Stmt[] { new If(test, body, next, cur.Span) };
        }

        private Stmt LowerWhile(WhileStatement w)
        {
            return new While(
                LowerExpr(w.Test, ExprContext.Load),
                LowerSuite(w.Body),
                w.Else != null ? LowerSuite(w.Else) : null,
                w.Span);
        }

        private Stmt LowerFor(ForStatement fs)
        {
            Expr target = LowerExpr(fs.Target, ExprContext.Store);
            Expr iter = LowerExpr(fs.Iter, ExprContext.Load);
            List<Stmt> body = LowerSuite(fs.Body);
            IReadOnlyList<Stmt> orElse = fs.Else != null ? (IReadOnlyList<Stmt>)LowerSuite(fs.Else) : Array.Empty<Stmt>();
            if (fs.IsAsync) return new AsyncFor(target, iter, body, orElse, null, fs.Span);
            return new For(target, iter, body, orElse, null, fs.Span);
        }

        private Stmt LowerTry(TryStatement t)
        {
            List<Stmt> body = LowerSuite(t.Body);
            List<ExceptHandler> handlers = new List<ExceptHandler>(t.Handlers.Count);
            bool anyStar = false;
            foreach (Cst.ExceptHandler h in t.Handlers)
            {
                Expr type = h.ExceptionType != null ? LowerExpr(h.ExceptionType, ExprContext.Load) : null;
                List<Stmt> handlerBody = h.Body != null ? LowerSuite(h.Body) : new List<Stmt>();
                handlers.Add(new Ast.ExceptHandler(type, h.Name, handlerBody, h.Span));
                if (h.IsStar) anyStar = true;
            }
            IReadOnlyList<Stmt> orElse = t.Else != null ? (IReadOnlyList<Stmt>)LowerSuite(t.Else) : Array.Empty<Stmt>();
            IReadOnlyList<Stmt> finalBody = t.Finally != null ? (IReadOnlyList<Stmt>)LowerSuite(t.Finally) : Array.Empty<Stmt>();
            if (anyStar) return new TryStar(body, handlers, orElse, finalBody, t.Span);
            return new Try(body, handlers, orElse, finalBody, t.Span);
        }

        private Stmt LowerWith(WithStatement ws)
        {
            List<WithItem> items = new List<WithItem>(ws.Items.Count);
            foreach (Cst.WithItem wi in ws.Items)
            {
                Expr ctx = LowerExpr(wi.ContextExpression, ExprContext.Load);
                Expr opt = wi.OptionalVars != null ? LowerExpr(wi.OptionalVars, ExprContext.Store) : null;
                items.Add(new Ast.WithItem(ctx, opt, wi.Span));
            }
            List<Stmt> body = LowerSuite(ws.Body);
            if (ws.IsAsync) return new AsyncWith(items, body, null, ws.Span);
            return new With(items, body, null, ws.Span);
        }

        private Stmt LowerMatch(MatchStatement m)
        {
            Expr subject = LowerExpr(m.Subject, ExprContext.Load);
            List<MatchCase> cases = new List<MatchCase>(m.Cases.Count);
            foreach (CaseBlock cb in m.Cases)
            {
                Pattern pat = LowerPattern(cb.Pattern);
                Expr guard = cb.Guard != null ? LowerExpr(cb.Guard, ExprContext.Load) : null;
                List<Stmt> body = cb.Body != null ? LowerSuite(cb.Body) : new List<Stmt>();
                cases.Add(new MatchCase(pat, guard, body, cb.Span));
            }
            return new Match(subject, cases, m.Span);
        }

        private List<Stmt> LowerSuite(SuiteSyntax suite)
        {
            List<Stmt> list = new List<Stmt>(suite.Statements.Count);
            foreach (StatementSyntax s in suite.Statements)
            {
                Stmt lowered = LowerStmt(s);
                if (lowered != null) list.Add(lowered);
            }
            return list;
        }

        // ---------- Expressions ----------

        private Expr LowerExpr(ExpressionSyntax e, ExprContext ctx)
        {
            if (e == null) return null;
            switch (e)
            {
                case NameExpression n: return new Name(n.Name, ctx, n.Span);
                case LiteralExpression l: return LowerLiteral(l);
                case DottedName dn: return LowerDottedName(dn, ctx);
                case BinaryExpression be: return LowerBinary(be);
                case UnaryExpression ue: return LowerUnary(ue);
                case ComparisonExpression ce: return LowerComparison(ce);
                case ConditionalExpression cond:
                    return new IfExp(
                        LowerExpr(cond.Condition, ExprContext.Load),
                        LowerExpr(cond.IfTrue, ExprContext.Load),
                        LowerExpr(cond.IfFalse, ExprContext.Load),
                        cond.Span);
                case Cst.NamedExpression ne:
                    return new Ast.NamedExpr(
                        LowerExpr(ne.Target, ExprContext.Store),
                        LowerExpr(ne.Value, ExprContext.Load),
                        ne.Span);
                case LambdaExpression lam:
                    return new Lambda(LowerParameters(lam.Parameters, lam.Span),
                        LowerExpr(lam.Body, ExprContext.Load), lam.Span);
                case YieldExpression y:
                    if (y.IsFrom) return new YieldFrom(LowerExpr(y.Value, ExprContext.Load), y.Span);
                    return new Yield(y.Value != null ? LowerExpr(y.Value, ExprContext.Load) : null, y.Span);
                case CallExpression call: return LowerCall(call);
                case SubscriptExpression se:
                    return new Subscript(LowerExpr(se.Target, ExprContext.Load),
                        LowerExpr(se.Index, ExprContext.Load), ctx, se.Span);
                case Cst.SliceExpression sl:
                    return new Ast.Slice(
                        sl.Lower != null ? LowerExpr(sl.Lower, ExprContext.Load) : null,
                        sl.Upper != null ? LowerExpr(sl.Upper, ExprContext.Load) : null,
                        sl.Step != null ? LowerExpr(sl.Step, ExprContext.Load) : null,
                        sl.Span);
                case AttributeExpression ae:
                    return new AttributeExpr(LowerExpr(ae.Target, ExprContext.Load), ae.Name, ctx, ae.Span);
                case StarredExpression ss:
                    // **x only ever appears in argument/mapping contexts; flag by returning a Starred that
                    // the validator can distinguish from single-star later. Single-star: Starred(value, ctx).
                    if (ss.IsDouble)
                    {
                        // Double-star isn't an AST expr in CPython; treat as Starred for now and let the
                        // call/dict lowering handle it. Keep as Starred with Load ctx as a placeholder.
                        return new Starred(LowerExpr(ss.Value, ExprContext.Load), ExprContext.Load, ss.Span);
                    }
                    return new Starred(LowerExpr(ss.Value, ctx), ctx, ss.Span);
                case Cst.TupleExpression tup:
                    return new Ast.TupleExpr(LowerExprList(tup.Elements, ctx), ctx, tup.Span);
                case Cst.ListExpression lst:
                    return new Ast.ListExpr(LowerExprList(lst.Elements, ctx), ctx, lst.Span);
                case Cst.SetExpression setx:
                    return new Ast.SetExpr(LowerExprList(setx.Elements, ExprContext.Load), setx.Span);
                case Cst.DictExpression dex: return LowerDict(dex);
                case ComprehensionExpression cex: return LowerComprehension(cex);
                case ParenthesizedExpression pe: return LowerExpr(pe.Inner, ctx);
                case Cst.EllipsisExpression el:
                    return new Constant(ConstantEllipsis.Instance, null, el.Span);
                case FStringExpression fs: return LowerFString(fs);
                case UnparsedExpression _:
                    return new Constant(null, null, e.Span);  // recovery fallback
                default:
                    return new Constant(null, null, e.Span);
            }
        }

        private Expr LowerLiteral(LiteralExpression l)
        {
            if (l.Token == null) return new Constant(null, null, l.Span);
            switch (l.Token.Kind)
            {
                case TokenKind.KwTrue: return new Constant(true, null, l.Span);
                case TokenKind.KwFalse: return new Constant(false, null, l.Span);
                case TokenKind.KwNone: return new Constant(null, null, l.Span);
                case TokenKind.String:
                    // A b"..." / rb"..." byte-string literal is `bytes`, not `str`. The lexer decodes
                    // the body to a C# string and drops the bytes flag, so recover it from the literal
                    // prefix here and hand InferConstant a byte[] (which it maps to bytes). Without
                    // this, `data: bytes = b""` false-flagged PY6008 (click _termui_impl.py:728).
                    if (IsBytesLiteralText(l.Token.Text))
                    {
                        string body = (l.Token.Value as string) ?? string.Empty;
                        return new Constant(System.Text.Encoding.UTF8.GetBytes(body), null, l.Span);
                    }
                    return new Constant(l.Token.Value ?? (object)l.Token.Text, null, l.Span);
                case TokenKind.Number:
                default:
                    // Preserve the parsed Value when the lexer produced one; fall back to raw text.
                    return new Constant(l.Token.Value ?? (object)l.Token.Text, null, l.Span);
            }
        }

        // True when a string-literal's raw text carries a bytes prefix (b, B, rb, Rb, bR, …). The
        // prefix is the run of letters before the opening quote; a 'b'/'B' anywhere in it means bytes.
        private static bool IsBytesLiteralText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int q = text.IndexOfAny(new[] { '"', '\'' });
            if (q <= 0) return false;   // no prefix (q == 0) or no quote → not a bytes literal
            for (int i = 0; i < q; i++)
                if (text[i] == 'b' || text[i] == 'B') return true;
            return false;
        }

        private Expr LowerDottedName(DottedName dn, ExprContext ctx)
        {
            // a.b.c → Attribute(Attribute(Name('a'), 'b'), 'c')  — only outermost gets `ctx`.
            if (dn.Parts == null || dn.Parts.Count == 0) return new Name(string.Empty, ctx, dn.Span);
            Expr current = new Name(dn.Parts[0], dn.Parts.Count == 1 ? ctx : ExprContext.Load, dn.Span);
            for (int i = 1; i < dn.Parts.Count; i++)
            {
                ExprContext c = (i == dn.Parts.Count - 1) ? ctx : ExprContext.Load;
                current = new AttributeExpr(current, dn.Parts[i], c, dn.Span);
            }
            return current;
        }

        private Expr LowerBinary(BinaryExpression be)
        {
            // Generated algebra sources can contain thousands of left-associated operators in one
            // expression. Recursively lowering that CST chain exhausts the CLR stack, so perform a
            // post-order traversal explicitly and construct each AST node after its binary children.
            var lowered = new Dictionary<BinaryExpression, Expr>();
            var pending = new Stack<KeyValuePair<BinaryExpression, bool>>();
            pending.Push(new KeyValuePair<BinaryExpression, bool>(be, false));
            while (pending.Count > 0)
            {
                KeyValuePair<BinaryExpression, bool> item = pending.Pop();
                BinaryExpression current = item.Key;
                if (!item.Value)
                {
                    pending.Push(new KeyValuePair<BinaryExpression, bool>(current, true));
                    if (current.Right is BinaryExpression rightBinary)
                        pending.Push(new KeyValuePair<BinaryExpression, bool>(rightBinary, false));
                    if (current.Left is BinaryExpression leftBinary)
                        pending.Push(new KeyValuePair<BinaryExpression, bool>(leftBinary, false));
                    continue;
                }

                Expr left = current.Left is BinaryExpression lb
                    ? lowered[lb]
                    : LowerExpr(current.Left, ExprContext.Load);
                Expr right = current.Right is BinaryExpression rb
                    ? lowered[rb]
                    : LowerExpr(current.Right, ExprContext.Load);
                lowered[current] = MakeBinaryNode(current, left, right);
            }
            return lowered[be];
        }

        private static Expr MakeBinaryNode(BinaryExpression expression, Expr left, Expr right)
        {
            // CST's And/Or are classed as BinaryOperator; CPython lifts them to BoolOp.
            if (expression.Operator == BinaryOperator.And)
                return new BoolOpExpr(BoolOp.And, new[] { left, right }, expression.Span);
            if (expression.Operator == BinaryOperator.Or)
                return new BoolOpExpr(BoolOp.Or, new[] { left, right }, expression.Span);
            return new BinOpExpr(left, MapBinOp(expression.Operator), right, expression.Span);
        }

        private Expr LowerUnary(UnaryExpression ue)
        {
            Expr operand = LowerExpr(ue.Operand, ExprContext.Load);
            switch (ue.Operator)
            {
                case UnaryOperator.Plus: return new UnaryOpExpr(Ast.UnaryOp.UAdd, operand, ue.Span);
                case UnaryOperator.Minus: return new UnaryOpExpr(Ast.UnaryOp.USub, operand, ue.Span);
                case UnaryOperator.BitNot: return new UnaryOpExpr(Ast.UnaryOp.Invert, operand, ue.Span);
                case UnaryOperator.Not: return new UnaryOpExpr(Ast.UnaryOp.Not, operand, ue.Span);
                case UnaryOperator.Await: return new Await(operand, ue.Span);
                default: return operand;
            }
        }

        private Expr LowerComparison(ComparisonExpression ce)
        {
            List<CmpOp> ops = new List<CmpOp>(ce.Operators.Count);
            foreach (ComparisonOperator co in ce.Operators) ops.Add(MapCmpOp(co));
            List<Expr> comps = new List<Expr>(ce.Comparators.Count);
            foreach (ExpressionSyntax c in ce.Comparators) comps.Add(LowerExpr(c, ExprContext.Load));
            return new Compare(LowerExpr(ce.Left, ExprContext.Load), ops, comps, ce.Span);
        }

        private Expr LowerCall(CallExpression call)
        {
            List<Expr> posArgs = new List<Expr>();
            List<Keyword> kws = new List<Keyword>();
            foreach (CallArgument a in call.Arguments)
            {
                switch (a.Kind)
                {
                    case CallArgument.ArgKind.Positional:
                        posArgs.Add(LowerExpr(a.Value, ExprContext.Load));
                        break;
                    case CallArgument.ArgKind.Star:
                        posArgs.Add(new Starred(LowerExpr(a.Value, ExprContext.Load), ExprContext.Load, a.Span));
                        break;
                    case CallArgument.ArgKind.Keyword:
                        kws.Add(new Keyword(a.Name, LowerExpr(a.Value, ExprContext.Load), a.Span));
                        break;
                    case CallArgument.ArgKind.DoubleStar:
                        kws.Add(new Keyword(null, LowerExpr(a.Value, ExprContext.Load), a.Span));
                        break;
                }
            }
            return new Call(LowerExpr(call.Callee, ExprContext.Load), posArgs, kws, call.Span);
        }

        private Expr LowerDict(Cst.DictExpression dex)
        {
            List<Expr> keys = new List<Expr>(dex.Entries.Count);
            List<Expr> values = new List<Expr>(dex.Entries.Count);
            foreach (DictEntry entry in dex.Entries)
            {
                keys.Add(entry.Key != null ? LowerExpr(entry.Key, ExprContext.Load) : null);
                values.Add(LowerExpr(entry.Value, ExprContext.Load));
            }
            return new Dict(keys, values, dex.Span);
        }

        private Expr LowerComprehension(ComprehensionExpression cex)
        {
            List<Comprehension> gens = new List<Comprehension>(cex.Clauses.Count);
            foreach (ComprehensionClause cl in cex.Clauses)
            {
                List<Expr> ifs = new List<Expr>(cl.Ifs.Count);
                foreach (ExpressionSyntax ife in cl.Ifs) ifs.Add(LowerExpr(ife, ExprContext.Load));
                gens.Add(new Comprehension(
                    LowerExpr(cl.Target, ExprContext.Store),
                    LowerExpr(cl.Iter, ExprContext.Load),
                    ifs, cl.IsAsync, cl.Span));
            }
            switch (cex.Kind)
            {
                case ComprehensionKind.List:
                    return new ListComp(LowerExpr(cex.Element, ExprContext.Load), gens, cex.Span);
                case ComprehensionKind.Set:
                    return new SetComp(LowerExpr(cex.Element, ExprContext.Load), gens, cex.Span);
                case ComprehensionKind.Dict:
                    return new DictComp(
                        LowerExpr(cex.Element, ExprContext.Load),
                        LowerExpr(cex.ValueElement, ExprContext.Load),
                        gens, cex.Span);
                case ComprehensionKind.Generator:
                default:
                    return new GeneratorExp(LowerExpr(cex.Element, ExprContext.Load), gens, cex.Span);
            }
        }

        private Expr LowerFString(FStringExpression fs)
        {
            List<Expr> parts = new List<Expr>(fs.Parts.Count);
            foreach (SyntaxNode part in fs.Parts)
            {
                if (part is ExpressionSyntax ex)
                {
                    if (ex is LiteralExpression litPart)
                    {
                        parts.Add(LowerLiteral(litPart));
                    }
                    else
                    {
                        parts.Add(new FormattedValue(LowerExpr(ex, ExprContext.Load), -1, null, ex.Span));
                    }
                }
            }
            return new JoinedStr(parts, fs.Span);
        }

        // ---------- Patterns ----------

        private Pattern LowerPattern(PatternSyntax p)
        {
            if (p == null) return null;
            switch (p)
            {
                case Cst.LiteralPattern lp:
                    if (lp.Value is LiteralExpression litExpr && litExpr.Token != null)
                    {
                        switch (litExpr.Token.Kind)
                        {
                            case TokenKind.KwTrue: return new MatchSingleton(true, p.Span);
                            case TokenKind.KwFalse: return new MatchSingleton(false, p.Span);
                            case TokenKind.KwNone: return new MatchSingleton(null, p.Span);
                        }
                    }
                    return new MatchValue(LowerExpr(lp.Value, ExprContext.Load), p.Span);
                case Cst.CapturePattern cp:
                    return new MatchAs(null, cp.Name, p.Span);
                case Cst.WildcardPattern _:
                    return new MatchAs(null, null, p.Span);
                case Cst.ValuePattern vp:
                    return new MatchValue(BuildAttributeChain(vp.DottedName, p.Span), p.Span);
                case Cst.GroupPattern gp:
                    return LowerPattern(gp.Inner);
                case Cst.SequencePattern sp:
                    {
                        List<Pattern> items = new List<Pattern>(sp.Items.Count);
                        foreach (PatternSyntax it in sp.Items) items.Add(LowerPattern(it));
                        return new MatchSequence(items, p.Span);
                    }
                case Cst.StarPattern stp:
                    return new MatchStar(stp.Name, p.Span);
                case Cst.MappingPattern mp:
                    {
                        List<Expr> keys = new List<Expr>(mp.Entries.Count);
                        List<Pattern> patterns = new List<Pattern>(mp.Entries.Count);
                        foreach (KeyValuePatternEntry kv in mp.Entries)
                        {
                            keys.Add(LowerExpr(kv.Key, ExprContext.Load));
                            patterns.Add(LowerPattern(kv.Value));
                        }
                        return new MatchMapping(keys, patterns, mp.DoubleStarName, p.Span);
                    }
                case Cst.ClassPattern classP:
                    {
                        Expr cls = BuildAttributeChain(classP.ClassName, p.Span);
                        List<Pattern> posPats = new List<Pattern>(classP.PositionalPatterns.Count);
                        foreach (PatternSyntax it in classP.PositionalPatterns) posPats.Add(LowerPattern(it));
                        List<string> attrs = new List<string>(classP.KeywordPatterns.Count);
                        List<Pattern> kwPats = new List<Pattern>(classP.KeywordPatterns.Count);
                        foreach (KeywordPatternEntry kpe in classP.KeywordPatterns)
                        {
                            attrs.Add(kpe.Name);
                            kwPats.Add(LowerPattern(kpe.Value));
                        }
                        return new MatchClass(cls, posPats, attrs, kwPats, p.Span);
                    }
                case Cst.OrPattern op:
                    {
                        List<Pattern> alts = new List<Pattern>(op.Alternatives.Count);
                        foreach (PatternSyntax alt in op.Alternatives) alts.Add(LowerPattern(alt));
                        return new MatchOr(alts, p.Span);
                    }
                case Cst.AsPattern ap:
                    return new MatchAs(LowerPattern(ap.Pattern), ap.Name, p.Span);
                default:
                    return new MatchAs(null, null, p.Span);
            }
        }

        private Expr BuildAttributeChain(IReadOnlyList<string> parts, SourceSpan span)
        {
            if (parts == null || parts.Count == 0) return new Name(string.Empty, ExprContext.Load, span);
            Expr cur = new Name(parts[0], ExprContext.Load, span);
            for (int i = 1; i < parts.Count; i++)
            {
                cur = new AttributeExpr(cur, parts[i], ExprContext.Load, span);
            }
            return cur;
        }

        // ---------- Helpers ----------

        private List<Expr> LowerExprList(IReadOnlyList<ExpressionSyntax> exprs, ExprContext ctx)
        {
            List<Expr> list = new List<Expr>(exprs.Count);
            foreach (ExpressionSyntax e in exprs) list.Add(LowerExpr(e, ctx));
            return list;
        }

        private static BinOp MapBinOp(BinaryOperator op)
        {
            switch (op)
            {
                case BinaryOperator.Add: return BinOp.Add;
                case BinaryOperator.Subtract: return BinOp.Sub;
                case BinaryOperator.Multiply: return BinOp.Mult;
                case BinaryOperator.MatMul: return BinOp.MatMult;
                case BinaryOperator.Divide: return BinOp.Div;
                case BinaryOperator.FloorDivide: return BinOp.FloorDiv;
                case BinaryOperator.Modulo: return BinOp.Mod;
                case BinaryOperator.Power: return BinOp.Pow;
                case BinaryOperator.LShift: return BinOp.LShift;
                case BinaryOperator.RShift: return BinOp.RShift;
                case BinaryOperator.BitOr: return BinOp.BitOr;
                case BinaryOperator.BitXor: return BinOp.BitXor;
                case BinaryOperator.BitAnd: return BinOp.BitAnd;
                default: return BinOp.Add;
            }
        }

        private static CmpOp MapCmpOp(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Eq: return CmpOp.Eq;
                case ComparisonOperator.NotEq: return CmpOp.NotEq;
                case ComparisonOperator.Less: return CmpOp.Lt;
                case ComparisonOperator.LessEq: return CmpOp.LtE;
                case ComparisonOperator.Greater: return CmpOp.Gt;
                case ComparisonOperator.GreaterEq: return CmpOp.GtE;
                case ComparisonOperator.In: return CmpOp.In;
                case ComparisonOperator.NotIn: return CmpOp.NotIn;
                case ComparisonOperator.Is: return CmpOp.Is;
                case ComparisonOperator.IsNot: return CmpOp.IsNot;
                default: return CmpOp.Eq;
            }
        }
    }

    /// <summary>Singleton marker for <c>...</c> (CPython's <c>Ellipsis</c> constant).</summary>
    public sealed class ConstantEllipsis
    {
        public static readonly ConstantEllipsis Instance = new ConstantEllipsis();
        private ConstantEllipsis() { }
        public override string ToString() { return "..."; }
    }
}
