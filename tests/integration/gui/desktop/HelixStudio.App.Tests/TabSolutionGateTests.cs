using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 334 (user directive): top-level tabs that depend on a project/solution must not be shown
    /// until one is created or opened. With nothing loaded the strip is Welcome + Terminal; the rest
    /// return when a solution opens.
    ///
    /// The subtle requirement is that gating must not be mistaken for a user preference: hiding a
    /// tab because there is no solution must never be persisted as "the user closed this tab", and a
    /// tab the user really did close must stay closed when a solution opens.
    /// </summary>
    [TestClass]
    public class TabSolutionGateTests
    {
        private static readonly string[] SolutionScoped =
        {
            "designer", "editor", "debug", "analysis", "buildResults", "problems",
            "lint", "deadcode", "metrics", "complexity", "tests", "profile",
            "references", "callHierarchy", "hiddenElements",
        };

        /// <summary>
        /// Mirrors PanelCoordinator.ResultTabs: solution-scoped tabs whose CONTENT is produced by an
        /// engine run and cleared when that solution closes. They never restore — not on solution
        /// open, not from settings. Keep in step with the coordinator's list.
        /// </summary>
        private static readonly string[] ResultTabs =
        {
            "analysis", "buildResults", "problems", "lint", "deadcode", "metrics",
            "complexity", "tests", "profile", "references", "callHierarchy",
        };

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void SetSolution(MainForm form, object solution)
        {
            typeof(MainForm).GetField("_currentSolution", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(form, solution);
        }

        private static bool Shows(MainForm form, string key)
        {
            var known = Field<Dictionary<string, TabPage>>(form, "_knownTabs");
            var main = Field<TabControl>(form, "_mainTabControl");
            TabPage tab;
            return known.TryGetValue(key, out tab) && tab != null && main.TabPages.Contains(tab);
        }

        [TestMethod]
        public void WithNoSolution_OnlyTheSolutionFreeTabsRemain()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, null);
                form.UpdateMenuWorkflowVisibility();

                foreach (string key in SolutionScoped)
                    Assert.IsFalse(Shows(form, key),
                        key + " needs a solution, so it should not be on the strip yet");

                Assert.IsTrue(Shows(form, "welcome"), "Welcome is the entry workflow and always stays");
            }
        }

        [TestMethod]
        public void OpeningASolution_BringsThemBack()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, null);
                form.UpdateMenuWorkflowVisibility();

                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();

                foreach (string key in new[] { "designer", "editor", "debug" })
                    Assert.IsTrue(Shows(form, key), key + " should return once a solution is open");
            }
        }

        /// <summary>
        /// Result tabs are NOT part of "bring the workflow tabs back". A brand-new solution has
        /// produced no lint findings, no metrics and no output, so it must open with none of those
        /// tabs on the strip — the engine command puts each one there when it has something to show.
        /// </summary>
        [TestMethod]
        public void OpeningASolution_DoesNotBringBackTheResultTabs()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();

                foreach (string key in ResultTabs)
                    Assert.IsFalse(Shows(form, key),
                        key + " holds results this solution has not produced — it must not open with the solution");
            }
        }

        /// <summary>
        /// The reported bug , end to end at the tab-strip level: run engines in a Python
        /// solution, close it, open an unrelated C++/CUDA solution — the Python session's result tabs
        /// came back, empty, because ShowTab had stamped a GLOBAL visibility preference that the
        /// solution gate replayed for every later solution.
        /// </summary>
        [TestMethod]
        public void ResultTabsOfAClosedSolution_DoNotReappearUnderTheNextSolution()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\py\pyproject.toml", "Py"));
                form.UpdateMenuWorkflowVisibility();

                // The engines run and each puts its results tab up, exactly as the commands do.
                foreach (string key in ResultTabs) form.ShowTab(key);
                foreach (string key in ResultTabs)
                    Assert.IsTrue(Shows(form, key), "precondition: " + key + " is up in the first solution");

                object coordinator = Field<object>(form, "_solutionCoordinator");
                coordinator.GetType()
                    .GetMethod("NewEmptyWorkspace", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(coordinator, null);

                // A different solution, in a different language, that has produced nothing.
                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\cuda\CMakeLists.txt", "MultiPackageWorkspace"));
                form.UpdateMenuWorkflowVisibility();

                foreach (string key in ResultTabs)
                    Assert.IsFalse(Shows(form, key),
                        key + " belonged to the closed Python solution and must not reappear over the CUDA one");
            }
        }

        /// <summary>
        /// The mechanism behind the bug above: showing a result tab must never be recorded in the
        /// global VisibleTopLevelTabs map, because that map is replayed for every solution and at
        /// every startup. Also covers pruning entries an older build already wrote there.
        /// </summary>
        [TestMethod]
        public void ShowingAResultTab_IsNotRecordedAsAGlobalUserPreference()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();

                foreach (string key in ResultTabs) form.ShowTab(key);

                foreach (string key in ResultTabs)
                    Assert.IsFalse(settings.Settings.VisibleTopLevelTabs.ContainsKey(key),
                        key + ": a result tab's visibility is not a user preference and must not be persisted");
            }
        }

        /// <summary>
        /// Settings files written by earlier builds still carry the stamped result-tab entries. They
        /// must be ignored AND pruned on replay, or a restart resurrects the previous solution's tabs.
        /// </summary>
        [TestMethod]
        public void StaleResultTabPreferences_AreIgnoredAndPruned()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                foreach (string key in ResultTabs) settings.Settings.VisibleTopLevelTabs[key] = true;

                typeof(MainForm).GetMethod("ApplyTabVisibilityFromSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);

                foreach (string key in ResultTabs)
                {
                    Assert.IsFalse(settings.Settings.VisibleTopLevelTabs.ContainsKey(key),
                        key + ": the stale preference should have been pruned");
                    Assert.IsFalse(Shows(form, key), key + " must not be restored from a stale preference");
                }
            }
        }

        /// <summary>
        /// Opening a solution straight over an open one does its own teardown instead of routing
        /// through File ▸ Close, so it has to drop the outgoing solution's result tabs itself.
        /// </summary>
        [TestMethod]
        public void HideResultTabs_TakesEveryResultTabOffTheStrip()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();
                foreach (string key in ResultTabs) form.ShowTab(key);

                form.HideResultTabs();

                foreach (string key in ResultTabs)
                    Assert.IsFalse(Shows(form, key), key + " should have been taken off the strip");
                Assert.IsTrue(Shows(form, "editor"), "workflow tabs are untouched by HideResultTabs");
            }
        }

        [TestMethod]
        public void GatingDoesNotOverwriteTheUsersVisibilityPreferences()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                var before = new Dictionary<string, bool>(settings.Settings.VisibleTopLevelTabs);

                SetSolution(form, null);
                form.UpdateMenuWorkflowVisibility();

                foreach (KeyValuePair<string, bool> pair in before)
                {
                    bool now;
                    Assert.IsTrue(settings.Settings.VisibleTopLevelTabs.TryGetValue(pair.Key, out now),
                        pair.Key + " disappeared from the saved preferences");
                    Assert.AreEqual(pair.Value, now,
                        pair.Key + ": gating must not be recorded as the user hiding a tab");
                }
            }
        }

        /// <summary>
        /// The Terminal is deliberately NOT gated by UpdateMenuWorkflowVisibility (a solution-free
        /// shell must not vanish just because the user touched the menu bar), but File ▸ Close
        /// Solution must still take it away: its session was started in the closed solution's
        /// directory. Regression for the Terminal tab surviving Close Solution in every language.
        /// </summary>
        [TestMethod]
        public void TerminalVisibility_RespectsSuppressionAndClosesWithItsSolution()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                if (MainForm.AiTerminalSuppressed)
                {
                    Assert.IsFalse(Shows(form, "terminal"),
                        "a product-suppressed terminal must not appear in the tab strip");
                    return;
                }

                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();

                form.ShowTab("terminal");
                Assert.IsTrue(Shows(form, "terminal"), "precondition: the Terminal tab is on the strip");

                object coordinator = Field<object>(form, "_solutionCoordinator");
                coordinator.GetType()
                    .GetMethod("NewEmptyWorkspace", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(coordinator, null);

                Assert.IsFalse(Shows(form, "terminal"),
                    "the closed solution's Terminal tab must not stay on the strip");

                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                bool wanted;
                if (settings.Settings.VisibleTopLevelTabs.TryGetValue("terminal", out wanted))
                    Assert.IsTrue(wanted,
                        "solution close must not be recorded as the user closing the Terminal");
            }
        }

        [TestMethod]
        public void ATabTheUserClosed_StaysClosedWhenASolutionOpens()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                var settings = Field<UserSettingsManager>(form, "_settingsManager");
                // A WORKFLOW tab, deliberately: result tabs no longer restore either way, so closing
                // one of those would pass this test without exercising the preference path at all.
                settings.Settings.VisibleTopLevelTabs["debug"] = false;   // an explicit close

                SetSolution(form, new HelixPythonNamespace.Core.Model.SolutionModel(@"C:\p\pyproject.toml", "P"));
                form.UpdateMenuWorkflowVisibility();

                Assert.IsFalse(Shows(form, "debug"),
                    "opening a solution must not re-open a tab the user deliberately closed");
                Assert.IsTrue(Shows(form, "editor"), "tabs the user kept should still come back");
            }
        }
    }
}
