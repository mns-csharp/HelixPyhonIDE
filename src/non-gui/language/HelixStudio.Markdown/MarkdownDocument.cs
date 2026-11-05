using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Markdown
{
    /// <summary>The block-level kinds this engine produces.</summary>
    public enum MarkdownBlockKind
    {
        Document,
        Heading,
        Paragraph,
        CodeBlock,
        BlockQuote,
        List,
        ListItem,
        ThematicBreak,
        Table,
        TableRow,
        TableCell,
        HtmlBlock,
    }

    /// <summary>The inline-level kinds this engine produces.</summary>
    public enum MarkdownInlineKind
    {
        Text,
        Emphasis,
        Strong,
        Strikethrough,
        Code,
        Link,
        Image,
        LineBreak,
        SoftBreak,
        Html,
    }

    /// <summary>Column alignment declared by a GFM table's delimiter row.</summary>
    public enum MarkdownAlignment { None, Left, Center, Right }

    /// <summary>
    /// A run of inline content. A tree, not a flat list: emphasis nests, and a link's label is
    /// itself inline content — <c>[**bold** text](url)</c> has to survive as a link containing a
    /// strong containing text, or the renderer cannot style it.
    /// </summary>
    public sealed class MarkdownInline
    {
        public MarkdownInline(MarkdownInlineKind kind, string text = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            Children = new List<MarkdownInline>();
        }

        public MarkdownInlineKind Kind { get; }

        /// <summary>Literal text for <see cref="MarkdownInlineKind.Text"/>, <see cref="MarkdownInlineKind.Code"/>
        /// and <see cref="MarkdownInlineKind.Html"/>; the alt text for an image.</summary>
        public string Text { get; set; }

        /// <summary>Destination for a link or image. Null otherwise.</summary>
        public string Destination { get; set; }

        /// <summary>Title attribute for a link or image (the <c>"…"</c> after the destination).</summary>
        public string Title { get; set; }

        public List<MarkdownInline> Children { get; }

        /// <summary>The visible text of this run and everything under it, with no markup.</summary>
        public string ToPlainText()
        {
            if (Children.Count == 0) return Text;
            var sb = new System.Text.StringBuilder();
            AppendPlainText(sb);
            return sb.ToString();
        }

        private void AppendPlainText(System.Text.StringBuilder sb)
        {
            if (Children.Count == 0) { sb.Append(Text); return; }
            foreach (MarkdownInline child in Children) child.AppendPlainText(sb);
        }
    }

    /// <summary>
    /// A block-level node.
    ///
    /// One type for every block kind rather than a class hierarchy: the renderer switches on
    /// <see cref="Kind"/> anyway, and the alternative (a dozen tiny subclasses plus a visitor) buys
    /// nothing at this size while making the layout pass harder to read.
    /// </summary>
    public sealed class MarkdownBlock
    {
        public MarkdownBlock(MarkdownBlockKind kind)
        {
            Kind = kind;
            Children = new List<MarkdownBlock>();
            Inlines = new List<MarkdownInline>();
            ColumnAlignments = new List<MarkdownAlignment>();
        }

        public MarkdownBlockKind Kind { get; }

        /// <summary>Child blocks — list items, blockquote contents, table rows, cells.</summary>
        public List<MarkdownBlock> Children { get; }

        /// <summary>Inline content, for headings, paragraphs and table cells.</summary>
        public List<MarkdownInline> Inlines { get; }

        /// <summary>1–6 for <see cref="MarkdownBlockKind.Heading"/>.</summary>
        public int Level { get; set; }

        /// <summary>Raw text for a code block or HTML block — never inline-parsed.</summary>
        public string Literal { get; set; }

        /// <summary>The info string of a fenced code block (<c>```python</c> → <c>python</c>).</summary>
        public string InfoString { get; set; }

        /// <summary>True when a <see cref="MarkdownBlockKind.List"/> is ordered.</summary>
        public bool IsOrdered { get; set; }

        /// <summary>The first number of an ordered list, so <c>3.</c> starts at three.</summary>
        public int StartNumber { get; set; } = 1;

        /// <summary>
        /// A list is loose when any item is separated by a blank line; loose items render their
        /// contents as paragraphs with spacing, tight ones do not. Getting this wrong is the most
        /// visible way a Markdown renderer looks wrong.
        /// </summary>
        public bool IsLoose { get; set; }

        /// <summary>Task-list state: null when the item is not a checkbox item.</summary>
        public bool? IsChecked { get; set; }

        /// <summary>True for a table's header row.</summary>
        public bool IsHeader { get; set; }

        /// <summary>Per-column alignment for a <see cref="MarkdownBlockKind.Table"/>.</summary>
        public List<MarkdownAlignment> ColumnAlignments { get; }

        /// <summary>1-based line in the source this block started on, for source↔preview sync.</summary>
        public int SourceLine { get; set; }
    }

    /// <summary>
    /// A parsed Markdown document: the root block plus the link reference definitions collected
    /// while parsing.
    /// </summary>
    public sealed class MarkdownDocument
    {
        public MarkdownDocument()
        {
            Root = new MarkdownBlock(MarkdownBlockKind.Document);
            LinkReferences = new Dictionary<string, MarkdownLinkReference>(StringComparer.OrdinalIgnoreCase);
        }

        public MarkdownBlock Root { get; }

        /// <summary>
        /// <c>[label]: /url "title"</c> definitions, keyed case-insensitively as the spec requires.
        /// </summary>
        public Dictionary<string, MarkdownLinkReference> LinkReferences { get; }

        /// <summary>
        /// The document's title: the text of the first level-1 heading, or null. Used for the
        /// preview tab caption, so a README shows its own name rather than "README.md".
        /// </summary>
        public string Title
        {
            get
            {
                foreach (MarkdownBlock block in Root.Children)
                {
                    if (block.Kind != MarkdownBlockKind.Heading || block.Level != 1) continue;
                    var sb = new System.Text.StringBuilder();
                    foreach (MarkdownInline inline in block.Inlines) sb.Append(inline.ToPlainText());
                    string text = sb.ToString().Trim();
                    return text.Length == 0 ? null : text;
                }
                return null;
            }
        }
    }

    /// <summary>A resolved <c>[label]: destination "title"</c> definition.</summary>
    public sealed class MarkdownLinkReference
    {
        public MarkdownLinkReference(string destination, string title)
        {
            Destination = destination;
            Title = title;
        }

        public string Destination { get; }
        public string Title { get; }
    }
}
