using System;

namespace HelixTextEditorNET.Lexing
{
    // Shared fold builders the lexer engines delegate to. Port of lexers/fold.cpp.
    internal static class Folding
    {
        public const int FoldLevelBase = 0x400;       // SC_FOLDLEVELBASE
        public const int FoldLevelWhiteFlag = 0x1000;  // SC_FOLDLEVELWHITEFLAG
        public const int FoldLevelHeaderFlag = 0x2000; // SC_FOLDLEVELHEADERFLAG
        public const int FoldLevelNumberMask = 0x0FFF; // SC_FOLDLEVELNUMBERMASK

        // Brace-depth folding using the already-computed style bytes (skips braces in
        // comments/strings); used by the C-family and CMake-style languages.
        // `isStringLike` reports style bytes whose braces must not count.
        public static void FoldByBraces(Core.DocumentModel doc, Func<byte, bool> isStringLike)
        {
            int n = doc.LinesTotal;
            int d = 0;
            for (int l = 0; l < n; l++) d = FoldBraceLine(doc, l, d, isStringLike);
        }

        // Fold one line given the brace depth entering it; sets its level + stores
        // the entering depth in the line state. Returns the depth entering the NEXT
        // line.
        private static int FoldBraceLine(Core.DocumentModel doc, int l, int startDepth,
                                         Func<byte, bool> isStringLike)
        {
            int s = doc.LineStart(l);
            int e = doc.LineEnd(l);
            int d = startDepth;
            bool blank = true;
            for (int p = s; p < e; p++)
            {
                char c = doc.CharAt(p);
                if (c != ' ' && c != '\t') blank = false;
                if (isStringLike(doc.StyleAt(p))) continue;
                if (c == '{') ++d;
                else if (c == '}') d = Math.Max(0, d - 1);
            }
            int levelNum = FoldLevelBase + Math.Min(startDepth, d);  // `}` line -> outer level
            int level = levelNum;
            if (blank) level |= FoldLevelWhiteFlag;
            else if (d > startDepth) level |= FoldLevelHeaderFlag;
            doc.SetLevel(l, level);
            doc.SetLineState(l, startDepth);  // remember entering depth for incremental resume
            return d;
        }

        // Indentation folding for Python.
        public static void FoldByIndent(Core.DocumentModel doc)
        {
            int n = doc.LinesTotal;
            for (int l = 0; l < n; l++) FoldIndentLine(doc, l, n);
        }

        // Visual indent (tab = next multiple of 8) of a line's leading whitespace.
        private static int IndentOf(Core.DocumentModel doc, int l, out bool blank)
        {
            int s = doc.LineStart(l);
            int e = doc.LineEnd(l);
            int col = 0;
            int p = s;
            for (; p < e; p++)
            {
                char c = doc.CharAt(p);
                if (c == ' ') ++col;
                else if (c == '\t') col += 8 - (col % 8);
                else break;
            }
            blank = p >= e;
            return col;
        }

        private static void FoldIndentLine(Core.DocumentModel doc, int l, int n)
        {
            bool blank;
            int ind = IndentOf(doc, l, out blank);
            if (blank)
            {
                doc.SetLevel(l, FoldLevelBase | FoldLevelWhiteFlag);
                return;
            }
            int nl = l + 1;  // next non-blank line decides whether this line opens a fold
            bool nlBlank = true;
            int nlInd = 0;
            while (nl < n)
            {
                nlInd = IndentOf(doc, nl, out nlBlank);
                if (!nlBlank) break;
                ++nl;
            }
            int level = FoldLevelBase + ind;
            if (nl < n && !nlBlank && nlInd > ind) level |= FoldLevelHeaderFlag;
            doc.SetLevel(l, level);
        }
    }
}
