using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS §9 (solution lifecycle): closing a solution must close EVERY editor tab it owns —
    /// pinned tabs included — so no document, timer, or model reference survives the close.
    /// TabsToClose is the pure victim-selection rule behind Close All (unpinned-only) and the
    /// solution-close teardown (includePinned: true).
    /// Also covers §8.1's save-guard seam: every disk write an EditorTabPage performs enters the
    /// host-supplied guard (the file-watcher suspension), held through the FileSaved event.
    /// </summary>
    [TestClass]
    public class SolutionCloseTabTeardownTests
    {
        private static TabControl RealizedTabs(out List<EditorTabPage> pages, params string[] names)
        {
            var tabs = new TabControl();
            EditorCoordinator.EnsureRealized(tabs);
            pages = new List<EditorTabPage>();
            foreach (string n in names)
            {
                var p = new EditorTabPage { Text = n };
                tabs.TabPages.Add(p);
                pages.Add(p);
            }
            return tabs;
        }

        [TestMethod]
        public void CloseAll_WithoutPinned_SkipsPinnedTabs()
        {
            List<EditorTabPage> p;
            using (TabControl tabs = RealizedTabs(out p, "a", "b", "c"))
            {
                p[1].IsPinned = true;
                var victims = EditorCoordinator.TabsToClose(tabs, includePinned: false);
                CollectionAssert.AreEquivalent(new[] { p[0], p[2] }, victims,
                    "the user's Close All protects pinned tabs");
            }
        }

        [TestMethod]
        public void SolutionClose_IncludesPinnedTabs()
        {
            List<EditorTabPage> p;
            using (TabControl tabs = RealizedTabs(out p, "a", "b", "c"))
            {
                p[1].IsPinned = true;
                var victims = EditorCoordinator.TabsToClose(tabs, includePinned: true);
                CollectionAssert.AreEquivalent(new[] { p[0], p[1], p[2] }, victims,
                    "§9: no editor tab may survive its solution, pinned or not");
            }
        }

        private sealed class RecordingScope : IDisposable
        {
            private readonly List<string> _log;
            public RecordingScope(List<string> log) { _log = log; }
            public void Dispose() { _log.Add("guard-exit"); }
        }

        [TestMethod]
        public void SaveFile_EntersGuard_WritesAndRaisesFileSaved_ThenExits()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_guard_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\n");
            try
            {
                var log = new List<string>();
                using (var tab = new EditorTabPage(tmp))
                {
                    tab.SaveGuard = () => { log.Add("guard-enter"); return new RecordingScope(log); };
                    tab.FileSaved += (s, e) => log.Add("saved");

                    tab.Editor.Text = "x = 2\n";
                    Assert.IsTrue(tab.SaveFile(), "the save itself must succeed");
                }

                CollectionAssert.AreEqual(new[] { "guard-enter", "saved", "guard-exit" }, log,
                    "the watcher guard must wrap the disk write AND the FileSaved event " +
                    "(subscribers there rewrite further solution files)");
                Assert.AreEqual("x = 2\n", File.ReadAllText(tmp), "the content must actually be saved");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [TestMethod]
        public void SaveFile_WithoutGuard_StillSaves()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "helix_guard_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(tmp, "x = 1\n");
            try
            {
                using (var tab = new EditorTabPage(tmp))
                {
                    tab.Editor.Text = "x = 3\n";
                    Assert.IsTrue(tab.SaveFile(), "a null SaveGuard must not break saving");
                }
                Assert.AreEqual("x = 3\n", File.ReadAllText(tmp));
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
