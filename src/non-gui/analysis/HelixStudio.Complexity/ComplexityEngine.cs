namespace HelixPythonNamespace.Complexity
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Python;
    using HelixPythonNamespace.Python.Syntax;
    using OrderKind = HelixPythonNamespace.Complexity.GrowthOrderKind;

    /// <summary>
    /// Heuristic, AST-based asymptotic-complexity analyzer (MIT 6.042J §13.7 definitions —
    /// see docs\reference\asymptotic-notations\mit6_042js15_session24.pdf and
    /// .claude\skills\helixstudio\references\asymptotic-complexity-analysis.md). True Big-O
    /// inference is undecidable for arbitrary code, so — in the same spirit as
    /// <c>HelixPythonNamespace.Metrics</c>'s cyclomatic complexity — this reads structural AST shape
    /// (loop nesting depth, loop-halving patterns, recursion call shape, a couple of
    /// known-cost stdlib calls) and reports a best-effort classification with the evidence
    /// that produced it, not a formal proof.
    /// </summary>
    public sealed class ComplexityEngine
    {
        public ComplexityReport Analyze(IReadOnlyList<ComplexitySource> sources, CancellationToken cancel = default(CancellationToken))
        {
            ComplexityReport report = new ComplexityReport();
            if (sources == null) return report;

            foreach (ComplexitySource src in sources)
            {
                if (cancel.IsCancellationRequested) break;
                ParseResult parsed = Python.ParseModule(src.Text ?? string.Empty, src.FilePath);
                foreach (StatementSyntax s in parsed.Module.Statements)
                    CollectFromStatement(s, src.FilePath, string.Empty, report.Findings);
            }
            return report;
        }

        private static void CollectFromStatement(StatementSyntax s, string filePath, string qualifierPrefix, List<ComplexityFinding> outList)
        {
            FunctionDefSyntax fn = s as FunctionDefSyntax;
            if (fn != null)
            {
                outList.Add(ClassifyFunction(fn, filePath, qualifierPrefix));
                string nestedPrefix = string.IsNullOrEmpty(qualifierPrefix) ? fn.Name : qualifierPrefix + "." + fn.Name;
                if (fn.Body != null)
                    foreach (StatementSyntax inner in fn.Body.Statements)
                        CollectFromStatement(inner, filePath, nestedPrefix, outList);
                return;
            }
            ClassDefSyntax cls = s as ClassDefSyntax;
            if (cls != null)
            {
                string nestedPrefix = string.IsNullOrEmpty(qualifierPrefix) ? cls.Name : qualifierPrefix + "." + cls.Name;
                if (cls.Body != null)
                    foreach (StatementSyntax inner in cls.Body.Statements)
                        CollectFromStatement(inner, filePath, nestedPrefix, outList);
            }
        }

        // ---- per-function classification ----

        private static ComplexityFinding ClassifyFunction(FunctionDefSyntax fn, string filePath, string qualifierPrefix)
        {
            string name = string.IsNullOrEmpty(qualifierPrefix) ? fn.Name : qualifierPrefix + "." + fn.Name;
            ComplexityFinding finding = new ComplexityFinding { MemberName = name, FilePath = filePath, Line = fn.Span.Start.Line };

            RecursionShape rec = DetectRecursion(fn);
            Order worst;
            string evidence;
            if (rec.MaxCallsInOneStatement > 0 || rec.ReturnStatementCallCount > 0 || rec.OtherStatementCallCount > 0)
            {
                worst = ClassifyRecursion(rec, out evidence);
            }
            else
            {
                int plainDepth = 0;
                bool hasHalving = false;
                AnalyzeLoops(fn.Body, 0, ref plainDepth, ref hasHalving);
                bool hasSortCall = fn.Body != null && HasKnownCostlyCall(fn.Body);
                worst = ClassifyLoops(plainDepth, hasHalving, hasSortCall, out evidence);
            }

            bool earlyExit = fn.Body != null && ContainsConditionalEarlyExit(fn.Body);
            Order next = worst.NextRung();

            finding.WorstCase = "O(" + worst.Format() + ")";
            finding.BestCase = earlyExit ? "Ω(1)" : "Ω(" + worst.Format() + ")";
            finding.AverageCase = "Θ(" + worst.Format() + ")";
            finding.LittleO = worst.Kind == OrderKind.Exponential ? "(top of modeled ladder)" : "o(" + next.Format() + ")";
            finding.WorstCaseKind = worst.Kind;
            finding.WorstCaseDegree = worst.Degree;
            finding.BestCaseKind = earlyExit ? OrderKind.Constant : worst.Kind;
            finding.BestCaseDegree = earlyExit ? 0 : worst.Degree;
            finding.Evidence = evidence + " (line " + fn.Span.Start.Line + ")";
            finding.Explanation = BuildExplanation(worst, earlyExit, evidence);
            return finding;
        }

        private static string BuildExplanation(Order worst, bool earlyExit, string evidence)
        {
            string s = "Worst case O(" + worst.Format() + "): " + evidence + ". ";
            s += earlyExit
                ? "A conditional early exit (return/break inside a guarded branch) was found inside a loop, so the best case is Ω(1) while the worst/average cases keep the structural order above."
                : "No conditional early exit was found, so best (Ω), worst (O), and average (Θ) all share the same order.";
            return s;
        }

        // ---- recursion shape ----

        // A flat count of self-call occurrences can't tell "two calls that ALWAYS both run"
        // (mergesort's `left = f(...); right = f(...)`, genuinely O(n log n)) apart from "two
        // calls that are almost always mutually-exclusive early returns" (binary search's
        // `if ...: return f(a)` / `return f(b)`, really a SINGLE recursive path, O(log n)). This
        // walker classifies each self-call by which kind of statement directly holds it:
        //   - two-or-more calls found within the SAME expression (`f(a) + f(b)`) -> true
        //     unconditional branching (naive Fibonacci shape).
        //   - two-or-more calls found in SEPARATE non-return statements (assignments) -> also
        //     unconditional (they're not exit points, so nothing stops both from running).
        //   - any other count (including 2+ calls spread across separate `return f(...)`
        //     statements) -> treated as one effective recursive path, since guard-clause /
        //     early-return recursion is by far the most common shape that produces this pattern.
        private sealed class RecursionShape
        {
            public int MaxCallsInOneStatement;
            public int ReturnStatementCallCount;
            public int OtherStatementCallCount;
            public bool AnyHalvingArg;
            public bool AnyDecrementArg;
            public bool HasLoopInBody;
        }

        private static RecursionShape DetectRecursion(FunctionDefSyntax fn)
        {
            RecursionShape shape = new RecursionShape();
            if (fn.Body == null) return shape;

            WalkForRecursion(fn.Body, fn.Name, shape);

            foreach (SyntaxNode node in fn.Body.Descendants())
            {
                if (node is ForStatement || node is WhileStatement) { shape.HasLoopInBody = true; break; }
            }
            return shape;
        }

        private static void WalkForRecursion(SuiteSyntax body, string fnName, RecursionShape shape)
        {
            if (body == null) return;
            foreach (StatementSyntax s in body.Statements)
                WalkForRecursionStmt(s, fnName, shape);
        }

        private static void WalkForRecursionStmt(StatementSyntax s, string fnName, RecursionShape shape)
        {
            IfStatement iff = s as IfStatement;
            if (iff != null)
            {
                WalkForRecursion(iff.Body, fnName, shape);
                foreach (IfStatement elf in iff.Elifs) WalkForRecursion(elf.Body, fnName, shape);
                WalkForRecursion(iff.Else, fnName, shape);
                return;
            }
            TryStatement tr = s as TryStatement;
            if (tr != null)
            {
                WalkForRecursion(tr.Body, fnName, shape);
                foreach (ExceptHandler h in tr.Handlers) WalkForRecursion(h.Body, fnName, shape);
                WalkForRecursion(tr.Else, fnName, shape);
                WalkForRecursion(tr.Finally, fnName, shape);
                return;
            }
            WithStatement wi = s as WithStatement;
            if (wi != null) { WalkForRecursion(wi.Body, fnName, shape); return; }
            ForStatement fo = s as ForStatement;
            if (fo != null) { WalkForRecursion(fo.Body, fnName, shape); WalkForRecursion(fo.Else, fnName, shape); return; }
            WhileStatement w = s as WhileStatement;
            if (w != null) { WalkForRecursion(w.Body, fnName, shape); WalkForRecursion(w.Else, fnName, shape); return; }

            // Leaf statement: count self-calls within whichever expression it carries.
            ExpressionSyntax exprToScan = null;
            ReturnStatement ret = s as ReturnStatement;
            if (ret != null) exprToScan = ret.Value;
            else
            {
                AssignmentStatement asn = s as AssignmentStatement;
                if (asn != null) exprToScan = asn.Value;
                else
                {
                    AugmentedAssignmentStatement aug = s as AugmentedAssignmentStatement;
                    if (aug != null) exprToScan = aug.Value;
                    else
                    {
                        ExpressionStatement es = s as ExpressionStatement;
                        if (es != null) exprToScan = es.Expression;
                    }
                }
            }
            if (exprToScan == null) return;

            int callsHere = CountSelfCalls(exprToScan, fnName, shape);
            if (callsHere == 0) return;
            if (callsHere > shape.MaxCallsInOneStatement) shape.MaxCallsInOneStatement = callsHere;
            if (ret != null) shape.ReturnStatementCallCount += callsHere;
            else shape.OtherStatementCallCount += callsHere;
        }

        private static int CountSelfCalls(ExpressionSyntax expr, string fnName, RecursionShape shape)
        {
            if (expr == null) return 0;
            int count = CheckOneCallNode(expr, fnName, shape) ? 1 : 0;
            foreach (SyntaxNode d in expr.Descendants())
                if (CheckOneCallNode(d, fnName, shape)) count++;
            return count;
        }

        private static bool CheckOneCallNode(SyntaxNode node, string fnName, RecursionShape shape)
        {
            CallExpression call = node as CallExpression;
            if (call == null) return false;
            string calleeName = null;
            NameExpression cn = call.Callee as NameExpression;
            if (cn != null) calleeName = cn.Name;
            else
            {
                AttributeExpression ca = call.Callee as AttributeExpression;
                if (ca != null)
                {
                    // `super().__init__(...)` / `super().foo(...)` calls the PARENT class's
                    // same-named method, not this one — the single most common false-positive
                    // trigger for this heuristic (every overriding __init__ would otherwise
                    // look like linear recursion). Exclude it explicitly.
                    CallExpression targetCall = ca.Target as CallExpression;
                    bool isSuperCall = targetCall != null && targetCall.Callee is NameExpression tn && tn.Name == "super";
                    if (!isSuperCall) calleeName = ca.Name;
                }
            }
            if (calleeName != fnName) return false;

            foreach (CallArgument arg in call.Arguments)
            {
                // `foo(n // 2)` / `foo(mid + 1, hi)` — the classic recursive binary-search idiom
                // narrows the range to one side of a locally-computed midpoint, not via a literal
                // `/2` in the call itself.
                if (NameContainsMid(arg.Value)) shape.AnyHalvingArg = true;
                BinaryExpression be = arg.Value as BinaryExpression;
                if (be == null) continue;
                if (be.Operator == BinaryOperator.FloorDivide || be.Operator == BinaryOperator.Divide) shape.AnyHalvingArg = true;
                if (be.Operator == BinaryOperator.Subtract) shape.AnyDecrementArg = true;
                if ((be.Operator == BinaryOperator.Add || be.Operator == BinaryOperator.Subtract) &&
                    (NameContainsMid(be.Left) || NameContainsMid(be.Right)))
                    shape.AnyHalvingArg = true;
            }
            return true;
        }

        private static Order ClassifyRecursion(RecursionShape rec, out string evidence)
        {
            if (rec.MaxCallsInOneStatement >= 2)
            {
                if (rec.AnyHalvingArg)
                {
                    evidence = "two or more recursive calls in the same expression on halved input (divide-and-conquer shape)";
                    return Order.LinearLogOrder();
                }
                evidence = "two or more recursive calls in the same expression without a halving pattern (branching/naive-recursion shape, e.g. naive Fibonacci)";
                return Order.ExponentialOrder();
            }

            if (rec.OtherStatementCallCount >= 2)
            {
                if (rec.AnyHalvingArg)
                {
                    evidence = rec.HasLoopInBody
                        ? "two or more recursive calls, in separate unconditionally-executed statements, on halved input plus linear work (divide-and-conquer / mergesort shape)"
                        : "two or more recursive calls, in separate unconditionally-executed statements, on halved input (divide-and-conquer shape)";
                    return Order.LinearLogOrder();
                }
                evidence = "two or more recursive calls in separate unconditionally-executed statements without a halving pattern";
                return Order.ExponentialOrder();
            }

            // Includes 2+ self-calls spread across separate `return f(...)` statements — almost
            // always mutually-exclusive early-return branches (if x: return f(a) / return f(b)),
            // i.e. one effective recursive path per call, not a branching recurrence.
            if (rec.AnyHalvingArg)
            {
                evidence = "a recursive call whose argument is derived from a halved/bisected range (binary-search-style recursion)";
                return Order.LogOrder();
            }
            evidence = "a recursive call" + (rec.AnyDecrementArg ? " with the input reduced by a constant each time (linear recursion)" : string.Empty);
            return Order.LinearOrder();
        }

        // ---- loop shape ----

        private static void AnalyzeLoops(SuiteSyntax body, int currentDepth, ref int maxPlainDepth, ref bool hasHalving)
        {
            if (body == null) return;
            foreach (StatementSyntax s in body.Statements)
                AnalyzeLoopsStmt(s, currentDepth, ref maxPlainDepth, ref hasHalving);
        }

        private static void AnalyzeLoopsStmt(StatementSyntax s, int depth, ref int maxPlainDepth, ref bool hasHalving)
        {
            WhileStatement w = s as WhileStatement;
            if (w != null)
            {
                bool halving = LoopBodyHasHalvingUpdate(w.Body, GetControlVarNames(w.Test));
                int newDepth = depth;
                if (halving) hasHalving = true;
                else
                {
                    newDepth = depth + 1;
                    if (newDepth > maxPlainDepth) maxPlainDepth = newDepth;
                }
                AnalyzeLoops(w.Body, newDepth, ref maxPlainDepth, ref hasHalving);
                AnalyzeLoops(w.Else, depth, ref maxPlainDepth, ref hasHalving);
                return;
            }

            ForStatement fo = s as ForStatement;
            if (fo != null)
            {
                int newDepth = depth + 1;
                if (newDepth > maxPlainDepth) maxPlainDepth = newDepth;
                AnalyzeLoops(fo.Body, newDepth, ref maxPlainDepth, ref hasHalving);
                AnalyzeLoops(fo.Else, depth, ref maxPlainDepth, ref hasHalving);
                return;
            }

            IfStatement iff = s as IfStatement;
            if (iff != null)
            {
                AnalyzeLoops(iff.Body, depth, ref maxPlainDepth, ref hasHalving);
                foreach (IfStatement elf in iff.Elifs) AnalyzeLoops(elf.Body, depth, ref maxPlainDepth, ref hasHalving);
                AnalyzeLoops(iff.Else, depth, ref maxPlainDepth, ref hasHalving);
                return;
            }

            TryStatement tr = s as TryStatement;
            if (tr != null)
            {
                AnalyzeLoops(tr.Body, depth, ref maxPlainDepth, ref hasHalving);
                foreach (ExceptHandler h in tr.Handlers) AnalyzeLoops(h.Body, depth, ref maxPlainDepth, ref hasHalving);
                AnalyzeLoops(tr.Else, depth, ref maxPlainDepth, ref hasHalving);
                AnalyzeLoops(tr.Finally, depth, ref maxPlainDepth, ref hasHalving);
                return;
            }

            WithStatement wi = s as WithStatement;
            if (wi != null)
            {
                AnalyzeLoops(wi.Body, depth, ref maxPlainDepth, ref hasHalving);
            }
            // Nested FunctionDef / ClassDef get their own records — don't recurse here.
        }

        private static HashSet<string> GetControlVarNames(ExpressionSyntax test)
        {
            HashSet<string> names = new HashSet<string>();
            if (test == null) return names;
            NameExpression ne = test as NameExpression;
            if (ne != null) names.Add(ne.Name);
            foreach (SyntaxNode d in test.Descendants())
            {
                NameExpression n = d as NameExpression;
                if (n != null) names.Add(n.Name);
            }
            return names;
        }

        private static bool LoopBodyHasHalvingUpdate(SuiteSyntax body, HashSet<string> controlVars)
        {
            if (body == null || controlVars.Count == 0) return false;
            foreach (SyntaxNode node in body.Descendants())
            {
                AugmentedAssignmentStatement aug = node as AugmentedAssignmentStatement;
                if (aug != null)
                {
                    NameExpression ne = aug.Target as NameExpression;
                    if (ne != null && controlVars.Contains(ne.Name) &&
                        (aug.Operator == BinaryOperator.FloorDivide || aug.Operator == BinaryOperator.Divide || aug.Operator == BinaryOperator.Multiply))
                        return true;
                    continue;
                }
                AssignmentStatement asn = node as AssignmentStatement;
                if (asn != null && asn.Targets.Count == 1)
                {
                    NameExpression tne = asn.Targets[0] as NameExpression;
                    if (tne != null && controlVars.Contains(tne.Name) && IsHalvingOrDoublingExpr(asn.Value, tne.Name))
                        return true;
                }
            }
            return false;
        }

        private static bool IsHalvingOrDoublingExpr(ExpressionSyntax expr, string varName)
        {
            BinaryExpression be = expr as BinaryExpression;
            if (be != null)
            {
                if (be.Operator == BinaryOperator.FloorDivide || be.Operator == BinaryOperator.Divide)
                    return ReferencesName(be.Left, varName);
                if (be.Operator == BinaryOperator.Multiply)
                    return ReferencesName(be.Left, varName) || ReferencesName(be.Right, varName);
                // The classic binary-search bounds update is NOT `x //= 2` — it's narrowing lo/hi
                // to one side of the midpoint: `lo = mid + 1` / `hi = mid - 1`. Treat "one operand
                // names the midpoint" as the same halving idiom as the loop-condition-var check below.
                if (be.Operator == BinaryOperator.Add || be.Operator == BinaryOperator.Subtract)
                    return NameContainsMid(be.Left) || NameContainsMid(be.Right);
                return false;
            }
            // `hi = mid` / `lo = mid` — reassigning a bisection bound to the midpoint is the
            // other half of the classic binary-search halving idiom (the midpoint itself is
            // usually computed as `(lo + hi) // 2` in a separate statement).
            return NameContainsMid(expr);
        }

        private static bool ReferencesName(ExpressionSyntax expr, string name)
        {
            NameExpression ne = expr as NameExpression;
            return ne != null && ne.Name == name;
        }

        // Checks expr itself AND everything nested inside it — not just a direct NameExpression.
        // `items[:mid]` / `items[mid:]` (the merge_sort halving idiom) parse as
        // SubscriptExpression(Index: SliceExpression(Upper/Lower: NameExpression("mid"))), two
        // levels below the argument's own top-level expression, so a shallow check alone misses
        // it entirely (regressed merge_sort from O(n log n) to O(2^n) — the mergesort shape needs
        // AnyHalvingArg to fire on THIS pattern, not just a literal "mid" argument or "n // 2").
        private static bool NameContainsMid(ExpressionSyntax expr)
        {
            if (expr == null) return false;
            NameExpression self = expr as NameExpression;
            if (self != null && self.Name.IndexOf("mid", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (SyntaxNode d in expr.Descendants())
            {
                NameExpression ne = d as NameExpression;
                if (ne != null && ne.Name.IndexOf("mid", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool HasKnownCostlyCall(SuiteSyntax body)
        {
            foreach (SyntaxNode node in body.Descendants())
            {
                CallExpression call = node as CallExpression;
                if (call == null) continue;
                NameExpression ne = call.Callee as NameExpression;
                if (ne != null && ne.Name == "sorted") return true;
                AttributeExpression ae = call.Callee as AttributeExpression;
                if (ae != null && ae.Name == "sort") return true;
            }
            return false;
        }

        private static Order ClassifyLoops(int plainDepth, bool hasHalving, bool hasSortCall, out string evidence)
        {
            if (plainDepth == 0 && !hasHalving)
            {
                if (hasSortCall)
                {
                    evidence = "no explicit loop or recursion, but a sort call (sorted()/.sort()) over input-sized data dominates";
                    return Order.LinearLogOrder();
                }
                evidence = "no loop and no recursion found";
                return Order.ConstantOrder();
            }
            if (plainDepth == 0)
            {
                evidence = "a single loop whose control variable is halved/doubled each iteration (binary-search / bisection shape)";
                return Order.LogOrder();
            }
            if (plainDepth == 1 && hasHalving)
            {
                evidence = "one full n-sized loop plus a loop that halves its bound each iteration";
                return Order.LinearLogOrder();
            }
            if (plainDepth == 1)
            {
                evidence = "a single non-nested loop over the input";
                return Order.LinearOrder();
            }
            evidence = plainDepth + "-deep nested loops, each iterating roughly n times";
            return Order.PolyOrder(plainDepth);
        }

        // ---- best/worst-case divergence ----

        private static bool ContainsConditionalEarlyExit(SuiteSyntax body)
        {
            foreach (SyntaxNode node in body.Descendants())
            {
                SuiteSyntax loopBody = null;
                ForStatement fo = node as ForStatement;
                if (fo != null) loopBody = fo.Body;
                else
                {
                    WhileStatement w = node as WhileStatement;
                    if (w != null) loopBody = w.Body;
                }
                if (loopBody == null) continue;

                foreach (SyntaxNode inner in loopBody.Descendants())
                {
                    IfStatement iff = inner as IfStatement;
                    if (iff == null) continue;
                    if (BodyHasReturnOrBreak(iff.Body)) return true;
                    foreach (IfStatement elf in iff.Elifs) if (BodyHasReturnOrBreak(elf.Body)) return true;
                    if (BodyHasReturnOrBreak(iff.Else)) return true;
                }
            }
            return false;
        }

        private static bool BodyHasReturnOrBreak(SuiteSyntax body)
        {
            if (body == null) return false;
            foreach (StatementSyntax s in body.Statements)
                if (s is ReturnStatement || s is BreakStatement) return true;
            return false;
        }

        // ---- growth-rate ladder: O(1) < O(log n) < O(n) < O(n log n) < O(n^2) < ... < O(2^n) ----

        private sealed class Order
        {
            public OrderKind Kind;
            public int Degree;

            public static Order ConstantOrder() { return new Order { Kind = OrderKind.Constant }; }
            public static Order LogOrder() { return new Order { Kind = OrderKind.Log }; }
            public static Order LinearOrder() { return new Order { Kind = OrderKind.Linear }; }
            public static Order LinearLogOrder() { return new Order { Kind = OrderKind.LinearLog }; }
            public static Order PolyOrder(int degree) { return new Order { Kind = OrderKind.Poly, Degree = degree }; }
            public static Order ExponentialOrder() { return new Order { Kind = OrderKind.Exponential }; }

            public string Format()
            {
                switch (Kind)
                {
                    case OrderKind.Constant: return "1";
                    case OrderKind.Log: return "log n";
                    case OrderKind.Linear: return "n";
                    case OrderKind.LinearLog: return "n log n";
                    case OrderKind.Poly: return "n^" + Degree;
                    case OrderKind.Exponential: return "2^n";
                    default: return "?";
                }
            }

            /// <summary>The next-higher rung on the fixed growth ladder — used to answer the
            /// little-o "is this strictly slower than the next canonical class?" question.</summary>
            public Order NextRung()
            {
                switch (Kind)
                {
                    case OrderKind.Constant: return LogOrder();
                    case OrderKind.Log: return LinearOrder();
                    case OrderKind.Linear: return LinearLogOrder();
                    case OrderKind.LinearLog: return PolyOrder(2);
                    case OrderKind.Poly: return Degree >= 3 ? ExponentialOrder() : PolyOrder(Degree + 1);
                    default: return ExponentialOrder();
                }
            }
        }
    }
}
