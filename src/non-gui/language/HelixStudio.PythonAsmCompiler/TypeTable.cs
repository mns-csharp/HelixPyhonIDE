using System;
using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>The two machine types this subset has. Everything else is refused.</summary>
    internal enum VType
    {
        /// <summary>A 64-bit signed integer, carried in RAX.</summary>
        Int,
        /// <summary>An IEEE-754 double, carried in XMM0.</summary>
        Float
    }

    /// <summary>The declared shape of one function: what it takes and what it gives back.</summary>
    internal sealed class FunctionType
    {
        public string Name { get; }
        public IReadOnlyList<VType> Parameters { get; }
        public VType Returns { get; }

        public FunctionType(string name, IReadOnlyList<VType> parameters, VType returns)
        {
            Name = name;
            Parameters = parameters ?? new VType[0];
            Returns = returns;
        }
    }

    /// <summary>
    /// Static types for one function's locals and expressions.
    ///
    /// A register is not polymorphic: RAX holds an integer and XMM0 holds a double, and the
    /// instruction that adds them is a different instruction. So every value has to have a type
    /// known at compile time, and a local that is an int on one line cannot become a float on the
    /// next — Python allows it, this subset refuses it, because the alternative is a tagged
    /// representation and a runtime to interpret the tag.
    /// </summary>
    internal sealed class TypeTable
    {
        private readonly IReadOnlyDictionary<string, FunctionType> _functions;
        private readonly Dictionary<string, VType> _locals = new Dictionary<string, VType>(StringComparer.Ordinal);

        public string Error { get; private set; }
        public int ErrorLine { get; private set; }

        private TypeTable(IReadOnlyDictionary<string, FunctionType> functions)
        {
            _functions = functions;
        }

        /// <summary>The type of every parameter and local, or an <see cref="Error"/>.</summary>
        public static TypeTable Build(FunctionDef fn, IReadOnlyDictionary<string, FunctionType> functions)
        {
            var table = new TypeTable(functions);
            FunctionType self = functions[fn.Name];
            if (fn.Args != null && fn.Args.Args != null)
                for (int i = 0; i < fn.Args.Args.Count; i++)
                    table._locals[fn.Args.Args[i].Name] = self.Parameters[i];
            table.WalkBlock(fn.Body);
            return table;
        }

        public bool TryGetLocal(string name, out VType type) { return _locals.TryGetValue(name, out type); }

        public FunctionType FunctionOf(string name)
        {
            FunctionType t;
            return _functions.TryGetValue(name, out t) ? t : null;
        }

        private void Fail(string reason, int line)
        {
            if (Error == null) { Error = reason; ErrorLine = line; }
        }

        private void Declare(string name, VType type, int line)
        {
            VType existing;
            if (_locals.TryGetValue(name, out existing))
            {
                if (existing != type)
                    Fail("local '" + name + "' is used as both " + Describe(existing) + " and "
                         + Describe(type) + "; a stack slot has one machine type, so this subset "
                         + "has no way to hold either", line);
                return;
            }
            _locals[name] = type;
        }

        public static string Describe(VType t) { return t == VType.Int ? "int" : "float"; }

        private void WalkBlock(IReadOnlyList<Stmt> body)
        {
            if (body == null) return;
            foreach (Stmt s in body)
            {
                if (Error != null) return;
                WalkStatement(s);
            }
        }

        private void WalkStatement(Stmt s)
        {
            switch (s)
            {
                case Assign a:
                    {
                        var targets = a.Targets.Count == 1 ? Elements(a.Targets[0]) : null;
                        if (targets != null)
                        {
                            IReadOnlyList<Expr> sources = Elements(a.Value);
                            if (sources == null || sources.Count != targets.Count) return;   // refused later
                            for (int i = 0; i < targets.Count; i++)
                            {
                                var n = targets[i] as Name;
                                if (n != null) Declare(n.Id, TypeOf(sources[i]), a.Span.Start.Line);
                            }
                            return;
                        }
                        VType t = TypeOf(a.Value);
                        foreach (Expr target in a.Targets)
                        {
                            var n = target as Name;
                            if (n != null) Declare(n.Id, t, a.Span.Start.Line);
                        }
                        return;
                    }

                case AnnAssign an:
                    {
                        var n = an.Target as Name;
                        if (n == null) return;
                        VType declared;
                        if (!TryAnnotation(an.Annotation, out declared))
                        {
                            Fail("'" + n.Id + "' must be annotated 'int' or 'float'", an.Span.Start.Line);
                            return;
                        }
                        Declare(n.Id, declared, an.Span.Start.Line);
                        if (an.Value != null && TypeOf(an.Value) == VType.Float && declared == VType.Int)
                            Fail("a float cannot be stored in the int variable '" + n.Id
                                 + "'; use int(...) to truncate it explicitly", an.Span.Start.Line);
                        return;
                    }

                case AugAssign ag:
                    {
                        var n = ag.Target as Name;
                        if (n == null) return;
                        VType current;
                        if (!_locals.TryGetValue(n.Id, out current)) return;   // refused by LocalFlow
                        VType result = Combine(ag.Op, current, TypeOf(ag.Value));
                        if (result != current)
                            Fail("this augmented assignment would change '" + n.Id + "' from "
                                 + Describe(current) + " to " + Describe(result)
                                 + "; a stack slot has one machine type", ag.Span.Start.Line);
                        return;
                    }

                case If iff: WalkBlock(iff.Body); WalkBlock(iff.OrElse); return;
                case While w: WalkBlock(w.Body); WalkBlock(w.OrElse); return;
                case For f:
                    {
                        var n = f.Target as Name;
                        // range() yields integers, always.
                        if (n != null) Declare(n.Id, VType.Int, f.Span.Start.Line);
                        WalkBlock(f.Body); WalkBlock(f.OrElse);
                        return;
                    }
                default: return;
            }
        }

        private static IReadOnlyList<Expr> Elements(Expr e)
        {
            var tuple = e as TupleExpr;
            if (tuple != null) return tuple.Elts;
            var list = e as ListExpr;
            if (list != null) return list.Elts;
            return null;
        }

        public static bool TryAnnotation(Expr annotation, out VType type)
        {
            type = VType.Int;
            var name = annotation as Name;
            if (name == null) return false;
            if (name.Id == "int") { type = VType.Int; return true; }
            if (name.Id == "float") { type = VType.Float; return true; }
            return false;
        }

        /// <summary>
        /// Python's numeric tower in miniature: any operand being a float makes the result a
        /// float, and <c>/</c> is a float divide even when both sides are integers.
        /// </summary>
        public static VType Combine(BinOp op, VType left, VType right)
        {
            if (op == BinOp.Div) return VType.Float;
            return (left == VType.Float || right == VType.Float) ? VType.Float : VType.Int;
        }

        /// <summary>The static type of an expression. Unknown constructs answer Int and are refused later.</summary>
        public VType TypeOf(Expr e)
        {
            switch (e)
            {
                case Constant c:
                    return (c.Value is double || c.Value is float) ? VType.Float : VType.Int;

                case Name n:
                    {
                        VType t;
                        return _locals.TryGetValue(n.Id, out t) ? t : VType.Int;
                    }

                case UnaryOpExpr u:
                    return (u.Op == UnaryOp.Not || u.Op == UnaryOp.Invert) ? VType.Int : TypeOf(u.Operand);

                case BinOpExpr b:
                    return Combine(b.Op, TypeOf(b.Left), TypeOf(b.Right));

                case Compare _:
                    return VType.Int;      // a comparison is a bool, and a bool is an int here

                case BoolOpExpr bo:
                    {
                        // Python yields the operand itself, so the operands must agree on a type
                        // or the result would need a tag to say which one came back.
                        VType first = bo.Values.Count > 0 ? TypeOf(bo.Values[0]) : VType.Int;
                        for (int i = 1; i < bo.Values.Count; i++)
                            if (TypeOf(bo.Values[i]) != first) return VType.Float;
                        return first;
                    }

                case IfExp t:
                    return (TypeOf(t.Body) == VType.Float || TypeOf(t.OrElse) == VType.Float)
                        ? VType.Float : VType.Int;

                case Call call:
                    {
                        var callee = call.Func as Name;
                        if (callee == null) return VType.Int;
                        if (callee.Id == "float") return VType.Float;
                        if (callee.Id == "int") return VType.Int;
                        FunctionType f = FunctionOf(callee.Id);
                        return f != null ? f.Returns : VType.Int;
                    }

                default:
                    return VType.Int;
            }
        }
    }
}
