using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    public sealed class ModuleSyntax : SyntaxNode
    {
        public string FileName { get; }
        public IReadOnlyList<StatementSyntax> Statements { get; }
        public IReadOnlyList<Token> LeadingTrivia { get; }
        public IReadOnlyList<Token> TrailingTrivia { get; }

        public ModuleSyntax(
            string fileName,
            SourceSpan span,
            IReadOnlyList<StatementSyntax> statements,
            IReadOnlyList<Token> leadingTrivia,
            IReadOnlyList<Token> trailingTrivia)
            : base(span)
        {
            FileName = fileName ?? string.Empty;
            Statements = statements ?? Array.Empty<StatementSyntax>();
            LeadingTrivia = leadingTrivia ?? Array.Empty<Token>();
            TrailingTrivia = trailingTrivia ?? Array.Empty<Token>();
            AdoptChildren();
        }

        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Statements;
    }
}
