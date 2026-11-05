using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler.Ast
{
    // CPython pattern_ty hierarchy (Python 3.10+).

    public sealed class MatchValue : Pattern
    {
        public Expr Value { get; }
        public MatchValue(Expr value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class MatchSingleton : Pattern
    {
        public object Value { get; }       // None, True, False
        public MatchSingleton(object value, SourceSpan span) : base(span) { Value = value; }
    }

    public sealed class MatchSequence : Pattern
    {
        public IReadOnlyList<Pattern> Patterns { get; }
        public MatchSequence(IReadOnlyList<Pattern> patterns, SourceSpan span) : base(span)
        {
            Patterns = patterns ?? Array.Empty<Pattern>();
        }
    }

    public sealed class MatchMapping : Pattern
    {
        public IReadOnlyList<Expr> Keys { get; }
        public IReadOnlyList<Pattern> Patterns { get; }
        public string Rest { get; }       // name for **rest, nullable
        public MatchMapping(IReadOnlyList<Expr> keys, IReadOnlyList<Pattern> patterns, string rest, SourceSpan span) : base(span)
        {
            Keys = keys ?? Array.Empty<Expr>();
            Patterns = patterns ?? Array.Empty<Pattern>();
            Rest = rest;
        }
    }

    public sealed class MatchClass : Pattern
    {
        public Expr Cls { get; }
        public IReadOnlyList<Pattern> Patterns { get; }
        public IReadOnlyList<string> KwdAttrs { get; }
        public IReadOnlyList<Pattern> KwdPatterns { get; }
        public MatchClass(Expr cls, IReadOnlyList<Pattern> patterns,
            IReadOnlyList<string> kwdAttrs, IReadOnlyList<Pattern> kwdPatterns, SourceSpan span) : base(span)
        {
            Cls = cls;
            Patterns = patterns ?? Array.Empty<Pattern>();
            KwdAttrs = kwdAttrs ?? Array.Empty<string>();
            KwdPatterns = kwdPatterns ?? Array.Empty<Pattern>();
        }
    }

    public sealed class MatchStar : Pattern
    {
        public string Name { get; }    // nullable for *_
        public MatchStar(string name, SourceSpan span) : base(span) { Name = name; }
    }

    public sealed class MatchAs : Pattern
    {
        public Pattern PatternOpt { get; }    // nullable
        public string Name { get; }           // nullable for bare '_'
        public MatchAs(Pattern pattern, string name, SourceSpan span) : base(span)
        {
            PatternOpt = pattern; Name = name;
        }
    }

    public sealed class MatchOr : Pattern
    {
        public IReadOnlyList<Pattern> Patterns { get; }
        public MatchOr(IReadOnlyList<Pattern> patterns, SourceSpan span) : base(span)
        {
            Patterns = patterns ?? Array.Empty<Pattern>();
        }
    }
}
