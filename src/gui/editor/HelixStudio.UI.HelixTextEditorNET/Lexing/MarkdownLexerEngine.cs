using System;

namespace HelixTextEditorNET.Lexing
{
    // Markdown highlighter emitting the SCE_MARKDOWN_* numbering (SCLEX_MARKDOWN = 98).
    //
    // This styles the SOURCE view. The rendered preview is a different thing entirely
    // (HelixPythonNamespace.Editor.Markdown.MarkdownViewControl over the HelixPythonNamespace.Markdown parser); this
    // exists so that a user who switches to Source sees Markdown, not the wall of one colour a
    // NullLexerEngine produces — and, more to the point, not the Python keyword colouring that
    // .md files used to get by falling through to Lexer.Python. A README talking about `is`, `not`
    // and `class` came out looking like source code.
    //
    // Line-oriented and single-pass, matching the other engines in this folder. It deliberately
    // does NOT try to be the block parser: a lexer that has to agree with the renderer about lazy
    // continuation and nested containers is a second implementation of the same grammar. Fenced
    // code is tracked (it must be, or every `#` inside a shell example becomes a heading) and
    // everything else is decided from the line in front of it.
    internal sealed class MarkdownLexerEngine : LexerEngine
    {
        private const byte StyleDefault = 0;      // SCE_MARKDOWN_DEFAULT
        private const byte StyleLineBegin = 1;    // SCE_MARKDOWN_LINE_BEGIN
        private const byte StyleStrong1 = 2;      // SCE_MARKDOWN_STRONG1  (**)
        private const byte StyleEm1 = 4;          // SCE_MARKDOWN_EM1      (*)
        private const byte StyleHeader1 = 6;      // SCE_MARKDOWN_HEADER1 … +5
        private const byte StylePrechar = 12;     // SCE_MARKDOWN_PRECHAR
        private const byte StyleUListItem = 13;   // SCE_MARKDOWN_ULIST_ITEM
        private const byte StyleOListItem = 14;   // SCE_MARKDOWN_OLIST_ITEM
        private const byte StyleBlockQuote = 15;  // SCE_MARKDOWN_BLOCKQUOTE
        private const byte StyleHRule = 17;       // SCE_MARKDOWN_HRULE
        private const byte StyleLink = 18;        // SCE_MARKDOWN_LINK
        private const byte StyleCode = 19;        // SCE_MARKDOWN_CODE     (`…`)
        private const byte StyleCodeBk = 21;      // SCE_MARKDOWN_CODEBK   (fenced / indented)

        public override void StyleRange(Core.DocumentModel doc, int startPos, int endPos)
        {
            int lines = doc.LinesTotal;
            bool inFence = false;
            char fenceChar = '\0';
            int fenceLength = 0;

            for (int l = 0; l < lines; l++)
            {
                int s = doc.LineStart(l);
                int e = doc.LineEnd(l);
                int nextStart = (l + 1 < lines) ? doc.LineStart(l + 1) : doc.Length;
                if (nextStart > e) doc.SetStyleFor(e, nextStart - e, StyleDefault);
                if (e <= s) { doc.SetStyleFor(s, 0, StyleDefault); continue; }

                int p = s;
                int indent = 0;
                while (p < e && (doc.CharAt(p) == ' ' || doc.CharAt(p) == '\t'))
                {
                    indent += doc.CharAt(p) == '\t' ? 4 - (indent % 4) : 1;
                    p++;
                }

                // A fence opener/closer, and everything between, is code.
                char c0 = p < e ? doc.CharAt(p) : '\0';
                if ((c0 == '`' || c0 == '~') && indent < 4)
                {
                    int run = 0;
                    while (p + run < e && doc.CharAt(p + run) == c0) run++;
                    if (run >= 3)
                    {
                        if (!inFence) { inFence = true; fenceChar = c0; fenceLength = run; }
                        else if (c0 == fenceChar && run >= fenceLength) { inFence = false; }
                        doc.SetStyleFor(s, e - s, StyleCodeBk);
                        continue;
                    }
                }
                if (inFence) { doc.SetStyleFor(s, e - s, StyleCodeBk); continue; }

                // Indented code.
                if (indent >= 4) { doc.SetStyleFor(s, e - s, StyleCodeBk); continue; }

                if (p > s) doc.SetStyleFor(s, p - s, StyleLineBegin);

                if (IsHorizontalRule(doc, p, e)) { doc.SetStyleFor(p, e - p, StyleHRule); continue; }

                if (c0 == '#')
                {
                    int level = 0;
                    while (p + level < e && doc.CharAt(p + level) == '#') level++;
                    if (level <= 6 && (p + level >= e || doc.CharAt(p + level) == ' '))
                    {
                        byte style = (byte)(StyleHeader1 + Math.Min(level, 6) - 1);
                        doc.SetStyleFor(p, e - p, style);
                        continue;
                    }
                }

                if (c0 == '>') { doc.SetStyleFor(p, e - p, StyleBlockQuote); continue; }

                // A list marker is styled on its own; the item text after it is styled inline, so a
                // link or a code span inside a bullet still gets its colour.
                int contentStart = p;
                if ((c0 == '-' || c0 == '*' || c0 == '+') && p + 1 < e && doc.CharAt(p + 1) == ' ')
                {
                    doc.SetStyleFor(p, 2, StyleUListItem);
                    contentStart = p + 2;
                }
                else
                {
                    int digits = 0;
                    while (p + digits < e && doc.CharAt(p + digits) >= '0' && doc.CharAt(p + digits) <= '9') digits++;
                    if (digits > 0 && p + digits < e
                        && (doc.CharAt(p + digits) == '.' || doc.CharAt(p + digits) == ')'))
                    {
                        int width = digits + 1;
                        if (p + width < e && doc.CharAt(p + width) == ' ') width++;
                        doc.SetStyleFor(p, width, StyleOListItem);
                        contentStart = p + width;
                    }
                }

                StyleInline(doc, contentStart, e);
            }
        }

        // Inline pass over one line. Code spans win over emphasis, for the same reason they do in
        // the parser: `**` inside a backtick run is literal, and colouring it as strong is how a
        // document full of shell and regex examples ends up striped.
        private static void StyleInline(Core.DocumentModel doc, int from, int to)
        {
            int p = from;
            while (p < to)
            {
                char c = doc.CharAt(p);

                if (c == '\\' && p + 1 < to) { doc.SetStyleFor(p, 2, StyleDefault); p += 2; continue; }

                if (c == '`')
                {
                    int run = 0;
                    while (p + run < to && doc.CharAt(p + run) == '`') run++;
                    int close = FindRun(doc, p + run, to, '`', run);
                    if (close >= 0) { doc.SetStyleFor(p, close + run - p, StyleCode); p = close + run; continue; }
                }

                if (c == '[' || c == '!')
                {
                    int end = ScanLink(doc, p, to);
                    if (end > p) { doc.SetStyleFor(p, end - p, StyleLink); p = end; continue; }
                }

                if (c == '*' || c == '_')
                {
                    int run = 0;
                    while (p + run < to && doc.CharAt(p + run) == c) run++;
                    int width = run >= 2 ? 2 : 1;
                    int close = FindRun(doc, p + width, to, c, width);
                    if (close > p + width)
                    {
                        doc.SetStyleFor(p, close + width - p, width >= 2 ? StyleStrong1 : StyleEm1);
                        p = close + width;
                        continue;
                    }
                }

                doc.SetStyleFor(p, 1, c == '|' ? StylePrechar : StyleDefault);
                p++;
            }
        }

        private static int ScanLink(Core.DocumentModel doc, int p, int to)
        {
            int i = p;
            if (doc.CharAt(i) == '!') i++;
            if (i >= to || doc.CharAt(i) != '[') return p;
            int close = -1;
            for (int j = i + 1; j < to; j++) { if (doc.CharAt(j) == ']') { close = j; break; } }
            if (close < 0) return p;
            if (close + 1 < to && (doc.CharAt(close + 1) == '(' || doc.CharAt(close + 1) == '['))
            {
                char open = doc.CharAt(close + 1);
                char shut = open == '(' ? ')' : ']';
                for (int j = close + 2; j < to; j++) if (doc.CharAt(j) == shut) return j + 1;
                return p;
            }
            return close + 1;
        }

        private static int FindRun(Core.DocumentModel doc, int from, int to, char c, int length)
        {
            for (int p = from; p + length <= to; p++)
            {
                bool match = true;
                for (int k = 0; k < length; k++) if (doc.CharAt(p + k) != c) { match = false; break; }
                if (match) return p;
            }
            return -1;
        }

        private static bool IsHorizontalRule(Core.DocumentModel doc, int from, int to)
        {
            if (to - from < 3) return false;
            char c = doc.CharAt(from);
            if (c != '-' && c != '*' && c != '_') return false;
            int count = 0;
            for (int p = from; p < to; p++)
            {
                char ch = doc.CharAt(p);
                if (ch == c) count++;
                else if (ch != ' ' && ch != '\t') return false;
            }
            return count >= 3;
        }
    }
}
