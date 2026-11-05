using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The application icon is one file -- assets/product/app/app_main.ico -- reaching the user
    /// three ways: the HelixPython.exe resource, the window and taskbar icon, and the tray icon.
    ///
    /// <para>
    /// These tests exist because that icon was ABSENT for an unknown length of time and nothing
    /// said so. <c>LunaForm.GetAppIcon</c> asked for the embedded resource by the literal
    /// "HelixPythonNamespace.UI.app_main.ico" while the csproj embedded it as
    /// "HelixStudio.UI.app_main.ico" -- the namespace migration renamed the code and not the
    /// resource. <c>GetManifestResourceStream</c> returns null for a name it does not know rather
    /// than throwing, the lookup was inside a catch-all, and the fallbacks were deliberately quiet:
    /// every window took the default WinForms glyph and the tray took SystemIcons.Application. A
    /// build that is branded and a build that is not are indistinguishable to every other test in
    /// this assembly, so the failure had no way to surface. It does now.
    /// </para>
    /// </summary>
    [TestClass]
    public class AppIconTests
    {
        // Every size the Windows shell asks for. 20 and 40 are 16 and 32 at 125% and 250% DPI;
        // without them the shell box-filters a neighbouring frame, which is what makes a
        // single-frame .ico look muddy in the tray while looking correct on the desktop.
        private static readonly int[] ExpectedFrames = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        private static string IconPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "Could not find the repository root from the test output directory.");
            return Path.Combine(dir.FullName, "assets", "product", "app", "app_main.ico");
        }

        [TestMethod]
        public void AppIcon_IsEmbeddedAndResolves()
        {
            Icon icon = LunaForm.GetAppIcon();
            Assert.IsNotNull(icon,
                "LunaForm.GetAppIcon returned null: the icon resource embedded in HelixStudio.UI " +
                "could not be found. Every window, the taskbar and the tray are unbranded.");
        }

        [TestMethod]
        public void AppIcon_ResourceIsFoundByNameNotByAStaleLiteral()
        {
            // The resource's own name is the csproj's business and may change again. What must hold
            // is that exactly one icon resource is embedded and the code finds it -- which is why
            // GetAppIcon matches on the file name rather than on a fully qualified literal.
            string[] icons = typeof(LunaForm).Assembly.GetManifestResourceNames()
                .Where(n => n.EndsWith("app_main.ico", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.AreEqual(1, icons.Length,
                "Expected exactly one embedded app_main.ico in HelixStudio.UI, found: " +
                (icons.Length == 0 ? "(none)" : string.Join(", ", icons)));
        }

        [TestMethod]
        public void TrayIcon_IsTheSmallFrameNotADownscaledLargeOne()
        {
            Icon tray = LunaForm.GetTrayIcon();
            Assert.IsNotNull(tray, "LunaForm.GetTrayIcon returned null; the tray would be unbranded.");
            Assert.AreEqual(SystemInformation.SmallIconSize.Width, tray.Width,
                "The tray icon must be the shell's small-icon size. A NotifyIcon hands its icon to " +
                "the shell as-is, so anything larger is reduced there with a box filter.");
            Assert.AreEqual(SystemInformation.SmallIconSize.Height, tray.Height, "tray icon height");
        }

        [TestMethod]
        public void AppIcon_CarriesEveryFrameTheShellAsksFor()
        {
            // Read the .ico directory directly: the header's 5th and 6th bytes are the image count,
            // then each 16-byte entry starts with its width and height (0 meaning 256). GDI+ cannot
            // report this -- new Icon(icon, 256, 256) silently hands back the 128 frame -- so the
            // file is the only honest source.
            byte[] bytes = File.ReadAllBytes(IconPath());
            int count = BitConverter.ToUInt16(bytes, 4);
            var frames = new int[count];
            for (int i = 0; i < count; i++)
            {
                int width = bytes[6 + (i * 16)];
                frames[i] = width == 0 ? 256 : width;
            }

            Array.Sort(frames);
            CollectionAssert.AreEqual(ExpectedFrames.OrderBy(f => f).ToArray(), frames,
                "app_main.ico must include the expected frames; " +
                "found: " + string.Join(", ", frames.Select(f => f.ToString()).ToArray()));
        }
    }
}
