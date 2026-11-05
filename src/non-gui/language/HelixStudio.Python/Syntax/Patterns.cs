using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    // Literal: 42, "x", True, None, etc.
    public sealed class LiteralPattern : PatternSyntax
    {
        public ExpressionSyntax Value { get; }
        public LiteralPattern(ExpressionSyntax value, SourceSpan span) : base(span)
        { Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    // NAME (bare identifier, not qualified)
    public sealed class CapturePattern : PatternSyntax
    {
        public string Name { get; }
        public CapturePattern(string name, SourceSpan span) : base(span) { Name = name ?? string.Empty; }
    }

    // _ (wildcard)
    public sealed class WildcardPattern : PatternSyntax
    {
        public WildcardPattern(SourceSpan span) : base(span) { }
    }

    // X.Y (dotted value check)
    public sealed class ValuePattern : PatternSyntax
    {
        public IReadOnlyList<string> DottedName { get; }
        public ValuePattern(IReadOnlyList<string> dotted, SourceSpan span) : base(span)
        { DottedName = dotted ?? Array.Empty<string>(); }
    }

    // (pattern) — disambiguation group
    public sealed class GroupPattern : PatternSyntax
    {
        public PatternSyntax Inner { get; }
        public GroupPattern(PatternSyntax inner, SourceSpan span) : base(span)
        { Inner = inner; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Inner != null ? new SyntaxNode[] { Inner } : Array.Empty<SyntaxNode>();
    }

    // [p1, p2, *rest]
    public sealed class SequencePattern : PatternSyntax
    {
        public IReadOnlyList<PatternSyntax> Items { get; }
        public SequencePattern(IReadOnlyList<PatternSyntax> items, SourceSpan span) : base(span)
        { Items = items ?? Array.Empty<PatternSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Items;
    }

    // *name  (used inside sequence patterns)
    public sealed class StarPattern : PatternSyntax
    {
        public string Name { get; } // may be null for *_
        public StarPattern(string name, SourceSpan span) : base(span) { Name = name; }
    }

    // {key: pattern, ...**rest}
    public sealed class MappingPattern : PatternSyntax
    {
        public IReadOnlyList<KeyValuePatternEntry> Entries { get; }
        public string DoubleStarName { get; } // for **rest; nullable
        public MappingPattern(IReadOnlyList<KeyValuePatternEntry> entries, string doubleStarName, SourceSpan span) : base(span)
        {
            Entries = entries ?? Array.Empty<KeyValuePatternEntry>();
            DoubleStarName = doubleStarName;
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Entries;
    }

    public sealed class KeyValuePatternEntry : SyntaxNode
    {
        public ExpressionSyntax Key { get; }
        public PatternSyntax Value { get; }
        public KeyValuePatternEntry(ExpressionSyntax key, PatternSyntax value, SourceSpan span) : base(span)
        { Key = key; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => new SyntaxNode[] { Key, Value };
    }

    // ClassName(pos, name=pat, ...)
    public sealed class ClassPattern : PatternSyntax
    {
        public IReadOnlyList<string> ClassName { get; }
        public IReadOnlyList<PatternSyntax> PositionalPatterns { get; }
        public IReadOnlyList<KeywordPatternEntry> KeywordPatterns { get; }
        public ClassPattern(IReadOnlyList<string> className, IReadOnlyList<PatternSyntax> pos,
            IReadOnlyList<KeywordPatternEntry> kw, SourceSpan span) : base(span)
        {
            ClassName = className ?? Array.Empty<string>();
            PositionalPatterns = pos ?? Array.Empty<PatternSyntax>();
            KeywordPatterns = kw ?? Array.Empty<KeywordPatternEntry>();
            AdoptChildren();
        }
        public override IReadOnlyList<SyntaxNode> Children
        {
            get
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (PatternSyntax p in PositionalPatterns) list.Add(p);
                foreach (KeywordPatternEntry k in KeywordPatterns) list.Add(k);
                return list;
            }
        }
    }

    public sealed class KeywordPatternEntry : SyntaxNode
    {
        public string Name { get; }
        public PatternSyntax Value { get; }
        public KeywordPatternEntry(string name, PatternSyntax value, SourceSpan span) : base(span)
        { Name = name ?? string.Empty; Value = value; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Value != null ? new SyntaxNode[] { Value } : Array.Empty<SyntaxNode>();
    }

    // p1 | p2 | p3
    public sealed class OrPattern : PatternSyntax
    {
        public IReadOnlyList<PatternSyntax> Alternatives { get; }
        public OrPattern(IReadOnlyList<PatternSyntax> alternatives, SourceSpan span) : base(span)
        { Alternatives = alternatives ?? Array.Empty<PatternSyntax>(); AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => (IReadOnlyList<SyntaxNode>)Alternatives;
    }

    // pattern as name
    public sealed class AsPattern : PatternSyntax
    {
        public PatternSyntax Pattern { get; }
        public string Name { get; }
        public AsPattern(PatternSyntax pattern, string name, SourceSpan span) : base(span)
        { Pattern = pattern; Name = name ?? string.Empty; AdoptChildren(); }
        public override IReadOnlyList<SyntaxNode> Children => Pattern != null ? new SyntaxNode[] { Pattern } : Array.Empty<SyntaxNode>();
    }
}
