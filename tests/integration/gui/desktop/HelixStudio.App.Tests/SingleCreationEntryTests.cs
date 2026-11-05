using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 246 consolidated the two redundant creation entries ("New Solution…" dialog + "New
    /// Project (Wizard)…") into a single VS2022-style "New Project…". "New Solution…" was later
    /// reinstated deliberately, as a DIFFERENT command rather than the old duplicate: New Project
    /// joins the currently-open solution, so without an explicit escape hatch every new solution
    /// nested inside the previous one whenever the user never closed anything
    /// (<see cref="MainForm.OnFileNewSolution"/> → RunStandaloneNewProjectWizard).
    ///
    /// So the contract is exactly one of each, not one command total: no duplicate "New Project"
    /// entries, and no second way to spell "New Solution".
    /// </summary>
    [TestClass]
    public class SingleCreationEntryTests
    {
        // File ▸ New ▸ Project... / Solution...: the children of the unified "New" parent drop the
        // word "New" (MainForm.Designer.cs, newMenuItem), so a child is read as the user reads it --
        // "New" + its own text -- and still has to be counted exactly once.
        private static void Collect(ToolStripItemCollection items, List<string> texts, string parentPlain = "")
        {
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                string text = mi.Text ?? "";
                texts.Add(parentPlain == "New" ? "New " + text : text);
                if (mi.HasDropDownItems) Collect(mi.DropDownItems, texts, text.Replace("&", "").Trim());
            }
        }

        [TestMethod]
        public void FileMenu_HasExactlyOneNewProjectEntry_AndOneNewSolutionEntry()
        {
            using (var form = new MainForm())
            {
                Assert.IsNotNull(form.MainMenuStrip);
                var texts = new List<string>();
                Collect(form.MainMenuStrip.Items, texts);

                int newProject = 0, newSolution = 0;
                foreach (string t in texts)
                {
                    string plain = (t ?? "").Replace("&", "");
                    if (plain.Contains("New Project")) newProject++;
                    if (plain.Contains("New Solution")) newSolution++;
                }

                Assert.AreEqual(1, newProject, "There must be exactly one 'New Project…' creation entry.");
                Assert.AreEqual(1, newSolution,
                    "There must be exactly one 'New Solution…' entry — the standalone-solution escape hatch.");
            }
        }
    }
}
