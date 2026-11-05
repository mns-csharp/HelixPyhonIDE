using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 322 (user report: "the fonts look incompatible with the OS"). The chrome hard-coded
    /// Tahoma 8.25–11pt — the authentic XP Luna face, but foreign on Windows 10/11 where the shell
    /// font is Segoe UI 9pt. Typography now derives from SystemFonts.MessageBoxFont via LunaFonts,
    /// while the three colour skins are untouched.
    /// </summary>
    [TestClass]
    public class OsFontTests
    {
        private static string ShellFamily
        {
            get { return SystemFonts.MessageBoxFont.FontFamily.Name; }
        }

        [TestMethod]
        public void LunaFonts_FollowTheShellFont()
        {
            Assert.AreEqual(ShellFamily, LunaFonts.Ui.FontFamily.Name);
            Assert.AreEqual(ShellFamily, LunaFonts.UiBold.FontFamily.Name);
            Assert.AreEqual(ShellFamily, LunaFonts.Title.FontFamily.Name);
            Assert.AreEqual(SystemFonts.MessageBoxFont.SizeInPoints, LunaFonts.BaseSize, 0.01);
            Assert.IsTrue(LunaFonts.UiBold.Bold);
        }

        [TestMethod]
        public void DerivedSizes_AreRelativeToTheShellFont_NotAbsolute()
        {
            Assert.AreEqual(LunaFonts.BaseSize + 1.5f, LunaFonts.Sized(1.5f).SizeInPoints, 0.01);
            Assert.AreEqual(LunaFonts.BaseSize - 0.5f, LunaFonts.Sized(-0.5f).SizeInPoints, 0.01);
            Assert.IsTrue(LunaFonts.Title.SizeInPoints > LunaFonts.BaseSize,
                "the Welcome title should still be the largest step in the hierarchy");
        }

        [TestMethod]
        public void FontsAreCached_SoRepaintsDoNotLeakGdiHandles()
        {
            // A Font is a GDI handle; the caption repaints constantly, so these must be shared.
            Assert.AreSame(LunaFonts.Ui, LunaFonts.Ui);
            Assert.AreSame(LunaFonts.UiBold, LunaFonts.UiBold);
            Assert.AreSame(LunaFonts.Sized(1f), LunaFonts.Sized(1f));
        }

        [TestMethod]
        public void TheMainWindowChrome_UsesTheShellFont()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.AreEqual(ShellFamily, form.Font.FontFamily.Name,
                    "the form font is what every child control inherits");

                var strip = Field<MenuStrip>(form, "_menuStrip");
                Assert.AreEqual(ShellFamily, strip.Font.FontFamily.Name);

                var status = Field<StatusStrip>(form, "_statusStrip");
                Assert.AreEqual(ShellFamily, status.Font.FontFamily.Name);
            }
        }

        [TestMethod]
        public void TheColourSkinsAreUnaffected()
        {
            // Typography changed; the three Luna palettes must not have.
            LunaThemes.Apply(LunaThemeKind.Blue);
            Assert.AreEqual(Color.FromArgb(10, 82, 214), LunaThemes.Current.StatusBottom);
            LunaThemes.Apply(LunaThemeKind.Green);
            Assert.AreEqual(Color.FromArgb(8, 96, 62), LunaThemes.Current.StatusBottom);
            LunaThemes.Apply(LunaThemeKind.Crimson);
            Assert.AreEqual(Color.FromArgb(150, 20, 34), LunaThemes.Current.StatusBottom);
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
