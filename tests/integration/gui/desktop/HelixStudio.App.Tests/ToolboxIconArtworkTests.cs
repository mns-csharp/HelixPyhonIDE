using HelixPythonNamespace.AddIns.GuiDesigner;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The GUI Designer Canvas toolbox's drawn icons: one PNG per control in
    /// <c>assets/product/toolbox</c>, looked up by <c>GuiControlKind</c> name.
    ///
    /// <para>
    /// The failure this exists to catch is silent. A file named for a kind that does not exist —
    /// a typo, a rename, a plural — is simply never asked for, and the toolbox row keeps the old
    /// two-letter badge. Nothing throws, nothing logs, and the row looks like a control whose icon
    /// was never drawn rather than one whose icon is sitting on disk under the wrong name. The
    /// only way to see it is to compare the two sets by name.
    /// </para>
    /// </summary>
    [TestClass]
    public class ToolboxIconArtworkTests
    {
        /// <summary>Every image key the toolbox asks for: "Pointer" plus one per entry.</summary>
        private static List<string> ToolboxKeys()
        {
            FieldInfo f = typeof(GuiDesignerForm).GetField("ToolboxItems",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "GuiDesignerForm.ToolboxItems should still exist");

            var keys = new List<string> { "Pointer" };
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (object entry in (Array)f.GetValue(null))
            {
                // Kind is a field today and was a property in an earlier shape of this type.
                // Accepting either keeps this fixture about the icons rather than about which of
                // the two it happens to be.
                Type t = entry.GetType();
                FieldInfo field = t.GetField("Kind", Any);
                PropertyInfo property = field == null ? t.GetProperty("Kind", Any) : null;
                Assert.IsTrue(field != null || property != null, "ToolboxEntry.Kind should still exist");
                object value = field != null ? field.GetValue(entry) : property.GetValue(entry, null);
                keys.Add(value.ToString());
            }
            return keys;
        }

        private static string ArtworkDirectory()
        {
            // Same walk the loader does: up from the test output to the repository marker.
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "assets", "product", "toolbox");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        [TestMethod]
        public void EveryArtworkFile_NamesAToolboxControl()
        {
            string dir = ArtworkDirectory();
            Assert.IsNotNull(dir, "assets/product/toolbox should be findable from the test output");

            var keys = new HashSet<string>(ToolboxKeys(), StringComparer.Ordinal);
            var orphans = Directory.GetFiles(dir, "*.png")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !keys.Contains(name))
                .ToArray();

            Assert.AreEqual(0, orphans.Length,
                "these PNGs are named for nothing the toolbox asks for, so they are never loaded "
                + "and the control keeps its old badge: " + string.Join(", ", orphans));
        }

        [TestMethod]
        public void TheArtworkIs16x16WithAnAlphaChannel()
        {
            string dir = ArtworkDirectory();
            Assert.IsNotNull(dir);

            foreach (string path in Directory.GetFiles(dir, "*.png"))
            {
                using (var bmp = new Bitmap(path))
                {
                    string name = Path.GetFileName(path);
                    Assert.AreEqual(16, bmp.Width, name + " is not 16px wide");
                    Assert.AreEqual(16, bmp.Height, name + " is not 16px tall");
                    // The list draws on the toolbox's own grey; an opaque tile would show its
                    // square corners against it.
                    Assert.IsTrue((bmp.PixelFormat & System.Drawing.Imaging.PixelFormat.Alpha)
                                  == System.Drawing.Imaging.PixelFormat.Alpha
                                  || bmp.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                        name + " has no alpha channel");
                }
            }
        }

        /// <summary>
        /// The image list the designer actually builds has an entry for every key the toolbox will
        /// ask for. A missing entry is not an exception — ListView draws nothing — so this is the
        /// assertion that keeps the fallback chain honest.
        /// </summary>
        [TestMethod]
        public void TheImageList_CoversEveryToolboxKey()
        {
            MethodInfo build = typeof(GuiDesignerForm).GetMethod("BuildToolboxImages",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(build, "GuiDesignerForm.BuildToolboxImages should still exist");

            using (var images = (ImageList)build.Invoke(null, null))
            {
                var missing = ToolboxKeys().Where(k => !images.Images.ContainsKey(k)).ToArray();
                Assert.AreEqual(0, missing.Length,
                    "no image for: " + string.Join(", ", missing));
            }
        }

        /// <summary>
        /// The drawn set is actually being used — not sitting on disk while every row still shows
        /// a generated badge. Asserted through the loader the designer uses, so a broken search
        /// path fails here rather than showing up as an unchanged-looking toolbox.
        /// </summary>
        [TestMethod]
        public void TheDrawnArtwork_IsWhatTheToolboxLoads()
        {
            MethodInfo artwork = typeof(GuiDesignerForm).GetMethod("ToolboxArtwork",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(artwork, "GuiDesignerForm.ToolboxArtwork should still exist");

            foreach (string key in new[] { "Pointer", "Button", "CheckBox", "TreeView", "ColorDialog" })
                using (var img = (Image)artwork.Invoke(null, new object[] { key }))
                    Assert.IsNotNull(img, key + " should load its drawn icon from disk");

            Assert.IsNull(artwork.Invoke(null, new object[] { "NoSuchControlKind" }),
                "a key with no file must return null so the caller can fall back");
            Assert.IsNull(artwork.Invoke(null, new object[] { null }));
        }
    }
}
