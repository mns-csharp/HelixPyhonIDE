namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;

    /// <summary>The three shipped Luna accent themes.</summary>
    public enum LunaThemeKind { Blue, Green, Crimson }

    /// <summary>
    /// The theme-varying "accent" colors — the caption gradient, window frame, status bar, active
    /// tab and selection tones. The neutral surface (the tan face, body text, white inputs, borders)
    /// is identical across themes and stays hard-coded in LunaForm / LunaColors. Living in the
    /// low-level UI assembly lets BOTH LunaForm (here) and HelixPythonNamespace.App.LunaColors (which references
    /// this assembly) read a single active palette, so a theme switch recolors the whole app at once.
    /// </summary>
    public sealed class LunaPalette
    {
        public Color CaptionLeft;        // caption gradient endpoints (deep)
        public Color CaptionMid;         // caption gradient centre (bright)
        public Color CaptionRight;
        public Color CaptionHighlight;   // glossy top band
        public Color CaptionInactiveLeft;
        public Color CaptionInactiveRight;
        public Color Frame;              // solid window border (== deep accent)
        public Color Accent;             // deep accent: frame, accent text, check marks
        public Color TitleBar;           // mid tone: title-bar fill, status/tab gradient top
        public Color TabActive;          // selected tab background
        public Color StatusTop;          // status-bar gradient top
        public Color StatusBottom;       // status-bar gradient bottom (== solid status background)
        public Color Hover;
        public Color Selection;
        public Color Pressed;
    }

    /// <summary>Registry + active selection for the Luna accent themes.</summary>
    public static class LunaThemes
    {
        // Authentic XP "Luna Blue" — the original palette, unchanged.
        public static readonly LunaPalette Blue = new LunaPalette
        {
            CaptionLeft = Color.FromArgb(10, 82, 214),
            CaptionMid = Color.FromArgb(61, 143, 240),
            CaptionRight = Color.FromArgb(10, 82, 214),
            CaptionHighlight = Color.FromArgb(120, 180, 255),
            CaptionInactiveLeft = Color.FromArgb(157, 181, 232),
            CaptionInactiveRight = Color.FromArgb(195, 211, 242),
            Frame = Color.FromArgb(0, 70, 213),
            Accent = Color.FromArgb(0, 70, 213),
            TitleBar = Color.FromArgb(61, 143, 240),
            TabActive = Color.FromArgb(24, 84, 212),
            StatusTop = Color.FromArgb(61, 143, 240),
            StatusBottom = Color.FromArgb(10, 82, 214),
            Hover = Color.FromArgb(49, 106, 197),
            Selection = Color.FromArgb(49, 106, 197),
            Pressed = Color.FromArgb(43, 90, 160),
        };

        // Bottle Green — a deep, slightly teal green in the same value structure as Blue.
        public static readonly LunaPalette Green = new LunaPalette
        {
            CaptionLeft = Color.FromArgb(8, 96, 62),
            CaptionMid = Color.FromArgb(40, 150, 100),
            CaptionRight = Color.FromArgb(8, 96, 62),
            CaptionHighlight = Color.FromArgb(150, 224, 188),
            CaptionInactiveLeft = Color.FromArgb(165, 200, 183),
            CaptionInactiveRight = Color.FromArgb(200, 224, 212),
            Frame = Color.FromArgb(6, 82, 52),
            Accent = Color.FromArgb(6, 82, 52),
            TitleBar = Color.FromArgb(40, 150, 100),
            TabActive = Color.FromArgb(16, 104, 68),
            StatusTop = Color.FromArgb(40, 150, 100),
            StatusBottom = Color.FromArgb(8, 96, 62),
            Hover = Color.FromArgb(34, 116, 82),
            Selection = Color.FromArgb(34, 116, 82),
            Pressed = Color.FromArgb(22, 84, 58),
        };

        // Crimson Red — a deep crimson in the same value structure.
        public static readonly LunaPalette Crimson = new LunaPalette
        {
            CaptionLeft = Color.FromArgb(150, 20, 34),
            CaptionMid = Color.FromArgb(200, 55, 72),
            CaptionRight = Color.FromArgb(150, 20, 34),
            CaptionHighlight = Color.FromArgb(255, 170, 180),
            CaptionInactiveLeft = Color.FromArgb(222, 178, 184),
            CaptionInactiveRight = Color.FromArgb(238, 205, 210),
            Frame = Color.FromArgb(140, 16, 30),
            Accent = Color.FromArgb(140, 16, 30),
            TitleBar = Color.FromArgb(200, 55, 72),
            TabActive = Color.FromArgb(168, 28, 44),
            StatusTop = Color.FromArgb(200, 55, 72),
            StatusBottom = Color.FromArgb(150, 20, 34),
            Hover = Color.FromArgb(176, 48, 62),
            Selection = Color.FromArgb(176, 48, 62),
            Pressed = Color.FromArgb(140, 30, 44),
        };

        /// <summary>The currently active palette (defaults to Blue).</summary>
        public static LunaPalette Current { get; private set; } = Blue;

        /// <summary>The currently active theme kind.</summary>
        public static LunaThemeKind Kind { get; private set; } = LunaThemeKind.Blue;

        /// <summary>Switch the active accent theme. Callers repaint open windows to show the change.</summary>
        public static void Apply(LunaThemeKind kind)
        {
            Kind = kind;
            Current = Palette(kind);
        }

        public static LunaPalette Palette(LunaThemeKind kind)
        {
            switch (kind)
            {
                case LunaThemeKind.Green: return Green;
                case LunaThemeKind.Crimson: return Crimson;
                default: return Blue;
            }
        }

        /// <summary>Parse a persisted theme name (case-insensitive); unknown values fall back to Blue.</summary>
        public static LunaThemeKind FromName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                if (name.Equals("Green", StringComparison.OrdinalIgnoreCase)) return LunaThemeKind.Green;
                if (name.Equals("Crimson", StringComparison.OrdinalIgnoreCase)) return LunaThemeKind.Crimson;
            }
            return LunaThemeKind.Blue;
        }

        /// <summary>The human-readable name for a theme, as shown in Preferences.</summary>
        public static string DisplayName(LunaThemeKind kind)
        {
            switch (kind)
            {
                case LunaThemeKind.Green: return "Bottle Green";
                case LunaThemeKind.Crimson: return "Crimson Red";
                default: return "Luna Blue";
            }
        }
    }
}
