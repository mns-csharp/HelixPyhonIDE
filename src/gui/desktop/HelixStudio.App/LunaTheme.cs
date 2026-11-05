namespace HelixPythonNamespace.App
{
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;   // LunaThemes: the active accent palette (Blue / Green / Crimson)
    using HelixPythonNamespace.AddIns.Kit;

    // Supplies the exact colors ToolStripProfessionalRenderer uses for menus, toolbars,
    // and status strips. Each override replaces one slot of the default (silver) theme
    // with an XP-Luna color, so the standard renderer draws the XP look for free.
    public class LunaColorTable : ProfessionalColorTable
    {
        // Toolbar: tan gloss (light top -> tan bottom).
        public override Color ToolStripGradientBegin { get { return LunaColors.ToolbarGlossTop; } }
        public override Color ToolStripGradientMiddle { get { return Color.FromArgb(244, 242, 233); } }
        public override Color ToolStripGradientEnd { get { return LunaColors.ToolbarGlossBottom; } }
        public override Color ToolStripBorder { get { return LunaColors.Separator; } }
        public override Color ToolStripDropDownBackground { get { return LunaColors.DropdownBackground; } }

        // Menu bar: flat tan.
        public override Color MenuStripGradientBegin { get { return LunaColors.MenuBackground; } }
        public override Color MenuStripGradientEnd { get { return LunaColors.MenuBackground; } }
        public override Color MenuBorder { get { return LunaColors.Border; } }
        public override Color MenuItemBorder { get { return LunaColors.Accent; } }

        // Selected menu item: solid XP blue (white text supplied by the renderer).
        public override Color MenuItemSelected { get { return LunaColors.Selection; } }
        public override Color MenuItemSelectedGradientBegin { get { return LunaColors.Selection; } }
        public override Color MenuItemSelectedGradientEnd { get { return LunaColors.Selection; } }
        public override Color MenuItemPressedGradientBegin { get { return LunaColors.ToolbarGlossTop; } }
        public override Color MenuItemPressedGradientMiddle { get { return LunaColors.ToolbarGlossBottom; } }
        public override Color MenuItemPressedGradientEnd { get { return LunaColors.ToolbarGlossBottom; } }

        // Tan gutter strip down the left of dropdown menus.
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(244, 242, 233); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(236, 233, 216); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(228, 224, 206); } }

        public override Color SeparatorDark { get { return LunaColors.Separator; } }
        public override Color SeparatorLight { get { return Color.White; } }

        public override Color ButtonSelectedHighlight { get { return LunaColors.Selection; } }
        public override Color ButtonSelectedHighlightBorder { get { return LunaColors.Accent; } }
        public override Color ButtonSelectedGradientBegin { get { return LunaColors.Selection; } }
        public override Color ButtonSelectedGradientEnd { get { return LunaColors.Selection; } }
        public override Color ButtonSelectedGradientMiddle { get { return LunaColors.Selection; } }
        public override Color ButtonSelectedBorder { get { return LunaColors.Accent; } }

        public override Color ButtonPressedHighlight { get { return LunaColors.Pressed; } }
        public override Color ButtonPressedHighlightBorder { get { return LunaColors.Accent; } }
        public override Color ButtonPressedGradientBegin { get { return LunaColors.Pressed; } }
        public override Color ButtonPressedGradientEnd { get { return LunaColors.Pressed; } }
        public override Color ButtonPressedGradientMiddle { get { return LunaColors.Pressed; } }
        public override Color ButtonPressedBorder { get { return LunaColors.Accent; } }

        public override Color ButtonCheckedHighlight { get { return LunaColors.Selection; } }
        public override Color ButtonCheckedHighlightBorder { get { return LunaColors.Accent; } }
        public override Color ButtonCheckedGradientBegin { get { return Color.FromArgb(200, 217, 242); } }
        public override Color ButtonCheckedGradientEnd { get { return Color.FromArgb(180, 203, 236); } }
        public override Color ButtonCheckedGradientMiddle { get { return Color.FromArgb(190, 210, 239); } }

        public override Color CheckBackground { get { return LunaColors.Accent; } }
        public override Color CheckSelectedBackground { get { return LunaColors.Accent; } }
        public override Color CheckPressedBackground { get { return LunaColors.Accent; } }

        public override Color GripDark { get { return LunaColors.Border; } }
        public override Color GripLight { get { return Color.White; } }

        public override Color OverflowButtonGradientBegin { get { return LunaColors.ToolbarGlossTop; } }
        public override Color OverflowButtonGradientMiddle { get { return Color.FromArgb(244, 242, 233); } }
        public override Color OverflowButtonGradientEnd { get { return LunaColors.ToolbarGlossBottom; } }

        // Status bar: blue gradient.
        public override Color StatusStripGradientBegin { get { return LunaColors.StatusBarGradientTop; } }
        public override Color StatusStripGradientEnd { get { return LunaColors.StatusBarGradientBottom; } }

        public override Color RaftingContainerGradientBegin { get { return LunaColors.MainBackground; } }
        public override Color RaftingContainerGradientEnd { get { return LunaColors.MainBackground; } }
    }

    // The renderer bound to every themed ToolStrip/MenuStrip. It drives fills from the
    // color table above but overrides the handful of draw steps (text/arrow colors, the
    // toolbar gloss) the color table alone cannot express.
    public class LunaRenderer : ToolStripProfessionalRenderer
    {
        // Square (non-rounded) edges match the XP flat-chrome look.
        public LunaRenderer() : base(new LunaColorTable())
        {
            RoundedEdges = false;
        }

        // Choose the item text color by state so it stays legible on every background.
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = LunaColors.TextDisabled;
            }
            else if (e.Item.Selected || e.Item.Pressed)
            {
                // White on the solid blue selection bar (XP look). MenuStrip top-level
                // items render on tan even when selected, so keep those dark.
                bool onMenuBar = e.Item.OwnerItem == null && e.ToolStrip is MenuStrip;
                e.TextColor = onMenuBar ? LunaColors.TextPrimary : LunaColors.TextOnAccent;
            }
            else
            {
                e.TextColor = LunaColors.TextPrimary;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Selected ? LunaColors.TextOnAccent : LunaColors.TextSecondary;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            // Vertical tan gloss for ToolStrips (ProfessionalRenderer handles MenuStrip/StatusStrip).
            ToolStrip ts = e.ToolStrip;
            if (ts is MenuStrip || ts is StatusStrip)
            {
                base.OnRenderToolStripBackground(e);
                return;
            }

            // Guard against a zero-size strip (LinearGradientBrush throws on empty rects).
            Rectangle r = new Rectangle(Point.Empty, ts.Size);
            if (r.Width <= 0 || r.Height <= 0)
            {
                base.OnRenderToolStripBackground(e);
                return;
            }

            using (var brush = new LinearGradientBrush(
                r, LunaColors.ToolbarGlossTop, LunaColors.ToolbarGlossBottom, LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, r);
            }
        }
    }
}
