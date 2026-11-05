namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// The row of commands along the top of a design canvas: icons on a strip, one click away,
    /// with the name on hover.
    ///
    /// <para>Commands use image-only buttons with their names as tooltips and accessible labels,
    /// square edges, and no overflow chevron —
    /// an overflow chevron is a drop-down again, which is the thing these buttons replace.</para>
    ///
    /// <para><b>Dock is None on purpose.</b> A ToolStrip defaults to Dock=Top, which ignores
    /// Location and stretches across the whole bar, covering the framework combo it is meant to sit
    /// beside. Docking it Right instead puts it as far from the combo as the bar allows AND — with
    /// AutoSize — measures itself before it has any items, then hides every button under an
    /// overflow chevron.</para>
    /// </summary>
    public sealed class DesignCommandStrip : ToolStrip
    {
        private const int ButtonSize = 26;
        private const int StripHeight = 28;

        public DesignCommandStrip(int left, int top)
        {
            Dock = DockStyle.None;
            GripStyle = ToolStripGripStyle.Hidden;
            AutoSize = false;
            Location = new Point(left, top);
            Size = new Size(ButtonSize, StripHeight);
            CanOverflow = false;
            LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
            Renderer = new ToolStripProfessionalRenderer { RoundedEdges = false };
        }

        public ToolStripButton Add(string name, Image icon, EventHandler onClick)
        {
            ToolStripButton button = Button(name, icon);
            button.Click += onClick;
            Items.Add(button);
            Remeasure();
            return button;
        }

        /// <summary>A command that is either on or off — Snap to Grid, a visible tool window. It
        /// stays pressed while it is on, which is the only thing a menu's tick mark was doing.</summary>
        public ToolStripButton AddToggle(string name, Image icon, bool on, EventHandler onClick)
        {
            ToolStripButton button = Button(name, icon);
            button.CheckOnClick = true;
            button.Checked = on;
            button.Click += onClick;
            Items.Add(button);
            Remeasure();
            return button;
        }

        /// <summary>
        /// A dropdown, for a choice that is one of many rather than a command.
        ///
        /// <para>DropDownList, never editable: the entries are a fixed set, and a combo a user can
        /// type into invites a value that is not on the list and has to be rejected afterwards.</para>
        ///
        /// <para>Wired to <c>SelectionChangeCommitted</c>, not <c>SelectedIndexChanged</c>: the
        /// latter fires on a programmatic <c>SelectedItem</c> assignment too (so code that sets the
        /// dropdown to reflect state would re-trigger the very handler that reacts to a user pick),
        /// and it does not fire at all when the user re-picks the item already selected. Committed
        /// selection fires only for an actual user action, every time.</para>
        /// </summary>
        public ToolStripComboBox AddDropDown(string name, int width, EventHandler onChanged)
        {
            var combo = new ToolStripComboBox
            {
                ToolTipText = name,
                AccessibleName = name,
                DropDownStyle = ComboBoxStyle.DropDownList,
                AutoSize = false,
                Width = width,
            };
            combo.ComboBox.SelectionChangeCommitted += onChanged;
            Items.Add(combo);
            Remeasure();
            return combo;
        }

        public void AddSeparator()
        {
            Items.Add(new ToolStripSeparator());
            Remeasure();
        }

        private static ToolStripButton Button(string name, Image icon)
        {
            return new ToolStripButton
            {
                Image = icon,
                ToolTipText = name,
                // The same words to a screen reader as to a mouse. These strips are image-only, so
                // without this the button has no accessible name at all -- the tooltip is not one,
                // and a toolbar of unnamed buttons is a toolbar that cannot be used without sight.
                AccessibleName = name,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoSize = false,
                Size = new Size(ButtonSize, ButtonSize),
            };
        }

        /// <summary>
        /// Widens the strip to whatever it now holds.
        ///
        /// <para>AutoSize is off — see the class note — so the width is arithmetic, and it is done
        /// here rather than left as a hand-tuned constant at the call site.</para>
        /// </summary>
        private void Remeasure()
        {
            int width = Padding.Horizontal + 8;
            // Each item's OWN width, not a button's. The strip used to assume every item was a
            // square button, so the first dropdown added to it was measured as 26px wide and the
            // strip clipped everything after it.
            foreach (ToolStripItem item in Items)
            {
                int own = item is ToolStripSeparator ? 8
                        : (item is ToolStripButton ? ButtonSize : item.Width);
                width += own + item.Margin.Horizontal;
            }
            Size = new Size(width, Height);
        }

        /// <summary>The right-hand edge, for anything that has to sit after the strip.</summary>
        public int RightEdge { get { return Left + Width; } }

        /// <summary>The same, for an icon out of the shell's shared toolbar set.</summary>
        public static Image SharedIcon(Func<Image> load, Image fallback)
        {
            try { return load() ?? fallback; }
            catch (System.IO.IOException) { return fallback; }
        }
    }
}
