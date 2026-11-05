using System;

namespace HelixTextEditorNET.Lexing
{
    // CMake highlighter emitting the SCE_CMAKE_* numbering (SCLEX_CMAKE = 80).
    // Keyword set 0 -> Commands. Variables use ${...}.
    internal sealed class CMakeLexerEngine : LexerEngine
    {
        private const byte StyleDefault = 0;     // SCE_CMAKE_DEFAULT
        private const byte StyleComment = 1;     // SCE_CMAKE_COMMENT
        private const byte StyleStringDq = 2;    // SCE_CMAKE_STRINGDQ
        private const byte StyleCommands = 5;    // SCE_CMAKE_COMMANDS
        private const byte StyleParameters = 6;  // SCE_CMAKE_PARAMETERS
        private const byte StyleVariable = 7;    // SCE_CMAKE_VARIABLE
        private const byte StyleWhileDef = 9;    // SCE_CMAKE_WHILEDEF
        private const byte StyleForeachDef = 10; // SCE_CMAKE_FOREACHDEF
        private const byte StyleIfDefineDef = 11; // SCE_CMAKE_IFDEFINEDEF
        private const byte StyleMacroDef = 12;   // SCE_CMAKE_MACRODEF
        private const byte StyleStringVar = 13;  // SCE_CMAKE_STRINGVAR
        private const byte StyleNumber = 14;     // SCE_CMAKE_NUMBER

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

                if (c == '#')
                {
                    int start = i;
                    while (i < n && text[i] != '\n') i++;
                    doc.SetStyleFor(start, i - start, StyleComment);
                    continue;
                }

                if (c == '"')
                {
                    int start = i;
                    i++;
                    while (i < n && text[i] != '"')
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '$' && i + 1 < n && text[i + 1] == '{')
                        {
                            // Style the pending string part, then the ${var} inside.
                            doc.SetStyleFor(start, i - start, StyleStringDq);
                            int varStart = i;
                            while (i < n && text[i] != '}' && text[i] != '\n') i++;
                            if (i < n && text[i] == '}') i++;
                            doc.SetStyleFor(varStart, i - varStart, StyleStringVar);
                            start = i;
                            continue;
                        }
                        i++;
                    }
                    if (i < n) i++;  // closing quote
                    doc.SetStyleFor(start, i - start, StyleStringDq);
                    continue;
                }

                if (c == '$' && i + 1 < n && text[i + 1] == '{')
                {
                    int start = i;
                    while (i < n && text[i] != '}' && text[i] != '\n') i++;
                    if (i < n && text[i] == '}') i++;
                    doc.SetStyleFor(start, i - start, StyleVariable);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                    doc.SetStyleFor(start, i - start, StyleNumber);
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    string word = new string(text, start, i - start);
                    string lower = word.ToLowerInvariant();

                    // A command is an identifier followed by '(' (ignoring spaces).
                    int look = i;
                    while (look < n && (text[look] == ' ' || text[look] == '\t')) look++;
                    bool isCall = look < n && text[look] == '(';

                    byte style;
                    if (isCall)
                    {
                        if (lower == "while" || lower == "endwhile") style = StyleWhileDef;
                        else if (lower == "foreach" || lower == "endforeach") style = StyleForeachDef;
                        else if (lower == "if" || lower == "elseif" || lower == "else" || lower == "endif")
                            style = StyleIfDefineDef;
                        else if (lower == "macro" || lower == "endmacro" || lower == "function" ||
                                 lower == "endfunction") style = StyleMacroDef;
                        else if (Keywords[0].Empty || Keywords[0].Contains(lower)) style = StyleCommands;
                        else style = StyleCommands;
                    }
                    else
                    {
                        style = StyleParameters;
                    }
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

                doc.SetStyleFor(i, 1, StyleDefault);
                i++;
            }
        }

        public override void FoldDocument(Core.DocumentModel doc)
        {
            if (!PropertyOn("fold", true)) return;
            // CMake folds on block commands; the brace folder does not apply, so use
            // an indent fold which matches how the IDE's CMake files are written.
            Folding.FoldByIndent(doc);
        }
    }
}
