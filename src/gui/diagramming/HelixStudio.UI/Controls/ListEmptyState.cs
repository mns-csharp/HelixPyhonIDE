namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// Shows a centred message over a results ListView while it has no items.
    ///
    /// The result panels (Compile/Problems, Metrics, Dead Code, Lint, Profiler) are grid views that
    /// start empty and stay empty until an engine runs. Without this they read as a broken or
    /// still-loading grid — an expanse of blank ruled rows under column headers — rather than
    /// "nothing to report yet, here is how to populate me". The Editor tab and the Solution Explorer
    /// already get this treatment; this makes it reusable for the panels.
    ///
    /// The label is a sibling of the list inside the same parent, kept just below the column header
    /// band so the headers (which tell the user what the grid will contain) stay visible.
    /// </summary>
    public static class ListEmptyState
    {
        private const int HeaderBand = 24;

        /// <summary>
        /// Attaches an empty-state message to <paramref name="list"/>. Safe to call once during
        /// panel construction; the label tracks the list's item count from then on.
        /// </summary>
        public static Label Attach(ListView list, string message)
        {
            if (list == null) throw new ArgumentNullException("list");

            var label = new Label
            {
                Text = message,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = LunaFonts.Ui,
                BackColor = SystemColors.Window,
                Visible = false,
            };

            // Positioned rather than docked: a Dock=Fill sibling would fight the Dock=Fill list and
            // one of them would collapse to zero height.
            EventHandler layout = delegate { Reposition(list, label); };
            list.SizeChanged += layout;
            list.LocationChanged += layout;

            list.ParentChanged += delegate
            {
                if (list.Parent == null) return;
                if (!list.Parent.Controls.Contains(label)) list.Parent.Controls.Add(label);
                label.BringToFront();
                Reposition(list, label);
                Refresh(list, label);
            };

            if (list.Parent != null)
            {
                list.Parent.Controls.Add(label);
                label.BringToFront();
                Reposition(list, label);
            }

            Refresh(list, label);
            return label;
        }

        /// <summary>Re-evaluates visibility. Call after the list's items change.</summary>
        public static void Refresh(ListView list, Label label)
        {
            if (list == null || label == null) return;
            label.Visible = list.Items.Count == 0;
            if (label.Visible) label.BringToFront();
        }

        private static void Reposition(ListView list, Label label)
        {
            if (list == null || label == null) return;
            int top = list.Top + (list.HeaderStyle == ColumnHeaderStyle.None ? 0 : HeaderBand);
            label.SetBounds(list.Left, top, list.Width, Math.Max(0, list.Height - (top - list.Top)));
        }
    }
}
