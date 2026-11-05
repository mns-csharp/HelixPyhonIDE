using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Markdown
{
    /// <summary>
    /// Inline (span-level) Markdown: code spans, emphasis, strong, strikethrough, links, images,
    /// autolinks, hard line breaks, backslash escapes and raw inline HTML.
    ///
    /// <b>Order of operations is the whole design.</b> Code spans are matched FIRST and their
    /// contents are never looked at again, because a backtick span is the one construct that
    /// suppresses every other. Get that ordering wrong and the README that motivated this engine
    /// renders `_xxx_kernel = None` with the middle word italicised — which is exactly the class of
    /// bug that makes a renderer feel untrustworthy on technical documents.
    /// </summary>
    internal sealed class MarkdownInlineParser
    {
        public List<MarkdownInline> Parse(string text, MarkdownDocument document)
        {
            var result = new List<MarkdownInline>();
            if (string.IsNullOrEmpty(text)) return result;
            ParseInto(text, 0, text.Length, document, result);
            return result;
        }

        private void ParseInto(string text, int from, int to, MarkdownDocument document, List<MarkdownInline> into)
        {
            var literal = new StringBuilder();
            int i = from;

            while (i < to)
            {
                char c = text[i];

                // Backslash escape: the next punctuation character is literal.
                if (c == '\\' && i + 1 < to && IsEscapable(text[i + 1]))
                {
                    literal.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                // Hard line break: two or more trailing spaces, or a backslash, before a newline.
                if (c == '\n')
                {
                    bool hard = literal.Length >= 2
                        && literal[literal.Length - 1] == ' ' && literal[literal.Length - 2] == ' ';
                    if (hard) while (literal.Length > 0 && literal[literal.Length - 1] == ' ') literal.Length--;
                    Flush(literal, into);
                    into.Add(new MarkdownInline(hard ? MarkdownInlineKind.LineBreak : MarkdownInlineKind.SoftBreak));
                    i++;
                    continue;
                }

                if (c == '`')
                {
                    int consumed = TryCodeSpan(text, i, to, literal, into);
                    if (consumed > 0) { i += consumed; continue; }
                }

                if (c == '<')
                {
                    int consumed = TryAutolinkOrHtml(text, i, to, literal, into);
                    if (consumed > 0) { i += consumed; continue; }
                }

                if (c == '!' && i + 1 < to && text[i + 1] == '[')
                {
                    int consumed = TryLink(text, i + 1, to, document, literal, into, isImage: true);
                    if (consumed > 0) { i += consumed + 1; continue; }
                }

                if (c == '[')
                {
                    int consumed = TryLink(text, i, to, document, literal, into, isImage: false);
                    if (consumed > 0) { i += consumed; continue; }
                }

                if (c == '~' && i + 1 < to && text[i + 1] == '~')
                {
                    int consumed = TryDelimited(text, i, to, "~~", MarkdownInlineKind.Strikethrough, document, literal, into);
                    if (consumed > 0) { i += consumed; continue; }
                }

                if (c == '*' || c == '_')
                {
                    // Strong before emphasis: `**x**` must not be read as an empty emphasis
                    // containing an emphasis.
                    string doubled = new string(c, 2);
                    if (i + 1 < to && text[i + 1] == c)
                    {
                        int consumed = TryDelimited(text, i, to, doubled, MarkdownInlineKind.Strong, document, literal, into);
                        if (consumed > 0) { i += consumed; continue; }
                    }
                    if (CanOpenEmphasis(text, i, to, c))
                    {
                        int consumed = TryDelimited(text, i, to, c.ToString(), MarkdownInlineKind.Emphasis, document, literal, into);
                        if (consumed > 0) { i += consumed; continue; }
                    }
                }

                literal.Append(c);
                i++;
            }

            Flush(literal, into);
        }

        private static void Flush(StringBuilder literal, List<MarkdownInline> into)
        {
            if (literal.Length == 0) return;
            into.Add(new MarkdownInline(MarkdownInlineKind.Text, literal.ToString()));
            literal.Length = 0;
        }

        private static bool IsEscapable(char c)
        {
            return "\\`*_{}[]()#+-.!|<>~\"'".IndexOf(c) >= 0;
        }

        // ---- code spans ------------------------------------------------------------------------

        /// <summary>
        /// Matches a code span opened by a run of N backticks and closed by the next run of exactly
        /// N. Returns the characters consumed, or 0 when there is no closer (in which case the
        /// backticks are literal text, as the spec requires).
        /// </summary>
        private static int TryCodeSpan(string text, int start, int to, StringBuilder literal, List<MarkdownInline> into)
        {
            int open = 0;
            while (start + open < to && text[start + open] == '`') open++;

            int i = start + open;
            while (i < to)
            {
                if (text[i] != '`') { i++; continue; }
                int run = 0;
                while (i + run < to && text[i + run] == '`') run++;
                if (run == open)
                {
                    string content = text.Substring(start + open, i - (start + open));
                    // One leading and trailing space is stripped, so `` ` `` can hold a backtick.
                    if (content.Length >= 2 && content[0] == ' ' && content[content.Length - 1] == ' '
                        && content.Trim().Length > 0)
                        content = content.Substring(1, content.Length - 2);
                    // A code span's contents are literal, INCLUDING newlines, which become spaces.
                    content = content.Replace('\n', ' ');

                    Flush(literal, into);
                    into.Add(new MarkdownInline(MarkdownInlineKind.Code, content));
                    return (i + run) - start;
                }
                i += run;
            }
            return 0;
        }

        // ---- autolinks and raw HTML ----------------------------------------------------------------

        private static int TryAutolinkOrHtml(string text, int start, int to, StringBuilder literal, List<MarkdownInline> into)
        {
            int close = text.IndexOf('>', start + 1);
            if (close < 0 || close >= to) return 0;
            string inner = text.Substring(start + 1, close - start - 1);
            if (inner.Length == 0) return 0;

            if (LooksLikeUri(inner) || LooksLikeEmail(inner))
            {
                Flush(literal, into);
                var link = new MarkdownInline(MarkdownInlineKind.Link)
                {
                    Destination = LooksLikeEmail(inner) ? "mailto:" + inner : inner,
                };
                link.Children.Add(new MarkdownInline(MarkdownInlineKind.Text, inner));
                into.Add(link);
                return close - start + 1;
            }

            // A tag. Kept as raw HTML rather than dropped: the renderer decides whether to show it,
            // and silently deleting content a user typed is worse than showing it verbatim.
            if (char.IsLetter(inner[0]) || inner[0] == '/' || inner[0] == '!')
            {
                Flush(literal, into);
                into.Add(new MarkdownInline(MarkdownInlineKind.Html, text.Substring(start, close - start + 1)));
                return close - start + 1;
            }
            return 0;
        }

        private static bool LooksLikeUri(string s)
        {
            int colon = s.IndexOf(':');
            if (colon <= 0) return false;
            for (int i = 0; i < colon; i++)
            {
                char c = s[i];
                if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
            }
            return s.IndexOf(' ') < 0;
        }

        private static bool LooksLikeEmail(string s)
        {
            int at = s.IndexOf('@');
            return at > 0 && at < s.Length - 1 && s.IndexOf(' ') < 0 && s.IndexOf(':') < 0
                && s.IndexOf('.', at) > at;
        }

        // ---- links and images -------------------------------------------------------------------

        /// <summary>
        /// Parses <c>[label](dest "title")</c>, <c>[label][ref]</c>, <c>[label][]</c> and
        /// <c>[label]</c> (shortcut reference). Returns characters consumed from
        /// <paramref name="start"/> (the <c>[</c>), or 0 if this is not a link after all.
        /// </summary>
        private int TryLink(string text, int start, int to, MarkdownDocument document,
                            StringBuilder literal, List<MarkdownInline> into, bool isImage)
        {
            int labelEnd = FindClosingBracket(text, start, to);
            if (labelEnd < 0) return 0;
            string label = text.Substring(start + 1, labelEnd - start - 1);

            string destination = null, title = null;
            int end;

            if (labelEnd + 1 < to && text[labelEnd + 1] == '(')
            {
                int paren = FindClosingParen(text, labelEnd + 1, to);
                if (paren < 0) return 0;
                SplitDestinationAndTitle(text.Substring(labelEnd + 2, paren - labelEnd - 2), out destination, out title);
                end = paren + 1;
            }
            else if (labelEnd + 1 < to && text[labelEnd + 1] == '[')
            {
                int refEnd = FindClosingBracket(text, labelEnd + 1, to);
                if (refEnd < 0) return 0;
                string key = text.Substring(labelEnd + 2, refEnd - labelEnd - 2).Trim();
                if (key.Length == 0) key = label.Trim();
                MarkdownLinkReference reference;
                if (!document.LinkReferences.TryGetValue(key, out reference)) return 0;
                destination = reference.Destination;
                title = reference.Title;
                end = refEnd + 1;
            }
            else
            {
                // Shortcut reference. Only a link if the label actually resolves — otherwise
                // `[TODO]` in prose would become a dead link.
                MarkdownLinkReference reference;
                if (!document.LinkReferences.TryGetValue(label.Trim(), out reference)) return 0;
                destination = reference.Destination;
                title = reference.Title;
                end = labelEnd + 1;
            }

            Flush(literal, into);
            var node = new MarkdownInline(isImage ? MarkdownInlineKind.Image : MarkdownInlineKind.Link)
            {
                Destination = destination,
                Title = title,
            };
            if (isImage) node.Text = label;
            else ParseInto(label, 0, label.Length, document, node.Children);
            into.Add(node);
            return end - start;
        }

        /// <summary>Finds the <c>]</c> matching the <c>[</c> at <paramref name="start"/>, honouring nesting.</summary>
        private static int FindClosingBracket(string text, int start, int to)
        {
            int depth = 0;
            for (int i = start; i < to; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static int FindClosingParen(string text, int start, int to)
        {
            int depth = 0;
            for (int i = start; i < to; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static void SplitDestinationAndTitle(string inside, out string destination, out string title)
        {
            destination = null; title = null;
            string s = inside.Trim();
            if (s.Length == 0) { destination = string.Empty; return; }

            if (s[0] == '<')
            {
                int close = s.IndexOf('>');
                if (close > 0)
                {
                    destination = s.Substring(1, close - 1);
                    s = s.Substring(close + 1).Trim();
                    title = ExtractTitle(s);
                    return;
                }
            }

            int space = IndexOfUnescapedSpace(s);
            if (space < 0) { destination = s; return; }
            destination = s.Substring(0, space);
            title = ExtractTitle(s.Substring(space + 1).Trim());
        }

        private static int IndexOfUnescapedSpace(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == ' ' || s[i] == '\t') return i;
            }
            return -1;
        }

        private static string ExtractTitle(string s)
        {
            if (s.Length < 2) return null;
            char open = s[0];
            char close = open == '(' ? ')' : open;
            if ((open == '"' || open == '\'' || open == '(') && s[s.Length - 1] == close)
                return s.Substring(1, s.Length - 2);
            return null;
        }

        // ---- emphasis --------------------------------------------------------------------------

        /// <summary>
        /// Matches a run delimited by <paramref name="delimiter"/> and parses its contents
        /// recursively. Returns characters consumed, or 0 when unmatched.
        /// </summary>
        private int TryDelimited(string text, int start, int to, string delimiter, MarkdownInlineKind kind,
                                 MarkdownDocument document, StringBuilder literal, List<MarkdownInline> into)
        {
            int contentStart = start + delimiter.Length;
            if (contentStart >= to) return 0;
            // `** **` is not strong: an emphasis run may not open on whitespace.
            if (text[contentStart] == ' ' || text[contentStart] == '\n') return 0;

            int i = contentStart;
            while (i < to)
            {
                if (text[i] == '\\') { i += 2; continue; }
                // A code span inside emphasis is skipped whole, so a backtick run containing the
                // delimiter cannot close it: `*a `b*c` d*` must not end at the middle asterisk.
                if (text[i] == '`')
                {
                    int skip = SkipCodeSpan(text, i, to);
                    if (skip > 0) { i += skip; continue; }
                }
                if (Matches(text, i, to, delimiter) && text[i - 1] != ' ')
                {
                    string content = text.Substring(contentStart, i - contentStart);
                    if (content.Length == 0) return 0;
                    Flush(literal, into);
                    var node = new MarkdownInline(kind);
                    ParseInto(content, 0, content.Length, document, node.Children);
                    into.Add(node);
                    return (i + delimiter.Length) - start;
                }
                i++;
            }
            return 0;
        }

        private static int SkipCodeSpan(string text, int start, int to)
        {
            int open = 0;
            while (start + open < to && text[start + open] == '`') open++;
            int i = start + open;
            while (i < to)
            {
                if (text[i] != '`') { i++; continue; }
                int run = 0;
                while (i + run < to && text[i + run] == '`') run++;
                if (run == open) return (i + run) - start;
                i += run;
            }
            return 0;
        }

        private static bool Matches(string text, int at, int to, string delimiter)
        {
            if (at + delimiter.Length > to) return false;
            for (int i = 0; i < delimiter.Length; i++)
                if (text[at + i] != delimiter[i]) return false;
            // A doubled delimiter is a different construct; `*` must not match the first half of `**`.
            if (at + delimiter.Length < to && text[at + delimiter.Length] == delimiter[0]
                && delimiter.Length == 1) return false;
            return true;
        }

        /// <summary>
        /// Whether a <c>*</c>/<c>_</c> at this position may open emphasis.
        ///
        /// The <c>_</c> case is the one that matters in this repository: intra-word underscores are
        /// NOT emphasis, or every <c>snake_case_identifier</c> in a document turns into italics with
        /// the middle word eaten. <c>*</c> keeps the looser rule because <c>a*b*c</c> is genuinely
        /// emphasis in the spec.
        /// </summary>
        private static bool CanOpenEmphasis(string text, int at, int to, char delimiter)
        {
            if (at + 1 >= to) return false;
            char next = text[at + 1];
            if (next == ' ' || next == '\n' || next == delimiter) return false;
            if (delimiter != '_') return true;
            if (at == 0) return true;
            char previous = text[at - 1];
            return !char.IsLetterOrDigit(previous);
        }
    }
}
