namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = HelixPythonNamespace.Refactoring.Vectorization.VecHelpers;

    /// <summary>
    /// Renders a function/lambda's <see cref="Arguments"/> back to Python source
    /// and detects single-return / if-return-else-return body shapes. Port of the
    /// argument helpers in the C++ lambda <c>pattern_matcher.cpp</c>.
    /// </summary>
    public static class LambdaArgRenderer
    {
        public static string RenderArguments(Arguments args, bool includeAnnotations)
        {
            if (args == null) return "";
            StringBuilder outSb = new StringBuilder();
            bool first = true;
            System.Action<string> emit = part =>
            {
                if (!first) outSb.Append(", ");
                outSb.Append(part);
                first = false;
            };

            IReadOnlyList<Arg> positional = args.Args;
            IReadOnlyList<Expr> defaults = args.Defaults;
            int defaultsStart = positional.Count - defaults.Count;

            if (args.PosOnlyArgs.Count > 0)
            {
                foreach (Arg arg in args.PosOnlyArgs)
                {
                    string token = arg.Name;
                    if (includeAnnotations && arg.Annotation != null)
                        token += ": " + H.PyExprPrint(arg.Annotation);
                    emit(token);
                }
                emit("/");
            }

            for (int i = 0; i < positional.Count; i++)
            {
                string token = positional[i].Name;
                if (includeAnnotations && positional[i].Annotation != null)
                    token += ": " + H.PyExprPrint(positional[i].Annotation);
                int defaultIdx = i - defaultsStart;
                if (defaultIdx >= 0 && defaultIdx < defaults.Count)
                    token += "=" + H.PyExprPrint(defaults[defaultIdx]);
                emit(token);
            }

            if (args.VarArg != null)
            {
                string token = "*" + args.VarArg.Name;
                if (includeAnnotations && args.VarArg.Annotation != null)
                    token += ": " + H.PyExprPrint(args.VarArg.Annotation);
                emit(token);
            }
            else if (args.KwOnlyArgs.Count > 0)
            {
                emit("*");
            }

            IReadOnlyList<Arg> kwOnly = args.KwOnlyArgs;
            IReadOnlyList<Expr> kwDefaults = args.KwDefaults;
            for (int i = 0; i < kwOnly.Count; i++)
            {
                string token = kwOnly[i].Name;
                if (includeAnnotations && kwOnly[i].Annotation != null)
                    token += ": " + H.PyExprPrint(kwOnly[i].Annotation);
                if (i < kwDefaults.Count && kwDefaults[i] != null)
                    token += "=" + H.PyExprPrint(kwDefaults[i]);
                emit(token);
            }

            if (args.KwArg != null)
            {
                string token = "**" + args.KwArg.Name;
                if (includeAnnotations && args.KwArg.Annotation != null)
                    token += ": " + H.PyExprPrint(args.KwArg.Annotation);
                emit(token);
            }
            return outSb.ToString();
        }

        public static string RenderArgumentsWithAnnotations(Arguments args)
        {
            return RenderArguments(args, true);
        }

        /// <summary>True if <paramref name="body"/> is exactly one <c>return EXPR</c>; sets the printed expr.</summary>
        public static bool IsSingleReturnBody(IReadOnlyList<Stmt> body, out string exprPretty)
        {
            exprPretty = null;
            if (body.Count != 1) return false;
            Return ret = body[0] as Return;
            if (ret == null || ret.Value == null) return false;
            exprPretty = H.PyExprPrint(ret.Value);
            return true;
        }

        /// <summary>Detect <c>if COND: return A</c> with either an <c>else: return B</c> or a trailing <c>return B</c>.</summary>
        public static bool IsIfReturnElseReturnBody(IReadOnlyList<Stmt> body, out string cond, out string trueExpr, out string falseExpr)
        {
            cond = null; trueExpr = null; falseExpr = null;
            if (body.Count == 0) return false;
            If ifStmt = body[0] as If;
            if (ifStmt == null) return false;
            if (ifStmt.Body.Count != 1) return false;
            Return trueRet = ifStmt.Body[0] as Return;
            if (trueRet == null || trueRet.Value == null) return false;

            Return falseRet;
            if (ifStmt.OrElse.Count > 0)
            {
                if (body.Count != 1) return false;
                if (ifStmt.OrElse.Count != 1) return false;
                falseRet = ifStmt.OrElse[0] as Return;
            }
            else
            {
                if (body.Count != 2) return false;
                falseRet = body[1] as Return;
            }
            if (falseRet == null || falseRet.Value == null) return false;

            cond = H.PyExprPrint(ifStmt.Test);
            trueExpr = H.PyExprPrint(trueRet.Value);
            falseExpr = H.PyExprPrint(falseRet.Value);
            return true;
        }
    }
}
