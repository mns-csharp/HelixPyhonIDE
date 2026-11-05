using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// "Configure your new project" remembers the last-used Project name and
    /// Location across wizard instances (persisted via UserSettingsManager, the
    /// ImportWizardForm convention). The remembered name auto-increments when its
    /// folder already exists in the remembered location — which it always does
    /// right after the previous Create — so the page never opens directly onto a
    /// "folder already exists" validation error.
    /// </summary>
    [TestClass]
    public class NewProjectWizardMemoryTests
    {
        private string _stateDir;
        private string _usagePath;
        private string _location;
        private string _prevState;
        private string _prevUsage;

        [TestInitialize]
        public void Setup()
        {
            // Isolate BOTH persisted stores this wizard touches: user settings
            // (HELIXSTUDIO_STATE_DIR) and the template usage store (HELIX_USAGE_PATH) —
            // DoCreate() writes to each, and neither may leak into the real files.
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_npw_state_" + Guid.NewGuid().ToString("N"));
            _usagePath = Path.Combine(_stateDir, "usage.json");
            _location = Path.Combine(Path.GetTempPath(), "helix_npw_loc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDir);
            Directory.CreateDirectory(_location);
            _prevState = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            _prevUsage = Environment.GetEnvironmentVariable("HELIX_USAGE_PATH");
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", _usagePath);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _prevState);
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", _prevUsage);
            try { Directory.Delete(_stateDir, true); } catch { }
            try { Directory.Delete(_location, true); } catch { }
        }

        [TestMethod]
        public void ConfigurePage_RemembersNameAndLocation_AcrossInstances()
        {
            var settings = new UserSettingsManager();
            Assert.AreEqual(string.Empty, settings.Settings.LastNewProjectName ?? string.Empty,
                            "nothing remembered yet in this isolated state dir");

            using (var wiz = new NewProjectWizardForm(null, settings))
            {
                wiz.CreateControl();
                wiz.SetSearch("Python Console");
                wiz.SelectFirstTemplate();
                Assert.IsNotNull(wiz.SelectedTemplate, "the built-in Python Console App template must match");
                wiz.SetOptions(git: false, readme: false, gitignore: false, license: false);
                Assert.IsTrue(wiz.CreateNow(_location, "MemProj"), "creation succeeds");
            }

            // A SECOND instance, with reloaded settings, pre-fills the remembered location and —
            // because MemProj\ now exists there — the auto-incremented name.
            var reloaded = new UserSettingsManager();
            Assert.AreEqual("MemProj", reloaded.Settings.LastNewProjectName);
            Assert.AreEqual(_location, reloaded.Settings.LastNewProjectLocation);
            using (var wiz2 = new NewProjectWizardForm(null, reloaded))
            {
                wiz2.CreateControl();
                Assert.AreEqual(_location, wiz2.LocationText, "location restored verbatim");
                Assert.AreEqual("MemProj2", wiz2.ProjectNameText,
                    "remembered name bumps past the folder the previous Create just made");
            }

            // With the created folder gone, the remembered name comes back verbatim.
            Directory.Delete(Path.Combine(_location, "MemProj"), true);
            using (var wiz3 = new NewProjectWizardForm(null, new UserSettingsManager()))
            {
                wiz3.CreateControl();
                Assert.AreEqual("MemProj", wiz3.ProjectNameText);
            }
        }

        [TestMethod]
        public void WithoutASettingsManager_DefaultsAreUnchanged()
        {
            using (var wiz = new NewProjectWizardForm())
            {
                wiz.CreateControl();
                Assert.AreEqual("MyApp", wiz.ProjectNameText, "legacy default preserved");
            }
        }

        [TestMethod]
        public void AddToSolution_DoesNotPoisonStandaloneSolutionLocation()
        {
            var settings = new UserSettingsManager();
            settings.Settings.LastNewSolutionLocation = _location;
            settings.Settings.LastNewSolutionName = "Standalone";
            settings.Save();
            string activeSolution = Path.Combine(_location, "ExistingSolution");
            Directory.CreateDirectory(activeSolution);

            using (var wiz = new NewProjectWizardForm(null, settings))
            {
                wiz.CreateControl();
                wiz.SetSearch("Python Console");
                wiz.SelectFirstTemplate();
                wiz.SetFixedLocation(activeSolution);
                wiz.SetAddToSolutionMode();
                Assert.IsTrue(wiz.CreateNow(activeSolution, "Member"));
            }

            var reloaded = new UserSettingsManager();
            Assert.AreEqual(_location, reloaded.Settings.LastNewSolutionLocation);
            Assert.AreEqual("Standalone", reloaded.Settings.LastNewSolutionName);
        }

        [TestMethod]
        public void StandaloneSolution_IgnoresPoisonedProjectLocation_AndRejectsActiveSolutionTree()
        {
            var settings = new UserSettingsManager();
            string activeSolution = Path.Combine(_location, "ExistingSolution");
            Directory.CreateDirectory(activeSolution);
            settings.Settings.LastNewProjectLocation = activeSolution;
            settings.Settings.LastNewProjectName = "Member";
            settings.Settings.LastNewSolutionLocation = Path.Combine(activeSolution, "Nested");
            settings.Save();

            using (var wiz = new NewProjectWizardForm(null, settings))
            {
                wiz.CreateControl();
                wiz.SetStandaloneSolutionMode(activeSolution);
                Assert.AreNotEqual(activeSolution, wiz.LocationText);
                Assert.IsFalse(wiz.LocationText.StartsWith(activeSolution + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual("MyApp", wiz.ProjectNameText);
            }
        }

        [TestMethod]
        public void SelectTemplatePage_HasColoredIcons_AndACategoryFilter()
        {
            using (var wiz = new NewProjectWizardForm())
            {
                wiz.CreateControl();

                var listField = typeof(NewProjectWizardForm).GetField("_list",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var list = (System.Windows.Forms.ListView)listField.GetValue(wiz);
                Assert.IsNotNull(list.SmallImageList, "colored language icons are a real ImageList (emoji render monochrome in GDI)");
                Assert.IsTrue(list.SmallImageList.Images.Count >= 4, "one colored monogram per shipped language (Python, CUDA C, C++) plus the unknown-language fallback");
                Assert.IsTrue(list.Items.Count > 0);
                foreach (System.Windows.Forms.ListViewItem it in list.Items)
                    Assert.IsFalse(string.IsNullOrEmpty(it.ImageKey) && it.ImageIndex < 0,
                        "every template row carries an icon: " + it.Text);

                var catField = typeof(NewProjectWizardForm).GetField("_categoryFilter",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var cat = (System.Windows.Forms.ComboBox)catField.GetValue(wiz);
                Assert.IsNotNull(cat, "third facet filter: category");
                Assert.AreEqual("All categories", cat.Items[0]);
                Assert.IsTrue(cat.Items.Count >= 2, "real category values populate the filter");
            }
        }

        [TestMethod]
        public void UniqueProjectName_IncrementsATrailingCounter_InsteadOfAppendingToIt()
        {
            Directory.CreateDirectory(Path.Combine(_location, "App2"));
            Assert.AreEqual("App3", NewProjectWizardForm.UniqueProjectName(_location, "App2"));

            Directory.CreateDirectory(Path.Combine(_location, "App3"));
            Assert.AreEqual("App4", NewProjectWizardForm.UniqueProjectName(_location, "App2"),
                "keeps counting past every taken suffix");

            Assert.AreEqual("Fresh", NewProjectWizardForm.UniqueProjectName(_location, "Fresh"),
                "an untaken name is returned verbatim");
        }
    }
}
