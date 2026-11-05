using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace HelixTextEditorNET.Rendering
{
    // Parameters describing the current view state for a paint pass. Port of
    // view/edit_view.h ViewState.
    internal sealed class RenderState
    {
        public int FirstVisibleLine;         // display-line index of the top row
        public double XOffset;               // horizontal scroll in pixels
        public Core.SelectionModel Sel;
        public double TextLeftPad = 4.0;
        public Core.ContractionState Cont;   // folding; null = every line visible
        public int BracePos0 = -1;           // highlighted brace pair
        public int BracePos1 = -1;
        public bool BraceBad;
        public int TabWidthColumns = 4;
        public bool ShowCaret = true;
    }

    // Renders document text + chrome through a GdiSurface. Port of
    // view/edit_view.{h,cpp} EditView.
    internal sealed class EditViewRenderer
    {
        private readonly GdiSurface _surface;

        public EditViewRenderer(GdiSurface surface)
        {
            _surface = surface;
        }

        public double LineHeight(ViewStyleModel vs)
        {
            return _surface.Height(vs.StyleAt(ViewStyleModel.StyleDefaultIndex));
        }

        private static bool IsBackgroundMarker(int symbol)
        {
            return symbol == (int)MarkerSymbol.Background || symbol == (int)MarkerSymbol.FullRect;
        }

        // Blend `over` onto `base` by alpha (0..256, 256 = fully `over`).
        private static Color Blend(Color baseC, Color over, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 256) alpha = 256;
            int r = (baseC.R * (256 - alpha) + over.R * alpha) / 256;
            int g = (baseC.G * (256 - alpha) + over.G * alpha) / 256;
            int b = (baseC.B * (256 - alpha) + over.B * alpha) / 256;
            return Color.FromArgb(r, g, b);
        }

        public void Paint(Graphics g, Rectangle rcClient, Core.DocumentModel doc, ViewStyleModel vs,
                          RenderState state)
        {
            double lineHeight = LineHeight(vs);
            double ascent = _surface.Ascent(vs.StyleAt(ViewStyleModel.StyleDefaultIndex));
            Core.SelectionModel sel = state.Sel ?? new Core.SelectionModel();
            int mainCaretLine = doc.LineFromPosition(sel.Main.Caret.Position);

            using (var backBrush = new SolidBrush(vs.DefaultBack))
                g.FillRectangle(backBrush, rcClient);

            double marginWidth = vs.MarginsWidth();
            double x0 = rcClient.Left + marginWidth + state.TextLeftPad - state.XOffset;
            double textLeft = rcClient.Left + marginWidth;
            StyleSpec defaultSpec = vs.StyleAt(ViewStyleModel.StyleDefaultIndex);
            double spaceWidth = _surface.WidthChar(defaultSpec, ' ');

            // Begin at the first visible row instead of walking from line 0 every
            // paint, so painting stays O(visible lines). The doc-line == display-row
            // shortcut only holds when nothing is folded AND no line reserves extra
            // annotation rows — otherwise a doc line can occupy more than one row.
            bool simpleLayout = state.Cont == null || (state.Cont.AllLinesVisible && !state.Cont.HasAnnotations);

            // Paint is the authority on the wrap width: hit-testing and the caret measure through
            // WrapWidth, so setting it here from the rectangle actually being painted keeps all
            // three agreeing even if the control has not refreshed it yet.
            _wrapWidth = Math.Max(0, rcClient.Width - marginWidth - state.TextLeftPad);

            int skipRows, line;
            FirstVisibleLineAndRow(doc, vs, state, simpleLayout, out line, out skipRows);
            int marginStartLine = line, marginSkipRows = skipRows;   // margins reuse this walk
            int displayRow = state.FirstVisibleLine;
            bool wrapDone = false;
            for (; line < doc.LinesTotal && !wrapDone; line++)
            {
                if (!simpleLayout && !state.Cont.GetVisible(line)) continue;

                // Word wrap: a document line occupies several display rows. The body below is
                // driven by lineStart/lineEnd, so running it once per row -- with those set to
                // the row's slice -- wraps text, selection, indicators and the caret together.
                // With wrap off, rowStarts is a single {0} and behaviour is byte-for-byte
                // identical to the unwrapped path.
                int docLineStart = doc.LineStart(line);
                int docLineEnd = doc.LineEnd(line);
                int[] rowStarts = RowStartsFor(doc, vs, line);

                // skipRows > 0 only on the first line, when the view is scrolled into the middle
                // of a wrapped line; its earlier rows are above the top edge.
                for (int wrapRow = skipRows; wrapRow < rowStarts.Length; wrapRow++)
                {
                double yTop = rcClient.Top + (displayRow - state.FirstVisibleLine) * lineHeight;
                displayRow += 1;
                if (wrapRow == rowStarts.Length - 1 && !simpleLayout)
                    displayRow += state.Cont.AnnotationGetLines(line);
                if (yTop >= rcClient.Bottom) { wrapDone = true; break; }
                double yBottom = yTop + lineHeight;

                int lineStart = docLineStart + rowStarts[wrapRow];
                int lineEnd = (wrapRow + 1 < rowStarts.Length)
                    ? docLineStart + rowStarts[wrapRow + 1]
                    : docLineEnd;

                if (vs.CaretLineVisible && line == mainCaretLine)
                {
                    using (var brush = new SolidBrush(vs.CurrentLineBack))
                        g.FillRectangle(brush, RectD(textLeft, yTop, rcClient.Right, yBottom));
                }

                // Background markers tint the whole text area of the line.
                int marks = doc.MarkersAtLine(line);
                if (marks != 0)
                {
                    for (int mn = 0; mn < ViewStyleModel.MaxMarkers; mn++)
                    {
                        if ((marks & (1 << mn)) == 0) continue;
                        MarkerSpec ms = vs.Markers[mn];
                        if (!IsBackgroundMarker(ms.Symbol)) continue;
                        Color fill = ms.Alpha >= 256 ? ms.BackColor : Blend(vs.DefaultBack, ms.BackColor, ms.Alpha);
                        using (var brush = new SolidBrush(fill))
                            g.FillRectangle(brush, RectD(textLeft, yTop, rcClient.Right, yBottom));
                    }
                }

                // Selection backgrounds for every range overlapping this line.
                for (int ri = 0; ri < sel.Ranges.Count; ri++)
                {
                    Core.SelectionRange r = sel.Ranges[ri];
                    int selS = r.Start;
                    int selE = r.End;
                    if (selS >= selE) continue;
                    if (selE <= lineStart || selS > lineEnd) continue;
                    int a = Math.Max(selS, lineStart);
                    int b = Math.Min(selE, lineEnd);
                    double xa = XFromPosition(doc, vs, state, a);
                    double xb = XAdvance(doc, vs, a, xa, b);
                    if (selE > lineEnd) xb += spaceWidth;  // EOL selected
                    using (var brush = new SolidBrush(vs.SelectionBack))
                        g.FillRectangle(brush, RectD(xa, yTop, xb, yBottom));
                }

                // Indicators drawn beneath the text (box fills / under-flagged).
                PaintIndicators(g, doc, vs, state, lineStart, lineEnd, yTop, yBottom, true);

                // Leading-whitespace column count drives indent guides.
                int tabW = Math.Max(1, state.TabWidthColumns);
                int indentCols = 0;
                for (int p = lineStart; p < lineEnd; p++)
                {
                    char wc = doc.CharAt(p);
                    if (wc == ' ') indentCols++;
                    else if (wc == '\t') indentCols += tabW - (indentCols % tabW);
                    else break;
                }

                // Indent guides: dotted vertical lines every indentWidth columns.
                if (vs.IndentationGuides != (int)IndentView.None && indentCols > 0)
                {
                    int step = Math.Max(1, vs.IndentWidth);
                    for (int gcol = step; gcol < indentCols; gcol += step)
                        DrawDottedVLine(g, x0 + gcol * spaceWidth, yTop, yBottom, vs.IndentGuideColour);
                }

                // Text runs grouped by style byte, tabs expanded to column stops.
                double x = x0;
                int pos = lineStart;
                int col = 0;
                while (pos < lineEnd)
                {
                    char c = doc.CharAt(pos);
                    if (c == '\t')
                    {
                        // Tab advance = spaces to the next stop, in default-style space widths.
                        int nextStop = col + (tabW - (col % tabW));
                        x += (nextStop - col) * spaceWidth;
                        col = nextStop;
                        pos++;
                        continue;
                    }
                    byte style = doc.StyleAt(pos);
                    int runEnd = pos;
                    while (runEnd < lineEnd && doc.StyleAt(runEnd) == style && doc.CharAt(runEnd) != '\t')
                        runEnd++;
                    int runLen = runEnd - pos;
                    string runText = doc.GetText(pos, runLen);
                    StyleSpec spec = vs.StyleAt(style);
                    double runWidth = _surface.WidthText(spec, runText);
                    _surface.DrawText(g, spec, x, yTop, runText, spec.ForeColor);
                    if (spec.Underline)
                    {
                        using (var pen = new Pen(spec.ForeColor))
                            g.DrawLine(pen, (float)x, (float)(yBottom - 2), (float)(x + runWidth), (float)(yBottom - 2));
                    }
                    x += runWidth;
                    col += runLen;
                    pos = runEnd;
                }

                // Indicators drawn over the text (squiggles / underlines / strikes).
                PaintIndicators(g, doc, vs, state, lineStart, lineEnd, yTop, yBottom, false);

                // Matched (or mismatched) brace highlight.
                for (int bi = 0; bi < 2; bi++)
                {
                    int bp = bi == 0 ? state.BracePos0 : state.BracePos1;
                    if (bp < lineStart || bp >= lineEnd) continue;
                    StyleSpec spec = vs.StyleAt(doc.StyleAt(bp));
                    double bx = XFromPosition(doc, vs, state, bp);
                    double bw = _surface.WidthChar(spec, doc.CharAt(bp));
                    Color colr = state.BraceBad ? vs.BraceBadFore : vs.BraceLightFore;
                    using (var pen = new Pen(colr))
                        g.DrawRectangle(pen, (float)(bx - 1), (float)(yTop + 1),
                                        (float)(bw + 2), (float)(lineHeight - 2));
                }

                // Collapsed fold header: a "..." affordance trailing the line.
                if (state.Cont != null &&
                    (doc.GetLevel(line) & Lexing.Folding.FoldLevelHeaderFlag) != 0 &&
                    !state.Cont.GetExpanded(line))
                {
                    double bx = x + 8;
                    var rcDots = RectD(bx, yTop + 2, bx + 24, yBottom - 2);
                    using (var brush = new SolidBrush(vs.FoldMarginColour))
                        g.FillRectangle(brush, rcDots);
                    using (var pen = new Pen(vs.FoldBoxColour))
                        g.DrawRectangle(pen, rcDots.X, rcDots.Y, rcDots.Width, rcDots.Height);
                    _surface.DrawText(g, defaultSpec, bx + 5, yTop, "...", vs.FoldBoxColour);
                }

                // Carets for every range whose caret is on this line.
                if (state.ShowCaret)
                {
                    for (int ri = 0; ri < sel.Ranges.Count; ri++)
                    {
                        Core.SelectionRange r = sel.Ranges[ri];
                        if (doc.LineFromPosition(r.Caret.Position) != line) continue;
                        double caretX = XFromPosition(doc, vs, state, r.Caret.Position);
                        using (var pen = new Pen(vs.CaretColour, (float)Math.Max(1.0, vs.CaretWidth)))
                            g.DrawLine(pen, (float)caretX, (float)yTop, (float)caretX, (float)yBottom);
                    }
                }
                }
                skipRows = 0;
            }

            // Edge ruler (long-line marker) drawn once across the whole client.
            if (vs.EdgeMode != (int)HelixTextEditorNET.EdgeMode.None)
            {
                double ex = x0 + vs.EdgeColumn * spaceWidth;
                if (ex < rcClient.Right && ex > rcClient.Left + marginWidth)
                {
                    if (vs.EdgeMode == (int)HelixTextEditorNET.EdgeMode.Line)
                    {
                        using (var pen = new Pen(vs.EdgeColour))
                            g.DrawLine(pen, (float)ex, rcClient.Top, (float)ex, rcClient.Bottom);
                    }
                    else if (vs.EdgeMode == (int)HelixTextEditorNET.EdgeMode.Background)
                    {
                        using (var brush = new SolidBrush(Blend(vs.DefaultBack, vs.EdgeColour, 90)))
                            g.FillRectangle(brush, RectD(ex, rcClient.Top, rcClient.Right, rcClient.Bottom));
                    }
                }
            }

            PaintMargins(g, rcClient, doc, vs, state, lineHeight, marginStartLine, marginSkipRows);
        }

        // startLine/skipRows come from Paint's own resolution of the first visible row: the margins
        // must start exactly where the text did, and resolving it a second time would walk (and
        // re-measure) the document from line 0 all over again on every paint.
        private void PaintMargins(Graphics g, Rectangle rcClient, Core.DocumentModel doc,
                                  ViewStyleModel vs, RenderState state, double lineHeight,
                                  int startLine, int skipRows)
        {
            double marginWidth = vs.MarginsWidth();
            if (marginWidth <= 0) return;

            StyleSpec lineNumberSpec = vs.StyleAt(Style.LineNumber);
            double ascent = _surface.Ascent(vs.StyleAt(ViewStyleModel.StyleDefaultIndex));

            // Margin strip backgrounds.
            double mx = rcClient.Left;
            for (int i = 0; i < ViewStyleModel.MaxMargins; i++)
            {
                MarginSpecData m = vs.Margins[i];
                if (m.Width <= 0) continue;
                bool foldMargin = (m.Mask & Marker.MaskFolders) != 0 && m.Type == (int)MarginType.Symbol;
                using (var brush = new SolidBrush(foldMargin ? vs.FoldMarginColour : vs.MarginBack))
                    g.FillRectangle(brush, RectD(mx, rcClient.Top, mx + m.Width, rcClient.Bottom));
                mx += m.Width;
            }

            bool simpleLayout = state.Cont == null || (state.Cont.AllLinesVisible && !state.Cont.HasAnnotations);
            int displayRow = state.FirstVisibleLine;
            for (int line = startLine; line < doc.LinesTotal; line++)
            {
                if (!simpleLayout && !state.Cont.GetVisible(line)) continue;

                // A wrapped line owns several display rows but is still ONE line: its number,
                // fold box and markers belong to its first row only, and the next line starts
                // after all of its rows. Advancing one row per document line here -- while the
                // text area advances one per wrap row -- is what printed a fresh number against
                // every wrapped row.
                int rows = _wrap.Enabled ? RowStartsFor(doc, vs, line).Length : 1;

                // y of this line's FIRST row. Negative when the view is scrolled into the middle
                // of the line, in which case its number is off the top edge and isn't drawn.
                double yTop = rcClient.Top + (displayRow - state.FirstVisibleLine - skipRows) * lineHeight;
                displayRow += (rows - skipRows) + (simpleLayout ? 0 : state.Cont.AnnotationGetLines(line));
                skipRows = 0;
                if (yTop >= rcClient.Bottom) break;
                if (yTop + lineHeight <= rcClient.Top) continue;
                int marks = doc.MarkersAtLine(line);
                int level = doc.GetLevel(line);

                double cx = rcClient.Left;
                for (int i = 0; i < ViewStyleModel.MaxMargins; i++)
                {
                    MarginSpecData m = vs.Margins[i];
                    if (m.Width <= 0) continue;
                    bool foldMargin = (m.Mask & Marker.MaskFolders) != 0 && m.Type == (int)MarginType.Symbol;
                    if (m.Type == (int)MarginType.Number)
                    {
                        string num = (line + 1).ToString();
                        double tw = _surface.WidthText(lineNumberSpec, num);
                        _surface.DrawText(g, lineNumberSpec, cx + m.Width - tw - 3, yTop, num,
                                          vs.MarginNumberFore);
                    }
                    else if (foldMargin)
                    {
                        // VS-style [+]/[-] box on fold header lines.
                        if ((level & Lexing.Folding.FoldLevelHeaderFlag) != 0)
                        {
                            bool expanded = state.Cont == null || state.Cont.GetExpanded(line);
                            double half = Math.Min(5.0, Math.Min(m.Width, lineHeight) / 2 - 1);
                            DrawFoldBox(g, cx + m.Width / 2.0, yTop + lineHeight / 2, half, !expanded, vs);
                        }
                    }
                    else if (m.Type == (int)MarginType.Symbol && marks != 0)
                    {
                        for (int mn = 0; mn < ViewStyleModel.MaxMarkers; mn++)
                        {
                            if ((marks & (1 << mn)) == 0 || (m.Mask & (1u << mn)) == 0) continue;
                            MarkerSpec ms = vs.Markers[mn];
                            if (IsBackgroundMarker(ms.Symbol)) continue;  // tinted in text area
                            double side = Math.Min(m.Width, lineHeight);
                            double gx = cx + (m.Width - side) / 2;
                            double gy = yTop + (lineHeight - side) / 2;
                            DrawMarkerSymbol(g, RectD(gx, gy, gx + side, gy + side), ms);
                        }
                    }
                    cx += m.Width;
                }
            }
        }

        private void PaintIndicators(Graphics g, Core.DocumentModel doc, ViewStyleModel vs,
                                     RenderState state, int lineStart, int lineEnd,
                                     double yTop, double yBottom, bool under)
        {
            Core.Decorations decs = doc.Indicators;
            if (decs.All.Count == 0) return;
            foreach (KeyValuePair<int, Core.Decoration> kv in decs.All)
            {
                int indic = kv.Key;
                if (indic < 0 || indic >= ViewStyleModel.MaxIndicators) continue;
                IndicatorSpec ind = vs.Indicators[indic];
                if (IndicatorDrawsUnder(ind) != under) continue;
                foreach (Core.Decoration.Range r in kv.Value.Ranges)
                {
                    if (r.Start > lineEnd) break;  // ranges sorted by start
                    if (r.End <= lineStart) continue;
                    int a = Math.Max(r.Start, lineStart);
                    int b = Math.Min(r.End, lineEnd);
                    if (b < a) continue;
                    double xa = XFromPosition(doc, vs, state, a);
                    double xb = XAdvance(doc, vs, a, xa, b);
                    DrawIndicatorRun(g, ind, vs.DefaultBack, xa, xb, yTop, yBottom);
                }
            }
        }

        private static bool IndicatorDrawsUnder(IndicatorSpec ind)
        {
            if (ind.Under) return true;
            switch ((IndicatorStyle)ind.Style)
            {
                case IndicatorStyle.RoundBox:
                case IndicatorStyle.StraightBox:
                case IndicatorStyle.FullBox:
                case IndicatorStyle.DotBox:
                    return true;
                default:
                    return false;
            }
        }

        private static void DrawIndicatorRun(Graphics g, IndicatorSpec ind, Color back, double xa,
                                             double xb, double yTop, double yBottom)
        {
            if (xb <= xa) xb = xa + 1;  // zero-width: still show a tick
            double yUnder = yBottom - 1;
            switch ((IndicatorStyle)ind.Style)
            {
                case IndicatorStyle.Squiggle:
                case IndicatorStyle.SquiggleLow:
                    DrawSquiggle(g, xa, xb, yUnder, ind.ForeColor);
                    break;
                case IndicatorStyle.Strike:
                    using (var pen = new Pen(ind.ForeColor))
                        g.DrawLine(pen, (float)xa, (float)((yTop + yBottom) / 2), (float)xb,
                                   (float)((yTop + yBottom) / 2));
                    break;
                case IndicatorStyle.Hidden:
                    break;
                case IndicatorStyle.Box:
                    using (var pen = new Pen(ind.ForeColor))
                        g.DrawRectangle(pen, (float)xa, (float)yTop, (float)(xb - xa),
                                        (float)(yBottom - yTop - 1));
                    break;
                case IndicatorStyle.RoundBox:
                case IndicatorStyle.StraightBox:
                case IndicatorStyle.FullBox:
                case IndicatorStyle.DotBox:
                {
                    double top = ind.Style == (int)IndicatorStyle.FullBox ? yTop : yTop + 1;
                    Color fill = Blend(back, ind.ForeColor, Clamp255(ind.Alpha));
                    Color outline = Blend(back, ind.ForeColor, Clamp255(ind.OutlineAlpha));
                    using (var brush = new SolidBrush(fill))
                        g.FillRectangle(brush, RectD(xa, top, xb, yBottom));
                    using (var pen = new Pen(outline))
                        g.DrawRectangle(pen, (float)xa, (float)top, (float)(xb - xa),
                                        (float)(yBottom - top - 1));
                    break;
                }
                default:
                    using (var pen = new Pen(ind.ForeColor))
                        g.DrawLine(pen, (float)xa, (float)yUnder, (float)xb, (float)yUnder);
                    break;
            }
        }

        private static int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        private static void DrawSquiggle(Graphics g, double x0, double x1, double y, Color colour)
        {
            const double amp = 2.0;
            const double step = 2.0;
            bool up = true;
            double prevX = x0;
            double prevY = y;
            using (var pen = new Pen(colour))
            {
                for (double x = x0 + step; x <= x1; x += step)
                {
                    double yy = up ? y - amp : y;
                    g.DrawLine(pen, (float)prevX, (float)prevY, (float)x, (float)yy);
                    prevX = x;
                    prevY = yy;
                    up = !up;
                }
            }
        }

        private static void DrawDottedVLine(Graphics g, double x, double yTop, double yBottom, Color colour)
        {
            using (var brush = new SolidBrush(colour))
            {
                for (double y = yTop; y < yBottom; y += 2.0)
                    g.FillRectangle(brush, (float)x, (float)y, 1f, 1f);
            }
        }

        // Visual-Studio-style fold box: a small square holding a "-" (expanded) or
        // "+" (contracted) glyph. Pixel-snapped to an odd side for crisp arms.
        private static void DrawFoldBox(Graphics g, double cx, double cy, double half, bool plus,
                                        ViewStyleModel vs)
        {
            int side = (int)Math.Round(half * 2);
            if (side % 2 == 0) ++side;
            float left = (float)(Math.Round(cx) - (side - 1) / 2.0);
            float top = (float)(Math.Round(cy) - (side - 1) / 2.0);
            using (var brush = new SolidBrush(vs.DefaultBack))
                g.FillRectangle(brush, left, top, side, side);
            using (var pen = new Pen(vs.FoldBoxColour))
            {
                g.DrawRectangle(pen, left, top, side, side);
                float mx = left + side / 2f;
                float my = top + side / 2f;
                float arm = (float)Math.Round(side * 0.25);
                g.DrawLine(pen, mx - arm, my, mx + arm, my);  // minus
                if (plus) g.DrawLine(pen, mx, my - arm, mx, my + arm);
            }
        }

        // Draws one marker glyph centred in `rc` (a square cell in a symbol margin).
        private static void DrawMarkerSymbol(Graphics g, RectangleF rc, MarkerSpec m)
        {
            float cx = (rc.Left + rc.Right) / 2;
            float cy = (rc.Top + rc.Bottom) / 2;
            float r = Math.Min(rc.Width, rc.Height) / 2 - 1;
            // SC_MARK_CHARACTER + ch: the marker is the character itself, drawn in the
            // marker's foreground colour (native parity). Previously unimplemented — the
            // default arm rendered these as filled circles, which is why an error mark
            // was pixel-identical to a breakpoint disc. Cross-like characters are drawn
            // as vector strokes rather than text: GDI+ DrawString does no font fallback,
            // so '✕' silently rendered as NOTHING with fonts lacking the glyph.
            if (m.Symbol >= (int)MarkerSymbol.Character)
            {
                int code = m.Symbol - (int)MarkerSymbol.Character;
                var prevSmooth = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (code == '✕' || code == '×' || code == 'X' || code == 'x')
                {
                    float a = r * 0.75f;
                    using (var pen = new Pen(m.ForeColor, Math.Max(2f, r * 0.5f)))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
                        g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
                    }
                }
                else
                {
                    string ch = char.ConvertFromUtf32(code);
                    using (var font = new Font(FontFamily.GenericSansSerif, rc.Height * 0.62f,
                               FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(m.ForeColor))
                    using (var fmt = new StringFormat
                           { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.DrawString(ch, font, brush, new RectangleF(rc.Left, rc.Top, rc.Width, rc.Height), fmt);
                    }
                }
                g.SmoothingMode = prevSmooth;
                return;
            }
            switch ((MarkerSymbol)m.Symbol)
            {
                case MarkerSymbol.Circle:
                case MarkerSymbol.CirclePlus:
                case MarkerSymbol.CircleMinus:
                {
                    // Glossy "3D" sphere (VS-style breakpoint): base fill, a top-left
                    // specular highlight fading to transparent, and a rim in the marker's
                    // fore colour — reads as a raised ball instead of a flat dot.
                    var prevSmoothing = g.SmoothingMode;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var bounds = new RectangleF(cx - r, cy - r, r * 2, r * 2);
                    using (var brush = new SolidBrush(m.BackColor))
                        g.FillEllipse(brush, bounds);
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.AddEllipse(bounds);
                        using (var glow = new System.Drawing.Drawing2D.PathGradientBrush(path))
                        {
                            glow.CenterPoint = new PointF(cx - r * 0.35f, cy - r * 0.4f);
                            glow.CenterColor = Color.FromArgb(190, 255, 255, 255);
                            glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                            g.FillEllipse(glow, bounds);
                        }
                    }
                    using (var pen = new Pen(m.ForeColor))
                        g.DrawEllipse(pen, bounds);
                    g.SmoothingMode = prevSmoothing;
                    break;
                }
                case MarkerSymbol.RoundRect:
                case MarkerSymbol.SmallRect:
                case MarkerSymbol.BoxPlus:
                case MarkerSymbol.BoxMinus:
                case MarkerSymbol.BoxPlusConnected:
                case MarkerSymbol.BoxMinusConnected:
                    using (var brush = new SolidBrush(m.BackColor))
                        g.FillRectangle(brush, cx - r, cy - r, r * 2, r * 2);
                    using (var pen = new Pen(m.ForeColor))
                        g.DrawRectangle(pen, cx - r, cy - r, r * 2, r * 2);
                    break;
                case MarkerSymbol.FullRect:
                case MarkerSymbol.LeftRect:
                    using (var brush = new SolidBrush(m.BackColor))
                        g.FillRectangle(brush, cx - r, cy - r, r * 2, r * 2);
                    break;
                case MarkerSymbol.Arrow:
                {
                    var pts = new[] { new PointF(cx - r, cy - r), new PointF(cx + r, cy), new PointF(cx - r, cy + r) };
                    using (var brush = new SolidBrush(m.BackColor)) g.FillPolygon(brush, pts);
                    break;
                }
                case MarkerSymbol.ShortArrow:
                {
                    var pts = new[] { new PointF(cx - r / 2, cy - r), new PointF(cx + r, cy), new PointF(cx - r / 2, cy + r) };
                    using (var brush = new SolidBrush(m.BackColor)) g.FillPolygon(brush, pts);
                    break;
                }
                case MarkerSymbol.ArrowDown:
                {
                    var pts = new[] { new PointF(cx - r, cy - r / 2), new PointF(cx + r, cy - r / 2), new PointF(cx, cy + r) };
                    using (var brush = new SolidBrush(m.BackColor)) g.FillPolygon(brush, pts);
                    break;
                }
                case MarkerSymbol.Plus:
                    using (var brush = new SolidBrush(m.ForeColor))
                    {
                        g.FillRectangle(brush, cx - r, cy - 1, r * 2, 2);
                        g.FillRectangle(brush, cx - 1, cy - r, 2, r * 2);
                    }
                    break;
                case MarkerSymbol.Minus:
                    using (var brush = new SolidBrush(m.ForeColor))
                        g.FillRectangle(brush, cx - r, cy - 1, r * 2, 2);
                    break;
                case MarkerSymbol.VLine:
                    using (var pen = new Pen(m.BackColor))
                        g.DrawLine(pen, cx, rc.Top, cx, rc.Bottom);
                    break;
                case MarkerSymbol.LCorner:
                case MarkerSymbol.LCornerCurve:
                    using (var pen = new Pen(m.BackColor))
                    {
                        g.DrawLine(pen, cx, rc.Top, cx, cy);
                        g.DrawLine(pen, cx, cy, rc.Right, cy);
                    }
                    break;
                case MarkerSymbol.TCorner:
                case MarkerSymbol.TCornerCurve:
                    using (var pen = new Pen(m.BackColor))
                    {
                        g.DrawLine(pen, cx, rc.Top, cx, rc.Bottom);
                        g.DrawLine(pen, cx, cy, rc.Right, cy);
                    }
                    break;
                case MarkerSymbol.Bookmark:
                {
                    var pts = new[]
                    {
                        new PointF(cx - r, cy - r), new PointF(cx + r, cy - r), new PointF(cx + r, cy + r),
                        new PointF(cx, cy + r / 2), new PointF(cx - r, cy + r)
                    };
                    using (var brush = new SolidBrush(m.BackColor)) g.FillPolygon(brush, pts);
                    break;
                }
                case MarkerSymbol.Empty:
                    break;
                default:
                    using (var brush = new SolidBrush(m.BackColor))
                        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                    break;
            }
        }

        // Which margin (0..count-1) contains client x, or -1 if x is over text.
        public int MarginFromX(ViewStyleModel vs, Rectangle rcClient, double x)
        {
            double cx = rcClient.Left;
            for (int i = 0; i < ViewStyleModel.MaxMargins; i++)
            {
                MarginSpecData m = vs.Margins[i];
                if (m.Width <= 0) continue;
                if (x >= cx && x < cx + m.Width) return i;
                cx += m.Width;
            }
            return -1;
        }

        // Pixel X (in client coords) of a document position on its line.
        public double XFromPosition(Core.DocumentModel doc, ViewStyleModel vs, RenderState state, int pos)
        {
            int line = doc.LineFromPosition(pos);
            int lineStart = doc.LineStart(line);
            if (_wrap.Enabled)
            {
                // On a wrapped line the caret's x is its offset within its own row.
                int[] rs = RowStartsFor(doc, vs, line);
                lineStart += rs[WrapLayout.RowOfOffset(rs, pos - lineStart)];
            }
            double x = vs.MarginsWidth() + state.TextLeftPad - state.XOffset;
            return XAdvance(doc, vs, lineStart, x, pos);
        }

        // X of `to`, given the X of `from` on the same line is already known — sums
        // only [from,to) instead of re-walking from line start. Tab-aware.
        public double XAdvance(Core.DocumentModel doc, ViewStyleModel vs, int from, double fromX, int to)
        {
            StyleSpec defaultSpec = vs.StyleAt(ViewStyleModel.StyleDefaultIndex);
            double spaceWidth = _surface.WidthChar(defaultSpec, ' ');
            int tabW = Math.Max(1, vs.TabWidth);
            // Column of `from` relative to its line start (needed for tab stops).
            int line = doc.LineFromPosition(from);
            int lineStart = doc.LineStart(line);
            int col = 0;
            for (int p = lineStart; p < from; p++)
            {
                if (doc.CharAt(p) == '\t') col += tabW - (col % tabW);
                else col++;
            }
            double x = fromX;
            int pos = from;
            while (pos < to)
            {
                char c = doc.CharAt(pos);
                if (c == '\t')
                {
                    int nextStop = col + (tabW - (col % tabW));
                    x += (nextStop - col) * spaceWidth;
                    col = nextStop;
                    pos++;
                    continue;
                }
                byte style = doc.StyleAt(pos);
                int runEnd = pos;
                while (runEnd < to && doc.StyleAt(runEnd) == style && doc.CharAt(runEnd) != '\t')
                    runEnd++;
                string runText = doc.GetText(pos, runEnd - pos);
                x += _surface.WidthText(vs.StyleAt(style), runText);
                col += runEnd - pos;
                pos = runEnd;
            }
            return x;
        }

        // Document position nearest a client point.
        private readonly WrapLayout _wrap = new WrapLayout();
        private double _wrapWidth;

        /// <summary>Text-area width to wrap at, in pixels. Set by the control on resize/paint.</summary>
        public double WrapWidth
        {
            get { return _wrapWidth; }
            set { _wrapWidth = value; }
        }

        /// <summary>Turn word wrap on or off (View &gt; Word Wrap).</summary>
        public bool WordWrap
        {
            get { return _wrap.Enabled; }
            set { _wrap.Enabled = value; }
        }

        /// <summary>
        /// Resolves <see cref="RenderState.FirstVisibleLine"/> to the document line painting starts
        /// at, plus how many of that line's wrapped rows are above the top edge.
        ///
        /// With wrap off this is the existing document-line mapping and <paramref name="skipRows"/>
        /// is always 0. With wrap on, FirstVisibleLine is a display ROW -- that is what the vertical
        /// scrollbar ranges over and what <c>PointYFromPosition</c> subtracts -- so treating it as a
        /// document line would paint a scrolled wrapped view from the wrong line, and put the caret
        /// somewhere other than its text. The text area and the margins both come through here, so
        /// they cannot drift apart.
        /// </summary>
        private void FirstVisibleLineAndRow(Core.DocumentModel doc, ViewStyleModel vs, RenderState state,
                                            bool simpleLayout, out int line, out int skipRows)
        {
            if (!_wrap.Enabled)
            {
                skipRows = 0;
                line = simpleLayout ? state.FirstVisibleLine : state.Cont.DocFromDisplay(state.FirstVisibleLine);
                return;
            }
            WalkToRow(doc, vs, state.Cont, simpleLayout, state.FirstVisibleLine, out line, out skipRows);
        }

        /// <summary>
        /// The document line a display row falls in. Everything that has to agree with painting —
        /// where paint starts, which lines an overlay culls to, where a click lands — comes through
        /// this one walk, counting wrapped rows and annotation rows exactly as Paint advances.
        /// </summary>
        public int DocLineOfDisplayRow(Core.DocumentModel doc, ViewStyleModel vs,
                                       Core.ContractionState cont, int displayRow)
        {
            if (!_wrap.Enabled) return cont != null ? cont.DocFromDisplay(displayRow) : displayRow;
            bool simpleLayout = cont == null || (cont.AllLinesVisible && !cont.HasAnnotations);
            int line, skipRows;
            WalkToRow(doc, vs, cont, simpleLayout, displayRow, out line, out skipRows);
            return line;
        }

        // Walks visible lines from the top of the document until `targetRow` is reached, reporting
        // the line it lands in and how many of that line's wrapped rows precede the target.
        private void WalkToRow(Core.DocumentModel doc, ViewStyleModel vs, Core.ContractionState cont,
                               bool simpleLayout, int targetRow, out int line, out int skipRows)
        {
            skipRows = 0;
            int row = 0;
            for (line = 0; line < doc.LinesTotal; line++)
            {
                if (!simpleLayout && !cont.GetVisible(line)) continue;
                int wrapRows = RowStartsFor(doc, vs, line).Length;
                int rows = wrapRows + (simpleLayout ? 0 : cont.AnnotationGetLines(line));
                if (targetRow < row + rows)
                {
                    // Clamped so a row landing in a line's annotation rows still resolves to a real
                    // wrapped row of that line rather than past its end.
                    skipRows = Math.Min(targetRow - row, wrapRows - 1);
                    if (skipRows < 0) skipRows = 0;
                    return;
                }
                row += rows;
            }
            line = Math.Max(0, doc.LinesTotal - 1);
        }

        /// <summary>Row starts for a document line, using the same measurement paint uses.</summary>
        private int[] RowStartsFor(Core.DocumentModel doc, ViewStyleModel vs, int line)
        {
            _wrap.Sync(_wrapWidth, vs.TabWidth, doc.Length);
            int s0 = doc.LineStart(line), e0 = doc.LineEnd(line);
            StyleSpec spec = vs.StyleAt(ViewStyleModel.StyleDefaultIndex);
            return _wrap.BreaksFor(line, e0 > s0 ? doc.GetText(s0, e0 - s0) : string.Empty,
                                   t => _surface.WidthText(spec, t));
        }

        /// <summary>
        /// Display rows occupied by <paramref name="line"/>: its wrapped rows plus any annotation
        /// rows reserved after it. This is the unit Paint advances by, so anything mapping a
        /// document line to a y coordinate has to advance by the same amount.
        /// </summary>
        public int RowsOccupiedBy(Core.DocumentModel doc, ViewStyleModel vs,
                                  Core.ContractionState cont, int line)
        {
            int rows = _wrap.Enabled ? RowStartsFor(doc, vs, line).Length : 1;
            return rows + (cont != null ? cont.AnnotationGetLines(line) : 0);
        }

        /// <summary>
        /// Display row of a document line's FIRST row. With wrap off this is the folding-aware
        /// mapping the port already had; with wrap on it walks the visible lines, counting both
        /// wrapped rows and annotation rows.
        ///
        /// Annotation rows must be counted here: virtual braces and type markers are painted into
        /// rows reserved after a line, and a mapping that skipped them put every overlay below the
        /// first wrapped line onto the wrong row.
        /// </summary>
        public int DisplayRowOfLine(Core.DocumentModel doc, ViewStyleModel vs,
                                    Core.ContractionState cont, int line)
        {
            if (!_wrap.Enabled) return cont != null ? cont.DisplayFromDoc(line) : line;

            bool simpleLayout = cont == null || (cont.AllLinesVisible && !cont.HasAnnotations);
            int rows = 0;
            int last = Math.Min(line, doc.LinesTotal);
            for (int i = 0; i < last; i++)
            {
                if (!simpleLayout && !cont.GetVisible(i)) continue;
                rows += RowStartsFor(doc, vs, i).Length + (simpleLayout ? 0 : cont.AnnotationGetLines(i));
            }
            return rows;
        }

        /// <summary>
        /// The display row a position falls on. With wrap off this is just its document line;
        /// with wrap on it counts the rows of every preceding line plus the row within this one.
        /// Painting advances the same way, so the caret lands where the text is drawn.
        /// </summary>
        public int DisplayRowOfPosition(Core.DocumentModel doc, ViewStyleModel vs,
                                        Core.ContractionState cont, int pos)
        {
            int line = doc.LineFromPosition(pos);
            if (!_wrap.Enabled) return line;
            int[] rs = RowStartsFor(doc, vs, line);
            return DisplayRowOfLine(doc, vs, cont, line)
                 + WrapLayout.RowOfOffset(rs, pos - doc.LineStart(line));
        }

        /// <summary>Total display rows, which is what the vertical scrollbar must range over.</summary>
        public int DisplayRowCount(Core.DocumentModel doc, ViewStyleModel vs, Core.ContractionState cont)
        {
            if (!_wrap.Enabled) return doc.LinesTotal;
            bool simpleLayout = cont == null || (cont.AllLinesVisible && !cont.HasAnnotations);
            int rows = 0;
            for (int line = 0; line < doc.LinesTotal; line++)
            {
                if (!simpleLayout && !cont.GetVisible(line)) continue;
                rows += RowStartsFor(doc, vs, line).Length
                      + (simpleLayout ? 0 : cont.AnnotationGetLines(line));
            }
            return rows;
        }

        public int PositionFromPoint(Core.DocumentModel doc, ViewStyleModel vs, RenderState state,
                                     Rectangle rcClient, Point pt)
        {
            double lineHeight = LineHeight(vs);
            int displayRow = state.FirstVisibleLine +
                             (int)((pt.Y - rcClient.Top) / (lineHeight > 0 ? lineHeight : 1));
            int line = state.Cont != null ? state.Cont.DocFromDisplay(displayRow) : displayRow;
            if (line < 0) line = 0;
            if (line > doc.LinesTotal - 1) line = doc.LinesTotal - 1;
            int lineStart = doc.LineStart(line);
            int lineEnd = doc.LineEnd(line);
            if (_wrap.Enabled)
            {
                // Which document line, and which of its wrapped rows, the click landed on. The same
                // walk paint uses — including annotation rows, which occupy real screen space, so a
                // click below a virtual brace would otherwise resolve to the wrong line.
                bool simpleLayout = state.Cont == null ||
                                    (state.Cont.AllLinesVisible && !state.Cont.HasAnnotations);
                int within;
                WalkToRow(doc, vs, state.Cont, simpleLayout, displayRow, out line, out within);
                int[] rs = RowStartsFor(doc, vs, line);
                if (within >= rs.Length) within = rs.Length - 1;
                lineStart = doc.LineStart(line) + rs[within];
                lineEnd = (within + 1 < rs.Length)
                    ? doc.LineStart(line) + rs[within + 1]
                    : doc.LineEnd(line);
            }
            double targetX = pt.X - rcClient.Left;
            double x = vs.MarginsWidth() + state.TextLeftPad - state.XOffset;
            StyleSpec defaultSpec = vs.StyleAt(ViewStyleModel.StyleDefaultIndex);
            double spaceWidth = _surface.WidthChar(defaultSpec, ' ');
            int tabW = Math.Max(1, vs.TabWidth);
            int col = 0;
            for (int p = lineStart; p < lineEnd; p++)
            {
                char c = doc.CharAt(p);
                double w;
                if (c == '\t')
                {
                    int nextStop = col + (tabW - (col % tabW));
                    w = (nextStop - col) * spaceWidth;
                    col = nextStop;
                }
                else
                {
                    w = _surface.WidthChar(vs.StyleAt(doc.StyleAt(p)), c);
                    col++;
                }
                if (targetX < x + w / 2) return p;
                x += w;
            }
            return lineEnd;
        }

        private static RectangleF RectD(double l, double t, double r, double b)
        {
            return new RectangleF((float)l, (float)t, (float)(r - l), (float)(b - t));
        }
    }
}
