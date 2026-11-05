using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HelixTextEditorNET;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Editor
{
    /// <summary>How the per-block overlay is drawn (SRS solution_explorer_rules §3.2/§3.4).</summary>
    public enum BlockOverlayMode
    {
        /// <summary>C#-style virtual braces: '{' after the header, '}' after the last body line (§3.2).</summary>
        VirtualBraces,

        /// <summary>Smooth anti-aliased Bézier connectors from header to body scope (§3.4).</summary>
        BezierConnectors,
    }

    /// <summary>
    /// Kind-colored scope overlays for Python blocks, drawn via GDI in the editor's
    /// <c>WM_PAINT</c> hook; nothing is ever written to the document (§3.5). Two
    /// modes per the SRS: virtual C#-style braces (§3.2), or — when braces are
    /// disabled — smooth Bézier connectors linking the header to its body scope
    /// (§3.4: anti-aliased, nested, live-updating, metric-scaled).
    /// </summary>
    public sealed class BlockScopeRenderer : IDisposable
    {
        private BlockOverlayMode _mode = BlockOverlayMode.VirtualBraces;

        /// <summary>The active §3.2/§3.4 rendering mode. Braces by default per the SRS.</summary>
        public BlockOverlayMode Mode
        {
            get { return _mode; }
            set
            {
                if (_mode == value) return;
                _mode = value;
                ApplyAnnotations();
                try { _editor.Invalidate(); } catch { }
            }
        }

        private readonly Scintilla _editor;
        private readonly Timer _debounce;
        private List<BlockRange> _blocks = new List<BlockRange>();
        // Line count as of the last ApplyAnnotations() — see the BlocksEqual skip in Recompute().
        // ContractionState.SetDocLines (Core/ContractionState.cs) truncates/appends its per-line
        // annotation-row bookkeeping at the END of its internal list on every edit, rather than at
        // the actual insert/delete position — so inserting or deleting a line ANYWHERE but the
        // very end of the file misaligns which document line owns which annotation rows (this is a
        // real, separate bug in that class, not introduced here). That misalignment used to get
        // silently corrected on every 400ms debounce tick because ApplyAnnotations() always fully
        // rebuilt annotation state from scratch; skipping the rebuild when the computed BLOCKS
        // didn't change (e.g. adding a blank line inside a function body changes the line count
        // but not any block's recorded boundaries) lets it persist instead, which surfaces as
        // Up/Down navigation landing on the wrong line ("getting stuck") since MoveCaretLines
        // (Scintilla.Input.cs) computes display<->document line mapping from that same state. Not
        // fixing ContractionState itself (a deeper, riskier change) — resyncing whenever the line
        // count changes is enough to prevent the drift from ever surviving past one debounce tick.
        private int _lastLineCount = -1;
        // Parallel to _blocks: which reserved annotation row (0-based, -1 = none) holds
        // this block's opening/closing brace. Rebuilt every ApplyAnnotations() call.
        private List<BlockAnnotationInfo> _blockRows = new List<BlockAnnotationInfo>();
        private bool _attached;
        private bool _disposed;
        // AnnotationClearAll/AnnotationSetLines mutate Scintilla's display metadata and can
        // surface through TextChanged just like a document edit. Without this guard, rebuilding
        // virtual braces schedules another debounce tick, which rebuilds the annotations again:
        // a self-sustaining 400 ms repaint loop visible as editor flicker.
        private bool _applyingAnnotations;

        private struct BlockAnnotationInfo
        {
            public int OpenRow;
            public int CloseRow;
        }

        // One entry queued for a document line while building annotation rows: which
        // block wants a row there, whether it's the opener or a closer, and the indent
        // column its glyph aligns to (used to order same-line closers deepest-first).
        private struct QueuedRow
        {
            public int BlockIndex;
            public bool IsOpen;
            public int IndentColumn;
        }

        // Per-kind colors. Stripe/closer are solid-ish (alpha ~180); header tint is
        // the same RGB with a much lower alpha so it reads as background wash.
        private static readonly Dictionary<BlockKind, Color> KindColor = new Dictionary<BlockKind, Color>
        {
            { BlockKind.Class, Color.FromArgb(180,  94, 156, 232) }, // muted blue
            { BlockKind.Def,   Color.FromArgb(180, 111, 181, 111) }, // muted green
            { BlockKind.If,    Color.FromArgb(180, 216, 166,  87) }, // amber
            { BlockKind.For,   Color.FromArgb(180, 166, 140, 206) }, // purple
            { BlockKind.While, Color.FromArgb(180, 166, 140, 206) }, // purple (same as for)
            { BlockKind.Try,   Color.FromArgb(180, 216, 127,  94) }, // red-orange
            { BlockKind.With,  Color.FromArgb(180,  94, 184, 194) }, // teal
            { BlockKind.Match, Color.FromArgb(180, 200, 116, 163) }, // pink
        };

        private const int HeaderTintAlpha = 40;   // 0-255; very faint wash on the header line
        private const int StripeWidth     = 2;    // pixels
        private const int CloserTickLen   = 8;    // pixels — horizontal spur at stripe bottom

        public BlockScopeRenderer(Scintilla editor)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _debounce = new Timer { Interval = 400 };
            _debounce.Tick += OnDebounceTick;
        }

        public void Attach()
        {
            if (_attached || _editor.IsDisposed) return;
            _editor.TextChanged += OnTextChanged;
            _attached = true;
            Recompute();
        }

        public void Detach()
        {
            if (!_attached) return;
            try { _editor.TextChanged -= OnTextChanged; } catch { }
            _blocks = new List<BlockRange>();
            _blockRows = new List<BlockAnnotationInfo>();
            _lastLineCount = -1;
            try { if (!_editor.IsDisposed) _editor.AnnotationClearAll(); } catch { }
            _attached = false;
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            if (_applyingAnnotations) return;
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>
        /// Runs the debounced recompute immediately instead of waiting for the 400ms timer.
        /// Annotation rows reserved for virtual braces shift the document's display-line
        /// numbering, so any code that positions the viewport by display line (GoToLine's
        /// CentreCaretLine) right after a fresh load — before the debounce has had a chance to
        /// fire — computes against a numbering that's about to change out from under it, landing
        /// the viewport somewhere else entirely once the real annotations land a moment later.
        /// </summary>
        public void FlushPendingRecompute()
        {
            _debounce.Stop();
            if (_disposed || _editor.IsDisposed) return;
            Recompute();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed || _editor.IsDisposed) return;
            Recompute();
            // ApplyAnnotations() only actually touches Scintilla state (and Invalidate() only
            // actually needs calling) when the recomputed block set changed — see the
            // BlocksEqual guard inside Recompute(). Reported as a "flicker": annotations reserve
            // real vertical space, so AnnotationClearAll()-then-rebuild on every debounce tick —
            // even when typing left the block structure unchanged (the common case: most
            // keystrokes are inside a body, not opening/closing a block) — was collapsing then
            // re-expanding the WHOLE file's layout below every edit on every 400ms pause.
        }

        private void Recompute()
        {
            if (_editor.IsDisposed) return;
            List<BlockRange> newBlocks = ComputeBlocks(_editor.Text);
            int newLineCount = _editor.Lines.Count;
            bool lineCountChanged = newLineCount != _lastLineCount;
            if (BlocksEqual(_blocks, newBlocks) && !lineCountChanged) return;   // nothing changed — skip the relayout
            _blocks = newBlocks;
            _lastLineCount = newLineCount;
            ApplyAnnotations();
            try { _editor.Invalidate(); } catch { }
        }

        private static List<BlockRange> ComputeBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<BlockRange>();
            ParseResult parsed;
            try { parsed = global::HelixPythonNamespace.Python.Python.ParseModule(text); }
            catch { return new List<BlockRange>(); }
            try { return BlockScopeAnalyzer.Analyze(parsed.Module, text); }
            catch { return new List<BlockRange>(); }
        }

        private static bool BlocksEqual(List<BlockRange> a, List<BlockRange> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                BlockRange x = a[i], y = b[i];
                if (x.Kind != y.Kind || x.HeaderLine != y.HeaderLine || x.HeaderStartLine != y.HeaderStartLine
                    || x.LastContentLine != y.LastContentLine
                    || x.IndentColumn != y.IndentColumn || x.ParentIndentColumn != y.ParentIndentColumn)
                    return false;
            }
            return true;
        }

        // Reserves one Scintilla annotation row per brace glyph that needs its own visual
        // row (SRS §3.2: strict Allman — brace on its own row, aligned to its owner's
        // indent column, not appended after the line's text). A single document line can
        // host several closers when nested blocks end together (e.g. the last line of a
        // deeply nested `if` also closes every enclosing block) — those stack deepest
        // (most-indented, closest to the code) first, shallowest last, the same order a
        // human closing braces by hand would use.
        private void ApplyAnnotations()
        {
            if (_applyingAnnotations) return;
            _applyingAnnotations = true;
            try
            {
                ApplyAnnotationsCore();
            }
            finally
            {
                _applyingAnnotations = false;
            }
        }

        private void ApplyAnnotationsCore()
        {
            var rows = new List<BlockAnnotationInfo>(_blocks.Count);
            for (int i = 0; i < _blocks.Count; i++) rows.Add(new BlockAnnotationInfo { OpenRow = -1, CloseRow = -1 });
            _blockRows = rows;

            if (_editor.IsDisposed) return;

            int totalLines = _editor.Lines.Count;
            var queue = new Dictionary<int, List<QueuedRow>>();
            if (_mode == BlockOverlayMode.VirtualBraces)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    BlockRange br = _blocks[i];
                    int headerDoc = br.HeaderLine - 1;
                    int lastDoc = br.LastContentLine - 1;
                    if (headerDoc < 0 || headerDoc >= totalLines) continue;
                    if (lastDoc < 0 || lastDoc >= totalLines) continue;

                    Enqueue(queue, headerDoc, new QueuedRow { BlockIndex = i, IsOpen = true, IndentColumn = br.ParentIndentColumn });
                    Enqueue(queue, lastDoc, new QueuedRow { BlockIndex = i, IsOpen = false, IndentColumn = br.ParentIndentColumn });
                }
            }

            foreach (KeyValuePair<int, List<QueuedRow>> kv in queue)
            {
                List<QueuedRow> list = kv.Value;
                list.Sort((a, b) => b.IndentColumn.CompareTo(a.IndentColumn));
                for (int row = 0; row < list.Count; row++)
                {
                    QueuedRow e = list[row];
                    BlockAnnotationInfo info = _blockRows[e.BlockIndex];
                    if (e.IsOpen) info.OpenRow = row; else info.CloseRow = row;
                    _blockRows[e.BlockIndex] = info;
                }
            }

            // Apply only changed line counts. The former ClearAll-then-rebuild sequence
            // collapsed every reserved brace row and expanded it again on each debounce,
            // producing a visible full-editor flash. A diff keeps stable layout stable and
            // also removes obsolete rows when a block disappears or the mode changes.
            for (int docLine = 0; docLine < totalLines; docLine++)
            {
                List<QueuedRow> desiredRows;
                int desired = queue.TryGetValue(docLine, out desiredRows) ? desiredRows.Count : 0;
                try
                {
                    if (_editor.AnnotationGetLines(docLine) != desired)
                        _editor.AnnotationSetLines(docLine, desired);
                }
                catch { }
            }
        }

        private static void Enqueue(Dictionary<int, List<QueuedRow>> queue, int docLine, QueuedRow row)
        {
            List<QueuedRow> list;
            if (!queue.TryGetValue(docLine, out list))
            {
                list = new List<QueuedRow>();
                queue[docLine] = list;
            }
            list.Add(row);
        }

        /// <summary>
        /// Paints the three overlay layers (header tint, vertical stripe, closer tick) for
        /// every cached block that intersects the current viewport. Called from
        /// <c>PythonEditorControl.WndProc</c> after Scintilla's native WM_PAINT completes.
        /// </summary>
        public void DrawOverlay(Graphics g)
        {
            if (g == null || _disposed || _editor.IsDisposed) return;
            if (_blocks == null || _blocks.Count == 0) return;

            int totalLines = _editor.Lines.Count;
            if (totalLines <= 0) return;

            int firstVisible = _editor.FirstVisibleLine;
            int linesOnScreen = _editor.LinesOnScreen;
            if (linesOnScreen <= 0) return;
            int lastVisibleDoc;
            try { lastVisibleDoc = _editor.DocLineFromVisible(firstVisible + linesOnScreen); }
            catch { lastVisibleDoc = totalLines - 1; }
            int firstVisibleDoc;
            try { firstVisibleDoc = _editor.DocLineFromVisible(firstVisible); }
            catch { firstVisibleDoc = 0; }

            int clientWidth = _editor.ClientSize.Width;

            var prevSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None; // crisp 1-2px lines

            try
            {
                for (int blockIndex = 0; blockIndex < _blocks.Count; blockIndex++)
                {
                    BlockRange br = _blocks[blockIndex];
                    BlockAnnotationInfo annotationInfo = blockIndex < _blockRows.Count
                        ? _blockRows[blockIndex] : new BlockAnnotationInfo { OpenRow = -1, CloseRow = -1 };
                    int headerDoc = br.HeaderLine - 1;
                    int lastDoc = br.LastContentLine - 1;
                    if (lastDoc < firstVisibleDoc || headerDoc > lastVisibleDoc) continue;
                    if (headerDoc < 0 || headerDoc >= totalLines) continue;
                    if (lastDoc < 0 || lastDoc >= totalLines) continue;

                    Color color;
                    if (!KindColor.TryGetValue(br.Kind, out color))
                        color = Color.FromArgb(180, 150, 150, 150);

                    // --- header tint band over the WHOLE header ---
                    // A header can occupy several physical lines (a wrapped parameter list, a
                    // parenthesized condition). Tinting only the last one made a wrapped `def`
                    // read as if the block started at its `) -> R:` line.
                    int headerStartDoc = br.HeaderStartLine - 1;
                    if (headerStartDoc < 0 || headerStartDoc > headerDoc) headerStartDoc = headerDoc;

                    int headerLineStartPos;
                    int headerYTop, headerHeight;
                    try
                    {
                        var headerLine = _editor.Lines[headerDoc];
                        headerLineStartPos = headerLine.Position;
                        headerYTop = _editor.PointYFromPosition(headerLineStartPos);
                        headerHeight = headerLine.Height;
                    }
                    catch { continue; }
                    int headerXLeft;
                    try { headerXLeft = _editor.PointXFromPosition(headerLineStartPos); }
                    catch { continue; }

                    Color tint = Color.FromArgb(HeaderTintAlpha, color.R, color.G, color.B);
                    using (var b = new SolidBrush(tint))
                    {
                        int w = clientWidth - headerXLeft;
                        for (int tintDoc = headerStartDoc; w > 0 && tintDoc <= headerDoc; tintDoc++)
                        {
                            try
                            {
                                var tintLine = _editor.Lines[tintDoc];
                                int y = _editor.PointYFromPosition(tintLine.Position);
                                if (tintLine.Height > 0) g.FillRectangle(b, headerXLeft, y, w, tintLine.Height);
                            }
                            catch { }
                        }
                    }

                    // --- vertical stripe at the body-indent column ---
                    // Body starts on the line after the header. Stripe top = bottom of header;
                    // stripe bottom = bottom of LastContentLine.
                    int bodyDoc = headerDoc + 1;
                    if (bodyDoc >= totalLines) continue;

                    int stripeX;
                    try
                    {
                        int bodyLineStart = _editor.Lines[bodyDoc].Position;
                        stripeX = _editor.PointXFromPosition(bodyLineStart + br.IndentColumn);
                    }
                    catch { continue; }

                    int stripeTopY = headerYTop + headerHeight;
                    int lastLineY, lastLineHeight;
                    try
                    {
                        var lastLine = _editor.Lines[lastDoc];
                        lastLineY = _editor.PointYFromPosition(lastLine.Position);
                        lastLineHeight = lastLine.Height;
                    }
                    catch { continue; }
                    int stripeBottomY = lastLineY + lastLineHeight;

                    if (Mode == BlockOverlayMode.VirtualBraces)
                    {
                        // §3.2: strict Allman — '{' and '}' each get their own reserved
                        // annotation row (real extra display rows, not text appended
                        // after existing lines), aligned to the header's own indent
                        // column. Rendering artifacts only; the buffer is untouched (§3.5).
                        int headerIndentX;
                        try { headerIndentX = _editor.PointXFromPosition(headerLineStartPos + br.ParentIndentColumn); }
                        catch { continue; }

                        if (annotationInfo.OpenRow >= 0)
                        {
                            stripeTopY = _editor.PointYFromAnnotationRow(headerDoc, annotationInfo.OpenRow) + headerHeight;
                            DrawBraceAtRow(g, "{", headerDoc, annotationInfo.OpenRow, headerIndentX, color);
                        }
                        if (annotationInfo.CloseRow >= 0)
                        {
                            stripeBottomY = _editor.PointYFromAnnotationRow(lastDoc, annotationInfo.CloseRow);
                            DrawBraceAtRow(g, "}", lastDoc, annotationInfo.CloseRow, headerIndentX, color);
                        }
                        if (stripeBottomY <= stripeTopY) continue;
                        // Keep a hairline stripe connecting '{' to '}' so the block
                        // extent stays readable between them.
                        Color faint = Color.FromArgb(70, color.R, color.G, color.B);
                        using (var faintBrush = new SolidBrush(faint))
                            g.FillRectangle(faintBrush, stripeX, stripeTopY, 1, stripeBottomY - stripeTopY);
                    }
                    else
                    {
                        if (stripeBottomY <= stripeTopY) continue;
                        // §3.4: smooth anti-aliased Bézier connector — curves from
                        // under the header into the body-indent column, runs down the
                        // scope, and curves out at the closer. Per-block, so nesting
                        // renders naturally; geometry derives from live line metrics,
                        // so it tracks edits and font scaling.
                        var prev = g.SmoothingMode;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        try
                        {
                            int r = Math.Min(10, Math.Max(4, (stripeBottomY - stripeTopY) / 6));
                            using (var pen = new Pen(color, 1.8f))
                            using (var path = new GraphicsPath())
                            {
                                var start = new Point(headerXLeft + 4, stripeTopY - headerHeight / 2);
                                var toColumn = new Point(stripeX, stripeTopY + r);
                                path.AddBezier(
                                    start,
                                    new Point(headerXLeft + 4, stripeTopY),
                                    new Point(stripeX, stripeTopY - 2),
                                    toColumn);
                                path.AddLine(toColumn, new Point(stripeX, stripeBottomY - r));
                                path.AddBezier(
                                    new Point(stripeX, stripeBottomY - r),
                                    new Point(stripeX, stripeBottomY),
                                    new Point(stripeX + 2, stripeBottomY),
                                    new Point(stripeX + CloserTickLen, stripeBottomY - 1));
                                g.DrawPath(pen, path);
                            }
                        }
                        finally { g.SmoothingMode = prev; }
                    }
                }
            }
            finally
            {
                g.SmoothingMode = prevSmoothing;
            }
        }

        // Draws one virtual brace glyph into its reserved annotation row — a genuinely
        // separate visual row below `docLine`, aligned to `x` (the header's own indent
        // column), not appended after any line's text.
        private void DrawBraceAtRow(Graphics g, string glyph, int docLine, int rowIndex, int x, Color color)
        {
            if (rowIndex < 0) return;
            try
            {
                int y = _editor.PointYFromAnnotationRow(docLine, rowIndex);
                using (var font = new Font(_editor.Font.FontFamily, _editor.Font.Size, FontStyle.Bold))
                {
                    TextRenderer.DrawText(g, glyph, font, new Point(x, y),
                        Color.FromArgb(255, color.R, color.G, color.B),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }
            catch { /* line metrics can transiently fail during edits */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _debounce.Stop(); _debounce.Dispose(); } catch { }
            Detach();
        }
    }
}
