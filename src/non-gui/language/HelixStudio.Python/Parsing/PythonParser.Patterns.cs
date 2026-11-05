using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Parsing
{
    /// <summary>match/case pattern grammar.</summary>
    public sealed partial class PythonParser
    {
        private PatternSyntax ParsePatterns()
        {
            SourcePosition start = Current.Span.Start;
            // A sequence pattern may start with a star capture: `case *x,:` / `case (*x,):`.
            PatternSyntax first = ParseMaybeStarPattern();
            if (Current.Kind != TokenKind.Comma) return first;
            // Open sequence pattern: p1, p2, ...
            List<PatternSyntax> items = new List<PatternSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.Colon || Current.Kind == TokenKind.KwIf
                    || Current.Kind == TokenKind.RightParen || Current.Kind == TokenKind.RightBracket) break;
                items.Add(ParseMaybeStarPattern());
            }
            return new SequencePattern(items, new SourceSpan(start, Previous.Span.End));
        }

        private PatternSyntax ParsePattern()
        {
            PatternSyntax p = ParseOrPattern();
            if (Current.Kind == TokenKind.KwAs)
            {
                Advance();
                string name = ExpectName();
                return new AsPattern(p, name, new SourceSpan(p.Span.Start, Previous.Span.End));
            }
            return p;
        }

        private PatternSyntax ParseOrPattern()
        {
            SourcePosition start = Current.Span.Start;
            PatternSyntax first = ParseClosedPattern();
            if (Current.Kind != TokenKind.Pipe) return first;
            List<PatternSyntax> alts = new List<PatternSyntax> { first };
            while (Current.Kind == TokenKind.Pipe)
            {
                Advance();
                alts.Add(ParseClosedPattern());
            }
            return new OrPattern(alts, new SourceSpan(start, Previous.Span.End));
        }

        // A literal pattern may be a complex number — `case 0 + 0j:` — which the grammar
        // spells as signed_real_number ('+'|'-') imaginary_number.
        private ExpressionSyntax TakeComplexTail(ExpressionSyntax real)
        {
            if ((Current.Kind != TokenKind.Plus && Current.Kind != TokenKind.Minus)
                || Peek(1).Kind != TokenKind.Number) return real;
            BinaryOperator op = Current.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            Advance();
            Token imag = Current;
            Advance();
            ExpressionSyntax imagExpr = new LiteralExpression(imag, imag.Span);
            return new BinaryExpression(op, real, imagExpr, new SourceSpan(real.Span.Start, imag.Span.End));
        }

        private PatternSyntax ParseClosedPattern()
        {
            SourcePosition start = Current.Span.Start;
            TokenKind k = Current.Kind;

            // Wildcard: _
            if (k == TokenKind.Name && Current.Text == "_")
            {
                Advance();
                return new WildcardPattern(new SourceSpan(start, Previous.Span.End));
            }

            // Literal number / string / True / False / None
            if (k == TokenKind.Number || k == TokenKind.String || k == TokenKind.KwTrue
                || k == TokenKind.KwFalse || k == TokenKind.KwNone)
            {
                ExpressionSyntax lit = TakeComplexTail(ParseAtom());
                return new LiteralPattern(lit, new SourceSpan(start, Previous.Span.End));
            }

            // Negative/positive literal: -1, +2.5
            if ((k == TokenKind.Minus || k == TokenKind.Plus) && Peek(1).Kind == TokenKind.Number)
            {
                ExpressionSyntax lit = TakeComplexTail(ParseFactor());
                return new LiteralPattern(lit, new SourceSpan(start, Previous.Span.End));
            }

            // Group pattern: ( pattern )
            if (k == TokenKind.LeftParen)
            {
                Advance();
                if (Current.Kind == TokenKind.RightParen)
                {
                    Advance();
                    return new SequencePattern(new List<PatternSyntax>(), new SourceSpan(start, Previous.Span.End));
                }
                PatternSyntax inner = ParsePatterns();
                Expect(TokenKind.RightParen);
                return new GroupPattern(inner, new SourceSpan(start, Previous.Span.End));
            }

            // Sequence pattern: [ ... ]
            if (k == TokenKind.LeftBracket)
            {
                Advance();
                List<PatternSyntax> items = new List<PatternSyntax>();
                while (Current.Kind != TokenKind.RightBracket && Current.Kind != TokenKind.EndOfFile)
                {
                    items.Add(ParseMaybeStarPattern());
                    if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                    break;
                }
                Expect(TokenKind.RightBracket);
                return new SequencePattern(items, new SourceSpan(start, Previous.Span.End));
            }

            // Mapping pattern: { ... }
            if (k == TokenKind.LeftBrace)
            {
                Advance();
                List<KeyValuePatternEntry> entries = new List<KeyValuePatternEntry>();
                string doubleStarName = null;
                while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.EndOfFile)
                {
                    if (Current.Kind == TokenKind.DoubleStar)
                    {
                        Advance();
                        doubleStarName = ExpectName();
                        if (Current.Kind == TokenKind.Comma) Advance();
                        break;
                    }
                    SourcePosition ks = Current.Span.Start;
                    ExpressionSyntax key = ParseSingleExpression();
                    Expect(TokenKind.Colon);
                    PatternSyntax value = ParsePattern();
                    entries.Add(new KeyValuePatternEntry(key, value, new SourceSpan(ks, Previous.Span.End)));
                    if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                    break;
                }
                Expect(TokenKind.RightBrace);
                return new MappingPattern(entries, doubleStarName, new SourceSpan(start, Previous.Span.End));
            }

            // Star in sequence: *name / *_
            if (k == TokenKind.Star)
            {
                Advance();
                string name = null;
                if (Current.Kind == TokenKind.Name)
                {
                    name = Current.Text == "_" ? null : Current.Text;
                    Advance();
                }
                return new StarPattern(name, new SourceSpan(start, Previous.Span.End));
            }

            // Name-based: capture / value / class-pattern
            if (k == TokenKind.Name)
            {
                // Walk a dotted name.
                List<string> dotted = new List<string> { Current.Text };
                Advance();
                while (Current.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Name)
                {
                    Advance();
                    dotted.Add(Current.Text);
                    Advance();
                }

                // Class pattern: Name ( args )
                if (Current.Kind == TokenKind.LeftParen)
                {
                    Advance();
                    List<PatternSyntax> pos = new List<PatternSyntax>();
                    List<KeywordPatternEntry> kw = new List<KeywordPatternEntry>();
                    while (Current.Kind != TokenKind.RightParen && Current.Kind != TokenKind.EndOfFile)
                    {
                        if (Current.Kind == TokenKind.Name && Peek(1).Kind == TokenKind.Assign)
                        {
                            string kwName = Current.Text; Advance(); Advance();
                            PatternSyntax val = ParsePattern();
                            kw.Add(new KeywordPatternEntry(kwName, val, new SourceSpan(Current.Span.Start, Previous.Span.End)));
                        }
                        else
                        {
                            pos.Add(ParsePattern());
                        }
                        if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                        break;
                    }
                    Expect(TokenKind.RightParen);
                    return new ClassPattern(dotted, pos, kw, new SourceSpan(start, Previous.Span.End));
                }

                if (dotted.Count == 1)
                {
                    // Simple capture pattern.
                    return new CapturePattern(dotted[0], new SourceSpan(start, Previous.Span.End));
                }
                // Dotted value pattern.
                return new ValuePattern(dotted, new SourceSpan(start, Previous.Span.End));
            }

            // Fallback: treat as wildcard to keep parser alive.
            Advance();
            return new WildcardPattern(new SourceSpan(start, Previous.Span.End));
        }

        private PatternSyntax ParseMaybeStarPattern()
        {
            if (Current.Kind == TokenKind.Star) return ParseClosedPattern();
            return ParsePattern();
        }
    }
}
