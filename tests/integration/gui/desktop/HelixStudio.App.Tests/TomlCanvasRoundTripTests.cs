using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Workspace.Toml;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 328. The canvas rendered a TOML config but could not change it — a viewer, not a
    /// designer. Double-clicking a table box now opens its scalar keys and the edit is written back
    /// through TomlWriter. These cover the two halves that make that a real round-trip: mapping a
    /// canvas box title back to its table, and parse -> edit -> write -> re-parse preserving the
    /// rest of the document.
    /// </summary>
    [TestClass]
    public class TomlCanvasRoundTripTests
    {
        private const string Sample =
            "[project]\n" +
            "name = \"demo\"\n" +
            "version = \"0.1.0\"\n" +
            "\n" +
            "[tool.helixstudio]\n" +
            "kind = \"app\"\n" +
            "\n" +
            "[[tool.helixstudio.targets]]\n" +
            "name = \"cli\"\n";

        [TestMethod]
        public void ACanvasBoxTitle_ResolvesBackToItsTable()
        {
            TomlDocument doc = TomlParser.Parse(Sample);

            Assert.AreSame(doc.Root, OopClassCanvasForm.ResolveTable(doc, "(root)"),
                "the root box must map to the document root");

            TomlTable project = OopClassCanvasForm.ResolveTable(doc, "project");
            Assert.IsNotNull(project);
            Assert.IsNotNull(project.Get("name"));

            TomlTable nested = OopClassCanvasForm.ResolveTable(doc, "tool.helixstudio");
            Assert.IsNotNull(nested, "dotted box titles must resolve through the table tree");
            Assert.IsNotNull(nested.Get("kind"));

            // An array-of-tables box shows its first element, so editing targets that element.
            TomlTable target = OopClassCanvasForm.ResolveTable(doc, "tool.helixstudio.targets");
            Assert.IsNotNull(target);
            Assert.IsNotNull(target.Get("name"));

            Assert.IsNull(OopClassCanvasForm.ResolveTable(doc, "no.such.table"));
        }

        [TestMethod]
        public void EditingAValue_WritesItBack_AndLeavesTheRestIntact()
        {
            string path = Path.Combine(Path.GetTempPath(), "helix_rt_" + Guid.NewGuid().ToString("N") + ".toml");
            File.WriteAllText(path, Sample);
            try
            {
                TomlDocument doc = TomlParser.Parse(File.ReadAllText(path));
                TomlTable project = OopClassCanvasForm.ResolveTable(doc, "project");

                // What the dialog does when the user types a new value.
                var version = (TomlValue)project.Get("version");
                version.Raw = "\"9.9.9\"";
                version.Kind = TomlValueKind.String;
                version.StringValue = "9.9.9";

                File.WriteAllText(path, TomlWriter.Write(doc));

                // Re-parse the file exactly as the canvas reload does.
                TomlDocument reread = TomlParser.Parse(File.ReadAllText(path));
                Assert.IsFalse(reread.Diagnostics.Any(d => d.IsError),
                    "the written file must still parse cleanly");

                var rereadProject = OopClassCanvasForm.ResolveTable(reread, "project");
                Assert.AreEqual("9.9.9", ((TomlValue)rereadProject.Get("version")).StringValue,
                    "the edit must survive the write");
                Assert.AreEqual("demo", ((TomlValue)rereadProject.Get("name")).StringValue,
                    "untouched keys must be preserved");

                Assert.IsNotNull(OopClassCanvasForm.ResolveTable(reread, "tool.helixstudio"),
                    "other tables must survive the write");
                Assert.IsNotNull(OopClassCanvasForm.ResolveTable(reread, "tool.helixstudio.targets"),
                    "arrays-of-tables must survive the write");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [TestMethod]
        public void TheEditDialog_OffersOnlyScalarKeys()
        {
            // Sub-tables are their own canvas boxes, so the dialog must not duplicate them.
            TomlDocument doc = TomlParser.Parse(Sample);
            TomlTable tool = OopClassCanvasForm.ResolveTable(doc, "tool");

            using (var dlg = new TomlTableEditDialog("tool", tool))
            {
                int textBoxes = dlg.Controls.OfType<System.Windows.Forms.TextBox>().Count();
                Assert.AreEqual(0, textBoxes,
                    "[tool] holds only the sub-table helixstudio, so there is nothing scalar to edit here");
                Assert.IsFalse(dlg.Changed);
            }

            TomlTable project = OopClassCanvasForm.ResolveTable(doc, "project");
            using (var dlg = new TomlTableEditDialog("project", project))
            {
                int textBoxes = dlg.Controls.OfType<System.Windows.Forms.TextBox>().Count();
                Assert.AreEqual(2, textBoxes, "[project] has name and version");
            }
        }
    }
}
