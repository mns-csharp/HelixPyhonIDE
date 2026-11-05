using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Markdown
{
    /// <summary>
    /// Block-level Markdown parser: CommonMark's block structure plus the GitHub-Flavoured
    /// extensions this repository's own documents actually use — pipe tables, strikethrough,
    /// task-list items, autolinks.
    ///
    /// <b>Scope, stated honestly.</b> This is not a CommonMark-certified implementation and does not
    /// claim to be. It is a line-oriented parser covering the constructs that appear in real README
    /// and docs files: ATX and setext headings, fenced and indented code, blockquotes, nested
    /// ordered/unordered lists, thematic breaks, pipe tables, HTML blocks, link reference
    /// definitions and paragraphs. The spec's genuinely hard corners — lazy continuation across
    /// every container type, the full HTML block grammar's seven conditions, link reference
    /// definitions interrupting other blocks — are simplified, and where they are, the code says so
    /// at the point of the simplification rather than in a footnote.
    ///
    /// It is deliberately dependency-free and allocation-light: the IDE re-parses on a keystroke
    /// debounce, so a document is walked once, line by line, with no regular expressions.
    /// </summary>
    public sealed class MarkdownParser
    {
        private readonly MarkdownInlineParser _inline = new MarkdownInlineParser();

        public static MarkdownDocument Parse(string text)
        {
            return new MarkdownParser().ParseDocument(text);
        }

        public MarkdownDocument ParseDocument(string text)
        {
            var document = new MarkdownDocument();
            string[] lines = SplitLines(text ?? string.Empty);

            // Two passes. Reference definitions are collected first because a link can refer to a
            // definition that appears LATER in the document — resolving inline content in the same
            // pass that discovers the definitions would silently drop every forward reference.
            int[] consumed = CollectLinkReferences(lines, document);

            var blocks = new List<MarkdownBlock>();
            ParseBlocks(lines, 0, lines.Length, consumed, blocks);
            foreach (MarkdownBlock block in blocks) document.Root.Children.Add(block);

            ResolveInlines(document.Root, document);
            return document;
        }

        // ---- line handling ---------------------------------------------------------------------

        /// <summary>
        /// Splits on CR, LF and CRLF alike, and drops a leading BOM.
        ///
        /// <c>string.Split('\n')</c> alone leaves a trailing <c>'\r'</c> on every line of a
        /// CRLF file, which then shows up as a stray glyph in code blocks and defeats every
        /// "is this line blank?" test — the kind of bug that only appears on the other platform.
        /// </summary>
        internal static string[] SplitLines(string text)
        {
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                lines.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            lines.Add(text.Substring(start));
            return lines.ToArray();
        }

        private static bool IsBlank(string line)
        {
            for (int i = 0; i < line.Length; i++)
                if (line[i] != ' ' && line[i] != '\t') return false;
            return true;
        }

        private static int IndentOf(string line)
        {
            int indent = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ') indent++;
                else if (line[i] == '\t') indent += 4 - (indent % 4);   // tab stop, per the spec
                else break;
            }
            return indent;
        }

        private static string StripIndent(string line, int amount)
        {
            int removed = 0, i = 0;
            while (i < line.Length && removed < amount)
            {
                if (line[i] == ' ') { removed++; i++; }
                else if (line[i] == '\t') { removed += 4 - (removed % 4); i++; }
                else break;
            }
            return line.Substring(i);
        }

        // ---- link reference definitions ----------------------------------------------------------

        /// <summary>
        /// First pass: harvest <c>[label]: destination "title"</c> lines and mark them consumed so
        /// the block pass does not also render them as paragraphs.
        ///
        /// Restricted to definitions that start their own line at the top level — the spec allows
        /// them at the start of a paragraph, but accepting that here would mean re-implementing
        /// paragraph parsing in this pass to know where paragraphs start. The narrow version is
        /// what real documents use; the broad version is a rewrite of the pass.
        /// </summary>
        private static int[] CollectLinkReferences(string[] lines, MarkdownDocument document)
        {
            var consumed = new int[lines.Length];
            bool inFence = false;
            string fence = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Never look inside a fenced code block: `[x]: y` there is content, not a definition.
                string openedWith = FenceMarkerOf(line);
                if (!inFence && openedWith != null) { inFence = true; fence = openedWith; continue; }
                if (inFence)
                {
                    if (ClosesFence(line, fence)) { inFence = false; fence = null; }
                    continue;
                }

                if (IndentOf(line) >= 4) continue;   // indented code
                string label, destination, title;
                if (!TryParseLinkReference(line.Trim(), out label, out destination, out title)) continue;

                // First definition wins, as the spec requires.
                if (!document.LinkReferences.ContainsKey(label))
                    document.LinkReferences[label] = new MarkdownLinkReference(destination, title);
                consumed[i] = 1;
            }
            return consumed;
        }

        private static bool TryParseLinkReference(string line, out string label, out string destination, out string title)
        {
            label = destination = title = null;
            if (line.Length < 4 || line[0] != '[') return false;
            int close = line.IndexOf(']');
            if (close <= 1 || close + 1 >= line.Length || line[close + 1] != ':') return false;

            label = line.Substring(1, close - 1).Trim();
            if (label.Length == 0) return false;

            string rest = line.Substring(close + 2).Trim();
            if (rest.Length == 0) return false;

            int space = rest.IndexOf(' ');
            if (space < 0) { destination = Unbracket(rest); return true; }

            destination = Unbracket(rest.Substring(0, space));
            string tail = rest.Substring(space + 1).Trim();
            if (tail.Length >= 2 &&
                ((tail[0] == '"' && tail[tail.Length - 1] == '"') ||
                 (tail[0] == '\'' && tail[tail.Length - 1] == '\'') ||
                 (tail[0] == '(' && tail[tail.Length - 1] == ')')))
                title = tail.Substring(1, tail.Length - 2);
            return true;
        }

        private static string Unbracket(string s)
        {
            if (s.Length >= 2 && s[0] == '<' && s[s.Length - 1] == '>') return s.Substring(1, s.Length - 2);
            return s;
        }

        // ---- block parsing -----------------------------------------------------------------------

        private void ParseBlocks(string[] lines, int from, int to, int[] consumed, List<MarkdownBlock> into)
        {
            int i = from;
            while (i < to)
            {
                string line = lines[i];

                if (consumed != null && consumed[i] != 0) { i++; continue; }
                if (IsBlank(line)) { i++; continue; }

                int indent = IndentOf(line);
                string trimmed = line.Trim();

                // Fenced code — checked before everything else, because its contents must never be
                // interpreted as any other construct.
                string fence = FenceMarkerOf(line);
                if (fence != null) { i = ParseFencedCode(lines, i, to, fence, indent, into); continue; }

                // Indented code, but only where a list would not claim the indentation.
                if (indent >= 4) { i = ParseIndentedCode(lines, i, to, into); continue; }

                if (IsThematicBreak(trimmed))
                {
                    var rule = new MarkdownBlock(MarkdownBlockKind.ThematicBreak) { SourceLine = i + 1 };
                    into.Add(rule);
                    i++;
                    continue;
                }

                int headingLevel = AtxHeadingLevel(trimmed);
                if (headingLevel > 0) { i = ParseAtxHeading(lines, i, trimmed, headingLevel, into); continue; }

                if (trimmed[0] == '>') { i = ParseBlockQuote(lines, i, to, into); continue; }

                if (IsHtmlBlockStart(trimmed)) { i = ParseHtmlBlock(lines, i, to, into); continue; }

                int markerWidth; bool ordered; int startNumber;
                if (TryParseListMarker(line, out markerWidth, out ordered, out startNumber))
                { i = ParseList(lines, i, to, ordered, into); continue; }

                // A pipe table is recognised by its DELIMITER row, not its first row — `a | b` on
                // its own is an ordinary paragraph, and only the `---|---` underneath makes the line
                // above a header.
                if (i + 1 < to && MarkdownTableReader.IsDelimiterRow(lines[i + 1]) && line.IndexOf('|') >= 0)
                { i = MarkdownTableReader.Parse(lines, i, to, into); continue; }

                i = ParseParagraph(lines, i, to, consumed, into);
            }
        }

        // ---- headings ----------------------------------------------------------------------------

        private static int AtxHeadingLevel(string trimmed)
        {
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#') level++;
            if (level == 0 || level > 6) return 0;
            // `#hashtag` is not a heading: the run of #s must be followed by a space or end of line.
            if (level < trimmed.Length && trimmed[level] != ' ' && trimmed[level] != '\t') return 0;
            return level;
        }

        private int ParseAtxHeading(string[] lines, int index, string trimmed, int level, List<MarkdownBlock> into)
        {
            string content = trimmed.Substring(level).Trim();
            // A closing run of #s is decoration and is dropped: `## Title ##`.
            int end = content.Length;
            while (end > 0 && content[end - 1] == '#') end--;
            if (end < content.Length && (end == 0 || content[end - 1] == ' ')) content = content.Substring(0, end).TrimEnd();

            var heading = new MarkdownBlock(MarkdownBlockKind.Heading)
            {
                Level = level,
                Literal = content,
                SourceLine = index + 1,
            };
            into.Add(heading);
            return index + 1;
        }

        // ---- code --------------------------------------------------------------------------------

        /// <summary>The fence string a line opens, or null. Backticks and tildes, three or more.</summary>
        private static string FenceMarkerOf(string line)
        {
            if (IndentOf(line) >= 4) return null;
            string trimmed = line.TrimStart(' ', '\t');
            if (trimmed.Length < 3) return null;
            char c = trimmed[0];
            if (c != '`' && c != '~') return null;
            int run = 0;
            while (run < trimmed.Length && trimmed[run] == c) run++;
            if (run < 3) return null;
            // A backtick fence's info string may not itself contain a backtick — that is what keeps
            // `` `a` `` on its own line from being read as a fence opener.
            if (c == '`' && trimmed.IndexOf('`', run) >= 0) return null;
            return new string(c, run);
        }

        private static bool ClosesFence(string line, string fence)
        {
            string trimmed = line.Trim();
            if (trimmed.Length < fence.Length) return false;
            char c = fence[0];
            for (int i = 0; i < trimmed.Length; i++) if (trimmed[i] != c) return false;
            return trimmed.Length >= fence.Length;
        }

        private static int ParseFencedCode(string[] lines, int index, int to, string fence, int indent, List<MarkdownBlock> into)
        {
            string opener = lines[index].TrimStart(' ', '\t');
            string info = opener.Substring(fence.Length).Trim();

            var body = new StringBuilder();
            int i = index + 1;
            bool closed = false;
            for (; i < to; i++)
            {
                if (ClosesFence(lines[i], fence)) { closed = true; i++; break; }
                // The opener's own indentation is removed from each content line, so an indented
                // fence inside a list item does not render with phantom leading spaces.
                body.Append(StripIndent(lines[i], indent)).Append('\n');
            }

            var block = new MarkdownBlock(MarkdownBlockKind.CodeBlock)
            {
                Literal = TrimTrailingNewline(body.ToString()),
                InfoString = info.Length == 0 ? null : info,
                SourceLine = index + 1,
            };
            into.Add(block);
            // An unclosed fence runs to the end of the document rather than being rejected — that is
            // what the spec says, and it is also what a user editing a file mid-fence needs.
            return closed ? i : to;
        }

        private static int ParseIndentedCode(string[] lines, int index, int to, List<MarkdownBlock> into)
        {
            var body = new StringBuilder();
            int i = index;
            int lastContent = index;
            while (i < to && (IndentOf(lines[i]) >= 4 || IsBlank(lines[i])))
            {
                if (!IsBlank(lines[i])) lastContent = i;
                body.Append(StripIndent(lines[i], 4)).Append('\n');
                i++;
            }
            // Trailing blank lines belong to the document, not to the code block.
            var kept = new StringBuilder();
            for (int j = index; j <= lastContent; j++) kept.Append(StripIndent(lines[j], 4)).Append('\n');

            into.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock)
            {
                Literal = TrimTrailingNewline(kept.ToString()),
                SourceLine = index + 1,
            });
            return lastContent + 1;
        }

        private static string TrimTrailingNewline(string s)
        {
            return s.Length > 0 && s[s.Length - 1] == '\n' ? s.Substring(0, s.Length - 1) : s;
        }

        // ---- thematic break ------------------------------------------------------------------------

        private static bool IsThematicBreak(string trimmed)
        {
            if (trimmed.Length < 3) return false;
            char c = trimmed[0];
            if (c != '-' && c != '*' && c != '_') return false;
            int count = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];
                if (ch == c) count++;
                else if (ch != ' ' && ch != '\t') return false;
            }
            return count >= 3;
        }

        // ---- blockquote ---------------------------------------------------------------------------

        private int ParseBlockQuote(string[] lines, int index, int to, List<MarkdownBlock> into)
        {
            var inner = new List<string>();
            int i = index;
            while (i < to)
            {
                string line = lines[i];
                string trimmed = line.TrimStart(' ', '\t');
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    string content = trimmed.Substring(1);
                    if (content.StartsWith(" ", StringComparison.Ordinal)) content = content.Substring(1);
                    inner.Add(content);
                    i++;
                    continue;
                }
                // Lazy continuation: a plain line directly under quoted text stays in the quote.
                if (!IsBlank(line) && inner.Count > 0 && !IsBlank(inner[inner.Count - 1]))
                { inner.Add(line); i++; continue; }
                break;
            }

            var quote = new MarkdownBlock(MarkdownBlockKind.BlockQuote) { SourceLine = index + 1 };
            string[] innerLines = inner.ToArray();
            ParseBlocks(innerLines, 0, innerLines.Length, null, quote.Children);
            into.Add(quote);
            return i;
        }

        // ---- lists ---------------------------------------------------------------------------------

        /// <summary>
        /// Recognises <c>-</c>/<c>*</c>/<c>+</c> and <c>1.</c>/<c>1)</c> markers, reporting how many
        /// columns the marker plus its trailing space occupies — that width is the continuation
        /// indent for the item's own content.
        /// </summary>
        private static bool TryParseListMarker(string line, out int markerWidth, out bool ordered, out int startNumber)
        {
            markerWidth = 0; ordered = false; startNumber = 1;
            int indent = IndentOf(line);
            if (indent >= 4) return false;

            string trimmed = line.TrimStart(' ', '\t');
            if (trimmed.Length == 0) return false;

            char c = trimmed[0];
            if (c == '-' || c == '*' || c == '+')
            {
                // `- - -` is a thematic break, not a one-item list.
                if (IsThematicBreak(trimmed)) return false;
                if (trimmed.Length > 1 && trimmed[1] != ' ' && trimmed[1] != '\t') return false;
                markerWidth = indent + 2;
                return true;
            }

            int digits = 0;
            while (digits < trimmed.Length && trimmed[digits] >= '0' && trimmed[digits] <= '9') digits++;
            if (digits == 0 || digits > 9 || digits >= trimmed.Length) return false;
            if (trimmed[digits] != '.' && trimmed[digits] != ')') return false;
            if (digits + 1 < trimmed.Length && trimmed[digits + 1] != ' ' && trimmed[digits + 1] != '\t') return false;

            ordered = true;
            int.TryParse(trimmed.Substring(0, digits), out startNumber);
            markerWidth = indent + digits + 2;
            return true;
        }

        private int ParseList(string[] lines, int index, int to, bool ordered, List<MarkdownBlock> into)
        {
            int firstMarkerWidth; bool firstOrdered; int startNumber;
            TryParseListMarker(lines[index], out firstMarkerWidth, out firstOrdered, out startNumber);

            var list = new MarkdownBlock(MarkdownBlockKind.List)
            {
                IsOrdered = ordered,
                StartNumber = startNumber,
                SourceLine = index + 1,
            };

            int i = index;
            bool sawBlankBetweenItems = false;

            while (i < to)
            {
                int markerWidth; bool itemOrdered; int ignored;
                if (IsBlank(lines[i]))
                {
                    // A blank line ends the list only if the NEXT non-blank line is not a
                    // continuation of it. Otherwise it makes the list loose.
                    int look = i;
                    while (look < to && IsBlank(lines[look])) look++;
                    if (look >= to) { i = look; break; }
                    bool continues = TryParseListMarker(lines[look], out markerWidth, out itemOrdered, out ignored)
                                     && itemOrdered == ordered;
                    if (!continues && IndentOf(lines[look]) < firstMarkerWidth) { i = look; break; }
                    sawBlankBetweenItems = true;
                    i = look;
                    continue;
                }

                if (!TryParseListMarker(lines[i], out markerWidth, out itemOrdered, out ignored)) break;
                // A different marker type starts a NEW list rather than continuing this one.
                if (itemOrdered != ordered) break;

                i = ParseListItem(lines, i, to, markerWidth, ordered, list);
            }

            list.IsLoose = sawBlankBetweenItems;
            into.Add(list);
            return i;
        }

        private int ParseListItem(string[] lines, int index, int to, int markerWidth, bool ordered, MarkdownBlock list)
        {
            string first = lines[index].TrimStart(' ', '\t');
            int skip = ordered ? first.IndexOfAny(new[] { '.', ')' }) + 1 : 1;
            string firstContent = skip < first.Length ? first.Substring(skip) : string.Empty;
            if (firstContent.StartsWith(" ", StringComparison.Ordinal)) firstContent = firstContent.Substring(1);

            var itemLines = new List<string>();
            bool? isChecked = TakeTaskMarker(ref firstContent);
            itemLines.Add(firstContent);

            int i = index + 1;
            while (i < to)
            {
                if (IsBlank(lines[i]))
                {
                    // Look ahead: a blank followed by content indented into this item continues it.
                    int look = i;
                    while (look < to && IsBlank(lines[look])) look++;
                    if (look < to && IndentOf(lines[look]) >= markerWidth)
                    {
                        for (int b = i; b < look; b++) itemLines.Add(string.Empty);
                        i = look;
                        continue;
                    }
                    break;
                }
                int mw; bool o; int n;
                if (TryParseListMarker(lines[i], out mw, out o, out n) && IndentOf(lines[i]) < markerWidth) break;
                if (IndentOf(lines[i]) >= markerWidth) { itemLines.Add(StripIndent(lines[i], markerWidth)); i++; continue; }
                // Lazy continuation of the item's paragraph.
                itemLines.Add(lines[i]);
                i++;
            }

            var item = new MarkdownBlock(MarkdownBlockKind.ListItem)
            {
                IsChecked = isChecked,
                SourceLine = index + 1,
            };
            string[] inner = itemLines.ToArray();
            ParseBlocks(inner, 0, inner.Length, null, item.Children);
            list.Children.Add(item);
            return i;
        }

        /// <summary>Consumes a GFM task-list marker (<c>[ ]</c> / <c>[x]</c>) from the item's start.</summary>
        private static bool? TakeTaskMarker(ref string content)
        {
            if (content.Length < 3 || content[0] != '[' || content[2] != ']') return null;
            char state = content[1];
            bool? result;
            if (state == ' ') result = false;
            else if (state == 'x' || state == 'X') result = true;
            else return null;
            content = content.Length > 3 ? content.Substring(3).TrimStart() : string.Empty;
            return result;
        }

        // ---- HTML blocks ------------------------------------------------------------------------

        /// <summary>
        /// True only when the line opens something that is actually a TAG.
        ///
        /// "Starts with &lt; and a letter" is not enough: <c>&lt;https://example.com&gt;</c> also
        /// matches it, and claiming that line as an HTML block means the inline parser never sees
        /// it and the autolink renders as escaped text. The tag name must be followed by
        /// whitespace, <c>/</c> or <c>&gt;</c> — which <c>https:</c> is not.
        /// </summary>
        private static bool IsHtmlBlockStart(string trimmed)
        {
            if (trimmed.Length < 2 || trimmed[0] != '<') return false;
            int i = 1;
            if (trimmed[i] == '!' || trimmed[i] == '?') return true;   // comment, doctype, PI
            if (trimmed[i] == '/') i++;
            if (i >= trimmed.Length || !char.IsLetter(trimmed[i])) return false;
            while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '-')) i++;
            if (i >= trimmed.Length) return false;
            char after = trimmed[i];
            return after == '>' || after == '/' || after == ' ' || after == '\t';
        }

        private static int ParseHtmlBlock(string[] lines, int index, int to, List<MarkdownBlock> into)
        {
            var body = new StringBuilder();
            int i = index;
            // Simplification, deliberately: an HTML block runs to the next blank line. The spec's
            // seven start/end condition pairs exist to handle things like a <pre> containing blank
            // lines; documents in this repository do not, and the full grammar is a parser of its
            // own. Anything this gets wrong renders as HTML source text, never as broken markup.
            while (i < to && !IsBlank(lines[i])) { body.Append(lines[i]).Append('\n'); i++; }

            into.Add(new MarkdownBlock(MarkdownBlockKind.HtmlBlock)
            {
                Literal = TrimTrailingNewline(body.ToString()),
                SourceLine = index + 1,
            });
            return i;
        }

        // ---- paragraphs and setext headings --------------------------------------------------------

        private int ParseParagraph(string[] lines, int index, int to, int[] consumed, List<MarkdownBlock> into)
        {
            var text = new StringBuilder();
            int i = index;
            while (i < to)
            {
                if (consumed != null && consumed[i] != 0) break;
                string line = lines[i];
                if (IsBlank(line)) break;

                string trimmed = line.Trim();

                // A setext underline turns everything collected so far into a heading.
                if (i > index && IsSetextUnderline(trimmed))
                {
                    var setext = new MarkdownBlock(MarkdownBlockKind.Heading)
                    {
                        Level = trimmed[0] == '=' ? 1 : 2,
                        Literal = text.ToString(),
                        SourceLine = index + 1,
                    };
                    into.Add(setext);
                    return i + 1;
                }

                // Blocks that interrupt a paragraph. Notably a setext underline of `-` is also a
                // thematic break, so the check above has to come first.
                if (i > index)
                {
                    if (AtxHeadingLevel(trimmed) > 0) break;
                    if (FenceMarkerOf(line) != null) break;
                    if (IsThematicBreak(trimmed)) break;
                    if (trimmed.Length > 0 && trimmed[0] == '>') break;
                    int mw; bool o; int n;
                    if (TryParseListMarker(line, out mw, out o, out n)) break;
                    if (MarkdownTableReader.IsDelimiterRow(line)) break;
                }

                if (text.Length > 0) text.Append('\n');
                // Leading whitespace goes; TRAILING whitespace only goes when it is not a hard
                // line break. `trimmed` cannot be used here — trimming both ends destroys the two
                // trailing spaces that ARE the break, and the inline parser then has nothing to see.
                text.Append(PreserveHardBreak(line));
                i++;
            }

            if (text.Length > 0)
            {
                into.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph)
                {
                    Literal = text.ToString(),
                    SourceLine = index + 1,
                });
            }
            return i > index ? i : index + 1;
        }

        /// <summary>
        /// Strips leading whitespace, and trailing whitespace unless it is a hard line break
        /// (two or more spaces), which is normalised to exactly two so the inline parser has one
        /// shape to recognise.
        /// </summary>
        private static string PreserveHardBreak(string line)
        {
            string s = line.TrimStart(' ', '\t');
            int trailing = 0;
            while (trailing < s.Length && s[s.Length - 1 - trailing] == ' ') trailing++;
            return trailing >= 2 ? s.Substring(0, s.Length - trailing) + "  " : s.TrimEnd();
        }

        private static bool IsSetextUnderline(string trimmed)
        {
            if (trimmed.Length == 0) return false;
            char c = trimmed[0];
            if (c != '=' && c != '-') return false;
            for (int i = 0; i < trimmed.Length; i++) if (trimmed[i] != c) return false;
            return true;
        }

        // ---- inline resolution -----------------------------------------------------------------------

        /// <summary>
        /// Walks the finished block tree and parses each block's stored text into inlines.
        ///
        /// Separate from block parsing so the whole document's link reference definitions are known
        /// before any inline is resolved — a forward reference otherwise resolves to nothing.
        /// </summary>
        private void ResolveInlines(MarkdownBlock block, MarkdownDocument document)
        {
            bool inlineBearing = block.Kind == MarkdownBlockKind.Heading
                              || block.Kind == MarkdownBlockKind.Paragraph
                              || block.Kind == MarkdownBlockKind.TableCell;

            if (inlineBearing && block.Literal != null)
            {
                foreach (MarkdownInline run in _inline.Parse(block.Literal, document))
                    block.Inlines.Add(run);
            }
            foreach (MarkdownBlock child in block.Children) ResolveInlines(child, document);
        }
    }
}
