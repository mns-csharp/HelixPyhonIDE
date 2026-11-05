using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 261: Align / Distribute / Refactor were only ever built in the dead MenuCoordinator
    /// fallback (which runs only when there is no designer menu), so these working features had NO
    /// live entry point. They are now appended to the live Diagram menu. Guards that they are present,
    /// that Refactor exposes the three refactorings, and that no Diagram mnemonic clashes.
    /// </summary>
    [TestClass]
    public class DiagramMenuTests
    {
        private static char? Mnemonic(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int i = 0; i + 1 < text.Length; i++)
                if (text[i] == '&')
                {
                    if (text[i + 1] == '&') { i++; continue; }
                    return char.ToUpperInvariant(text[i + 1]);
                }
            return null;
        }

        [TestMethod]
        public void DiagramMenu_ExposesAlignDistributeRefactor_WithNoMnemonicClash()
        {
            using (var form = new MainForm())
            {
                typeof(MainForm).GetMethod("EnsureDiagramMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);

                var diagram = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("diagramMenuItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert.IsNotNull(diagram, "diagramMenuItem expected");

                var subs = new Dictionary<string, ToolStripMenuItem>();
                var seen = new Dictionary<char, string>();
                foreach (ToolStripItem it in diagram.DropDownItems)
                {
                    var mi = it as ToolStripMenuItem;
                    if (mi == null) continue;
                    subs[(mi.Text ?? "").Replace("&", "")] = mi;
                    char? m = Mnemonic(mi.Text);
                    if (m.HasValue)
                    {
                        Assert.IsFalse(seen.ContainsKey(m.Value),
                            "duplicate Diagram mnemonic &" + m.Value + " (" +
                            (seen.ContainsKey(m.Value) ? seen[m.Value] : "") + " vs " + mi.Text + ")");
                        seen[m.Value] = mi.Text;
                    }
                }

                Assert.IsTrue(subs.ContainsKey("Align"), "Align submenu present");
                Assert.IsTrue(subs.ContainsKey("Distribute"), "Distribute submenu present");
                Assert.IsTrue(subs.ContainsKey("Refactor"), "Refactor submenu present");

                var refactorNames = new List<string>();
                foreach (ToolStripItem it in subs["Refactor"].DropDownItems)
                    if (it is ToolStripMenuItem mi) refactorNames.Add((mi.Text ?? "").Replace("&", ""));
                CollectionAssert.Contains(refactorNames, "Extract Interface...");
                CollectionAssert.Contains(refactorNames, "Pull Up Member...");
                CollectionAssert.Contains(refactorNames, "Push Down Member...");
            }
        }

        [TestMethod]
        public void DiagramMenu_GreysOutSelectionCommands_WhenNothingIsSelected()
        {
            using (var form = new MainForm())
            {
                typeof(MainForm).GetMethod("EnsureDiagramMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);
                // Simulate the Diagram menu opening with no active canvas / selection.
                typeof(MainForm).GetMethod("OnDiagramMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, new object[] { form, System.EventArgs.Empty });

                var diagram = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("diagramMenuItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                foreach (string name in new[] { "Align", "Distribute", "Refactor" })
                {
                    ToolStripMenuItem sub = null;
                    foreach (ToolStripItem it in diagram.DropDownItems)
                        if (it is ToolStripMenuItem mi && (mi.Text ?? "").Replace("&", "") == name) { sub = mi; break; }
                    Assert.IsNotNull(sub, name + " submenu present");
                    Assert.IsFalse(sub.Enabled, name + " must be disabled when nothing is selected");
                }
            }
        }
    }
}
