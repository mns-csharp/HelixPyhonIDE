using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 397 (SPPFE Slice 1, Rule 1): the Solution Explorer gives each package a language/kind
    /// icon (from assets/product/tree) instead of one generic project glyph, so App / GUI / Flask /
    /// library packages are visually distinguishable. Asserted at the ImageKey level (rendering the
    /// bitmap needs the assets on disk; the key mapping is deterministic).
    /// </summary>
    [TestClass]
    public class ProjectTreeKindIconTests
    {
        private static ProjectTreePanel LoadKinds(params (string name, ProjectKind kind)[] pkgs)
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            foreach (var p in pkgs)
                solution.Projects.Add(new ProjectModel(@"C:\ws\src\" + p.name + @"\pyproject.toml", p.name, solution) { Kind = p.kind });
            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        [TestMethod]
        public void EachKind_GetsADistinctImageKey()
        {
            using (var panel = LoadKinds(
                ("cli", ProjectKind.App),
                ("gui", ProjectKind.GuiApp),
                ("web", ProjectKind.FlaskWebApp),
                ("lib", ProjectKind.ClassLibrary)))
            {
                TreeNode sol = Tree(panel).Nodes[0];
                string appKey = ChildImageKey(sol, "cli");
                string guiKey = ChildImageKey(sol, "gui");
                string webKey = ChildImageKey(sol, "web");
                string libKey = ChildImageKey(sol, "lib");

                // All four kinds map to different image keys — no longer one shared "project" glyph.
                var keys = new[] { appKey, guiKey, webKey, libKey };
                CollectionAssert.AllItemsAreUnique(keys, "each kind should have its own icon key");
                foreach (string k in keys) Assert.IsFalse(string.IsNullOrEmpty(k));
            }
        }

        [TestMethod]
        public void SolutionNode_UsesThePythonSolutionIcon()
        {
            using (var panel = LoadKinds(("lib", ProjectKind.ClassLibrary)))
            {
                Assert.AreEqual("pySolution", Tree(panel).Nodes[0].ImageKey);
            }
        }

        [TestMethod]
        public void StartupPackage_KeepsThePlayGlyph_NotTheKindIcon()
        {
            using (var panel = LoadKinds(("cli", ProjectKind.App), ("srv", ProjectKind.App)))
            {
                TreeNode sol = Tree(panel).Nodes[0];
                // A sole-executable-less pair: mark one startup explicitly through the model.
                // (LoadKinds leaves IsStartup false; set it and reload to exercise the branch.)
                var solution = (SolutionModel)sol.Tag;
                solution.Projects[0].IsStartup = true;
                panel.LoadSolution(solution);
                sol = Tree(panel).Nodes[0];
                Assert.AreEqual("startupProject", ChildImageKey(sol, "cli"));
                Assert.AreEqual("pkgApp", ChildImageKey(sol, "srv"));
            }
        }

        /// <summary>
        /// Every type-declaration kind renders a real alphabet badge, NOT the coloured-square
        /// placeholder <c>AddKindIcon</c> substitutes when an asset is missing. Struct used to be
        /// the exception — there was no app_S.ico, so `struct` rows drew an unlabeled block beside
        /// lettered [C]/[I]/[A]/[E] siblings, which in a Rust tree meant no type row was ever
        /// labelled at all (Rust's only concrete-type construct is `struct`). Distinguishing the
        /// two at the ImageKey level is impossible — both register under the same key — so this
        /// asserts on the registered bitmap's colour count. <c>CreateIcon</c> paints one solid
        /// rectangle plus a one-pixel border with no antialiasing: exactly three ARGB values,
        /// transparent included. Every badge asset is a 256x256 vertical gradient with gloss and a
        /// white glyph, bicubic-scaled to 16x16 — hundreds. No threshold in between is delicate.
        /// </summary>
        [TestMethod]
        public void EveryTypeKind_UsesALetterBadgeAsset_NotThePlaceholderSquare()
        {
            using (var panel = LoadKinds(("lib", ProjectKind.ClassLibrary)))
            {
                ImageList images = Tree(panel).ImageList;
                Assert.IsNotNull(images, "tree image list");
                foreach (string key in new[] { "class", "interface", "abstractclass", "enum", "struct" })
                {
                    Assert.IsTrue(images.Images.ContainsKey(key), key + ": registered");
                    using (var bmp = new Bitmap(images.Images[key]))
                    {
                        var colors = new HashSet<int>();
                        for (int x = 0; x < bmp.Width; x++)
                            for (int y = 0; y < bmp.Height; y++)
                                colors.Add(bmp.GetPixel(x, y).ToArgb());
                        Assert.IsTrue(colors.Count > 16,
                            key + ": only " + colors.Count + " distinct colours — this is the " +
                            "coloured-square placeholder, so its badge asset is missing from " +
                            "assets/product/tree and assets/product/app");
                    }
                }
            }
        }

        private static string ChildImageKey(TreeNode solutionNode, string projectName)
        {
            foreach (TreeNode n in solutionNode.Nodes)
                if (n.Tag is ProjectModel pm && pm.Name == projectName) return n.ImageKey;
            return null;
        }
    }
}
