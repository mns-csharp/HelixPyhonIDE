using System;

namespace HelixTextEditorNET.Lexing
{
    // Python highlighter emitting the SCE_P_* style numbering (SCLEX_PYTHON).
    // Keyword set 0 -> Word, set 1 -> Word2. Folding is indentation-based.
    internal sealed class PythonLexerEngine : LexerEngine
    {
        private const byte StyleDefault = 0;       // SCE_P_DEFAULT
        private const byte StyleCommentLine = 1;   // SCE_P_COMMENTLINE
        private const byte StyleNumber = 2;        // SCE_P_NUMBER
        private const byte StyleString = 3;        // SCE_P_STRING (double-quoted)
        private const byte StyleCharacter = 4;     // SCE_P_CHARACTER (single-quoted)
        private const byte StyleWord = 5;          // SCE_P_WORD
        private const byte StyleTriple = 6;        // SCE_P_TRIPLE (''')
        private const byte StyleTripleDouble = 7;  // SCE_P_TRIPLEDOUBLE (""")
        private const byte StyleClassName = 8;     // SCE_P_CLASSNAME
        private const byte StyleDefName = 9;       // SCE_P_DEFNAME
        private const byte StyleOperator = 10;     // SCE_P_OPERATOR
        private const byte StyleIdentifier = 11;   // SCE_P_IDENTIFIER
        private const byte StyleCommentBlock = 12; // SCE_P_COMMENTBLOCK (## ...)
        private const byte StyleStringEol = 13;    // SCE_P_STRINGEOL
        private const byte StyleWord2 = 14;        // SCE_P_WORD2
        private const byte StyleDecorator = 15;    // SCE_P_DECORATOR

        // Full-range scan: multi-line string state is carried through the single
        // pass, so styling any range means styling from the start. Documents the
        // IDE edits are small enough that this stays well under a frame.
        public override void StyleRange(Core.DocumentModel doc, int startPos, int endPos)
        {
            int n = doc.Length;
            if (n == 0) return;
            var text = new char[n];
            doc.GetCharRange(text, 0, n);

            int i = 0;
            // Tracks whether the previous significant identifier was `class` / `def`
            // so the following name gets ClassName / DefName.
            int afterKeyword = 0;  // 0 none, 1 class, 2 def

            while (i < n)
            {
                char c = text[i];

                if (c == '#')
                {
                    // `##`-led comments are comment blocks in SciLexer's Python lexer.
                    byte style = (i + 1 < n && text[i + 1] == '#') ? StyleCommentBlock : StyleCommentLine;
                    int start = i;
                    while (i < n && text[i] != '\n') i++;
                    doc.SetStyleFor(start, i - start, style);
                    continue;
                }

                if (c == '@' && (i == 0 || text[i - 1] == '\n' || IsIndentOnly(text, i)))
                {
                    // Decorator: @ plus a dotted name.
                    int start = i;
                    i++;
                    while (i < n && (IsIdentChar(text[i]) || text[i] == '.')) i++;
                    doc.SetStyleFor(start, i - start, StyleDecorator);
                    afterKeyword = 0;
                    continue;
                }

                if (c == '\'' || c == '"')
                {
                    i = ScanString(doc, text, i, i);
                    afterKeyword = 0;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_' ||
                                     ((text[i] == '+' || text[i] == '-') &&
                                      (text[i - 1] == 'e' || text[i - 1] == 'E'))))
                        i++;
                    doc.SetStyleFor(start, i - start, StyleNumber);
                    afterKeyword = 0;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < n && IsIdentChar(text[i])) i++;
                    string word = new string(text, start, i - start);

                    // String prefix (r/b/u/f combinations) directly before a quote.
                    if (i < n && (text[i] == '\'' || text[i] == '"') && IsStringPrefix(word))
                    {
                        i = ScanString(doc, text, start, i);
                        afterKeyword = 0;
                        continue;
                    }

                    byte style;
                    if (afterKeyword == 1) style = StyleClassName;
                    else if (afterKeyword == 2) style = StyleDefName;
                    else if (Keywords[0].Contains(word)) style = StyleWord;
                    else if (Keywords[1].Contains(word)) style = StyleWord2;
                    else style = StyleIdentifier;
                    doc.SetStyleFor(start, i - start, style);

                    if (word == "class") afterKeyword = 1;
                    else if (word == "def") afterKeyword = 2;
                    else afterKeyword = 0;
                    continue;
                }

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    int start = i;
                    while (i < n && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
                    {
                        if (text[i] == '\n') afterKeyword = 0;
                        i++;
                    }
                    doc.SetStyleFor(start, i - start, StyleDefault);
                    continue;
                }

                // Operators / punctuation.
                doc.SetStyleFor(i, 1, StyleOperator);
                if (c != '(' && c != ',') afterKeyword = 0;  // `def f(` keeps DefName only for the name
                i++;
            }
        }

        public override void FoldDocument(Core.DocumentModel doc)
        {
            if (!PropertyOn("fold", true)) return;
            Folding.FoldByIndent(doc);
        }

        // Scans a string starting at quote index `quotePos`, with the style run
        // beginning at `styleStart` (covers any r/b/u/f prefix). Returns the index
        // just past the string.
        private static int ScanString(Core.DocumentModel doc, char[] text, int styleStart, int quotePos)
        {
            int n = text.Length;
            char quote = text[quotePos];
            bool triple = quotePos + 2 < n && text[quotePos + 1] == quote && text[quotePos + 2] == quote;
            bool raw = HasRawPrefix(text, styleStart, quotePos);

            if (triple)
            {
                byte style = quote == '"' ? StyleTripleDouble : StyleTriple;
                int i = quotePos + 3;
                while (i < n)
                {
                    if (!raw && text[i] == '\\')
                    {
                        // A backslash-escaped char (incl. an escaped quote just before a
                        // closing run) never terminates the string.
                        i += 2;
                        continue;
                    }
                    if (text[i] == quote && i + 2 < n &&
                        text[i + 1] == quote && text[i + 2] == quote)
                    {
                        i += 3;
                        doc.SetStyleFor(styleStart, i - styleStart, style);
                        return i;
                    }
                    i++;
                }
                // Unterminated triple: style to EOF, still "inside" the string.
                doc.SetStyleFor(styleStart, n - styleStart, style);
                return n;
            }
            else
            {
                byte style = quote == '"' ? StyleString : StyleCharacter;
                int i = quotePos + 1;
                while (i < n)
                {
                    char c = text[i];
                    if (!raw && c == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (c == quote)
                    {
                        i++;
                        doc.SetStyleFor(styleStart, i - styleStart, style);
                        return i;
                    }
                    if (c == '\n')
                    {
                        // Unterminated at EOL -> StringEol for the whole run.
                        doc.SetStyleFor(styleStart, i - styleStart, StyleStringEol);
                        return i;
                    }
                    i++;
                }
                doc.SetStyleFor(styleStart, n - styleStart, StyleStringEol);
                return n;
            }
        }

        private static bool HasRawPrefix(char[] text, int styleStart, int quotePos)
        {
            for (int p = styleStart; p < quotePos; p++)
                if (text[p] == 'r' || text[p] == 'R') return true;
            return false;
        }

        private static bool IsStringPrefix(string word)
        {
            if (word.Length > 3) return false;
            foreach (char c in word)
            {
                char l = char.ToLowerInvariant(c);
                if (l != 'r' && l != 'b' && l != 'u' && l != 'f') return false;
            }
            return true;
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c > '\u0080';
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c > '\u0080';
        }

        // True when everything between the line start and index i is whitespace.
        private static bool IsIndentOnly(char[] text, int i)
        {
            int p = i - 1;
            while (p >= 0 && text[p] != '\n')
            {
                if (text[p] != ' ' && text[p] != '\t') return false;
                p--;
            }
            return true;
        }
    }
}
