using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Markdown
{
    /// <summary>
    /// Serialises a parsed document to HTML.
    ///
    /// Two jobs, and the second is the reason it exists at all:
    ///
    /// <list type="bullet">
    /// <item>it is what File ▸ Export needs to turn a README into something shareable;</item>
    /// <item>it is the parser's <b>test oracle</b>. Asserting on a block tree means writing an
    /// assertion per node kind and getting a failure message that says "expected 3 children, got 2";
    /// asserting on HTML means comparing one string against the output every other Markdown
    /// implementation on earth agrees on, and getting a diff. Every parser test in this engine reads
    /// as input Markdown → expected HTML for that reason.</item>
    /// </list>
    ///
    /// Escaping is unconditional. Text nodes, code spans, code blocks, link destinations and titles
    /// are all escaped on the way out; the only content that passes through raw is what the parser
    /// explicitly classified as HTML, which is a decision the caller can turn off with
    /// <see cref="AllowRawHtml"/>.
    /// </summary>
    public sealed class MarkdownHtmlWriter
    {
        /// <summary>
        /// When false, an HTML block or inline HTML tag is emitted as escaped TEXT rather than as
        /// markup. Default false: rendering a document is not a reason to execute whatever is in it,
        /// and a Markdown file is content the IDE opened from disk, not something the user authored
        /// in this session.
        /// </summary>
        public bool AllowRawHtml { get; set; }

        public static string ToHtml(string markdown)
        {
            return new MarkdownHtmlWriter().Write(MarkdownParser.Parse(markdown));
        }

        public string Write(MarkdownDocument document)
        {
            var sb = new StringBuilder();
            if (document != null)
                foreach (MarkdownBlock block in document.Root.Children) WriteBlock(block, sb);
            return sb.ToString();
        }

        private void WriteBlock(MarkdownBlock block, StringBuilder sb)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    sb.Append("<h").Append(block.Level).Append('>');
                    WriteInlines(block.Inlines, sb);
                    sb.Append("</h").Append(block.Level).Append(">\n");
                    break;

                case MarkdownBlockKind.Paragraph:
                    sb.Append("<p>");
                    WriteInlines(block.Inlines, sb);
                    sb.Append("</p>\n");
                    break;

                case MarkdownBlockKind.CodeBlock:
                    sb.Append("<pre><code");
                    if (!string.IsNullOrEmpty(block.InfoString))
                    {
                        // Only the first word of the info string is the language; the rest is
                        // metadata some tools put there and none of it belongs in a class name.
                        string language = block.InfoString;
                        int space = language.IndexOf(' ');
                        if (space > 0) language = language.Substring(0, space);
                        sb.Append(" class=\"language-").Append(Escape(language)).Append('"');
                    }
                    sb.Append('>').Append(Escape(block.Literal ?? string.Empty)).Append("</code></pre>\n");
                    break;

                case MarkdownBlockKind.ThematicBreak:
                    sb.Append("<hr />\n");
                    break;

                case MarkdownBlockKind.BlockQuote:
                    sb.Append("<blockquote>\n");
                    foreach (MarkdownBlock child in block.Children) WriteBlock(child, sb);
                    sb.Append("</blockquote>\n");
                    break;

                case MarkdownBlockKind.List:
                    WriteList(block, sb);
                    break;

                case MarkdownBlockKind.ListItem:
                    WriteListItem(block, sb, loose: true);
                    break;

                case MarkdownBlockKind.Table:
                    WriteTable(block, sb);
                    break;

                case MarkdownBlockKind.HtmlBlock:
                    if (AllowRawHtml) sb.Append(block.Literal).Append('\n');
                    else sb.Append("<p>").Append(Escape(block.Literal ?? string.Empty)).Append("</p>\n");
                    break;
            }
        }

        private void WriteList(MarkdownBlock list, StringBuilder sb)
        {
            string tag = list.IsOrdered ? "ol" : "ul";
            sb.Append('<').Append(tag);
            if (list.IsOrdered && list.StartNumber != 1) sb.Append(" start=\"").Append(list.StartNumber).Append('"');
            sb.Append(">\n");
            foreach (MarkdownBlock item in list.Children) WriteListItem(item, sb, list.IsLoose);
            sb.Append("</").Append(tag).Append(">\n");
        }

        /// <summary>
        /// In a TIGHT list, EVERY paragraph directly inside an item is unwrapped — not only an
        /// item that consists of nothing else.
        ///
        /// The "only child" version looks right until the first nested list: <c>- a</c> with an
        /// indented <c>- b</c> under it gives the item two children, so the paragraph got wrapped
        /// and one bullet in a nested list rendered with different spacing from its siblings. That
        /// is the whole visible difference between tight and loose, applied to the wrong item.
        /// </summary>
        private void WriteListItem(MarkdownBlock item, StringBuilder sb, bool loose)
        {
            sb.Append("<li>");
            if (item.IsChecked.HasValue)
                sb.Append("<input type=\"checkbox\" disabled=\"\"")
                  .Append(item.IsChecked.Value ? " checked=\"\"" : string.Empty).Append(" /> ");

            bool wroteInline = false;
            for (int i = 0; i < item.Children.Count; i++)
            {
                MarkdownBlock child = item.Children[i];
                if (!loose && child.Kind == MarkdownBlockKind.Paragraph)
                {
                    if (i > 0) sb.Append('\n');
                    WriteInlines(child.Inlines, sb);
                    wroteInline = true;
                    continue;
                }
                if (i == 0 || wroteInline) sb.Append('\n');
                WriteBlock(child, sb);
                wroteInline = false;
            }
            sb.Append("</li>\n");
        }

        private void WriteTable(MarkdownBlock table, StringBuilder sb)
        {
            sb.Append("<table>\n");
            bool headerWritten = false;
            bool bodyOpen = false;

            foreach (MarkdownBlock row in table.Children)
            {
                if (row.IsHeader && !headerWritten)
                {
                    sb.Append("<thead>\n");
                    WriteTableRow(table, row, sb, "th");
                    sb.Append("</thead>\n");
                    headerWritten = true;
                    continue;
                }
                if (!bodyOpen) { sb.Append("<tbody>\n"); bodyOpen = true; }
                WriteTableRow(table, row, sb, "td");
            }
            if (bodyOpen) sb.Append("</tbody>\n");
            sb.Append("</table>\n");
        }

        private void WriteTableRow(MarkdownBlock table, MarkdownBlock row, StringBuilder sb, string cellTag)
        {
            sb.Append("<tr>\n");
            for (int c = 0; c < row.Children.Count; c++)
            {
                sb.Append('<').Append(cellTag);
                MarkdownAlignment alignment = c < table.ColumnAlignments.Count
                    ? table.ColumnAlignments[c] : MarkdownAlignment.None;
                if (alignment != MarkdownAlignment.None)
                    sb.Append(" style=\"text-align: ").Append(alignment.ToString().ToLowerInvariant()).Append(";\"");
                sb.Append('>');
                WriteInlines(row.Children[c].Inlines, sb);
                sb.Append("</").Append(cellTag).Append(">\n");
            }
            sb.Append("</tr>\n");
        }

        private void WriteInlines(List<MarkdownInline> inlines, StringBuilder sb)
        {
            foreach (MarkdownInline inline in inlines) WriteInline(inline, sb);
        }

        private void WriteInline(MarkdownInline inline, StringBuilder sb)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text: sb.Append(Escape(inline.Text)); break;
                case MarkdownInlineKind.Code: sb.Append("<code>").Append(Escape(inline.Text)).Append("</code>"); break;
                case MarkdownInlineKind.SoftBreak: sb.Append('\n'); break;
                case MarkdownInlineKind.LineBreak: sb.Append("<br />\n"); break;

                case MarkdownInlineKind.Emphasis:
                    sb.Append("<em>"); WriteInlines(inline.Children, sb); sb.Append("</em>"); break;
                case MarkdownInlineKind.Strong:
                    sb.Append("<strong>"); WriteInlines(inline.Children, sb); sb.Append("</strong>"); break;
                case MarkdownInlineKind.Strikethrough:
                    sb.Append("<del>"); WriteInlines(inline.Children, sb); sb.Append("</del>"); break;

                case MarkdownInlineKind.Link:
                    sb.Append("<a href=\"").Append(EscapeAttribute(inline.Destination)).Append('"');
                    if (!string.IsNullOrEmpty(inline.Title))
                        sb.Append(" title=\"").Append(EscapeAttribute(inline.Title)).Append('"');
                    sb.Append('>');
                    WriteInlines(inline.Children, sb);
                    sb.Append("</a>");
                    break;

                case MarkdownInlineKind.Image:
                    sb.Append("<img src=\"").Append(EscapeAttribute(inline.Destination))
                      .Append("\" alt=\"").Append(EscapeAttribute(inline.Text)).Append('"');
                    if (!string.IsNullOrEmpty(inline.Title))
                        sb.Append(" title=\"").Append(EscapeAttribute(inline.Title)).Append('"');
                    sb.Append(" />");
                    break;

                case MarkdownInlineKind.Html:
                    if (AllowRawHtml) sb.Append(inline.Text);
                    else sb.Append(Escape(inline.Text));
                    break;
            }
        }

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string EscapeAttribute(string s)
        {
            return Escape(s).Replace("\"", "&quot;");
        }
    }
}
