using System;
using System.Collections.Generic;
using System.Drawing;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// Fonts, colours and metrics for the notebook surface, with the same font-cache discipline the
    /// Markdown preview uses: a <see cref="Font"/> is an unmanaged handle, layout asks for one per
    /// run, and creating them per call is a handle leak per word on every resize. Callers must
    /// never dispose a font they got from here.
    /// </summary>
    public sealed class NotebookStyle : IDisposable
    {
        private readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>(StringComparer.Ordinal);

        public NotebookStyle(float codeFontSize = 11f)
        {
            CodeFontSize = codeFontSize;
        }

        public float CodeFontSize { get; private set; }

        // ---- metrics ----------------------------------------------------------------
        // Shaped after Colab: a wide left gutter holding the run button and the [n] label, a
        // rounded cell card, and generous spacing between cells so the page reads as a document
        // rather than as a grid.
        public int PagePadding { get { return 16; } }
        public int GutterWidth { get { return 62; } }
        public int CellSpacing { get { return 12; } }
        public int CellPadding { get { return 8; } }
        public int OutputPadding { get { return 6; } }
        public int CellCornerRadius { get { return 8; } }
        public int RunButtonSize { get { return 26; } }
        public int LineNumberWidth { get { return 34; } }

        /// <summary>
        /// The shortest a code cell is drawn, in text lines. A one-line box is a target to aim at,
        /// not a place to write code in — a new cell should be somewhere you can start typing.
        /// A cell with more lines than this is as tall as its content; this is a floor, not a size.
        /// </summary>
        public int MinimumCodeLines { get { return 5; } }

        /// <summary>The drag handle in a cell's bottom-right corner, square.</summary>
        public int ResizeGripSize { get { return 14; } }
        public int ToolbarButtonSize { get { return 24; } }
        public int MaxContentWidth { get { return 1100; } }

        /// <summary>
        /// How many lines of one text output are laid out before the rest is summarised. The corpus
        /// has cells whose stdout is tens of thousands of lines; laying all of it out costs seconds
        /// per scroll and tells the user nothing they could read anyway.
        /// </summary>
        public int MaxOutputLines { get { return 200; } }

        /// <summary>Widest an image output is drawn. Bigger ones are scaled down, never cropped.</summary>
        public int MaxImageWidth { get { return 720; } }

        // ---- colours ----------------------------------------------------------------
        public Color Background { get { return Color.White; } }
        public Color CellBackground { get { return Color.FromArgb(248, 249, 250); } }
        public Color CellBorder { get { return Color.FromArgb(222, 224, 227); } }
        public Color SelectedCellBorder { get { return Color.FromArgb(138, 180, 248); } }
        public Color RunningCellBorder { get { return Color.FromArgb(241, 163, 65); } }
        public Color CodeBackground { get { return Color.FromArgb(248, 249, 250); } }
        public Color MarkdownBackground { get { return Color.White; } }
        public Color RunButtonRing { get { return Color.FromArgb(95, 99, 104); } }
        public Color RunButtonHover { get { return Color.FromArgb(232, 240, 254); } }
        public Color ToolbarBackground { get { return Color.FromArgb(241, 243, 244); } }
        public Color Ok { get { return Color.FromArgb(29, 138, 78); } }
        public Color LineNumber { get { return Color.FromArgb(160, 160, 160); } }
        // Output is a result surface, not another source line. A cool tint keeps ordinary stdout
        // readable in the same monospace foreground while making the source/result boundary clear.
        public Color OutputBackground { get { return Color.FromArgb(242, 246, 252); } }
        public Color OutputBorder { get { return Color.FromArgb(215, 224, 238); } }
        public Color ErrorBackground { get { return Color.FromArgb(255, 245, 245); } }
        public Color PromptForeground { get { return Color.FromArgb(80, 110, 170); } }
        public Color Foreground { get { return Color.FromArgb(30, 30, 30); } }
        public Color Muted { get { return Color.FromArgb(130, 130, 130); } }
        public Color StdErrForeground { get { return Color.FromArgb(150, 40, 40); } }
        public Color ErrorForeground { get { return Color.FromArgb(180, 30, 30); } }

        // Python token colours, matching the editor's own scheme closely enough that a cell does not
        // change appearance when it gains focus and the real editor slides in over it.
        public Color Keyword { get { return Color.FromArgb(0, 0, 255); } }
        public Color Builtin { get { return Color.FromArgb(43, 145, 175); } }
        public Color StringLiteral { get { return Color.FromArgb(163, 21, 21); } }
        public Color Comment { get { return Color.FromArgb(0, 128, 0); } }
        public Color Number { get { return Color.FromArgb(9, 134, 88); } }
        public Color Decorator { get { return Color.FromArgb(128, 0, 128); } }
        public Color Definition { get { return Color.FromArgb(116, 83, 31); } }
        public Color TypeMarker { get { return Color.FromArgb(150, 120, 144, 156); } }

        // ---- fonts ------------------------------------------------------------------
        public Font Code { get { return Get("Consolas", CodeFontSize, FontStyle.Regular); } }
        public Font CodeBold { get { return Get("Consolas", CodeFontSize, FontStyle.Bold); } }
        public Font CodeItalic { get { return Get("Consolas", CodeFontSize, FontStyle.Italic); } }
        public Font Prompt { get { return Get("Consolas", CodeFontSize - 0.75f, FontStyle.Regular); } }
        public Font Ui { get { return Get("Segoe UI", 8.25f, FontStyle.Regular); } }

        public int CodeLineHeight
        {
            get { return Code.Height; }
        }

        private Font Get(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            Font font;
            if (_fonts.TryGetValue(key, out font)) return font;
            font = new Font(family, size, style);
            _fonts[key] = font;
            return font;
        }

        public void Dispose()
        {
            foreach (Font font in _fonts.Values) font.Dispose();
            _fonts.Clear();
        }
    }

    /// <summary>One coloured run of a source line.</summary>
    public struct CodeToken
    {
        public int Start;
        public int Length;
        public Color Color;
        public FontStyle FontStyle;
    }

    /// <summary>
    /// A deliberately small Python colouriser for the cells that are NOT being edited.
    ///
    /// <para>
    /// The real editor is a single shared control that moves into whichever cell has focus — one
    /// Scintilla document for a notebook, not seventy-eight of them. Every other cell is painted,
    /// and painting it in flat black would make a notebook look like a text dump and make the
    /// moment of focus jarring. This is not a parser and does not pretend to be: it colours
    /// keywords, strings, comments, numbers and decorators, line by line, with a carried-over
    /// triple-quote state, and gets everything else deliberately wrong-but-harmless.
    /// </para>
    /// </summary>
    public static class PythonPreviewColouriser
    {
        private static readonly HashSet<string> Keywords = Words(PythonEditorControl.PythonHighlightKeywords);
        private static readonly HashSet<string> Builtins = Words(PythonEditorControl.PythonHighlightBuiltins);

        /// <summary>
        /// Colour one line. <paramref name="inTripleQuote"/> carries the docstring state across
        /// lines — without it every line of a docstring after the first is coloured as code.
        /// </summary>
        public static List<CodeToken> Colour(string line, NotebookStyle style, ref char inTripleQuote)
        {
            var tokens = new List<CodeToken>();
            if (string.IsNullOrEmpty(line)) return tokens;

            int i = 0;
            int afterKeyword = 0;
            if (inTripleQuote != '\0')
            {
                int close = IndexOfTriple(line, 0, inTripleQuote);
                if (close < 0)
                {
                    tokens.Add(new CodeToken { Start = 0, Length = line.Length, Color = style.StringLiteral });
                    return tokens;
                }
                tokens.Add(new CodeToken { Start = 0, Length = close + 3, Color = style.StringLiteral });
                i = close + 3;
                inTripleQuote = '\0';
            }

            while (i < line.Length)
            {
                char c = line[i];

                if (c == '#')
                {
                    tokens.Add(new CodeToken { Start = i, Length = line.Length - i, Color = style.Comment,
                        FontStyle = FontStyle.Italic });
                    return tokens;
                }

                if (c == '"' || c == '\'')
                {
                    if (i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                    {
                        int close = IndexOfTriple(line, i + 3, c);
                        if (close < 0)
                        {
                            inTripleQuote = c;
                            tokens.Add(new CodeToken { Start = i, Length = line.Length - i, Color = style.StringLiteral });
                            return tokens;
                        }
                        tokens.Add(new CodeToken { Start = i, Length = close + 3 - i, Color = style.StringLiteral });
                        i = close + 3;
                        continue;
                    }
                    int end = i + 1;
                    while (end < line.Length && line[end] != c)
                    {
                        if (line[end] == '\\') end++;      // an escaped quote does not end the string
                        end++;
                    }
                    if (end < line.Length) end++;
                    tokens.Add(new CodeToken { Start = i, Length = end - i, Color = style.StringLiteral });
                    i = end;
                    continue;
                }

                if (c == '@' && (i == 0 || IsAllSpace(line, i)))
                {
                    int end = i + 1;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_' || line[end] == '.')) end++;
                    tokens.Add(new CodeToken { Start = i, Length = end - i, Color = style.Decorator });
                    i = end;
                    continue;
                }

                if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(line[i - 1])))
                {
                    int end = i;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '.' || line[end] == '_')) end++;
                    tokens.Add(new CodeToken { Start = i, Length = end - i, Color = style.Number });
                    i = end;
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    int end = i;
                    while (end < line.Length && IsIdentifierChar(line[end])) end++;
                    string word = line.Substring(i, end - i);
                    if (afterKeyword != 0)
                        tokens.Add(new CodeToken { Start = i, Length = end - i,
                            Color = afterKeyword == 1 ? style.Builtin : style.Definition,
                            FontStyle = FontStyle.Bold });
                    else if (Keywords.Contains(word))
                        tokens.Add(new CodeToken { Start = i, Length = end - i, Color = style.Keyword,
                            FontStyle = FontStyle.Bold });
                    else if (Builtins.Contains(word))
                        tokens.Add(new CodeToken { Start = i, Length = end - i, Color = style.Builtin });
                    afterKeyword = word == "class" ? 1 : (word == "def" ? 2 : 0);
                    i = end;
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                {
                    tokens.Add(new CodeToken { Start = i, Length = 1, Color = style.Foreground,
                        FontStyle = FontStyle.Bold });
                    afterKeyword = 0;
                }

                i++;
            }
            return tokens;
        }

        private static HashSet<string> Words(string text)
        {
            return new HashSet<string>((text ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
        }

        private static bool IsAllSpace(string line, int upTo)
        {
            for (int i = 0; i < upTo; i++) if (line[i] != ' ' && line[i] != '\t') return false;
            return true;
        }

        private static bool IsIdentifierStart(char c) { return char.IsLetter(c) || c == '_'; }
        private static bool IsIdentifierChar(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        private static int IndexOfTriple(string line, int from, char quote)
        {
            if (from >= line.Length) return -1;
            return line.IndexOf(new string(quote, 3), from, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Strips the ANSI escape sequences that IPython puts in a traceback. Real notebooks store them
    /// — every `traceback` in the fixture corpus is full of them — so a renderer that does not
    /// strip them shows the user `[0;31m` instead of a red line.
    /// </summary>
    public static class AnsiText
    {
        public static string Strip(string text)
        {
            const char escape = (char)27;
            if (string.IsNullOrEmpty(text) || text.IndexOf(escape) < 0) return text ?? string.Empty;

            var builder = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != escape) { builder.Append(text[i]); continue; }
                // CSI ... final byte in @-~ ends the sequence; an unterminated one eats the rest,
                // which is what a terminal does with it too.
                int j = i + 1;
                if (j < text.Length && text[j] == '[') j++;
                while (j < text.Length && !(text[j] >= '@' && text[j] <= '~')) j++;
                i = j;
            }
            return builder.ToString();
        }
    }
}
