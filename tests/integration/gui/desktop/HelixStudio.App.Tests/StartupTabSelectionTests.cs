using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    // Restoring the persisted tab set calls ShowTab per key, and ShowTab selects the tab it just
    // added — so before MainForm.ChooseStartupTab existed, the IDE opened on whichever key happened
    // to be last in the VisibleTopLevelTabs dictionary. In practice that was "terminal": a brand new
    // launch greeted the user with a full-window black console.
    [TestClass]
    public class StartupTabSelectionTests
    {
        private static TabControl BuildTabs(out Dictionary<string, TabPage> known, params string[] keys)
        {
            var tabs = new TabControl();
            known = new Dictionary<string, TabPage>();
            foreach (string key in keys)
            {
                var page = new TabPage(key);
                known[key] = page;
                tabs.TabPages.Add(page);
            }
            return tabs;
        }

        [TestMethod]
        public void WithNoSolution_OpensOnTheWelcomePage()
        {
            using (TabControl tabs = BuildTabs(out var known,
                "welcome", "designer", "editor", "problems", "terminal"))
            {
                TabPage chosen = MainForm.ChooseStartupTab(tabs, known, hasSolution: false);
                Assert.AreSame(known["welcome"], chosen);
            }
        }

        [TestMethod]
        public void WithASolutionLoaded_OpensOnTheDesignerCanvas()
        {
            using (TabControl tabs = BuildTabs(out var known,
                "welcome", "designer", "editor", "terminal"))
            {
                TabPage chosen = MainForm.ChooseStartupTab(tabs, known, hasSolution: true);
                Assert.AreSame(known["designer"], chosen);
            }
        }

        [TestMethod]
        public void SkipsPreferredTabsTheUserHasClosed()
        {
            using (TabControl tabs = BuildTabs(out var known, "welcome", "designer", "editor", "terminal"))
            {
                // The user closed Welcome and the canvas; Editor is the next preference.
                tabs.TabPages.Remove(known["welcome"]);
                tabs.TabPages.Remove(known["designer"]);

                TabPage chosen = MainForm.ChooseStartupTab(tabs, known, hasSolution: false);
                Assert.AreSame(known["editor"], chosen);
            }
        }

        [TestMethod]
        public void WithEveryPreferredTabClosed_FallsBackToTheLeftmostTab()
        {
            using (TabControl tabs = BuildTabs(out var known, "problems", "terminal"))
            {
                TabPage chosen = MainForm.ChooseStartupTab(tabs, known, hasSolution: false);
                Assert.AreSame(known["problems"], chosen, "must not land on the trailing Terminal tab");
            }
        }

        [TestMethod]
        public void WithNoTabsAtAll_ChoosesNothing()
        {
            using (var tabs = new TabControl())
            {
                Assert.IsNull(MainForm.ChooseStartupTab(tabs, new Dictionary<string, TabPage>(), false));
                Assert.IsNull(MainForm.ChooseStartupTab(null, new Dictionary<string, TabPage>(), false));
            }
        }

        // Iter 330: SelectStartupTab only runs during layout restore, so OPENING a solution later
        // left the user on the Welcome page with the solution loaded behind it.
        [TestMethod]
        public void OpeningASolution_MovesOffTheWelcomePage()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var welcome = Field<TabPage>(form, "_welcomeTab");
                if (welcome == null || !main.TabPages.Contains(welcome)) { Assert.Inconclusive("no welcome tab"); return; }

                main.SelectedTab = welcome;
                SetField(form, "_currentSolution",
                    new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                // Iter 334: opening a solution un-gates the workspace tabs before the selection runs.
                form.UpdateMenuWorkflowVisibility();

                form.SelectWorkspaceTabAfterSolutionOpen();

                Assert.AreNotSame(welcome, main.SelectedTab,
                    "with a solution open the IDE should land on the workspace, not the entry page");
            }
        }

        [TestMethod]
        public void OpeningASolution_DoesNotYankAUserOffTheirChosenTab()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                SetField(form, "_currentSolution",
                    new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();   // iter 334: un-gate the workspace tabs first

                var known = Field<System.Collections.Generic.Dictionary<string, TabPage>>(form, "_knownTabs");
                TabPage editor;
                if (!known.TryGetValue("editor", out editor) || !main.TabPages.Contains(editor))
                { Assert.Inconclusive("no editor tab"); return; }

                main.SelectedTab = editor;
                form.SelectWorkspaceTabAfterSolutionOpen();

                Assert.AreSame(editor, main.SelectedTab,
                    "a deliberate tab choice must survive opening a solution");
            }
        }

        private static void SetField(object obj, string name, object value)
        {
            var f = obj.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            f.SetValue(obj, value);
        }


        // Iter 331: the mirror transition. Closing a solution left the user on an emptied designer
        // canvas, and the status bar read "No solution loaded" beside the closed solution's name.
        [TestMethod]
        public void ClosingASolution_ReturnsToTheWelcomePage()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var welcome = Field<TabPage>(form, "_welcomeTab");
                SetField(form, "_currentSolution",
                    new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();   // iter 334: the designer tab exists once open

                var known = Field<System.Collections.Generic.Dictionary<string, TabPage>>(form, "_knownTabs");
                TabPage designer;
                if (welcome == null || !known.TryGetValue("designer", out designer)
                    || !main.TabPages.Contains(welcome) || !main.TabPages.Contains(designer))
                { Assert.Inconclusive("welcome/designer tabs unavailable"); return; }

                main.SelectedTab = designer;
                form.SelectEntryTabAfterSolutionClose();

                Assert.AreSame(welcome, main.SelectedTab,
                    "an emptied workspace tab should hand back to the entry page");
            }
        }

        [TestMethod]
        public void ClosingASolution_LeavesAPanelTabAlone()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var known = Field<System.Collections.Generic.Dictionary<string, TabPage>>(form, "_knownTabs");
                TabPage output;
                if (!known.TryGetValue("buildResults", out output)) { Assert.Inconclusive("no output tab"); return; }
                if (!main.TabPages.Contains(output)) main.TabPages.Add(output);

                main.SelectedTab = output;
                form.SelectEntryTabAfterSolutionClose();

                Assert.AreSame(output, main.SelectedTab,
                    "a panel the user is reading (e.g. build output) must not be swapped out from under them");
            }
        }

        // The two bugs reported directly: Welcome must reappear on solution close even if the user
        // had previously closed its tab, and it must disappear on solution open even if the user
        // wasn't looking at it when the solution finished loading.
        [TestMethod]
        public void ClosingASolution_ReAddsWelcomeTab_EvenIfTheUserHadPreviouslyClosedIt()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var welcome = Field<TabPage>(form, "_welcomeTab");
                if (welcome == null) { Assert.Inconclusive("no welcome tab"); return; }

                if (main.TabPages.Contains(welcome)) main.TabPages.Remove(welcome);
                Assert.IsFalse(main.TabPages.Contains(welcome), "test setup: welcome must start closed");

                form.SelectEntryTabAfterSolutionClose();

                Assert.IsTrue(main.TabPages.Contains(welcome),
                    "closing a solution must bring the Welcome tab back even if it was closed");
            }
        }

        [TestMethod]
        public void OpeningASolution_RemovesWelcomeTabFromStrip_EvenWhenNotSelected()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var welcome = Field<TabPage>(form, "_welcomeTab");
                if (welcome == null || !main.TabPages.Contains(welcome)) { Assert.Inconclusive("no welcome tab"); return; }

                // A synthetic tab stands in for "whatever the user was already looking at" — the point
                // is that the user is NOT on Welcome when the solution finishes loading, so this
                // deliberately doesn't depend on the real solution-gated workspace tabs existing.
                using (var other = new TabPage("other"))
                {
                    main.TabPages.Add(other);
                    main.SelectedTab = other;

                    form.SelectWorkspaceTabAfterSolutionOpen();

                    Assert.IsFalse(main.TabPages.Contains(welcome),
                        "opening a solution must remove the Welcome tab from the strip regardless of the current selection");
                    Assert.AreSame(other, main.SelectedTab, "the user's chosen tab must not change");
                }
            }
        }

        // Regression test for the bug this whole feature caused: gating Welcome via the SAME
        // persisting ShowTab/CloseTab primitive every other panel toggle uses meant every solution
        // open/close wrote "welcome": false into user-settings.json — so the NEXT app launch (with no
        // solution loaded at all) read that stale entry and suppressed Welcome forever, even though
        // ShowWelcomeOnStartup was still true. Opening/closing a solution must never touch
        // VisibleTopLevelTabs["welcome"] at all.
        [TestMethod]
        public void OpeningAndClosingASolution_NeverPersistsWelcomeVisibility()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var main = Field<TabControl>(form, "_mainTabControl");
                var welcome = Field<TabPage>(form, "_welcomeTab");
                var settingsManager = Field<UserSettingsManager>(form, "_settingsManager");
                if (welcome == null || settingsManager == null) { Assert.Inconclusive("no welcome tab / settings manager"); return; }

                using (var other = new TabPage("other"))
                {
                    main.TabPages.Add(other);
                    main.SelectedTab = other;
                    form.SelectWorkspaceTabAfterSolutionOpen(); // simulates a solution opening

                    var prefs = settingsManager.Settings.VisibleTopLevelTabs;
                    if (prefs != null)
                        Assert.IsFalse(prefs.ContainsKey("welcome"),
                            "opening a solution must not persist Welcome's automatic hide as a user preference");

                    form.SelectEntryTabAfterSolutionClose(); // simulates the solution closing again

                    prefs = settingsManager.Settings.VisibleTopLevelTabs;
                    if (prefs != null)
                        Assert.IsFalse(prefs.ContainsKey("welcome"),
                            "closing a solution must not persist Welcome's automatic reappearance as a user preference");
                }
            }
        }

        private static T Field<T>(object obj, string name)
        {
            var f = obj.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
