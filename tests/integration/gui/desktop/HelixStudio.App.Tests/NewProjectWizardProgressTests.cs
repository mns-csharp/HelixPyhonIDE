using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// New Project wizard Create behavior: (1) a wizard-created Python console
    /// project must LOAD AS A RUNNABLE APP — the template must persist
    /// [tool.helixstudio] kind/startup, since a missing kind silently defaults to
    /// ClassLibrary (the reported "loads as a library" bug); (2) Create shows the
    /// summary page's progress strip, with the venv step reported while
    /// `python -m venv` runs.
    /// </summary>
    [TestClass]
    public class NewProjectWizardProgressTests
    {
        private string _usageDir;
        private string _location;
        private string _prevUsage;

        [TestInitialize]
        public void Setup()
        {
            _usageDir = Path.Combine(Path.GetTempPath(), "helix_npwp_usage_" + Guid.NewGuid().ToString("N"));
            _location = Path.Combine(Path.GetTempPath(), "helix_npwp_loc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_usageDir);
            Directory.CreateDirectory(_location);
            _prevUsage = Environment.GetEnvironmentVariable("HELIX_USAGE_PATH");
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", Path.Combine(_usageDir, "usage.json"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", _prevUsage);
            try { Directory.Delete(_usageDir, true); } catch { }
            try { Directory.Delete(_location, true); } catch { }
        }

        // Drive the real user route up to the summary page so the template config
        // pages (with the "Create virtual environment" tick) actually exist.
        private static NewProjectWizardForm DriveToSummary()
        {
            var wiz = new NewProjectWizardForm();
            wiz.CreateControl();
            wiz.SetSearch("Python Console");
            wiz.SelectFirstTemplate();
            Assert.IsNotNull(wiz.SelectedTemplate, "Python Console App template must be selectable");
            wiz.SetOptions(git: false, readme: false, gitignore: false, license: false);
            wiz.GoNext();   // Select -> Configure
            wiz.GoNext();   // Configure -> Python options (builds config panels incl. UseVenv)
            wiz.GoNext();   // Python options -> Summary
            return wiz;
        }

        [TestMethod]
        public void CreatedConsoleProject_LoadsAsRunnableApp_NotLibrary()
        {
            using (var wiz = DriveToSummary())
            {
                Assert.IsTrue(wiz.SetConfigBool("UseVenv", false), "config page exposes the venv tick");
                Assert.IsTrue(wiz.CreateNow(_location, "AppKind"), "creation succeeds");
            }

            string toml = Path.Combine(_location, "AppKind", "pyproject.toml");
            StringAssert.Contains(File.ReadAllText(toml), "kind = \"app\"",
                "the template must persist the executable kind — a missing kind silently loads as ClassLibrary");

            Task<SolutionLoadResult> load = new SolutionService().LoadAsync(toml);
            load.Wait();
            Assert.IsTrue(load.Result.Success, load.Result.ErrorMessage);
            var project = load.Result.Solution.Projects.Single();
            Assert.AreEqual(ProjectKind.App, project.Kind, "wizard-created console project must load as an App");
            Assert.IsTrue(project.IsStartup, "the sole executable is the startup project");
        }

        [TestMethod]
        public void Create_WithVenvTicked_ShowsProgress_AndCreatesTheVenv()
        {
            using (var wiz = DriveToSummary())
            {
                Assert.IsTrue(wiz.SetConfigBool("UseVenv", true));
                Assert.IsTrue(wiz.CreateNow(_location, "VenvProj"), "creation succeeds");

                Assert.IsTrue(wiz.ProgressWasShownDuringCreate,
                    "the summary page's progress strip must be shown while Create runs");
                Assert.IsNotNull(wiz.PostGenResults);
                var venvStep = wiz.PostGenResults.Steps.FirstOrDefault(
                    s => s.Detail.Contains(".venv") || s.Name.Contains("python"));
                Assert.IsNotNull(venvStep, "a venv post-gen step must have run");
                Assert.IsTrue(venvStep.Ok, "venv creation must succeed on this machine: " + venvStep);
            }
            Assert.IsTrue(Directory.Exists(Path.Combine(_location, "VenvProj", ".venv")),
                ".venv directory physically created");
        }

        [TestMethod]
        public void Create_WithVenvUnticked_SkipsTheVenv_ButStillShowsProgress()
        {
            using (var wiz = DriveToSummary())
            {
                Assert.IsTrue(wiz.SetConfigBool("UseVenv", false));
                Assert.IsTrue(wiz.CreateNow(_location, "NoVenvProj"));
                Assert.IsTrue(wiz.ProgressWasShownDuringCreate,
                    "generation itself is also reported through the progress strip");
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(_location, "NoVenvProj", ".venv")));
        }
    }
}
