namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    // Mirrors CPython's expr_context (Python/Python-ast.h).
    public enum ExprContext
    {
        Load,
        Store,
        Del
    }

    // boolop
    public enum BoolOp
    {
        And,
        Or
    }

    // operator (binary)
    public enum BinOp
    {
        Add,
        Sub,
        Mult,
        MatMult,
        Div,
        Mod,
        Pow,
        LShift,
        RShift,
        BitOr,
        BitXor,
        BitAnd,
        FloorDiv
    }

    // unaryop
    public enum UnaryOp
    {
        Invert,
        Not,
        UAdd,
        USub
    }

    // cmpop
    public enum CmpOp
    {
        Eq,
        NotEq,
        Lt,
        LtE,
        Gt,
        GtE,
        Is,
        IsNot,
        In,
        NotIn
    }
}
