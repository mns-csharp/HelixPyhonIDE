using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Markdown;

namespace HelixPythonNamespace.Editor.Markdown
{
    /// <summary>
    /// Turns a parsed <see cref="MarkdownDocument"/> into positioned <see cref="MarkdownVisual"/>s.
    ///
    /// A real layout pass, not a rich-text conversion: it wraps inline content to the available
    /// width, measures table columns against their natural content widths and scales them to fit,
    /// nests lists and blockquotes by indentation, and lays code blocks out unwrapped so the
    /// horizontal scrollbar — rather than a reflow — deals with a long line. Nothing here paints;
    /// the control walks the result.
    ///
    /// Measurement goes through <see cref="TextRenderer"/> (GDI), not <c>Graphics.MeasureString</c>
    /// (GDI+), because the control paints with <see cref="TextRenderer.DrawText(IDeviceContext,
    /// string, Font, Point, Color)"/> and the two engines disagree about width by a few pixels per
    /// run. Measuring with one and drawing with the other is how wrapped text ends up a word short
    /// of the right margin on some lines and clipped on others.
    /// </summary>
    public sealed class MarkdownLayoutEngine
    {
        private const TextFormatFlags MeasureFlags =
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        private readonly MarkdownStyle _style;
        private List<MarkdownVisual> _visuals;
        private Graphics _graphics;
        private int _contentWidth;

        public MarkdownLayoutEngine(MarkdownStyle style)
        {
            _style = style ?? throw new ArgumentNullException(nameof(style));
        }

        /// <summary>
        /// Lays the document out for <paramref name="availableWidth"/> pixels of client area.
        /// <paramref name="totalSize"/> receives the full extent, which is what the host turns into
        /// its scroll range.
        /// </summary>
        public List<MarkdownVisual> Layout(MarkdownDocument document, Graphics graphics, int availableWidth,
                                           out Size totalSize)
        {
            _visuals = new List<MarkdownVisual>();
            _graphics = graphics;
            _contentWidth = Math.Max(80, availableWidth - _style.PagePadding * 2);

            int y = _style.PagePadding;
            if (document != null)
                y = LayoutBlocks(document.Root.Children, _style.PagePadding, _contentWidth, y, _style.Foreground);

            int widest = 0;
            foreach (MarkdownVisual v in _visuals) widest = Math.Max(widest, v.Bounds.Right);
            totalSize = new Size(widest + _style.PagePadding, y + _style.PagePadding);

            List<MarkdownVisual> result = _visuals;
            _visuals = null;
            _graphics = null;
            return result;
        }

        // ---- blocks ------------------------------------------------------------------------------

        private int LayoutBlocks(List<MarkdownBlock> blocks, int x, int width, int y, Color foreground)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                MarkdownBlock block = blocks[i];
                y = LayoutBlock(block, x, width, y, foreground);
                if (i < blocks.Count - 1) y += SpacingAfter(block);
            }
            return y;
        }

        private int SpacingAfter(MarkdownBlock block)
        {
            return block.Kind == MarkdownBlockKind.Paragraph ? _style.ParagraphSpacing : _style.BlockSpacing;
        }

        private int LayoutBlock(MarkdownBlock block, int x, int width, int y, Color foreground)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading: return LayoutHeading(block, x, width, y);
                case MarkdownBlockKind.Paragraph:
                    return FlowInlines(block.Inlines, x, width, y, _style.BodySize, false, false, foreground, block.SourceLine);
                case MarkdownBlockKind.CodeBlock: return LayoutCodeBlock(block, x, width, y);
                case MarkdownBlockKind.ThematicBreak: return LayoutThematicBreak(x, width, y);
                case MarkdownBlockKind.BlockQuote: return LayoutBlockQuote(block, x, width, y);
                case MarkdownBlockKind.List: return LayoutList(block, x, width, y, foreground);
                case MarkdownBlockKind.Table: return LayoutTable(block, x, width, y);
                case MarkdownBlockKind.HtmlBlock: return LayoutHtmlBlock(block, x, width, y);
                default: return y;
            }
        }

        private int LayoutHeading(MarkdownBlock block, int x, int width, int y)
        {
            float size = _style.HeadingSize(block.Level);
            y += block.Level <= 2 ? 6 : 2;
            int bottom = FlowInlines(block.Inlines, x, width, y, size, true, false,
                                     _style.HeadingForeground, block.SourceLine);

            // H1 and H2 get the underline every Markdown renderer draws; it is what makes a long
            // document scannable, and without it a preview of a README is a wall of bold text.
            if (block.Level <= 2)
            {
                bottom += 4;
                Add(new MarkdownVisual
                {
                    Kind = MarkdownVisualKind.Rule,
                    Bounds = new Rectangle(x, bottom, width, 1),
                    BackColor = _style.BorderColor,
                    SourceLine = block.SourceLine,
                });
                bottom += 1;
            }
            return bottom;
        }

        private int LayoutThematicBreak(int x, int width, int y)
        {
            y += 4;
            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Rule,
                Bounds = new Rectangle(x, y, width, 1),
                BackColor = _style.BorderColor,
            });
            return y + 5;
        }

        private int LayoutCodeBlock(MarkdownBlock block, int x, int width, int y)
        {
            Font font = _style.Code;
            string[] lines = (block.Literal ?? string.Empty).Split('\n');
            int lineHeight = MeasureHeight(font);

            int top = y;
            int textLeft = x + _style.CodePadding;
            int textTop = top + _style.CodePadding;
            int widest = 0;

            var runs = new List<MarkdownVisual>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                // Tabs are expanded here rather than left to GDI, which renders them as a single
                // space with TextFormatFlags.NoPadding — turning indented code into a straight edge.
                string line = ExpandTabs(lines[i]);
                int lineWidth = line.Length == 0 ? 0 : Measure(line, font).Width;
                widest = Math.Max(widest, lineWidth);
                runs.Add(new MarkdownVisual
                {
                    Kind = MarkdownVisualKind.Text,
                    Text = line,
                    Font = font,
                    ForeColor = _style.CodeBlockForeground,
                    Bounds = new Rectangle(textLeft, textTop + i * lineHeight, Math.Max(1, lineWidth), lineHeight),
                    SourceLine = block.SourceLine + i,
                });
            }

            int boxHeight = lines.Length * lineHeight + _style.CodePadding * 2;
            // The background spans the full column width even when the code is narrower — a box that
            // hugs the text reads as a quote, not as a code block. It also extends to cover an
            // overlong line so the block still looks like one region when scrolled horizontally.
            int boxWidth = Math.Max(width, widest + _style.CodePadding * 2);

            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Fill,
                Bounds = new Rectangle(x, top, boxWidth, boxHeight),
                BackColor = _style.CodeBackground,
                SourceLine = block.SourceLine,
            });
            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Rule,
                Bounds = new Rectangle(x, top, boxWidth, 1),
                BackColor = _style.BorderColor,
                SourceLine = block.SourceLine,
            });
            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Rule,
                Bounds = new Rectangle(x, top + boxHeight - 1, boxWidth, 1),
                BackColor = _style.BorderColor,
                SourceLine = block.SourceLine,
            });
            foreach (MarkdownVisual run in runs) Add(run);

            return top + boxHeight;
        }

        private static string ExpandTabs(string line)
        {
            if (line.IndexOf('\t') < 0) return line;
            var sb = new System.Text.StringBuilder(line.Length + 8);
            foreach (char c in line)
            {
                if (c != '\t') { sb.Append(c); continue; }
                int pad = 4 - (sb.Length % 4);
                sb.Append(' ', pad);
            }
            return sb.ToString();
        }

        private int LayoutHtmlBlock(MarkdownBlock block, int x, int width, int y)
        {
            // Raw HTML is shown as source, deliberately. Rendering it would mean implementing an
            // HTML engine, and silently hiding it would delete content the author wrote.
            var asCode = new MarkdownBlock(MarkdownBlockKind.CodeBlock)
            {
                Literal = block.Literal,
                SourceLine = block.SourceLine,
            };
            return LayoutCodeBlock(asCode, x, width, y);
        }

        private int LayoutBlockQuote(MarkdownBlock block, int x, int width, int y)
        {
            int top = y;
            int innerX = x + _style.QuoteIndent;
            int innerWidth = Math.Max(40, width - _style.QuoteIndent);
            int bottom = LayoutBlocks(block.Children, innerX, innerWidth, y + 2, _style.QuoteForeground);
            bottom += 2;

            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Fill,
                Bounds = new Rectangle(x, top, 3, Math.Max(1, bottom - top)),
                BackColor = _style.QuoteBar,
                SourceLine = block.SourceLine,
            });
            return bottom;
        }

        private int LayoutList(MarkdownBlock list, int x, int width, int y, Color foreground)
        {
            Font markerFont = _style.Body;
            int number = list.StartNumber;
            int innerX = x + _style.ListIndent;
            int innerWidth = Math.Max(40, width - _style.ListIndent);

            for (int i = 0; i < list.Children.Count; i++)
            {
                MarkdownBlock item = list.Children[i];
                int itemTop = y;

                string marker;
                if (item.IsChecked.HasValue) marker = item.IsChecked.Value ? "☑" : "☐";
                else if (list.IsOrdered) marker = (number++) + ".";
                else marker = "•";

                Size markerSize = Measure(marker, markerFont);
                Add(new MarkdownVisual
                {
                    Kind = MarkdownVisualKind.Text,
                    Text = marker,
                    Font = markerFont,
                    ForeColor = foreground,
                    // Right-aligned into the indent gutter so multi-digit ordinals stay flush and
                    // the item text starts at one column regardless of "9." vs "10.".
                    Bounds = new Rectangle(innerX - markerSize.Width - 6, itemTop, markerSize.Width, markerSize.Height),
                    SourceLine = item.SourceLine,
                });

                y = LayoutBlocks(item.Children, innerX, innerWidth, itemTop, foreground);
                if (y == itemTop) y = itemTop + markerSize.Height;   // an empty item still takes a row
                if (i < list.Children.Count - 1) y += list.IsLoose ? _style.ParagraphSpacing : 2;
            }
            return y;
        }

        // ---- tables --------------------------------------------------------------------------------

        private int LayoutTable(MarkdownBlock table, int x, int width, int y)
        {
            int columns = 0;
            foreach (MarkdownBlock row in table.Children) columns = Math.Max(columns, row.Children.Count);
            if (columns == 0) return y;

            int[] widths = ComputeColumnWidths(table, columns, width);

            int top = y;
            foreach (MarkdownBlock row in table.Children)
            {
                int rowTop = y;
                int cellX = x;
                int rowHeight = 0;

                // Two passes over the row: lay every cell out from the same top to discover the
                // tallest, then draw the shared background and gridlines behind them. A single pass
                // cannot do it — the row's height is not known until its last cell is measured.
                var cellTops = new List<int>();
                for (int c = 0; c < columns; c++)
                {
                    int cellWidth = widths[c];
                    int inner = Math.Max(8, cellWidth - _style.TableCellPadding * 2);
                    MarkdownBlock cell = c < row.Children.Count ? row.Children[c] : null;

                    int startIndex = _visuals.Count;
                    int bottom = rowTop + _style.TableCellPadding;
                    if (cell != null)
                        bottom = FlowInlines(cell.Inlines, cellX + _style.TableCellPadding, inner,
                                             rowTop + _style.TableCellPadding, _style.BodySize,
                                             row.IsHeader, false, _style.Foreground, cell.SourceLine,
                                             AlignmentFor(table, c), inner);
                    cellTops.Add(startIndex);
                    rowHeight = Math.Max(rowHeight, bottom - rowTop + _style.TableCellPadding);
                    cellX += cellWidth;
                }

                int totalWidth = 0;
                foreach (int w in widths) totalWidth += w;

                if (row.IsHeader)
                {
                    // Inserted BEFORE the cell text so the fill does not paint over it. The visuals
                    // list is painted in order, and this row's text is already in it.
                    _visuals.Insert(cellTops.Count > 0 ? cellTops[0] : _visuals.Count, new MarkdownVisual
                    {
                        Kind = MarkdownVisualKind.Fill,
                        Bounds = new Rectangle(x, rowTop, totalWidth, rowHeight),
                        BackColor = _style.TableHeaderBackground,
                        SourceLine = row.SourceLine,
                    });
                }

                // Gridlines: bottom of the row plus a separator before each column after the first.
                Add(new MarkdownVisual
                {
                    Kind = MarkdownVisualKind.Rule,
                    Bounds = new Rectangle(x, rowTop + rowHeight - 1, totalWidth, 1),
                    BackColor = _style.BorderColor,
                    SourceLine = row.SourceLine,
                });
                int lineX = x;
                for (int c = 0; c < columns - 1; c++)
                {
                    lineX += widths[c];
                    Add(new MarkdownVisual
                    {
                        Kind = MarkdownVisualKind.Rule,
                        Bounds = new Rectangle(lineX, rowTop, 1, rowHeight),
                        BackColor = _style.BorderColor,
                        SourceLine = row.SourceLine,
                    });
                }

                y = rowTop + rowHeight;
            }

            int tableWidth = 0;
            foreach (int w in widths) tableWidth += w;
            Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.Rule,
                Bounds = new Rectangle(x, top, tableWidth, 1),
                BackColor = _style.BorderColor,
                SourceLine = table.SourceLine,
            });
            return y;
        }

        /// <summary>
        /// Natural widths, scaled to fit.
        ///
        /// Each column is measured at the width its widest cell would like to be unwrapped; if the
        /// total exceeds the page, columns are shrunk in proportion to how much MORE than the
        /// minimum they asked for. Scaling everything uniformly instead would squeeze a two-character
        /// column as hard as a paragraph-length one, which is how tables end up with a column of
        /// single letters stacked vertically.
        /// </summary>
        private int[] ComputeColumnWidths(MarkdownBlock table, int columns, int available)
        {
            const int minimumColumn = 44;
            var natural = new int[columns];

            foreach (MarkdownBlock row in table.Children)
            {
                for (int c = 0; c < row.Children.Count && c < columns; c++)
                {
                    MarkdownBlock cell = row.Children[c];
                    Font font = _style.Get(row.IsHeader, false, false, false, _style.BodySize);
                    int textWidth = MeasureInlineWidth(cell.Inlines, font, row.IsHeader);
                    natural[c] = Math.Max(natural[c], textWidth + _style.TableCellPadding * 2);
                }
            }

            int total = 0;
            for (int c = 0; c < columns; c++)
            {
                natural[c] = Math.Max(minimumColumn, natural[c]);
                total += natural[c];
            }
            if (total <= available)
            {
                // Grow the last column to flush the table against the right margin when there is
                // room, so a narrow table does not leave a ragged gridline hanging in mid-page.
                natural[columns - 1] += available - total;
                return natural;
            }

            int slack = total - available;
            int shrinkable = 0;
            for (int c = 0; c < columns; c++) shrinkable += Math.Max(0, natural[c] - minimumColumn);
            if (shrinkable <= 0) return natural;

            for (int c = 0; c < columns && slack > 0; c++)
            {
                int room = Math.Max(0, natural[c] - minimumColumn);
                if (room == 0) continue;
                int take = (int)Math.Round((double)slack * room / shrinkable);
                take = Math.Min(take, room);
                natural[c] -= take;
            }
            return natural;
        }

        private int MeasureInlineWidth(List<MarkdownInline> inlines, Font font, bool bold)
        {
            var sb = new System.Text.StringBuilder();
            foreach (MarkdownInline inline in inlines) sb.Append(inline.ToPlainText());
            string text = sb.ToString();
            if (text.Length == 0) return 0;
            return Measure(text, font).Width;
        }

        private static MarkdownAlignment AlignmentFor(MarkdownBlock table, int column)
        {
            return column < table.ColumnAlignments.Count ? table.ColumnAlignments[column] : MarkdownAlignment.None;
        }

        // ---- inline flow -----------------------------------------------------------------------------

        private int FlowInlines(List<MarkdownInline> inlines, int x, int width, int y, float size,
                                bool bold, bool italic, Color foreground, int sourceLine,
                                MarkdownAlignment alignment = MarkdownAlignment.None, int alignWidth = 0)
        {
            var line = new List<MarkdownVisual>();
            int penX = x;
            int lineHeight = 0;
            int right = x + width;

            FlowState state = new FlowState
            {
                X = x, Right = right, PenX = penX, Y = y, LineHeight = lineHeight,
                Foreground = foreground, SourceLine = sourceLine, Line = line,
                Alignment = alignment, AlignWidth = alignWidth,
            };

            foreach (MarkdownInline inline in inlines)
                EmitInline(inline, ref state, size, bold, italic, false, false, null);

            FlushLine(ref state);
            return state.Y;
        }

        private struct FlowState
        {
            public int X, Right, PenX, Y, LineHeight, SourceLine, AlignWidth;
            public Color Foreground;
            public List<MarkdownVisual> Line;
            public MarkdownAlignment Alignment;
        }

        private void EmitInline(MarkdownInline inline, ref FlowState state, float size,
                                bool bold, bool italic, bool strike, bool code, string linkTarget)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text:
                    EmitWords(inline.Text, ref state, size, bold, italic, strike, code, linkTarget);
                    break;

                case MarkdownInlineKind.Code:
                    EmitWords(inline.Text, ref state, size, bold, italic, strike, true, linkTarget);
                    break;

                case MarkdownInlineKind.Html:
                    // Shown verbatim in code styling — see LayoutHtmlBlock for why it is not hidden.
                    EmitWords(inline.Text, ref state, size, bold, italic, strike, true, linkTarget);
                    break;

                case MarkdownInlineKind.SoftBreak:
                    // A soft break is a space, not a line break: Markdown reflows paragraphs, and
                    // breaking here would reproduce the source file's hard wrap in the preview.
                    EmitWords(" ", ref state, size, bold, italic, strike, code, linkTarget);
                    break;

                case MarkdownInlineKind.LineBreak:
                    FlushLine(ref state);
                    break;

                case MarkdownInlineKind.Emphasis:
                    foreach (MarkdownInline child in inline.Children)
                        EmitInline(child, ref state, size, bold, true, strike, code, linkTarget);
                    break;

                case MarkdownInlineKind.Strong:
                    foreach (MarkdownInline child in inline.Children)
                        EmitInline(child, ref state, size, true, italic, strike, code, linkTarget);
                    break;

                case MarkdownInlineKind.Strikethrough:
                    foreach (MarkdownInline child in inline.Children)
                        EmitInline(child, ref state, size, bold, italic, true, code, linkTarget);
                    break;

                case MarkdownInlineKind.Link:
                    foreach (MarkdownInline child in inline.Children)
                        EmitInline(child, ref state, size, bold, italic, strike, code, inline.Destination);
                    break;

                case MarkdownInlineKind.Image:
                    EmitImagePlaceholder(inline, ref state, size);
                    break;
            }
        }

        /// <summary>
        /// An image is drawn as a labelled box rather than fetched.
        ///
        /// Loading it would mean reading arbitrary paths — and for an <c>https://</c> source, making
        /// a network request — because a document was opened in an editor. A preview is not a reason
        /// to do either. The box carries the alt text so the document still reads.
        /// </summary>
        private void EmitImagePlaceholder(MarkdownInline image, ref FlowState state, float size)
        {
            string label = string.IsNullOrEmpty(image.Text) ? "image" : image.Text;
            Font font = _style.Get(false, true, false, false, size);
            Size measured = Measure(label, font);
            int boxWidth = measured.Width + 22;
            int boxHeight = measured.Height + 6;

            if (state.PenX + boxWidth > state.Right && state.PenX > state.X) FlushLine(ref state);

            state.Line.Add(new MarkdownVisual
            {
                Kind = MarkdownVisualKind.ImageBox,
                Bounds = new Rectangle(state.PenX, state.Y, boxWidth, boxHeight),
                Text = label,
                Font = font,
                ForeColor = _style.QuoteForeground,
                BackColor = _style.BorderColor,
                LinkTarget = image.Destination,
                SourceLine = state.SourceLine,
            });
            state.PenX += boxWidth;
            state.LineHeight = Math.Max(state.LineHeight, boxHeight);
        }

        /// <summary>
        /// Places one styled run, breaking it into words and wrapping at the right margin.
        ///
        /// Words are emitted with their trailing space attached, so a wrap never leaves a visible
        /// space hanging at the end of a line and the next line never starts with one.
        /// </summary>
        private void EmitWords(string text, ref FlowState state, float size,
                               bool bold, bool italic, bool strike, bool code, string linkTarget)
        {
            if (string.IsNullOrEmpty(text)) return;

            Font font = _style.Get(bold, italic, strike, code, code ? _style.CodeSize : size);
            Color color = linkTarget != null ? _style.LinkForeground
                        : code ? _style.CodeForeground : state.Foreground;
            Color background = code ? _style.CodeBackground : Color.Empty;

            int i = 0;
            while (i < text.Length)
            {
                int wordEnd = i;
                while (wordEnd < text.Length && text[wordEnd] != ' ') wordEnd++;
                while (wordEnd < text.Length && text[wordEnd] == ' ') wordEnd++;   // keep trailing spaces
                string word = text.Substring(i, wordEnd - i);
                i = wordEnd;

                Size measured = Measure(word, font);
                int wordWidth = measured.Width;

                bool atLineStart = state.PenX <= state.X;
                if (!atLineStart && state.PenX + wordWidth > state.Right)
                {
                    FlushLine(ref state);
                    // A run of spaces at a wrap point is dropped, not carried to the next line.
                    if (word.Trim().Length == 0) continue;
                    measured = Measure(word, font);
                    wordWidth = measured.Width;
                }

                state.Line.Add(new MarkdownVisual
                {
                    Kind = MarkdownVisualKind.Text,
                    Text = word,
                    Font = font,
                    ForeColor = color,
                    BackColor = background,
                    LinkTarget = linkTarget,
                    Bounds = new Rectangle(state.PenX, state.Y, Math.Max(1, wordWidth), measured.Height),
                    SourceLine = state.SourceLine,
                });
                state.PenX += wordWidth;
                state.LineHeight = Math.Max(state.LineHeight, measured.Height);
            }
        }

        /// <summary>
        /// Commits the pending line: aligns every run on it to a shared baseline, applies the
        /// column alignment when laying out a table cell, and advances Y.
        ///
        /// Baseline alignment is why this is a two-stage flow rather than "draw as you go". A line
        /// mixing 9pt Consolas with 9.75pt Segoe UI has two different cap heights; placing both at
        /// the same top makes inline code visibly ride high, which is the most common way a
        /// hand-rolled Markdown view looks amateurish.
        /// </summary>
        private void FlushLine(ref FlowState state)
        {
            if (state.Line.Count == 0)
            {
                state.Y += state.LineHeight > 0 ? state.LineHeight : MeasureHeight(_style.Body);
                state.LineHeight = 0;
                state.PenX = state.X;
                return;
            }

            float maxAscent = 0f;
            foreach (MarkdownVisual run in state.Line) maxAscent = Math.Max(maxAscent, AscentOf(run.Font));

            int offset = 0;
            if (state.Alignment != MarkdownAlignment.None && state.AlignWidth > 0)
            {
                int lineWidth = state.PenX - state.X;
                int slack = state.AlignWidth - lineWidth;
                if (slack > 0)
                {
                    if (state.Alignment == MarkdownAlignment.Right) offset = slack;
                    else if (state.Alignment == MarkdownAlignment.Center) offset = slack / 2;
                }
            }

            foreach (MarkdownVisual run in state.Line)
            {
                int shift = (int)Math.Round(maxAscent - AscentOf(run.Font));
                run.Bounds = new Rectangle(run.Bounds.X + offset, run.Bounds.Y + shift,
                                           run.Bounds.Width, run.Bounds.Height);
                Add(run);
            }

            state.Y += state.LineHeight;
            state.LineHeight = 0;
            state.PenX = state.X;
            state.Line.Clear();
        }

        private float AscentOf(Font font)
        {
            if (font == null) return 0f;
            try
            {
                FontFamily family = font.FontFamily;
                int ascent = family.GetCellAscent(font.Style);
                int emHeight = family.GetEmHeight(font.Style);
                if (emHeight == 0) return font.GetHeight(_graphics);
                return font.GetHeight(_graphics) * ascent / emHeight;
            }
            catch { return font.GetHeight(_graphics); }
        }

        // ---- measurement -------------------------------------------------------------------------------

        private Size Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return new Size(0, MeasureHeight(font));
            // MaxValue width: this is a single-run measurement, and wrapping is this class's job.
            return TextRenderer.MeasureText(_graphics, text, font,
                new Size(int.MaxValue, int.MaxValue), MeasureFlags);
        }

        private int MeasureHeight(Font font)
        {
            return TextRenderer.MeasureText(_graphics, "Ag", font,
                new Size(int.MaxValue, int.MaxValue), MeasureFlags).Height;
        }

        private void Add(MarkdownVisual visual)
        {
            _visuals.Add(visual);
        }
    }
}
