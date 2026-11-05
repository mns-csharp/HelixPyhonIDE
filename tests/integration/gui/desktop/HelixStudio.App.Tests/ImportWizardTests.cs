using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Drives the Import Python Directory wizard (SRS solution_explorer_rules §4)
    /// through its automation hooks on a real fixture: analyze → review →
    /// rename/startup → layout → execute, ending in a loadable workspace.
    /// </summary>
    [TestClass]
    public class ImportWizardTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            // A failed import must surface on wiz.Result, never as a modal "Import
            // failed" box no test can dismiss (it froze this whole suite once).
            Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");
            _root = Path.Combine(Path.GetTempPath(), "helix_wiz_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");   // restore the SAFE default, never null
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content = "")
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        [TestMethod]
        public void Wizard_EndToEnd_RenameStartupAndSrcLayout_ProducesLoadableWorkspace()
        {
            W(@"lib_one\__init__.py");
            W(@"lib_one\util.py", "X = 1\n");  // constant-shaped so the strict-OOP guard passes
            W(@"app\__init__.py");
            W(@"app\main.py", "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            string workingRoot = null;
            try
            {
                using (var wiz = new ImportWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("wizdemo");

                    Assert.AreEqual(0, wiz.CurrentPage);
                    Assert.IsTrue(wiz.GoNext(), "source page should validate and analyze");
                    Assert.AreEqual(1, wiz.CurrentPage, "analysis page");
                    Assert.IsTrue(wiz.FindingCount > 0, "analysis findings listed");
                    Assert.IsNotNull(wiz.Analysis);
                    Assert.AreEqual(2, wiz.Analysis.Packages.Count);

                    Assert.IsTrue(wiz.GoNext(), "to projects page");
                    Assert.AreEqual(2, wiz.CurrentPage);
                    Assert.IsTrue(wiz.RenameProject("lib_one", "core_lib"), "rename hook finds the row");
                    Assert.IsTrue(wiz.SetStartup("app"), "startup hook finds the row");

                    Assert.IsTrue(wiz.GoNext(), "to layout page");
                    Assert.AreEqual(3, wiz.CurrentPage);
                    wiz.SetLayout(ImportLayout.RearrangeToSrc);

                    Assert.IsTrue(wiz.GoNext(), "to summary page");
                    Assert.AreEqual(4, wiz.CurrentPage);
                    Assert.IsTrue(wiz.RunImportNow(), "execution succeeds");

                    // The wizard copies the source to a disposable working directory before
                    // doing anything destructive — _root (what the user actually pointed it at)
                    // must come out of this completely unchanged.
                    Assert.IsTrue(Directory.Exists(Path.Combine(_root, "lib_one")), "original left in place");
                    Assert.IsTrue(Directory.Exists(Path.Combine(_root, "app")), "original left in place");
                    Assert.IsFalse(Directory.Exists(Path.Combine(_root, "src")), "original never gets a src\\ layout");
                    Assert.IsFalse(File.Exists(Path.Combine(_root, "pyproject.toml")), "original never gets a generated toml");

                    workingRoot = Path.GetDirectoryName(wiz.Result.PyProjectTomlPath);
                    Assert.AreNotEqual(_root, workingRoot, "the import ran against a copy, not the original");

                    // SRS 4.6: files physically rearranged under src\ — in the COPY.
                    Assert.IsTrue(Directory.Exists(Path.Combine(workingRoot, "src", "core_lib")), "renamed + moved");
                    Assert.IsTrue(Directory.Exists(Path.Combine(workingRoot, "src", "app")));
                    Assert.IsFalse(Directory.Exists(Path.Combine(workingRoot, "lib_one")));

                    // SRS 4.5: generated workspace loads; startup restored from TOML.
                    Task<SolutionLoadResult> load = new SolutionService().LoadAsync(wiz.Result.PyProjectTomlPath);
                    load.Wait();
                    Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
                    Assert.AreEqual(2, load.Result.Solution.Projects.Count);
                    var startup = load.Result.Solution.Projects.FirstOrDefault(p => p.IsStartup);
                    Assert.IsNotNull(startup);
                    Assert.AreEqual("app", startup.Name);
                }
            }
            finally
            {
                // The working copy lives alongside _root, not under it — Cleanup() only
                // removes _root itself, so this test must remove its own copy.
                if (workingRoot != null) try { Directory.Delete(workingRoot, true); } catch { }
            }
        }

        [TestMethod]
        public void Wizard_DuplicateProjectNames_BlockTheProjectsPage()
        {
            W(@"one\core\__init__.py");
            W(@"two\core\__init__.py");

            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(_root);
                wiz.SetSolutionName("dups");
                Assert.IsTrue(wiz.GoNext());               // analysis
                Assert.IsTrue(wiz.GoNext());               // projects
                Assert.AreEqual(2, wiz.CurrentPage);
                // Undo the factory's auto-dedupe: force both rows to "core".
                Assert.IsTrue(wiz.RenameProjectAt(0, "core"));
                Assert.IsTrue(wiz.RenameProjectAt(1, "core"));
                Assert.IsFalse(wiz.GoNext(), "duplicate names must not pass validation");
                Assert.AreEqual(2, wiz.CurrentPage, "still on the projects page");
            }
        }

        [TestMethod]
        public void Wizard_DestinationField_DefaultsNextToSourceWithSolutionName()
        {
            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(_root);
                wiz.SetSolutionName("mydest");

                string expected = Path.Combine(Path.GetDirectoryName(_root), "mydest_helix_import");
                Assert.AreEqual(expected, wiz.DestinationText,
                    "destination must auto-suggest a sibling of the source named after the solution");
            }
        }

        [TestMethod]
        public void Wizard_CustomDestination_IsUsedForImport()
        {
            W(@"pkg\__init__.py");
            string customDestination = Path.Combine(Path.GetTempPath(), "helix_custom_dest_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var wiz = new ImportWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("customdest");
                    wiz.SetDestination(customDestination);

                    Assert.IsTrue(wiz.GoNext(), "source page should validate and analyze with a user-chosen destination");
                    Assert.IsTrue(wiz.GoNext());   // projects
                    Assert.IsTrue(wiz.GoNext());   // layout
                    Assert.IsTrue(wiz.GoNext());   // summary
                    Assert.IsTrue(wiz.RunImportNow());

                    string workingRoot = Path.GetDirectoryName(wiz.Result.PyProjectTomlPath);
                    Assert.AreEqual(Path.GetFullPath(customDestination), Path.GetFullPath(workingRoot),
                        "the import must be created in the destination directory the user chose, not an auto-picked one");
                    Assert.IsTrue(Directory.Exists(Path.Combine(_root, "pkg")), "original left in place");
                }
            }
            finally
            {
                try { Directory.Delete(customDestination, true); } catch { }
            }
        }

        [TestMethod]
        public void Wizard_DestinationInsideSource_BlocksSourcePage()
        {
            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(_root);
                wiz.SetSolutionName("nested");
                wiz.SetDestination(Path.Combine(_root, "nested_dest"));
                Assert.IsFalse(wiz.GoNext(), "a destination nested inside the source must not be allowed");
                Assert.AreEqual(0, wiz.CurrentPage);
            }
        }

        [TestMethod]
        public void Wizard_DestinationNonEmptyExisting_BlocksSourcePage_WhenOverwriteUnchecked()
        {
            string occupied = Path.Combine(Path.GetTempPath(), "helix_occupied_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(occupied);
            File.WriteAllText(Path.Combine(occupied, "already_here.txt"), "x");
            try
            {
                using (var wiz = new ImportWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("occ");
                    wiz.SetDestination(occupied);
                    wiz.SetOverwrite(false);
                    Assert.IsFalse(wiz.GoNext(), "a non-empty existing destination must not be allowed without Overwrite checked");
                    Assert.AreEqual(0, wiz.CurrentPage);
                }
            }
            finally
            {
                try { Directory.Delete(occupied, true); } catch { }
            }
        }

        [TestMethod]
        public void Wizard_DestinationNonEmptyExisting_IsOverwritten_WhenOverwriteChecked()
        {
            W(@"pkg\__init__.py");
            string occupied = Path.Combine(Path.GetTempPath(), "helix_occupied_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(occupied);
            // A leftover file from a PRIOR run at the same destination — must survive the
            // overwrite (only files the new copy actually provides get replaced), and the
            // import must succeed rather than throwing on the pre-existing directory.
            File.WriteAllText(Path.Combine(occupied, "stale_from_last_run.txt"), "old");
            try
            {
                using (var wiz = new ImportWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("occ2");
                    wiz.SetDestination(occupied);
                    wiz.SetOverwrite(true); // checked by default already; explicit for clarity
                    Assert.IsTrue(wiz.GoNext(), "an existing destination must be allowed when Overwrite is checked");
                    Assert.IsTrue(wiz.GoNext());   // projects
                    Assert.IsTrue(wiz.GoNext());   // layout
                    Assert.IsTrue(wiz.GoNext());   // summary
                    Assert.IsTrue(wiz.RunImportNow());

                    Assert.IsTrue(File.Exists(Path.Combine(occupied, "stale_from_last_run.txt")),
                        "files the new copy doesn't touch are left alone, not wiped");
                    string workingRoot = Path.GetDirectoryName(wiz.Result.PyProjectTomlPath);
                    Assert.AreEqual(Path.GetFullPath(occupied), Path.GetFullPath(workingRoot));
                }
            }
            finally
            {
                try { Directory.Delete(occupied, true); } catch { }
            }
        }

        [TestMethod]
        public void Wizard_InvalidSource_StaysOnSourcePage()
        {
            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(Path.Combine(_root, "does_not_exist"));
                wiz.SetSolutionName("x");
                Assert.IsFalse(wiz.GoNext(), "nonexistent source must not advance");
                Assert.AreEqual(0, wiz.CurrentPage);
            }
        }

        [TestMethod]
        public void Wizard_ExcludedPackage_IsNotEmitted()
        {
            W(@"keep\__init__.py");
            W(@"drop\__init__.py");

            string workingRoot = null;
            try
            {
                using (var wiz = new ImportWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("excl");
                    Assert.IsTrue(wiz.GoNext());   // analysis
                    Assert.IsTrue(wiz.GoNext());   // projects
                    Assert.IsTrue(wiz.SetDisposition("drop", PackageDisposition.Excluded));
                    Assert.IsTrue(wiz.GoNext());   // layout
                    Assert.IsTrue(wiz.GoNext());   // summary
                    Assert.IsTrue(wiz.RunImportNow());

                    workingRoot = Path.GetDirectoryName(wiz.Result.PyProjectTomlPath);
                    Assert.AreNotEqual(_root, workingRoot, "the import ran against a copy, not the original");
                    Assert.IsFalse(File.Exists(Path.Combine(workingRoot, "drop", "pyproject.toml")));
                    string rootToml = File.ReadAllText(wiz.Result.PyProjectTomlPath);
                    Assert.IsFalse(rootToml.Contains("drop"), "excluded package absent from workspace members");
                    Assert.IsTrue(rootToml.Contains("keep"));
                }
            }
            finally
            {
                if (workingRoot != null) try { Directory.Delete(workingRoot, true); } catch { }
            }
        }

        [TestMethod]
        public void Wizard_OverwriteExisting_IsCheckedByDefault()
        {
            W(@"pkg\__init__.py");
            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(_root);
                wiz.SetSolutionName("ovwtest");
                Assert.IsTrue(wiz.GoNext(), "source page");   // RunAnalysis() reads the checkbox into the plan
                Assert.IsTrue(wiz.Plan.OverwriteExisting, "overwrite must default to checked");
            }
        }

        [TestMethod]
        public void Wizard_LayoutPage_DefaultsToRearrangeToSrc()
        {
            W(@"pkg\__init__.py");
            using (var wiz = new ImportWizardForm())
            {
                wiz.CreateControl();
                wiz.SetSource(_root);
                wiz.SetSolutionName("layoutdefault");
                Assert.IsTrue(wiz.GoNext());   // analysis
                Assert.IsTrue(wiz.GoNext());   // projects
                Assert.IsTrue(wiz.GoNext());   // layout
                Assert.IsTrue(wiz.GoNext());   // layout -> summary; ReadLayoutIntoPlan() runs here
                Assert.AreEqual(ImportLayout.RearrangeToSrc, wiz.Plan.Layout,
                    "Rearrange must be the default without the caller ever calling SetLayout");
            }
        }

        [TestMethod]
        public void Wizard_RemembersLastSourceAndSolutionName_AcrossInstances()
        {
            // Isolate UserSettingsManager to a scratch directory for this test only — it
            // otherwise reads/writes %APPDATA%\HelixStudio\user-settings.json for real,
            // matching this suite's existing convention, but this test is specifically
            // about that persisted state and must not leave the real file polluted with a
            // temp test path.
            string stateDir = Path.Combine(Path.GetTempPath(), "helix_state_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stateDir);
            string previous = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", stateDir);
            string workingRoot = null;
            try
            {
                W(@"pkg\__init__.py");
                var settings = new UserSettingsManager();
                Assert.AreEqual(string.Empty, settings.Settings.LastImportSourceDirectory ?? string.Empty,
                                "nothing remembered yet in this isolated state dir");

                using (var wiz = new ImportWizardForm(null, settings))
                {
                    wiz.CreateControl();
                    wiz.SetSource(_root);
                    wiz.SetSolutionName("remembered_name");
                    Assert.IsTrue(wiz.GoNext());   // analysis
                    Assert.IsTrue(wiz.GoNext());   // projects
                    Assert.IsTrue(wiz.GoNext());   // layout
                    Assert.IsTrue(wiz.GoNext());   // summary
                    Assert.IsTrue(wiz.RunImportNow());
                    workingRoot = Path.GetDirectoryName(wiz.Result.PyProjectTomlPath);
                }

                // A SECOND instance, with nothing pre-supplied, must pre-fill from what the
                // first instance just saved.
                var settingsReloaded = new UserSettingsManager();
                Assert.AreEqual(_root, settingsReloaded.Settings.LastImportSourceDirectory);
                Assert.AreEqual("remembered_name", settingsReloaded.Settings.LastImportSolutionName);
                Assert.AreEqual(workingRoot, settingsReloaded.Settings.LastImportDestinationDirectory,
                    "destination actually used must be remembered too, even though it isn't restored into the field verbatim");

                using (var wiz2 = new ImportWizardForm(null, settingsReloaded))
                {
                    wiz2.CreateControl();
                    Assert.AreEqual(_root, wiz2.SourceText);
                    Assert.AreEqual("remembered_name", wiz2.SolutionNameText);
                    // Destination is NOT restored verbatim (the remembered directory already
                    // exists from the run above) — a fresh auto-suggestion is used instead.
                    Assert.AreNotEqual(workingRoot, wiz2.DestinationText);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", previous);
                try { Directory.Delete(stateDir, true); } catch { }
                if (workingRoot != null) try { Directory.Delete(workingRoot, true); } catch { }
            }
        }

        [TestMethod]
        public void ResolveBrowseStart_PrefersExistingCurrentValue()
        {
            Assert.AreEqual(_root, ImportWizardForm.ResolveBrowseStart(_root, @"C:\somewhere\else"));
        }

        [TestMethod]
        public void ResolveBrowseStart_FallsBackToParent_WhenCurrentValueNotYetCreated()
        {
            string notYetCreated = Path.Combine(_root, "not_created_yet");
            Assert.AreEqual(_root, ImportWizardForm.ResolveBrowseStart(notYetCreated, null));
        }

        [TestMethod]
        public void ResolveBrowseStart_FallsBackToPersisted_WhenCurrentValueEmpty()
        {
            Assert.AreEqual(_root, ImportWizardForm.ResolveBrowseStart(string.Empty, _root));
        }

        [TestMethod]
        public void ResolveBrowseStart_ReturnsNull_WhenNothingResolves()
        {
            Assert.IsNull(ImportWizardForm.ResolveBrowseStart(string.Empty, null));
            Assert.IsNull(ImportWizardForm.ResolveBrowseStart(
                Path.Combine(_root, "missing", "still_missing"), Path.Combine(_root, "also_missing")));
        }
    }
}
