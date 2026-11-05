using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    public sealed class NameExpression : ExpressionSyntax
    {
        public string Name { get; }
        public NameExpression(string name, SourceSpan span) : base(span) { Name = name ?? string.Empty; }
    }

    public sealed class DottedName : ExpressionSyntax
    {
        public IReadOnlyList<string> Parts { get; }
        public DottedName(IReadOnlyList<string> parts, SourceSpan span) : base(span)
        {
            Parts = parts ?? Array.Empty<string>();
        }
        public string AsString { get { return string.Join(".", (IReadOnlyList<string>)Parts); } }
    }

    public sealed class LiteralExpression : ExpressionSyntax
    {
        public Token Token { get; }
        public LiteralExpression(Token token, SourceSpan span) : base(span) { Token = token; }
    }

    /// <summary>
    /// Expression the parser did not descend into (e.g. method body contents).
    /// Stores the token span so downstream consumers can round-trip the text.
    /// </summary>
    public sealed class UnparsedExpression : ExpressionSyntax
    {
        public IReadOnlyList<Token> Tokens { get; }
        public UnparsedExpression(IReadOnlyList<Token> tokens, SourceSpan span) : base(span)
        {
            Tokens = tokens ?? Array.Empty<Token>();
        }
    }

    /// <summary>
    /// Statement the parser did not fully descend into. Preserves the tokens so
    /// the emitter can round-trip the source verbatim.
    /// </summary>
    public sealed class UnparsedStatement : StatementSyntax
    {
        public IReadOnlyList<Token> Tokens { get; }
        public UnparsedStatement(IReadOnlyList<Token> tokens, SourceSpan span) : base(span)
        {
            Tokens = tokens ?? Array.Empty<Token>();
        }
    }

    public sealed class SuiteSyntax : SyntaxNode
    {
        public IReadOnlyList<StatementSyntax> Statements { get; }
        public SuiteSyntax(IReadOnlyList<StatementSyntax> statements, SourceSpan span) : base(span)
        {
            Statements = statements ?? Array.Empty<StatementSyntax>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Statements;
    }

    public sealed class DecoratorSyntax : SyntaxNode
    {
        public ExpressionSyntax Expression { get; }
        public DecoratorSyntax(ExpressionSyntax expression, SourceSpan span) : base(span)
        {
            Expression = expression;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children => Expression != null ? new SyntaxNode[] { Expression } : Array.Empty<SyntaxNode>();
    }

    public enum ParameterKind
    {
        Positional,
        PositionalOnly,    // before '/'
        KeywordOnly,       // after '*'
        VarPositional,     // *args
        VarKeyword         // **kwargs
    }

    public sealed class ParameterSyntax : SyntaxNode
    {
        public string Name { get; }
        public ExpressionSyntax Annotation { get; }
        public ExpressionSyntax DefaultValue { get; }
        public ParameterKind Kind { get; }
        public ParameterSyntax(string name, ExpressionSyntax annotation, ExpressionSyntax defaultValue,
            ParameterKind kind, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Annotation = annotation;
            DefaultValue = defaultValue;
            Kind = kind;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>(2);
                if (Annotation != null) list.Add(Annotation);
                if (DefaultValue != null) list.Add(DefaultValue);
                return list;
            }
        }
    }

    public sealed class TypeParameterSyntax : SyntaxNode
    {
        public enum TypeParamKind { Type, TypeVarTuple, ParamSpec }
        public string Name { get; }
        public TypeParamKind Kind { get; }
        public ExpressionSyntax Bound { get; }
        public TypeParameterSyntax(string name, TypeParamKind kind, ExpressionSyntax bound, SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Kind = kind;
            Bound = bound;
            AdoptChildren();
        }

        // The bound (PEP 695: `class C[T: Bound]` / `def f[T: Bound]`) is a child expression — without
        // this override it was invisible to every tree walk (same omission as AssignmentStatement,
        // pass #23), so a name used ONLY in a bound was wrongly seen as unused.
        public override IReadOnlyList<SyntaxNode> Children =>
            Bound != null ? new SyntaxNode[] { Bound } : Array.Empty<SyntaxNode>();
    }

    public sealed class ClassDefSyntax : StatementSyntax
    {
        public string Name { get; }
        public IReadOnlyList<DecoratorSyntax> Decorators { get; }
        public IReadOnlyList<TypeParameterSyntax> TypeParameters { get; }
        public IReadOnlyList<ExpressionSyntax> BaseExpressions { get; }
        public IReadOnlyList<KeyValuePair<string, ExpressionSyntax>> KeywordArguments { get; }
        public SuiteSyntax Body { get; }

        public ClassDefSyntax(
            string name,
            IReadOnlyList<DecoratorSyntax> decorators,
            IReadOnlyList<TypeParameterSyntax> typeParameters,
            IReadOnlyList<ExpressionSyntax> baseExpressions,
            IReadOnlyList<KeyValuePair<string, ExpressionSyntax>> keywordArguments,
            SuiteSyntax body,
            SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            Decorators = decorators ?? Array.Empty<DecoratorSyntax>();
            TypeParameters = typeParameters ?? Array.Empty<TypeParameterSyntax>();
            BaseExpressions = baseExpressions ?? Array.Empty<ExpressionSyntax>();
            KeywordArguments = keywordArguments ?? Array.Empty<KeyValuePair<string, ExpressionSyntax>>();
            Body = body;
            AdoptChildren();
        }

        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (DecoratorSyntax d in Decorators) list.Add(d);
                foreach (TypeParameterSyntax tp in TypeParameters) list.Add(tp);
                foreach (ExpressionSyntax b in BaseExpressions) list.Add(b);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class FunctionDefSyntax : StatementSyntax
    {
        public string Name { get; }
        public bool IsAsync { get; }
        public IReadOnlyList<DecoratorSyntax> Decorators { get; }
        public IReadOnlyList<TypeParameterSyntax> TypeParameters { get; }
        public IReadOnlyList<ParameterSyntax> Parameters { get; }
        public ExpressionSyntax ReturnAnnotation { get; }
        public SuiteSyntax Body { get; }

        public FunctionDefSyntax(
            string name,
            bool isAsync,
            IReadOnlyList<DecoratorSyntax> decorators,
            IReadOnlyList<TypeParameterSyntax> typeParameters,
            IReadOnlyList<ParameterSyntax> parameters,
            ExpressionSyntax returnAnnotation,
            SuiteSyntax body,
            SourceSpan span) : base(span)
        {
            Name = name ?? string.Empty;
            IsAsync = isAsync;
            Decorators = decorators ?? Array.Empty<DecoratorSyntax>();
            TypeParameters = typeParameters ?? Array.Empty<TypeParameterSyntax>();
            Parameters = parameters ?? Array.Empty<ParameterSyntax>();
            ReturnAnnotation = returnAnnotation;
            Body = body;
            AdoptChildren();
        }

        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (DecoratorSyntax d in Decorators) list.Add(d);
                foreach (TypeParameterSyntax tp in TypeParameters) list.Add(tp);
                foreach (ParameterSyntax p in Parameters) list.Add(p);
                if (ReturnAnnotation != null) list.Add(ReturnAnnotation);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class ImportStatement : StatementSyntax
    {
        public sealed class ImportedName
        {
            public readonly DottedName Name;
            public readonly string Alias;
            public ImportedName(DottedName name, string alias) { Name = name; Alias = alias; }
        }
        public IReadOnlyList<ImportedName> Names { get; }
        public ImportStatement(IReadOnlyList<ImportedName> names, SourceSpan span) : base(span)
        {
            Names = names ?? Array.Empty<ImportedName>();
        }
    }

    public sealed class FromImportStatement : StatementSyntax
    {
        public int RelativeDots { get; }
        public DottedName Module { get; } // may be null for 'from . import foo'
        public IReadOnlyList<KeyValuePair<string, string>> Names { get; } // name, alias (alias may be null)
        public bool IsStar { get; }

        public FromImportStatement(int relativeDots, DottedName module,
            IReadOnlyList<KeyValuePair<string, string>> names, bool isStar, SourceSpan span) : base(span)
        {
            RelativeDots = relativeDots;
            Module = module;
            Names = names ?? Array.Empty<KeyValuePair<string, string>>();
            IsStar = isStar;
        }
    }

    public sealed class AssignmentStatement : StatementSyntax
    {
        public IReadOnlyList<ExpressionSyntax> Targets { get; }
        public ExpressionSyntax Value { get; }
        public ExpressionSyntax Annotation { get; }
        public AssignmentStatement(IReadOnlyList<ExpressionSyntax> targets, ExpressionSyntax value,
            ExpressionSyntax annotation, SourceSpan span) : base(span)
        {
            Targets = targets ?? Array.Empty<ExpressionSyntax>();
            Value = value;
            Annotation = annotation;
            AdoptChildren();
        }

        // Without this override the base returns NO children, so the targets, the annotation, and the
        // assigned value (RHS) are invisible to every tree walk (Descendants / WalkAll / AdoptChildren).
        // That made names used only in an assignment — e.g. `tk` in `root = tk.Tk()` — vanish from lint
        // traversal, producing false "unused import" warnings. Every sibling assignment node
        // (Augmented/Del/ExpressionStatement) overrides Children; this one was the lone omission.
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>(Targets);
                if (Annotation != null) list.Add(Annotation);
                if (Value != null) list.Add(Value);
                return list;
            }
        }
    }

    public sealed class ExpressionStatement : StatementSyntax
    {
        public ExpressionSyntax Expression { get; }
        public ExpressionStatement(ExpressionSyntax expression, SourceSpan span) : base(span)
        {
            Expression = expression;
            AdoptChildren();
        }

        public override IReadOnlyList<SyntaxNode> Children =>
            Expression != null ? new SyntaxNode[] { Expression } : Array.Empty<SyntaxNode>();
    }

    public sealed class PassStatement : StatementSyntax
    {
        public PassStatement(SourceSpan span) : base(span) { }
    }

    public sealed class BreakStatement : StatementSyntax { public BreakStatement(SourceSpan span) : base(span) { } }
    public sealed class ContinueStatement : StatementSyntax { public ContinueStatement(SourceSpan span) : base(span) { } }

    public sealed class ReturnStatement : StatementSyntax
    {
        public ExpressionSyntax Value { get; }
        public ReturnStatement(ExpressionSyntax value, SourceSpan span) : base(span) { Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    public sealed class RaiseStatement : StatementSyntax
    {
        public ExpressionSyntax Exception { get; }
        public ExpressionSyntax Cause { get; }
        public RaiseStatement(ExpressionSyntax exception, ExpressionSyntax cause, SourceSpan span) : base(span)
        { Exception = exception; Cause = cause; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Exception != null) list.Add(Exception);
                if (Cause != null) list.Add(Cause);
                return list;
            }
        }
    }

    public sealed class AssertStatement : StatementSyntax
    {
        public ExpressionSyntax Test { get; }
        public ExpressionSyntax Message { get; }
        public AssertStatement(ExpressionSyntax test, ExpressionSyntax message, SourceSpan span) : base(span)
        { Test = test; Message = message; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Test != null) list.Add(Test);
                if (Message != null) list.Add(Message);
                return list;
            }
        }
    }

    public sealed class DelStatement : StatementSyntax
    {
        public IReadOnlyList<ExpressionSyntax> Targets { get; }
        public DelStatement(IReadOnlyList<ExpressionSyntax> targets, SourceSpan span) : base(span)
        { Targets = targets ?? Array.Empty<ExpressionSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Targets;
    }

    public sealed class GlobalStatement : StatementSyntax
    {
        public IReadOnlyList<string> Names { get; }
        public GlobalStatement(IReadOnlyList<string> names, SourceSpan span) : base(span)
        { Names = names ?? Array.Empty<string>(); }
    }

    public sealed class NonlocalStatement : StatementSyntax
    {
        public IReadOnlyList<string> Names { get; }
        public NonlocalStatement(IReadOnlyList<string> names, SourceSpan span) : base(span)
        { Names = names ?? Array.Empty<string>(); }
    }

    public sealed class YieldStatement : StatementSyntax
    {
        public YieldExpression Expression { get; }
        public YieldStatement(YieldExpression expression, SourceSpan span) : base(span)
        { Expression = expression; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Expression != null ? new SyntaxNode[] { Expression } : Array.Empty<SyntaxNode>();
    }

    public sealed class AugmentedAssignmentStatement : StatementSyntax
    {
        public ExpressionSyntax Target { get; }
        public BinaryOperator Operator { get; }
        public ExpressionSyntax Value { get; }
        public AugmentedAssignmentStatement(ExpressionSyntax target, BinaryOperator op, ExpressionSyntax value, SourceSpan span)
            : base(span)
        { Target = target; Operator = op; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Target, Value };
    }

    public sealed class IfStatement : StatementSyntax
    {
        public ExpressionSyntax Test { get; }
        public SuiteSyntax Body { get; }
        public IReadOnlyList<IfStatement> Elifs { get; }
        public SuiteSyntax Else { get; }
        public IfStatement(ExpressionSyntax test, SuiteSyntax body, IReadOnlyList<IfStatement> elifs, SuiteSyntax elseBlock, SourceSpan span)
            : base(span)
        {
            Test = test; Body = body;
            Elifs = elifs ?? Array.Empty<IfStatement>();
            Else = elseBlock;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Test, Body };
                foreach (IfStatement e in Elifs) list.Add(e);
                if (Else != null) list.Add(Else);
                return list;
            }
        }
    }

    public sealed class WhileStatement : StatementSyntax
    {
        public ExpressionSyntax Test { get; }
        public SuiteSyntax Body { get; }
        public SuiteSyntax Else { get; }
        public WhileStatement(ExpressionSyntax test, SuiteSyntax body, SuiteSyntax elseBlock, SourceSpan span) : base(span)
        { Test = test; Body = body; Else = elseBlock; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Test, Body };
                if (Else != null) list.Add(Else);
                return list;
            }
        }
    }

    public sealed class ForStatement : StatementSyntax
    {
        public bool IsAsync { get; }
        public ExpressionSyntax Target { get; }
        public ExpressionSyntax Iter { get; }
        public SuiteSyntax Body { get; }
        public SuiteSyntax Else { get; }
        public ForStatement(bool isAsync, ExpressionSyntax target, ExpressionSyntax iter, SuiteSyntax body, SuiteSyntax elseBlock, SourceSpan span)
            : base(span)
        {
            IsAsync = isAsync; Target = target; Iter = iter; Body = body; Else = elseBlock;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Target, Iter, Body };
                if (Else != null) list.Add(Else);
                return list;
            }
        }
    }

    public sealed class ExceptHandler : SyntaxNode
    {
        public bool IsStar { get; }
        public ExpressionSyntax ExceptionType { get; }
        public string Name { get; }
        public SuiteSyntax Body { get; }
        public ExceptHandler(bool isStar, ExpressionSyntax exceptionType, string name, SuiteSyntax body, SourceSpan span)
            : base(span)
        {
            IsStar = isStar; ExceptionType = exceptionType; Name = name; Body = body;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (ExceptionType != null) list.Add(ExceptionType);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class TryStatement : StatementSyntax
    {
        public SuiteSyntax Body { get; }
        public IReadOnlyList<ExceptHandler> Handlers { get; }
        public SuiteSyntax Else { get; }
        public SuiteSyntax Finally { get; }
        public TryStatement(SuiteSyntax body, IReadOnlyList<ExceptHandler> handlers, SuiteSyntax elseBlock, SuiteSyntax finallyBlock, SourceSpan span)
            : base(span)
        {
            Body = body;
            Handlers = handlers ?? Array.Empty<ExceptHandler>();
            Else = elseBlock; Finally = finallyBlock;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode> { Body };
                foreach (ExceptHandler h in Handlers) list.Add(h);
                if (Else != null) list.Add(Else);
                if (Finally != null) list.Add(Finally);
                return list;
            }
        }
    }

    public sealed class WithItem : SyntaxNode
    {
        public ExpressionSyntax ContextExpression { get; }
        public ExpressionSyntax OptionalVars { get; }
        public WithItem(ExpressionSyntax contextExpression, ExpressionSyntax optionalVars, SourceSpan span) : base(span)
        { ContextExpression = contextExpression; OptionalVars = optionalVars; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (ContextExpression != null) list.Add(ContextExpression);
                if (OptionalVars != null) list.Add(OptionalVars);
                return list;
            }
        }
    }

    public sealed class WithStatement : StatementSyntax
    {
        public bool IsAsync { get; }
        public IReadOnlyList<WithItem> Items { get; }
        public SuiteSyntax Body { get; }
        public WithStatement(bool isAsync, IReadOnlyList<WithItem> items, SuiteSyntax body, SourceSpan span) : base(span)
        {
            IsAsync = isAsync;
            Items = items ?? Array.Empty<WithItem>();
            Body = body;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (WithItem i in Items) list.Add(i);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class CaseBlock : SyntaxNode
    {
        public PatternSyntax Pattern { get; }
        public ExpressionSyntax Guard { get; }
        public SuiteSyntax Body { get; }
        public CaseBlock(PatternSyntax pattern, ExpressionSyntax guard, SuiteSyntax body, SourceSpan span) : base(span)
        { Pattern = pattern; Guard = guard; Body = body; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Pattern != null) list.Add(Pattern);
                if (Guard != null) list.Add(Guard);
                if (Body != null) list.Add(Body);
                return list;
            }
        }
    }

    public sealed class MatchStatement : StatementSyntax
    {
        public ExpressionSyntax Subject { get; }
        public IReadOnlyList<CaseBlock> Cases { get; }
        public MatchStatement(ExpressionSyntax subject, IReadOnlyList<CaseBlock> cases, SourceSpan span) : base(span)
        {
            Subject = subject;
            Cases = cases ?? Array.Empty<CaseBlock>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                if (Subject != null) list.Add(Subject);
                foreach (CaseBlock c in Cases) list.Add(c);
                return list;
            }
        }
    }
}
