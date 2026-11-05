using System;
using System.Collections.Generic;

namespace HelixTextEditorNET.Lexing
{
    // A set of words for keyword classification (Scintilla's WordList). Port of
    // lexers/word_list.h; parses the space-separated string SetKeywords receives.
    internal sealed class WordList
    {
        private readonly HashSet<string> _words = new HashSet<string>(StringComparer.Ordinal);

        public WordList()
        {
        }

        public WordList(string spaceSeparated)
        {
            Set(spaceSeparated);
        }

        public void Set(string spaceSeparated)
        {
            _words.Clear();
            if (string.IsNullOrEmpty(spaceSeparated)) return;
            foreach (string w in spaceSeparated.Split(new[] { ' ', '\t', '\r', '\n' },
                                                     StringSplitOptions.RemoveEmptyEntries))
                _words.Add(w);
        }

        public bool Contains(string w)
        {
            return _words.Contains(w);
        }

        public bool Empty
        {
            get { return _words.Count == 0; }
        }
    }

    // A syntax highlighter over the DocumentModel: styles the document with
    // SciLexer-compatible style bytes and computes fold levels. Port of
    // lexers/lexer.h Lexer — but emitting the real SCE_* numbering the original
    // Scintilla/SciLexer style numbering is retained because consumers hard-code those ids.
    internal abstract class LexerEngine
    {
        public const int KeywordSets = 9;
        protected readonly WordList[] Keywords = new WordList[KeywordSets];
        protected readonly Dictionary<string, string> Properties =
            new Dictionary<string, string>(StringComparer.Ordinal);

        protected LexerEngine()
        {
            for (int i = 0; i < KeywordSets; i++) Keywords[i] = new WordList();
        }

        public void SetKeywords(int set, string keywords)
        {
            if (set >= 0 && set < KeywordSets) Keywords[set].Set(keywords ?? string.Empty);
        }

        public void SetProperty(string name, string value)
        {
            if (!string.IsNullOrEmpty(name)) Properties[name] = value ?? string.Empty;
        }

        protected bool PropertyOn(string name, bool dflt)
        {
            string v;
            if (!Properties.TryGetValue(name, out v)) return dflt;
            return v != "0";
        }

        // Style the whole document.
        public void StyleDocument(Core.DocumentModel doc)
        {
            StyleRange(doc, 0, doc.Length);
        }

        // Style [startPos, endPos). Implementations may widen the range as needed
        // for multi-line constructs; the base contract is that after the call every
        // character in the range carries its correct style byte.
        public abstract void StyleRange(Core.DocumentModel doc, int startPos, int endPos);

        // Compute per-line fold levels (SC_FOLDLEVEL*). Default: no folding.
        public virtual void FoldDocument(Core.DocumentModel doc)
        {
        }

        // Factory keyed by the SciLexer lexer id (the Lexer enum's underlying value).
        // Unknown ids fall back to a null lexer (default style everywhere).
        public static LexerEngine Create(int lexerId)
        {
            switch (lexerId)
            {
                case 2: return new PythonLexerEngine();    // SCLEX_PYTHON
                case 3: return new CppLexerEngine();       // SCLEX_CPP
                case 9: return new PropsLexerEngine();     // SCLEX_PROPERTIES
                case 80: return new CMakeLexerEngine();    // SCLEX_CMAKE
                case 98: return new MarkdownLexerEngine(); // SCLEX_MARKDOWN
                default: return new NullLexerEngine();
            }
        }
    }

    // SCLEX_NULL / unknown: everything default style, no folding.
    internal sealed class NullLexerEngine : LexerEngine
    {
        public override void StyleRange(Core.DocumentModel doc, int startPos, int endPos)
        {
            doc.SetStyleFor(startPos, endPos - startPos, 0);
        }
    }
}
