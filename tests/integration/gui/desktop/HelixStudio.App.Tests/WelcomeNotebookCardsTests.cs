using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Welcome page is the first screen of the IDE, and it must name the two things a person
    /// does when they arrive — not enumerate the program's file types.
    ///
    /// <para>
    /// It started as three cards that all required a solution, so somebody who booted the IDE to
    /// work on a notebook had nothing to click. Adding notebook cards beside the solution ones
    /// fixed that and introduced a second problem: four cards showing two glyphs, two plus-signs
    /// and two folders, differing only in the noun underneath. The panel was repeating itself.
    /// </para>
    ///
    /// <para>
    /// So: ONE New and ONE Open, one glyph each. New asks what afterwards — a second step, which
    /// reads as one. Open does not ask at all, because a pyproject.toml and an .ipynb are told
    /// apart by their names, which the Recent list has always done.
    /// </para>
    /// </summary>
    [TestClass]
    public class WelcomeNotebookCardsTests
    {
        private static List<object> Cards(WelcomePanel panel)
        {
            FieldInfo field = typeof(WelcomePanel).GetField("_cards",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "_cards field expected");
            var cards = new List<object>();
            foreach (object card in (IEnumerable)field.GetValue(panel)) cards.Add(card);
            return cards;
        }

        private static string Str(object card, string fieldName)
        {
            FieldInfo f = card.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "an action card has a " + fieldName);
            return (string)f.GetValue(card);
        }

        private static List<string> Titles(WelcomePanel panel)
        {
            var titles = new List<string>();
            foreach (object card in Cards(panel)) titles.Add(Str(card, "Title"));
            return titles;
        }

        [TestMethod]
        public void GetStarted_HasOneNewAndOneOpen_NotOnePerFileType()
        {
            using (var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager()))
            {
                List<string> titles = Titles(panel);

                CollectionAssert.Contains(titles, "New...");
                CollectionAssert.Contains(titles, "Open...");
                CollectionAssert.DoesNotContain(titles, "New notebook",
                    "a separate notebook card would put a second plus-sign back on the panel");
                CollectionAssert.DoesNotContain(titles, "New project");
                CollectionAssert.DoesNotContain(titles, "Open a notebook");
                CollectionAssert.DoesNotContain(titles, "Open a solution");
            }
        }

        /// <summary>
        /// The glyph is the claim being tested. Two cards drawing the same symbol tell the eye
        /// they are the same command, and then the panel has to explain in words that they are not.
        /// </summary>
        [TestMethod]
        public void EachGlyphIsUsedExactlyOnce()
        {
            using (var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager()))
            {
                var seen = new Dictionary<string, int>();
                foreach (object card in Cards(panel))
                {
                    string glyph = Str(card, "Glyph");
                    seen[glyph] = seen.ContainsKey(glyph) ? seen[glyph] + 1 : 1;
                }

                foreach (KeyValuePair<string, int> pair in seen)
                    Assert.AreEqual(1, pair.Value,
                        "glyph '" + pair.Key + "' is drawn on " + pair.Value + " cards; each must be unique");
            }
        }

        [TestMethod]
        public void TheNewMenuOffersNotebookFirst_ThenProjectAndSolution()
        {
            using (var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager()))
            {
                ToolStripItemCollection items = panel.NewMenuItems;

                Assert.AreEqual(3, items.Count);
                Assert.AreEqual("Jupyter notebook...", items[0].Text,
                    "it is the only one of the three that asks nothing beyond a filename");
                Assert.AreEqual("Project...", items[1].Text);
                Assert.AreEqual("Solution...", items[2].Text);
            }
        }

        [TestMethod]
        public void TheNewMenuAndTheOpenCardBothReachTheHost()
        {
            using (var panel = new WelcomePanel(new RecentFilesManager(), new UserSettingsManager()))
            {
                int notebooks = 0, projects = 0, solutions = 0, opens = 0;
                panel.NewNotebookClicked += delegate { notebooks++; };
                panel.NewSolutionClicked += delegate { projects++; };
                panel.NewStandaloneSolutionClicked += delegate { solutions++; };
                panel.OpenAnythingClicked += delegate { opens++; };

                ToolStripItemCollection items = panel.NewMenuItems;
                items[0].PerformClick();
                items[1].PerformClick();
                items[2].PerformClick();
                InvokeCard(panel, "Open...");

                Assert.AreEqual(1, notebooks, "Jupyter notebook... must reach the host");
                Assert.AreEqual(1, projects, "Project... must reach the host");
                Assert.AreEqual(1, solutions, "Solution... must reach the host");
                Assert.AreEqual(1, opens, "the Open card must reach the host");
            }
        }

        private static void InvokeCard(WelcomePanel panel, string title)
        {
            foreach (object card in Cards(panel))
            {
                if (Str(card, "Title") != title) continue;
                FieldInfo invoke = card.GetType().GetField("Invoke",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var action = (System.Action)invoke.GetValue(card);
                Assert.IsNotNull(action, title + " has no action");
                action();
                return;
            }
            Assert.Fail("no card titled " + title);
        }
    }
}
