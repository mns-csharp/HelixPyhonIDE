namespace HelixPythonNamespace.AddIns.Kit
{
    using System.Drawing;
    using HelixPythonNamespace.UI;

    // Strongly-typed catalog of toolbar/menu glyphs. Each method names one PNG so call
    // sites reference an icon by method (compile-checked) rather than a raw filename,
    // and every lookup funnels through the single Load helper below.
    public static class ToolbarIcons
    {
        public static Image NewFile() { return Load("new_file.png"); }
        public static Image OpenFolder() { return Load("open_folder.png"); }
        public static Image Save() { return Load("save.png"); }
        public static Image Interface() { return Load("interface.png"); }
        public static Image AbstractClass() { return Load("abstract_class.png"); }
        public static Image ConcreteClass() { return Load("concrete_class.png"); }
        public static Image Analyze() { return Load("analyze.png"); }
        public static Image Build() { return Load("build.png"); }
        public static Image PlayRun() { return Load("play_run.png"); }
        public static Image BuildSettings() { return Load("build_settings.png"); }
        public static Image Link() { return Load("link.png"); }
        public static Image Refresh() { return Load("refresh.png"); }

        // Undo and Redo had no artwork in any assets folder -- not shipped, not staged, not
        // superseded -- so the two 16px files were drawn to match the navigation arrows and put in
        // assets/product/toolbar with the rest. They load like every other icon here, which means a
        // designer replaces them by replacing a file.
        public static Image Undo() { return Load("undo.png"); }
        public static Image Redo() { return Load("redo.png"); }
        public static Image NavigateBackward() { return NavigationArrow(false); }
        public static Image NavigateForward() { return NavigationArrow(true); }

        private static Image NavigationArrow(bool forward)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(40, 90, 160), 2f))
                using (var brush = new SolidBrush(Color.FromArgb(40, 90, 160)))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    float start = forward ? 3f : 13f;
                    float end = forward ? 12f : 4f;
                    g.DrawLine(pen, start, 8f, end, 8f);
                    PointF[] head = forward
                        ? new[] { new PointF(13f, 8f), new PointF(8f, 3.5f), new PointF(8f, 12.5f) }
                        : new[] { new PointF(3f, 8f), new PointF(8f, 3.5f), new PointF(8f, 12.5f) };
                    g.FillPolygon(brush, head);
                }
            }
            return bmp;
        }
        public static Image SelectCursor() { return Load("select_cursor.png"); }
        public static Image ZoomIn() { return Load("zoom_in.png"); }
        public static Image ZoomOut() { return Load("zoom_out.png"); }
        public static Image ZoomFit() { return Load("zoom_fit.png"); }
        public static Image DrillUp()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(40, 90, 160), 2f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLine(pen, 8, 13, 8, 4);
                    g.DrawLine(pen, 8, 4, 3.5f, 8.5f);
                    g.DrawLine(pen, 8, 4, 12.5f, 8.5f);
                }
            }
            return bmp;
        }
        public static Image PanHand() { return Load("pan_hand.png"); }
        public static Image Import() { return Load("import.png"); }
        public static Image Compile() { return Load("compile.png"); }
        public static Image Lint() { return Load("lint.png"); }
        public static Image DeadCode() { return Load("dead_code.png"); }
        public static Image Metrics() { return Load("metrics.png"); }
        public static Image Profiler() { return Load("profiler.png"); }
        public static Image Tests() { return Load("tests.png"); }
        public static Image Git() { return Load("git.png"); }
        public static Image Welcome() { return Load("welcome.png"); }
        public static Image Designer() { return Load("designer.png"); }
        public static Image Editor() { return Load("editor.png"); }
        public static Image DebugPanel() { return Load("debug_panel.png"); }
        public static Image DebugContinue() { return Load("debug_continue.png"); }
        public static Image DebugStop() { return Load("debug_stop.png"); }
        // Step Over never shipped a real glyph — both the .resx blob and debug_step_over.png are a
        // stale "?" placeholder. Draw the classic "hop over the current statement" arrow instead, in
        // the same blue as the Into/Out arrows, so the debug toolbar reads cleanly without a new asset.
        public static Image DebugStepOver()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var col = Color.FromArgb(40, 90, 160);
                using (var pen = new Pen(col, 1.8f))
                using (var brush = new SolidBrush(col))
                {
                    // Arched hop from left, peaking over the top, landing on the right.
                    g.DrawBezier(pen, new PointF(2.5f, 11f), new PointF(4f, 2.5f), new PointF(12f, 2.5f), new PointF(13.5f, 11f));
                    // Arrowhead at the landing, pointing down.
                    PointF[] head = { new PointF(13.5f, 14f), new PointF(11f, 10.5f), new PointF(16f, 10.5f) };
                    g.FillPolygon(brush, head);
                    // The statement being stepped over.
                    g.FillEllipse(brush, 6.5f, 12.5f, 3f, 3f);
                }
            }
            return bmp;
        }
        public static Image DebugStepInto() { return Load("debug_step_into.png"); }
        public static Image DebugStepOut() { return Load("debug_step_out.png"); }
        public static Image Breakpoint() { return Load("breakpoint.png"); }

        // Pause has no shipped PNG — draw the classic two-bar glyph so the button has a clear icon
        // without adding an asset. Restart reuses the circular-arrow Refresh glyph (semantically apt).
        public static Image DebugPause()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(40, 90, 160)))
                {
                    g.FillRectangle(brush, 4, 3, 3, 10);
                    g.FillRectangle(brush, 9, 3, 3, 10);
                }
            }
            return bmp;
        }
        public static Image DebugRestart() { return Refresh(); }
        public static Image Output() { return Load("output.png"); }
        public static Image Hidden() { return Load("hidden.png"); }

        // No shipped PNG — draw a small dark console box with a ">" prompt chevron and cursor line.
        public static Image Terminal()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var body = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    g.FillRectangle(body, 1, 2, 14, 12);
                using (var pen = new Pen(Color.FromArgb(90, 220, 90), 1.8f)
                { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    g.DrawLine(pen, 3.5f, 5.5f, 6.5f, 8f);
                    g.DrawLine(pen, 3.5f, 10.5f, 6.5f, 8f);
                }
                using (var pen2 = new Pen(Color.FromArgb(220, 220, 220), 1.8f) { StartCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLine(pen2, 7.5f, 10.5f, 12.5f, 10.5f);
            }
            return bmp;
        }

        // No shipped PNG — draw a small eraser glyph (angled block with a lighter "worn" corner)
        // for "clear the screen" actions.
        public static Image Clear()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TranslateTransform(8f, 8f);
                g.RotateTransform(-30f);
                g.TranslateTransform(-6f, -3f);
                using (var brush = new SolidBrush(Color.FromArgb(220, 160, 90, 200)))
                    g.FillRectangle(brush, 0, 0, 12, 6);
                using (var brush2 = new SolidBrush(Color.FromArgb(220, 220, 220, 235)))
                    g.FillRectangle(brush2, 0, 0, 4, 6);
                using (var pen = new Pen(Color.FromArgb(120, 80, 60, 100), 1f))
                    g.DrawRectangle(pen, 0, 0, 12, 6);
            }
            return bmp;
        }

        // No shipped PNG — draw the classic "two overlapping sheets" copy glyph for the results
        // panels' Copy button (the terminal's Save/Clear already had assets; copy never did).
        public static Image Copy()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(Color.FromArgb(250, 250, 252)))
                using (var edge = new Pen(Color.FromArgb(110, 110, 120), 1f))
                {
                    // Back sheet, offset up-left; front sheet over it.
                    g.FillRectangle(fill, 2, 1, 8, 10);
                    g.DrawRectangle(edge, 2, 1, 8, 10);
                    g.FillRectangle(fill, 6, 4, 8, 10);
                    g.DrawRectangle(edge, 6, 4, 8, 10);
                }
                using (var line = new Pen(Color.FromArgb(150, 90, 90, 105), 1f))
                {
                    g.DrawLine(line, 8, 7, 12, 7);
                    g.DrawLine(line, 8, 9, 12, 9);
                    g.DrawLine(line, 8, 11, 11, 11);
                }
            }
            return bmp;
        }

        // Delegates to the shared UI loader, which resolves the PNG from the icons folder.
        private static Image Load(string fileName) { return ToolbarIconDiskLoader.Load(fileName); }
    }
}
