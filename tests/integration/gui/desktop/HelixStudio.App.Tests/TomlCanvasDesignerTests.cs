using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 326 (user directive): the TOML designer must be the GDI+ canvas, like every other
    /// language surface — not the tree + form editor it used to be.
    ///
    /// The canvas path already existed end to end (TomlParser -> TomlTypeExtractor -> DiagramCanvas,
    /// with OopClassCanvasForm.OpenTomlConfigOnCanvas as a ready entry point including a file picker
    /// and a solution-file fast path) but nothing in the product called it: the Tools menu opened the
    /// tree/form TomlDesignerForm instead. This pins the wiring.
    /// </summary>
    [TestClass]
    public class TomlCanvasDesignerTests
    {
        private static ToolStripItem FindToolsItem(MainForm form, string fragment)
        {
            var strip = (MenuStrip)typeof(MainForm)
                .GetField("_menuStrip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(form);

            foreach (ToolStripItem top in strip.Items)
            {
                var menu = top as ToolStripMenuItem;
                if (menu == null || (menu.Text ?? "").Replace("&", "") != "Tools") continue;
                TestWindow.ShowDropDown(menu);
                menu.HideDropDown();
                foreach (ToolStripItem sub in menu.DropDownItems)
                    if ((sub.Text ?? "").Replace("&", "").IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                        return sub;
            }
            return null;
        }

        [TestMethod]
        public void ToolsMenu_OffersTheCanvas_NotTheTreeFormDesigner()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.IsNotNull(FindToolsItem(form, "TOML Config Canvas"),
                    "Tools should offer the GDI+ TOML canvas");
                Assert.IsNull(FindToolsItem(form, "TOML Designer"),
                    "the tree/form TOML designer must no longer be offered");
            }
        }

        [TestMethod]
        public void TheCanvasEntry_IsAddedExactlyOnce()
        {
            // EnsureExtraToolsItems is idempotent by scanning existing labels; renaming the item
            // without updating that detector would silently add a second copy every build.
            using (var form = TestMainForm.CreateComposed())
            {
                var strip = (MenuStrip)typeof(MainForm)
                    .GetField("_menuStrip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .GetValue(form);

                int count = 0;
                foreach (ToolStripItem top in strip.Items)
                {
                    var menu = top as ToolStripMenuItem;
                    if (menu == null || (menu.Text ?? "").Replace("&", "") != "Tools") continue;
                    TestWindow.ShowDropDown(menu); menu.HideDropDown();
                    count += menu.DropDownItems.Cast<ToolStripItem>()
                        .Count(i => (i.Text ?? "").Replace("&", "")
                            .IndexOf("TOML Config Canvas", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                Assert.AreEqual(1, count, "the TOML canvas entry should appear exactly once");
            }
        }

        [TestMethod]
        public void ARealPyprojectRendersAsCanvasTypes()
        {
            // The projection the canvas draws: tables become types, keys become members.
            string toml =
                "[project]\nname = \"demo\"\nversion = \"1.0\"\n\n" +
                "[tool.helixstudio]\nkind = \"app\"\n";

            var sln = new HelixPythonNamespace.Core.Model.SolutionModel(
                Path.Combine(Path.GetTempPath(), "t.sln"), "T");
            var proj = new HelixPythonNamespace.Core.Model.ProjectModel(
                Path.Combine(Path.GetTempPath(), "pyproject.toml"), "toml_config", sln);
            sln.Projects.Add(proj);

            var doc = HelixPythonNamespace.Workspace.Toml.TomlParser.Parse(toml);
            HelixPythonNamespace.Workspace.Toml.TomlTypeExtractor.ExtractInto(proj, doc, "pyproject.toml");

            Assert.IsTrue(proj.Types.Count >= 2, "each TOML table should become a canvas type");
            var project = proj.Types.FirstOrDefault(t => t.Name == "project");
            Assert.IsNotNull(project, "the [project] table should render as a box");
            Assert.IsTrue(project.Members.Any(m => m.Name == "name"),
                "the table's keys should render as members inside the box");
        }
    }
}
