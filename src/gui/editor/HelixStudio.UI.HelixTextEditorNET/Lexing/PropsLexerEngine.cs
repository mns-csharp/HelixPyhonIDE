using System;

namespace HelixTextEditorNET.Lexing
{
    // Properties/INI highlighter emitting the SCE_PROPS_* numbering
    // (SCLEX_PROPERTIES = 9). Honors "lexer.props.allow.initial.spaces".
    internal sealed class PropsLexerEngine : LexerEngine
    {
        private const byte StyleDefault = 0;    // SCE_PROPS_DEFAULT
        private const byte StyleComment = 1;    // SCE_PROPS_COMMENT
        private const byte StyleSection = 2;    // SCE_PROPS_SECTION
        private const byte StyleAssignment = 3; // SCE_PROPS_ASSIGNMENT
        private const byte StyleDefVal = 4;     // SCE_PROPS_DEFVAL
        private const byte StyleKey = 5;        // SCE_PROPS_KEY

        public override void StyleRange(Core.DocumentModel doc, int startPos, int endPos)
        {
            bool allowInitialSpaces = PropertyOn("lexer.props.allow.initial.spaces", true);
            int lines = doc.LinesTotal;
            for (int l = 0; l < lines; l++)
            {
                int s = doc.LineStart(l);
                int e = doc.LineEnd(l);
                int nextStart = (l + 1 < lines) ? doc.LineStart(l + 1) : doc.Length;
                // Style the EOL bytes as default.
                if (nextStart > e) doc.SetStyleFor(e, nextStart - e, StyleDefault);
                StyleLine(doc, s, e, allowInitialSpaces);
            }
        }

        private static void StyleLine(Core.DocumentModel doc, int s, int e, bool allowInitialSpaces)
        {
            int p = s;
            if (allowInitialSpaces)
            {
                while (p < e && (doc.CharAt(p) == ' ' || doc.CharAt(p) == '\t')) p++;
                if (p > s) doc.SetStyleFor(s, p - s, StyleDefault);
            }
            if (p >= e) return;
            char c = doc.CharAt(p);
            if (c == '#' || c == ';' || c == '!')
            {
                doc.SetStyleFor(p, e - p, StyleComment);
                return;
            }
            if (c == '[')
            {
                doc.SetStyleFor(p, e - p, StyleSection);
                return;
            }
            if (c == '@')
            {
                // @keyword=default value
                int eq = FindChar(doc, p, e, '=');
                if (eq >= 0)
                {
                    doc.SetStyleFor(p, eq - p, StyleDefVal);
                    doc.SetStyleFor(eq, 1, StyleAssignment);
                    doc.SetStyleFor(eq + 1, e - eq - 1, StyleDefault);
                }
                else
                {
                    doc.SetStyleFor(p, e - p, StyleDefVal);
                }
                return;
            }
            {
                int eq = FindChar(doc, p, e, '=');
                if (eq >= 0)
                {
                    doc.SetStyleFor(p, eq - p, StyleKey);
                    doc.SetStyleFor(eq, 1, StyleAssignment);
                    doc.SetStyleFor(eq + 1, e - eq - 1, StyleDefault);
                }
                else
                {
                    doc.SetStyleFor(p, e - p, StyleDefault);
                }
            }
        }

        private static int FindChar(Core.DocumentModel doc, int from, int to, char c)
        {
            for (int p = from; p < to; p++)
                if (doc.CharAt(p) == c) return p;
            return -1;
        }
    }
}
