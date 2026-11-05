using System.Drawing;

namespace HelixPythonNamespace.Terminal.App.Terminal;

/// <summary>The xterm colour table, used to turn SGR codes into pixels.</summary>
public static class TerminalPalette
{
    private static readonly Color[] Normal =
    {
        Color.FromArgb(0x0C, 0x0C, 0x0C), // black
        Color.FromArgb(0xC5, 0x0F, 0x1F), // red
        Color.FromArgb(0x13, 0xA1, 0x0E), // green
        Color.FromArgb(0xC1, 0x9C, 0x00), // yellow
        Color.FromArgb(0x00, 0x37, 0xDA), // blue
        Color.FromArgb(0x88, 0x17, 0x98), // magenta
        Color.FromArgb(0x3A, 0x96, 0xDD), // cyan
        Color.FromArgb(0xCC, 0xCC, 0xCC), // white
    };

    private static readonly Color[] Bright =
    {
        Color.FromArgb(0x76, 0x76, 0x76),
        Color.FromArgb(0xE7, 0x48, 0x56),
        Color.FromArgb(0x16, 0xC6, 0x0C),
        Color.FromArgb(0xF9, 0xF1, 0xA5),
        Color.FromArgb(0x3B, 0x78, 0xFF),
        Color.FromArgb(0xB4, 0x00, 0x9E),
        Color.FromArgb(0x61, 0xD6, 0xD6),
        Color.FromArgb(0xF2, 0xF2, 0xF2),
    };

    private static readonly Color[] Extended256 = BuildExtended();

    public static Color Standard(int index, bool bright) =>
        (bright ? Bright : Normal)[Math.Clamp(index, 0, 7)];

    public static Color Extended(int index) => Extended256[Math.Clamp(index, 0, 255)];

    private static Color[] BuildExtended()
    {
        var colours = new Color[256];

        for (var i = 0; i < 8; i++) colours[i] = Normal[i];
        for (var i = 0; i < 8; i++) colours[i + 8] = Bright[i];

        // 6x6x6 colour cube.
        var levels = new[] { 0, 95, 135, 175, 215, 255 };
        var index = 16;

        foreach (var r in levels)
        foreach (var g in levels)
        foreach (var b in levels)
            colours[index++] = Color.FromArgb(r, g, b);

        // 24 steps of grey.
        for (var i = 0; i < 24; i++)
        {
            var value = 8 + i * 10;
            colours[232 + i] = Color.FromArgb(value, value, value);
        }

        return colours;
    }
}
