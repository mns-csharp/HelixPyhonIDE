namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Expression AST nodes. Port of the C++
    /// helix::cuda_vcompiler::ast expression classes (cuda_vcompiler/ast/expressions.h).
    /// C++ unique_ptr children map to nullable class references; nullptr maps to null.
    /// </summary>

    /// <summary>
    /// An identifier reference, possibly qualified (e.g. `cuda::foo`, `std::size_t`).
    /// <see cref="Id"/> holds the final segment; <see cref="Qualified"/> holds the full text.
    /// </summary>
    public sealed class NameExpr : Expr
    {
        public string Id { get; private set; }
        public string Qualified { get; private set; }

        public NameExpr(string id, string qualified, SourceSpan span) : base(span)
        {
            Id = id ?? string.Empty;
            Qualified = qualified ?? string.Empty;
        }
    }

    public enum LiteralKind { Int, Float, String, Char, Bool, Nullptr }

    public sealed class LiteralExpr : Expr
    {
        public LiteralKind Kind { get; private set; }
        public string Text { get; private set; }

        public LiteralExpr(LiteralKind kind, string text, SourceSpan span) : base(span)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }
    }

    public sealed class ParenExpr : Expr
    {
        public Expr Inner { get; private set; }

        public ParenExpr(Expr inner, SourceSpan span) : base(span)
        {
            Inner = inner;
        }
    }

    public sealed class UnaryExpr : Expr
    {
        public UnaryOpKind Op { get; private set; }
        public Expr Operand { get; private set; }

        public UnaryExpr(UnaryOpKind op, Expr operand, SourceSpan span) : base(span)
        {
            Op = op;
            Operand = operand;
        }
    }

    public sealed class BinaryExpr : Expr
    {
        public BinOpKind Op { get; private set; }
        public Expr Lhs { get; private set; }
        public Expr Rhs { get; private set; }

        public BinaryExpr(BinOpKind op, Expr lhs, Expr rhs, SourceSpan span) : base(span)
        {
            Op = op;
            Lhs = lhs;
            Rhs = rhs;
        }

        public bool IsAssignment()
        {
            return Op >= BinOpKind.Assign && Op <= BinOpKind.ShrAssign;
        }
    }

    public sealed class ConditionalExpr : Expr
    {
        public Expr Cond { get; private set; }
        public Expr Then { get; private set; }
        public Expr Else { get; private set; }

        public ConditionalExpr(Expr cond, Expr then, Expr els, SourceSpan span) : base(span)
        {
            Cond = cond;
            Then = then;
            Else = els;
        }
    }

    public sealed class CallExpr : Expr
    {
        public Expr Callee { get; private set; }
        public IReadOnlyList<Expr> Args { get; private set; }

        public CallExpr(Expr callee, List<Expr> args, SourceSpan span) : base(span)
        {
            Callee = callee;
            Args = args ?? new List<Expr>();
        }
    }

    /// <summary>
    /// kernel&lt;&lt;&lt;grid, block[, sharedMem[, stream]]&gt;&gt;&gt;(args...). The load-bearing
    /// CUDA expression node consumed by the launch-config and memory analyses.
    /// </summary>
    public sealed class KernelLaunchExpr : Expr
    {
        public Expr Callee { get; private set; }
        public Expr Grid { get; private set; }
        public Expr Block { get; private set; }
        public Expr SharedMem { get; private set; }  // nullable
        public Expr Stream { get; private set; }      // nullable
        public IReadOnlyList<Expr> Args { get; private set; }

        public KernelLaunchExpr(Expr callee, Expr grid, Expr block, Expr sharedMem, Expr stream,
                                List<Expr> args, SourceSpan span) : base(span)
        {
            Callee = callee;
            Grid = grid;
            Block = block;
            SharedMem = sharedMem;
            Stream = stream;
            Args = args ?? new List<Expr>();
        }
    }

    public sealed class MemberExpr : Expr
    {
        public Expr Object { get; private set; }
        public string Member { get; private set; }
        public bool IsArrow { get; private set; }

        public MemberExpr(Expr obj, string member, bool arrow, SourceSpan span) : base(span)
        {
            Object = obj;
            Member = member ?? string.Empty;
            IsArrow = arrow;
        }
    }

    public sealed class IndexExpr : Expr
    {
        public Expr Base { get; private set; }
        public Expr Index { get; private set; }

        public IndexExpr(Expr baseExpr, Expr index, SourceSpan span) : base(span)
        {
            Base = baseExpr;
            Index = index;
        }
    }

    /// <summary>C-style or functional/named cast: (T)e, T(e), static_cast&lt;T&gt;(e), etc.</summary>
    public sealed class CastExpr : Expr
    {
        public CxxType Type { get; private set; }
        public Expr Operand { get; private set; }

        public CastExpr(CxxType type, Expr operand, SourceSpan span) : base(span)
        {
            Type = type;
            Operand = operand;
        }
    }

    public sealed class NewExpr : Expr
    {
        public CxxType Type { get; private set; }
        public Expr ArraySize { get; private set; }  // nullable

        public NewExpr(CxxType type, Expr arraySize, SourceSpan span) : base(span)
        {
            Type = type;
            ArraySize = arraySize;
        }
    }

    public sealed class DeleteExpr : Expr
    {
        public Expr Operand { get; private set; }
        public bool IsArray { get; private set; }

        public DeleteExpr(Expr operand, bool isArray, SourceSpan span) : base(span)
        {
            Operand = operand;
            IsArray = isArray;
        }
    }

    /// <summary>Brace-enclosed initializer / aggregate: { a, b, c } (also dim3{x,y,z}).</summary>
    public sealed class InitListExpr : Expr
    {
        public IReadOnlyList<Expr> Elements { get; private set; }

        public InitListExpr(List<Expr> elements, SourceSpan span) : base(span)
        {
            Elements = elements ?? new List<Expr>();
        }
    }

    /// <summary>
    /// C++11 lambda: [captures](params) { body }. Captures and the parameter/return
    /// types are not modeled — only the parameter names and the body are kept, and
    /// the analyses deliberately do not descend into the body (captured names make
    /// resolution there unprovable). Parsed so lambda-bearing TUs stop producing
    /// spurious parse diagnostics.
    /// </summary>
    public sealed class LambdaExpr : Expr
    {
        public IReadOnlyList<string> ParamNames { get; private set; }
        public Stmt Body { get; private set; }  // nullable

        public LambdaExpr(List<string> paramNames, Stmt body, SourceSpan span) : base(span)
        {
            ParamNames = paramNames ?? new List<string>();
            Body = body;
        }
    }
}
