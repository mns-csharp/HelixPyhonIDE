using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Tests run while the user works on this machine, so no test window may reach the user's
    /// screen, mouse or keyboard (book Chapter 4, §4.6.5). <see cref="TestWindow.ShowOffScreen"/> is
    /// the one way a test shows a window; these tests pin what it promises and that nothing else is used.
    /// </summary>
    [TestClass]
    public class TestWindowGuardTests
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
        private const int GA_ROOT = 2;

        private string _stateDir;
        private string _previousStateDir;

        [TestInitialize]
        public void IsolateState()
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "helix-testwindow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDir);
            _previousStateDir = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _stateDir);
        }

        [TestCleanup]
        public void RestoreState()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _previousStateDir);
            try { Directory.Delete(_stateDir, true); } catch { }
        }

        /// <summary>
        /// The composed IDE (a <c>LunaForm</c>, whose <c>OnShown</c> pulls an off-screen window onto
        /// the desktop) is shown, stays off every screen, is not in front of the user even after a
        /// test gives it focus, and still draws a real picture for the screenshot suites.
        /// </summary>
        [TestMethod]
        public void ShowOffScreen_ShowsTheComposedIde_OffEveryScreen_NeverInFront_AndStillDrawable()
        {
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                form.Size = new Size(1200, 800);
                TestWindow.ShowOffScreen(form);
                IntPtr hwnd = form.Handle;

                Assert.IsTrue(form.Visible, "the helper must really show the form");
                Assert.AreNotEqual(hwnd, GetForegroundWindow(), "a shown test window must not take the foreground");
                Assert.AreNotEqual(hwnd, GetActiveWindow(), "the helper shows without activation");
                foreach (Screen screen in Screen.AllScreens)
                {
                    Assert.IsFalse(screen.WorkingArea.IntersectsWith(form.Bounds),
                        "the shown form " + form.Bounds + " is on " + screen.DeviceName + " (" + screen.WorkingArea + ")");
                    Assert.IsFalse(screen.Bounds.IntersectsWith(form.Bounds),
                        "the shown form " + form.Bounds + " overlaps " + screen.DeviceName + " (" + screen.Bounds + ")");
                }
                Assert.AreNotEqual(hwnd, GetAncestor(WindowFromPoint(Cursor.Position), GA_ROOT),
                    "the form must not be under the user's pointer");

                // Tests focus controls (editor.Focus() and the like). That activates the window
                // inside this thread only; the user keeps the foreground and the keyboard.
                form.Focus();
                Application.DoEvents();
                Assert.AreNotEqual(hwnd, GetForegroundWindow(), "focusing a test window must not bring it to the front");

                // Opacity stays 0 for the whole test; DrawToBitmap must still paint the real IDE.
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    var colours = new HashSet<int>();
                    for (int x = 0; x < bitmap.Width; x += 23)
                        for (int y = 0; y < bitmap.Height; y += 23)
                            colours.Add(bitmap.GetPixel(x, y).ToArgb());
                    Assert.IsTrue(colours.Count > 3,
                        "DrawToBitmap at Opacity 0 gave a flat picture (" + colours.Count + " colours)");
                }
            }
            finally { FixtureSolution.TearDown(form); }
        }

        // A raw way to put a window on screen: Show()/Show(owner), ShowDropDown(), ShowDialog, Activate,
        // Application.Run, Visible = true. Whole-line comments and lines marked
        // "offscreen-guard: <reason>" are skipped.
        private static readonly Regex RawShow = new Regex(
            @"\.Show\(\s*([A-Za-z_]\w*)?\s*\)|\.ShowDropDown\(\s*\)|\.ShowDialog\(|\.Activate\(\s*\)|Application\.Run\(|(?<!\w)Visible\s*=\s*true");

        /// <summary>
        /// Every test project's sources: a test shows a window only through
        /// <see cref="TestWindow.ShowOffScreen"/>. Names each offending file:line.
        /// </summary>
        [TestMethod]
        public void NoTestShowsAWindowExceptThroughTestWindow()
        {
            string tests = Directory.GetParent(FixtureSolution.Root()).FullName;
            var offenders = new List<string>();
            var files = new List<string>();
            foreach (string dir in Directory.GetDirectories(tests))
                if (!string.Equals(Path.GetFileName(dir), "fixtures", StringComparison.OrdinalIgnoreCase))
                    files.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
            foreach (string file in files)
            {
                string relative = file.Substring(tests.Length + 1);
                if (relative.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (relative.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string name = Path.GetFileName(file);
                if (name == "TestWindow.cs" || name == "TestWindowGuardTests.cs") continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("//") || line.StartsWith("*") || line.Contains("offscreen-guard:")) continue;
                    if (RawShow.IsMatch(line)) offenders.Add("tests\\" + relative + ":" + (i + 1) + ": " + line.Trim());
                }
            }
            Assert.AreEqual(0, offenders.Count,
                "show test windows only through TestWindow.ShowOffScreen (book Chapter 4, 4.6.5); raw shows:\n"
                + string.Join("\n", offenders));
        }
    }
}
