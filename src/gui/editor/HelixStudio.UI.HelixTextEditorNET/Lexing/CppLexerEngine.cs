using System;

namespace HelixTextEditorNET.Lexing
{
    // C/C++/CUDA-family highlighter emitting the SCE_C_* style numbering
    // (SCLEX_CPP). Keyword set 0 -> Word, set 1 -> Word2. Folding is brace-based.
    internal sealed class CppLexerEngine : LexerEngine
    {
        private const byte StyleDefault = 0;        // SCE_C_DEFAULT
        private const byte StyleComment = 1;        // SCE_C_COMMENT (/* */)
        private const byte StyleCommentLine = 2;    // SCE_C_COMMENTLINE (//)
        private const byte StyleCommentDoc = 3;     // SCE_C_COMMENTDOC (/** */)
        private const byte StyleNumber = 4;         // SCE_C_NUMBER
        private const byte StyleWord = 5;           // SCE_C_WORD
        private const byte StyleString = 6;         // SCE_C_STRING
        private const byte StyleCharacter = 7;      // SCE_C_CHARACTER
        private const byte StylePreprocessor = 9;   // SCE_C_PREPROCESSOR
        private const byte StyleOperator = 10;      // SCE_C_OPERATOR
        private const byte StyleIdentifier = 11;    // SCE_C_IDENTIFIER
        private const byte StyleStringEol = 12;     // SCE_C_STRINGEOL
        private const byte StyleVerbatim = 13;      // SCE_C_VERBATIM (raw strings)
        private const byte StyleCommentLineDoc = 15; // SCE_C_COMMENTLINEDOC (///)
        private const byte StyleWord2 = 16;         // SCE_C_WORD2
        private const byte StyleGlobalClass = 19;   // SCE_C_GLOBALCLASS

        public override void StyleRange(Core.DocumentModel doc, int startPos, int endPos)
        {
            int n = doc.Length;
            if (n == 0) return;
            var text = new char[n];
            doc.GetCharRange(text, 0, n);

            int i = 0;
            while (i < n)
            {
                char c = text[i];

                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    byte style = (i + 2 < n && text[i + 2] == '/') ? StyleCommentLineDoc : StyleCommentLine;
                    int start = i;
                    while (i < n && text[i] != '\n') i++;
                    doc.SetStyleFor(start, i - start, style);
                    continue;
                }

                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    byte style = (i + 2 < n && text[i + 2] == '*') ? StyleCommentDoc : StyleComment;
                    int start = i;
                    i += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/')) i++;
                    if (i < n) i += 2;  // consume the close
                    doc.SetStyleFor(start, i - start, style);
                    continue;
                }

                if (c == '#' && IsLineStartOnlyWhitespaceBefore(text, i))
                {
                    // Preprocessor line, honoring backslash-continuations.
                    int start = i;
                    while (i < n && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < n && (text[i + 1] == '\n' ||
                            (text[i + 1] == '\r' && i + 2 < n && text[i + 2] == '\n')))
                        {
                            i += (text[i + 1] == '\r') ? 3 : 2;
                            continue;
                        }
                        i++;
                    }
                    doc.SetStyleFor(start, i - start, StylePreprocessor);
                    continue;
                }

                if (c == '"')
                {
                    // Raw string R"delim( ... )delim" was handled by the prefix branch
                    // below (identifier + quote); a bare quote is a normal string.
                    i = ScanCString(doc, text, i, i, '"', StyleString);
                    continue;
                }

                if (c == '\'')
                {
                    i = ScanCString(doc, text, i, i, '\'', StyleCharacter);
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_' ||
                                     text[i] == '\'' ||
                                     ((text[i] == '+' || text[i] == '-') &&
                                      (text[i - 1] == 'e' || text[i - 1] == 'E' ||
                                       text[i - 1] == 'p' || text[i - 1] == 'P'))))
                        i++;
                    doc.SetStyleFor(start, i - start, StyleNumber);
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < n && IsIdentChar(text[i])) i++;
                    string word = new string(text, start, i - start);

                    // C++11 raw string literal: R"( ... )" (with optional delimiter).
                    if (i < n && text[i] == '"' && (word == "R" || word == "LR" || word == "uR" ||
                                                    word == "UR" || word == "u8R"))
                    {
                        i = ScanRawString(doc, text, start, i);
                        continue;
                    }

                    byte style;
                    if (Keywords[0].Contains(word)) style = StyleWord;
                    else if (Keywords[1].Contains(word)) style = StyleWord2;
                    else if (Keywords[3].Contains(word)) style = StyleGlobalClass;
                    else style = StyleIdentifier;
                    doc.SetStyleFor(start, i - start, style);
                    continue;
                }

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    int start = i;
                    while (i < n && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
                        i++;
                    doc.SetStyleFor(start, i - start, StyleDefault);
                    continue;
                }

                doc.SetStyleFor(i, 1, StyleOperator);
                i++;
            }
        }

        public override void FoldDocument(Core.DocumentModel doc)
        {
            if (!PropertyOn("fold", true)) return;
            Folding.FoldByBraces(doc, IsStringLikeStyle);
        }

        private static bool IsStringLikeStyle(byte s)
        {
            return s == StyleComment || s == StyleCommentLine || s == StyleCommentDoc ||
                   s == StyleCommentLineDoc || s == StyleString || s == StyleCharacter ||
                   s == StyleStringEol || s == StyleVerbatim;
        }

        // Scans a quoted literal with backslash escapes; unterminated at EOL ->
        // StringEol. Returns the index just past the literal.
        private static int ScanCString(Core.DocumentModel doc, char[] text, int styleStart,
                                       int quotePos, char quote, byte style)
        {
            int n = text.Length;
            int i = quotePos + 1;
            while (i < n)
            {
                char c = text[i];
                if (c == '\\')
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
                    doc.SetStyleFor(styleStart, i - styleStart, StyleStringEol);
                    return i;
                }
                i++;
            }
            doc.SetStyleFor(styleStart, n - styleStart, StyleStringEol);
            return n;
        }

        // R"delim( ... )delim" — no escapes inside. Styled as Verbatim.
        private static int ScanRawString(Core.DocumentModel doc, char[] text, int styleStart, int quotePos)
        {
            int n = text.Length;
            int i = quotePos + 1;
            int delimStart = i;
            while (i < n && text[i] != '(' && text[i] != '\n') i++;
            if (i >= n || text[i] == '\n')
            {
                doc.SetStyleFor(styleStart, i - styleStart, StyleStringEol);
                return i;
            }
            string closer = ")" + new string(text, delimStart, i - delimStart) + "\"";
            i++;  // past '('
            while (i < n)
            {
                if (text[i] == ')' && MatchesAt(text, i, closer))
                {
                    i += closer.Length;
                    doc.SetStyleFor(styleStart, i - styleStart, StyleVerbatim);
                    return i;
                }
                i++;
            }
            doc.SetStyleFor(styleStart, n - styleStart, StyleVerbatim);
            return n;
        }

        private static bool MatchesAt(char[] text, int pos, string s)
        {
            if (pos + s.Length > text.Length) return false;
            for (int k = 0; k < s.Length; k++)
                if (text[pos + k] != s[k]) return false;
            return true;
        }

        private static bool IsLineStartOnlyWhitespaceBefore(char[] text, int i)
        {
            int p = i - 1;
            while (p >= 0 && text[p] != '\n')
            {
                if (text[p] != ' ' && text[p] != '\t') return false;
                p--;
            }
            return true;
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c >= (char)128;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c >= (char)128;
        }
    }
}
