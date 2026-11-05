using System;

namespace HelixPythonNamespace.Python.Text
{
    public struct SourcePosition : IEquatable<SourcePosition>
    {
        public static readonly SourcePosition Zero = new SourcePosition(0, 1, 1);

        public readonly int Offset;
        public readonly int Line;
        public readonly int Column;

        public SourcePosition(int offset, int line, int column)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (line < 1) throw new ArgumentOutOfRangeException(nameof(line));
            if (column < 1) throw new ArgumentOutOfRangeException(nameof(column));
            Offset = offset;
            Line = line;
            Column = column;
        }

        public bool Equals(SourcePosition other)
        {
            return Offset == other.Offset && Line == other.Line && Column == other.Column;
        }

        public override bool Equals(object obj)
        {
            return obj is SourcePosition p && Equals(p);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Offset;
                h = (h * 397) ^ Line;
                h = (h * 397) ^ Column;
                return h;
            }
        }

        public override string ToString()
        {
            return "(" + Line + ":" + Column + "@" + Offset + ")";
        }

        public static bool operator ==(SourcePosition a, SourcePosition b) { return a.Equals(b); }
        public static bool operator !=(SourcePosition a, SourcePosition b) { return !a.Equals(b); }
    }

    public struct SourceSpan : IEquatable<SourceSpan>
    {
        public static readonly SourceSpan Empty = new SourceSpan(SourcePosition.Zero, SourcePosition.Zero);

        public readonly SourcePosition Start;
        public readonly SourcePosition End;

        public SourceSpan(SourcePosition start, SourcePosition end)
        {
            // During error recovery the parser can derive an `end` that precedes
            // `start` (e.g. a synthetic NEWLINE/DEDENT/EOF token positioned at or
            // before the node's start). Degenerate spans are legitimate there, so
            // clamp to a zero-width span at `start` rather than throwing — a hard
            // throw would crash any consumer (CLI, linter) run over malformed code.
            if (end.Offset < start.Offset) end = start;
            Start = start;
            End = end;
        }

        public int Length { get { return End.Offset - Start.Offset; } }

        public bool Equals(SourceSpan other)
        {
            return Start.Equals(other.Start) && End.Equals(other.End);
        }

        public override bool Equals(object obj)
        {
            return obj is SourceSpan s && Equals(s);
        }

        public override int GetHashCode()
        {
            unchecked { return (Start.GetHashCode() * 397) ^ End.GetHashCode(); }
        }

        public override string ToString()
        {
            return Start + ".." + End;
        }
    }
}
