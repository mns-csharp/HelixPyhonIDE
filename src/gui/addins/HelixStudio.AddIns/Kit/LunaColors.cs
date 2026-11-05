namespace HelixPythonNamespace.AddIns.Kit
{
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;   // LunaThemes: the active accent palette (Blue / Green / Crimson)

    // Windows XP "Luna" (Blue) palette. This is the single source of truth for the
    // client-area chrome (menus, toolbars, panels, tabs, status bar, dialogs). The
    // hand-drawn window caption/frame lives in HelixPythonNamespace.UI.LunaForm, which mirrors
    // the caption colors below so it can stay self-contained across the project split.
    public static class LunaColors
    {
        // The iconic XP tan "3D face" used for window/control backgrounds.
        public static readonly Color MainBackground = Color.FromArgb(236, 233, 216);
        public static readonly Color PanelBackground = Color.FromArgb(236, 233, 216);
        public static Color TitleBarBackground { get { return LunaThemes.Current.TitleBar; } }
        public static readonly Color MenuBackground = Color.FromArgb(236, 233, 216);
        public static readonly Color ToolbarBackground = Color.FromArgb(236, 233, 216);
        // XP dropdown menus are white with a tan gutter (gutter handled by the color table).
        public static readonly Color DropdownBackground = Color.FromArgb(252, 252, 250);
        public static readonly Color InputBackground = Color.White;
        // Active tab = caption blue with white text; inactive tab = tan with dark text.
        public static Color TabActiveBackground { get { return LunaThemes.Current.TabActive; } }
        public static readonly Color TabInactiveBackground = Color.FromArgb(219, 216, 201);
        public static readonly Color TabStripBackground = Color.FromArgb(198, 196, 182);
        public static readonly Color TreeBackground = Color.White;
        public static readonly Color GridBackground = Color.White;
        public static Color StatusBarBackground { get { return LunaThemes.Current.StatusBottom; } }
        public static readonly Color ScrollBarBackground = Color.FromArgb(224, 223, 212);

        // Text
        public static readonly Color TextPrimary = Color.FromArgb(30, 30, 30);
        public static readonly Color TextSecondary = Color.FromArgb(64, 64, 64);
        public static readonly Color TextDisabled = Color.FromArgb(160, 160, 160);
        public static Color TextAccent { get { return LunaThemes.Current.Accent; } }
        // Text drawn on top of a blue selection/caption fill.
        public static readonly Color TextOnAccent = Color.White;

        // Borders & Separators (XP sunken bluish-gray control border + tan separators)
        public static readonly Color Border = Color.FromArgb(127, 157, 185);
        public static readonly Color Separator = Color.FromArgb(172, 168, 153);
        public static readonly Color SplitterColor = Color.FromArgb(212, 208, 193);

        // Highlights — XP selection is solid blue #316AC5 with white text.
        public static Color Hover { get { return LunaThemes.Current.Hover; } }
        public static Color Selection { get { return LunaThemes.Current.Selection; } }
        public static Color Pressed { get { return LunaThemes.Current.Pressed; } }
        public static Color Accent { get { return LunaThemes.Current.Accent; } }
        public static readonly Color TreeLineColor = Color.FromArgb(170, 170, 170);

        // Toolbar gloss gradient (light top -> tan bottom)
        public static readonly Color ToolbarGlossTop = Color.FromArgb(251, 250, 247);
        public static readonly Color ToolbarGlossBottom = Color.FromArgb(236, 233, 216);

        // Status bar blue gradient (matches the caption aesthetic).
        public static Color StatusBarGradientTop { get { return LunaThemes.Current.StatusTop; } }
        public static Color StatusBarGradientBottom { get { return LunaThemes.Current.StatusBottom; } }

        // Editor specific (light theme)
        public static readonly Color EditorBackground = Color.White;
        public static readonly Color EditorText = Color.Black;
        public static readonly Color EditorLineNumber = Color.FromArgb(43, 145, 175);
        public static readonly Color EditorCurrentLine = Color.FromArgb(245, 245, 245);

        // Shell UI font.
        // The OS shell UI font — the colour skins stay Luna, the typography follows Windows.
        public static Font UiFont { get { return LunaFonts.Ui; } }

        // Per-control-type overloads that stamp the palette onto a live control. WinForms
        // has no CSS, so theming is applied imperatively by walking the control tree.
        public static void ApplyTo(Form form)
        {
            form.BackColor = MainBackground;
            form.ForeColor = TextPrimary;
        }

        public static void ApplyTo(TreeView tree)
        {
            tree.BackColor = TreeBackground;
            tree.ForeColor = TextPrimary;
            tree.LineColor = TreeLineColor;
            tree.BorderStyle = BorderStyle.None;
        }

        public static void ApplyTo(ListView listView)
        {
            listView.BackColor = GridBackground;
            listView.ForeColor = TextPrimary;
            listView.BorderStyle = BorderStyle.None;
        }

        public static void ApplyTo(SplitContainer splitter)
        {
            splitter.BackColor = SplitterColor;
        }

        public static void ApplyTo(TabControl tab)
        {
            tab.BackColor = PanelBackground;
            tab.ForeColor = TextPrimary;
        }

        public static void ApplyTo(Panel panel)
        {
            panel.BackColor = PanelBackground;
            panel.ForeColor = TextPrimary;
        }
    }
}
