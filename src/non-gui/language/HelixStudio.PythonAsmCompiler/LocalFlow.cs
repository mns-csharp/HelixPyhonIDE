using System;
using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// Definite-assignment analysis over a function body.
    ///
    /// CPython raises <c>UnboundLocalError</c> when a local is read before anything has been
    /// stored in it. A stack frame has no such notion: an unwritten slot holds whatever the
    /// previous call left there, so the compiled program would quietly compute a number from
    /// garbage. This pass finds those reads ahead of emission and refuses them.
    ///
    /// It is deliberately conservative — it reports a name as unassigned unless EVERY path to the
    /// read assigns it. A loop body is one such path that may not run at all, which is why
    /// <c>for i in range(n)</c> does not leave <c>i</c> assigned afterwards: neither does Python.
    /// </summary>
    internal sealed class LocalFlow
    {
        private readonly HashSet<string> _callables;
        private string _reason;
        private int _line;

        private LocalFlow(IEnumerable<string> callables)
        {
            _callables = new HashSet<string>(callables ?? new string[0], StringComparer.Ordinal);
            _callables.Add("range");     // handled structurally by the for-loop lowering
        }

        /// <summary>Null when the body is sound; otherwise the refusal to report.</summary>
        public static string Check(FunctionDef fn, IEnumerable<string> callables, out int line)
        {
            var flow = new LocalFlow(callables);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            if (fn.Args != null && fn.Args.Args != null)
                foreach (Arg a in fn.Args.Args) assigned.Add(a.Name);

            flow.WalkBlock(fn.Body, assigned);
            line = flow._line;
            return flow._reason;
        }

        private void Fail(string reason, int line)
        {
            if (_reason == null) { _reason = reason; _line = line; }
        }

        /// <summary>Walks a block; returns false when control cannot fall out of its end.</summary>
        private bool WalkBlock(IReadOnlyList<Stmt> body, HashSet<string> assigned)
        {
            if (body == null) return true;
            foreach (Stmt s in body)
            {
                if (_reason != null) return true;
                if (!WalkStatement(s, assigned)) return false;
            }
            return true;
        }

        private bool WalkStatement(Stmt s, HashSet<string> assigned)
        {
            switch (s)
            {
                case Return r:
                    if (r.Value != null) CheckUses(r.Value, assigned);
                    return false;

                case Break _:
                case Continue _:
                    return false;

                case Pass _:
                    return true;

                case ExprStmt e:
                    CheckUses(e.Value, assigned);
                    return true;

                case Assign a:
                    CheckUses(a.Value, assigned);
                    foreach (Expr t in a.Targets) BindTarget(t, assigned);
                    return true;

                case AnnAssign an:
                    if (an.Value != null)
                    {
                        CheckUses(an.Value, assigned);
                        BindTarget(an.Target, assigned);
                    }
                    // A bare `x: int` declares a type and stores nothing, so x stays unassigned.
                    return true;

                case AugAssign ag:
                    {
                        var target = ag.Target as Name;
                        if (target != null && !assigned.Contains(target.Id))
                            Fail("local '" + target.Id + "' is read by this augmented assignment "
                                 + "before anything is stored in it", ag.Span.Start.Line);
                        CheckUses(ag.Value, assigned);
                        if (target != null) assigned.Add(target.Id);
                        return true;
                    }

                case If iff:
                    {
                        CheckUses(iff.Test, assigned);
                        var thenSet = new HashSet<string>(assigned, StringComparer.Ordinal);
                        var elseSet = new HashSet<string>(assigned, StringComparer.Ordinal);
                        bool thenFalls = WalkBlock(iff.Body, thenSet);
                        bool elseFalls = WalkBlock(iff.OrElse, elseSet);

                        // Only branches that can reach the join contribute to what is assigned
                        // after it. A branch that returns cannot un-assign anything.
                        if (thenFalls && elseFalls) { assigned.Clear(); assigned.UnionWith(Intersect(thenSet, elseSet)); }
                        else if (thenFalls) { assigned.Clear(); assigned.UnionWith(thenSet); }
                        else if (elseFalls) { assigned.Clear(); assigned.UnionWith(elseSet); }
                        else return false;
                        return true;
                    }

                case While w:
                    {
                        CheckUses(w.Test, assigned);
                        var inner = new HashSet<string>(assigned, StringComparer.Ordinal);
                        WalkBlock(w.Body, inner);
                        // The body may run zero times, so nothing it assigns survives the loop.
                        return true;
                    }

                case For f:
                    {
                        CheckUses(f.Iter, assigned);
                        var inner = new HashSet<string>(assigned, StringComparer.Ordinal);
                        var loopVar = f.Target as Name;
                        if (loopVar != null) inner.Add(loopVar.Id);
                        WalkBlock(f.Body, inner);
                        // Python leaves the loop variable unbound after a zero-iteration loop, so
                        // it is not assigned here either.
                        return true;
                    }

                default:
                    // Anything else is refused by the emitter with its own message; this pass
                    // simply does not claim to understand it.
                    return true;
            }
        }

        private static IEnumerable<string> Intersect(HashSet<string> a, HashSet<string> b)
        {
            foreach (string name in a) if (b.Contains(name)) yield return name;
        }

        private void BindTarget(Expr target, HashSet<string> assigned)
        {
            var name = target as Name;
            if (name != null) { assigned.Add(name.Id); return; }
            var tuple = target as TupleExpr;
            if (tuple != null) { foreach (Expr e in tuple.Elts) BindTarget(e, assigned); return; }
            var list = target as ListExpr;
            if (list != null) { foreach (Expr e in list.Elts) BindTarget(e, assigned); }
        }

        private void CheckUses(Expr e, HashSet<string> assigned)
        {
            if (e == null || _reason != null) return;
            switch (e)
            {
                case Name n:
                    // A bare function name is not a local read; calling it is checked elsewhere.
                    if (!assigned.Contains(n.Id) && !_callables.Contains(n.Id))
                        Fail("local '" + n.Id + "' may be read before anything is stored in it",
                             n.Span.Start.Line);
                    return;

                case Constant _:
                    return;

                case BinOpExpr b: CheckUses(b.Left, assigned); CheckUses(b.Right, assigned); return;
                case UnaryOpExpr u: CheckUses(u.Operand, assigned); return;
                case BoolOpExpr bo: foreach (Expr v in bo.Values) CheckUses(v, assigned); return;
                case Compare c:
                    CheckUses(c.Left, assigned);
                    foreach (Expr v in c.Comparators) CheckUses(v, assigned);
                    return;
                case IfExp t:
                    CheckUses(t.Test, assigned); CheckUses(t.Body, assigned); CheckUses(t.OrElse, assigned);
                    return;
                case TupleExpr tup: foreach (Expr v in tup.Elts) CheckUses(v, assigned); return;
                case ListExpr lst: foreach (Expr v in lst.Elts) CheckUses(v, assigned); return;
                case Call call:
                    // The callee is a name in the module, not a local, so only the arguments are
                    // local reads.
                    foreach (Expr v in call.Args) CheckUses(v, assigned);
                    foreach (Keyword k in call.Keywords) CheckUses(k.Value, assigned);
                    return;
                default:
                    return;
            }
        }
    }
}
