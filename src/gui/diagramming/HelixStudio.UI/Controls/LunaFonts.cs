namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;

    /// <summary>
    /// The IDE's UI typography, taken from the OPERATING SYSTEM rather than hard-coded.
    ///
    /// The chrome used to specify "Tahoma" at 8.25–11pt throughout (the authentic XP Luna face),
    /// which reads as foreign on Windows 10/11 where the shell font is Segoe UI 9pt. The colour
    /// skins are unaffected — only the faces and sizes come from the system now.
    ///
    /// Everything is derived from <see cref="SystemFonts.MessageBoxFont"/>, so the IDE follows the
    /// user's shell font and their text-scaling setting automatically. Fonts are cached: a Font is a
    /// GDI handle, and creating one per paint or per control leaks handles.
    /// </summary>
    public static class LunaFonts
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        /// <summary>The shell UI font (Segoe UI 9pt on a stock Windows 10/11).</summary>
        public static Font Ui
        {
            get { return Derived(0f, FontStyle.Regular); }
        }

        /// <summary>The shell UI font, bold — for headings and emphasised labels.</summary>
        public static Font UiBold
        {
            get { return Derived(0f, FontStyle.Bold); }
        }

        /// <summary>
        /// The shell UI font, <paramref name="deltaPt"/> points larger (or smaller). Use this instead
        /// of a literal size so the relative hierarchy survives a different shell font or DPI.
        /// </summary>
        public static Font Sized(float deltaPt)
        {
            return Derived(deltaPt, FontStyle.Regular);
        }

        /// <summary>Bold variant of <see cref="Sized"/>.</summary>
        public static Font SizedBold(float deltaPt)
        {
            return Derived(deltaPt, FontStyle.Bold);
        }

        /// <summary>Large product/heading type (e.g. the Welcome page title).</summary>
        public static Font Title
        {
            get { return Derived(BaseSize * 1.4f, FontStyle.Bold); }
        }

        /// <summary>Point size of the shell UI font — the baseline every derived size is relative to.</summary>
        public static float BaseSize
        {
            get
            {
                Font f = SystemFonts.MessageBoxFont;
                return f != null ? f.SizeInPoints : 9f;
            }
        }

        private static Font Derived(float deltaPt, FontStyle style)
        {
            string key = deltaPt.ToString("0.##") + "|" + (int)style;
            lock (Gate)
            {
                Font cached;
                if (Cache.TryGetValue(key, out cached)) return cached;

                Font shell = SystemFonts.MessageBoxFont;
                string family = shell != null ? shell.FontFamily.Name : "Segoe UI";
                float size = Math.Max(6f, (shell != null ? shell.SizeInPoints : 9f) + deltaPt);

                Font made;
                try { made = new Font(family, size, style); }
                catch { made = new Font(FontFamily.GenericSansSerif, size, style); }

                Cache[key] = made;
                return made;
            }
        }
    }
}
