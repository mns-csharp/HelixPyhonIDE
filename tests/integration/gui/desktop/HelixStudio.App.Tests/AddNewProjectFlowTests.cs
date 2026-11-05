using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Templates;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Solution Explorer "Add New Project" (renamed from "Add Project"): the wizard's
    /// Select Template page restricted to the solution language's eligible set, the
    /// Location locked to the solution root, and the created project registered with
    /// the open solution (member TOML / linked CMake project; startup never stolen).
    /// Temp solution dirs are deliberately NOT deleted: AddCreatedProjectToSolution
    /// ends in the async-void ReloadSolutionAsync (see the suite-wide rule in
    /// helixstudio-build-test — deleting the dir can pop an undismissable dialog).
    /// </summary>
    [TestClass]
    public class AddNewProjectFlowTests
    {
        [TestMethod]
        public void Wizard_RestrictedToPythonEligibility_ListsExactlyThatSet_InOrder()
        {
            using (var wiz = new NewProjectWizardForm())
            {
                wiz.CreateControl();
                wiz.RestrictToTemplates(TemplateEligibility.EligibleTemplateIds("Python"));
                CollectionAssert.AreEqual(
                    new[] { "py.console", "py.notebook", "py.classlib", "py.test", "cuda.classlib", "py.flask", "py.gui.tk" },
                    wiz.ListedTemplateIds.ToArray(),
                    "Python solutions offer exactly the eligible templates, in eligibility order");
            }
        }

        [TestMethod]
        public void Wizard_RestrictedToCudaEligibility_ListsNativeAndPythonApp()
        {
            using (var wiz = new NewProjectWizardForm())
            {
                wiz.CreateControl();
                wiz.RestrictToTemplates(TemplateEligibility.EligibleTemplateIds("CUDA"));
                CollectionAssert.AreEqual(
                    new[] { "cuda.console", "cuda.classlib", "cuda.dll", "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test", "py.console" },
                    wiz.ListedTemplateIds.ToArray());
            }
        }

        [TestMethod]
        public void Wizard_SetFixedLocation_LocksTheLocationField()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_loc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using (var wiz = new NewProjectWizardForm())
            {
                wiz.CreateControl();
                wiz.SetFixedLocation(root);
                Assert.AreEqual(root, wiz.LocationText);
            }
            try { Directory.Delete(root, true); } catch { }
        }

        [TestMethod]
        public void AddCreatedPythonProject_BecomesAMember_AndDoesNotStealStartup()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_py_" + Guid.NewGuid().ToString("N"));
            string appDir = Path.Combine(root, "app");
            string newDir = Path.Combine(root, "newlib");
            Directory.CreateDirectory(appDir);
            Directory.CreateDirectory(newDir);

            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml,
                "[project]\nname = \"ws\"\n\n[tool.uv.workspace]\nmembers = [\"app\"]\n");
            File.WriteAllText(Path.Combine(appDir, "pyproject.toml"),
                "[project]\nname = \"app\"\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(appDir, "main.py"),
                "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");
            // What the py.console template writes: a fresh app claiming startup itself.
            File.WriteAllText(Path.Combine(newDir, "pyproject.toml"),
                "[project]\nname = \"newlib\"\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(newDir, "main.py"),
                "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            var sln = new SolutionModel(rootToml, "ws");
            var app = new ProjectModel(Path.Combine(appDir, "pyproject.toml"), "app", sln) { IsStartup = true };
            sln.Projects.Add(app);

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(newDir));

                string members = File.ReadAllText(rootToml);
                StringAssert.Contains(members, "newlib", "created project must join the workspace members");

                // SetStartup(false) REMOVES the startup line (absent = not startup) rather than
                // writing "startup = false" — either way the new member must not claim startup.
                string newToml = File.ReadAllText(Path.Combine(newDir, "pyproject.toml"));
                Assert.IsFalse(newToml.Contains("startup = true"),
                    "a project added to an existing solution must not steal its startup");
                StringAssert.Contains(newToml, "kind = \"app\"", "the kind survives the startup strip");

                form.Close();
            }
        }

        [TestMethod]
        public void NewCudaProject_CreatesAndOpensAsANativeWorkspace()
        {
            // Regression for "created a CUDA solution, the wizard finished, nothing opened":
            // a native template result has no pyproject.toml, so the wizard's success path
            // must adopt it through the CMake workspace route instead.
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_cuda_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string prevUsage = Environment.GetEnvironmentVariable("HELIX_USAGE_PATH");
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", Path.Combine(root, "usage.json"));
            try
            {
                string created;
                using (var wiz = new NewProjectWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSearch("CUDA Console");
                    wiz.SelectFirstTemplate();
                    Assert.IsNotNull(wiz.SelectedTemplate);
                    Assert.AreEqual("cuda.console", wiz.SelectedTemplate.Id);
                    wiz.SetOptions(git: false, readme: false, gitignore: false, license: false);
                    wiz.GoNext();   // Select -> Configure
                    wiz.GoNext();   // Configure -> CUDA options (CudaArch etc.)
                    wiz.GoNext();   // -> Summary
                    Assert.IsTrue(wiz.CreateNow(root, "GpuApp"), "CUDA project creation must succeed");
                    created = wiz.CreatedProjectRoot;
                }
                Assert.IsTrue(File.Exists(Path.Combine(created, "CMakeLists.txt")));
                Assert.IsTrue(File.Exists(Path.Combine(created, "main.cu")));
                Assert.IsFalse(File.Exists(Path.Combine(created, "pyproject.toml")),
                    "native template — the python-solution open path must not be the one used");

                // The same adoption call MainForm's wizard success path now makes.
                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    object descriptor = form.LoadCMakeFolderAsNativeProject(created);
                    Assert.IsNotNull(descriptor, "the created CUDA project must open as a native workspace");
                    Assert.IsNotNull(form._currentSolution, "the native workspace becomes the current solution");
                    Assert.IsTrue(form._currentSolution.Projects.Count > 0,
                        "the CMake target shows as a project");
                    form.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", prevUsage);
                // The dir stays: LoadCMakeFolderAsNativeProject wires watchers/persistence to it.
            }
        }

        [TestMethod]
        public void EveryTemplate_GeneratesAScaffold_TheIdeCanActuallyOpen()
        {
            // Regression for "the <language> template doesn't generate any scaffold" (reported
            // for all 12 C#/Java/Rust/Julia templates the combined build shipped): the files WERE
            // generated — the wizard's success path only knew how to open pyproject.toml and
            // CMakeLists.txt, so for the manifest languages the IDE opened NOTHING. Every
            // template must yield an anchor FindOpenableProjectAnchor resolves, or its language
            // looks broken end-to-end.
            var catalog = TemplateCatalog.BuiltIn();
            string root = Path.Combine(Path.GetTempPath(), "helix_anchor_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                int n = 0;
                foreach (var template in catalog.Templates)
                {
                    string name = "Proj" + (++n);
                    var opt = new HelixPythonNamespace.Workspace.Templates.GenerationOptions { TargetRoot = root };
                    opt.Variables["ProjectName"] = name;
                    opt.Variables["Description"] = "t";
                    var r = HelixPythonNamespace.Workspace.Templates.ProjectGenerator.Generate(template, opt);
                    Assert.IsTrue(r.Success, template.Id + ": " + r.Error);

                    string projectDir = Path.Combine(root, name);
                    Assert.IsNotNull(MainForm.FindOpenableProjectAnchor(projectDir),
                        template.Id + " (" + template.Name + ") generated a scaffold the IDE cannot open — " +
                        "files: " + string.Join(", ",
                            Directory.GetFiles(projectDir, "*", SearchOption.AllDirectories)
                                .Select(f => f.Substring(projectDir.Length + 1))));
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void AddToSolutionMode_SuppressesScaffoldingExtras()
        {
            // A project ADDED to an existing solution must not scaffold standalone-solution
            // extras: no nested git repo, no duplicate README/.gitignore/LICENSE, no
            // per-member .venv (the workspace root owns the venv), no eager restore.
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_bare_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string prevUsage = Environment.GetEnvironmentVariable("HELIX_USAGE_PATH");
            Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", Path.Combine(root, "usage.json"));
            try
            {
                using (var wiz = new NewProjectWizardForm())
                {
                    wiz.CreateControl();
                    wiz.SetSearch("Python Console");
                    wiz.SelectFirstTemplate();
                    Assert.IsNotNull(wiz.SelectedTemplate);
                    wiz.SetFixedLocation(root);
                    wiz.SetAddToSolutionMode();
                    wiz.GoNext();   // Select -> Configure
                    wiz.GoNext();   // Configure -> Python options (UseVenv auto-unchecked here)
                    wiz.GoNext();   // -> Summary
                    Assert.IsTrue(wiz.CreateNow(root, "BareMember"), "creation succeeds");

                    Assert.IsNotNull(wiz.PostGenResults);
                    Assert.AreEqual(0, wiz.PostGenResults.Steps.Count,
                        "no post-generation extras may run in add-to-solution mode; ran: " +
                        string.Join(", ", wiz.PostGenResults.Steps.Select(s => s.ToString())));
                }

                string dir = Path.Combine(root, "BareMember");
                Assert.IsTrue(File.Exists(Path.Combine(dir, "main.py")), "the project itself is generated");
                Assert.IsFalse(Directory.Exists(Path.Combine(dir, ".git")), "no nested git repository");
                Assert.IsFalse(Directory.Exists(Path.Combine(dir, ".venv")), "no per-member venv");
                Assert.IsFalse(File.Exists(Path.Combine(dir, "README.md")), "no duplicate README");
                Assert.IsFalse(File.Exists(Path.Combine(dir, ".gitignore")), "no duplicate .gitignore");
                Assert.IsFalse(File.Exists(Path.Combine(dir, "LICENSE")), "no duplicate LICENSE");
            }
            finally
            {
                Environment.SetEnvironmentVariable("HELIX_USAGE_PATH", prevUsage);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [TestMethod]
        public void AddNewProject_ToARootProjectSolution_KeepsTheSolution_AndGainsTheMember()
        {
            // The user-reported "the new project REPLACED my solution" shape: the open solution's
            // root pyproject.toml IS its sole project (main.py at the root, kind=app) and carries a
            // stale workspace member from an earlier bare-scaffold "Add Project". Adding a new
            // project must keep THIS solution (same root, root project still present and startup)
            // and merely gain the member — across a real SolutionService reload.
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_root_" + Guid.NewGuid().ToString("N"));
            string newDir = Path.Combine(root, "newproj");
            Directory.CreateDirectory(newDir);

            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml,
                "[project]\nname = \"rootsln\"\nversion = \"0.1.0\"\nrequires-python = \">=3.11\"\n\n" +
                "[tool.uv.workspace]\nmembers = [\"NewProject\"]\n\n" +      // stale: dir doesn't exist
                "[tool.helixstudio]\ninterface-style = \"protocol\"\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(root, "main.py"),
                "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");
            File.WriteAllText(Path.Combine(newDir, "pyproject.toml"),
                "[project]\nname = \"newproj\"\n\n[tool.helixstudio]\nkind = \"app\"\nstartup = true\n");
            File.WriteAllText(Path.Combine(newDir, "main.py"),
                "def main():\n    pass\n\nif __name__ == \"__main__\":\n    main()\n");

            var loadBefore = new HelixPythonNamespace.Workspace.Services.SolutionService().LoadAsync(rootToml);
            loadBefore.Wait();
            Assert.IsTrue(loadBefore.Result.Success, loadBefore.Result.ErrorMessage);
            SolutionModel sln = loadBefore.Result.Solution;
            Assert.IsTrue(sln.Projects.Any(p => PathIsRoot(p, root)),
                "precondition: the root project itself is part of the loaded solution; got: " +
                string.Join("; ", sln.Projects.Select(p => p.Name + " @ " + (p.ProjectDirectory ?? "<null>") +
                                                          " file=" + (p.FilePath ?? "<null>"))));

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(newDir));
                form.Close();
            }

            // The reload the real flow performs: same root TOML, not the new project's.
            var loadAfter = new HelixPythonNamespace.Workspace.Services.SolutionService().LoadAsync(rootToml);
            loadAfter.Wait();
            Assert.IsTrue(loadAfter.Result.Success, loadAfter.Result.ErrorMessage);
            SolutionModel after = loadAfter.Result.Solution;

            Assert.IsTrue(after.Projects.Any(p => PathIsRoot(p, root)),
                "the ORIGINAL root project must survive — the new project must not replace the solution");
            Assert.IsTrue(after.Projects.Any(p => string.Equals(p.Name, "newproj", StringComparison.OrdinalIgnoreCase)),
                "the new project joins the same solution");
            var startup = after.Projects.FirstOrDefault(p => p.IsStartup);
            Assert.IsNotNull(startup, "solution still has a startup project");
            Assert.IsTrue(PathIsRoot(startup, root), "startup stays with the original root project");
        }

        private static bool PathIsRoot(ProjectModel p, string root)
        {
            if (string.IsNullOrEmpty(p.ProjectDirectory)) return false;
            return string.Equals(Path.GetFullPath(p.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void AddCreatedCMakeProject_ToANativeSolution_JoinsViaAddSubdirectory()
        {
            // Regression for "added a C++ library to a CUDA solution, nothing new appeared":
            // a native solution's FilePath IS its root CMakeLists.txt — registration used to
            // append a TOML table into it (corrupting the file, adding nothing visible). The
            // CMake idiom is add_subdirectory, which the importer follows.
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_native_" + Guid.NewGuid().ToString("N"));
            string libDir = Path.Combine(root, "cpp_lib");
            Directory.CreateDirectory(libDir);

            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(rootCmake,
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n" +
                "add_executable(GpuApp main.cu)\n");
            File.WriteAllText(Path.Combine(root, "main.cu"), "int main() { return 0; }\n");
            File.WriteAllText(Path.Combine(libDir, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.18)\nproject(cpp_lib LANGUAGES CXX)\nadd_library(cpp_lib STATIC lib.cpp)\n");
            File.WriteAllText(Path.Combine(libDir, "lib.cpp"), "int answer() { return 42; }\n");

            var sln = new SolutionModel(rootCmake, "GpuApp");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(libDir));
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(libDir), "second registration is idempotent");
                form.Close();
            }

            string updated = File.ReadAllText(rootCmake);
            StringAssert.Contains(updated, "add_subdirectory(\"cpp_lib\")");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(updated, "add_subdirectory").Count,
                "registered exactly once");
            Assert.IsFalse(updated.Contains("[tool.helixstudio]"),
                "the TOML machinery must never touch a CMakeLists.txt");
            Assert.IsFalse(updated.Contains("enable_testing"),
                "a plain library must not switch the root into testing mode");
        }

        [TestMethod]
        public void AddCreatedGTestProject_ToANativeSolution_EnablesTestingAtTheRoot()
        {
            // Root-level ctest sees a test SUBPROJECT only when the root also calls
            // enable_testing() before its add_subdirectory — the GTest template enables
            // testing in its own directory only, so registration must add the root call
            // (otherwise Run All Tests finds no CTestTestfile.cmake at the build root).
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_gtest_" + Guid.NewGuid().ToString("N"));
            string testDir = Path.Combine(root, "gtests");
            Directory.CreateDirectory(testDir);

            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(rootCmake,
                "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n" +
                "add_executable(GpuApp main.cu)\n");
            File.WriteAllText(Path.Combine(testDir, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.18)\nproject(gtests LANGUAGES CXX)\n" +
                "enable_testing()\nadd_executable(gtests test_main.cpp)\n" +
                "include(GoogleTest)\ngtest_discover_tests(gtests)\n");
            File.WriteAllText(Path.Combine(testDir, "test_main.cpp"), "int main() { return 0; }\n");

            var sln = new SolutionModel(rootCmake, "GpuApp");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(testDir));
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(testDir), "second registration is idempotent");
                form.Close();
            }

            string updated = File.ReadAllText(rootCmake);
            StringAssert.Contains(updated, "add_subdirectory(\"gtests\")");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(updated, "enable_testing").Count,
                "root enable_testing() added exactly once");
            Assert.IsTrue(updated.IndexOf("enable_testing", StringComparison.Ordinal)
                          < updated.IndexOf("add_subdirectory", StringComparison.Ordinal),
                "enable_testing() must precede the add_subdirectory to take effect for it");
        }

        [TestMethod]
        public void AddCreatedPythonProject_ToANativeSolution_IsRefused_WithoutTouchingTheFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_pyref_" + Guid.NewGuid().ToString("N"));
            string pyDir = Path.Combine(root, "py_app");
            Directory.CreateDirectory(pyDir);

            string rootCmake = Path.Combine(root, "CMakeLists.txt");
            const string original = "cmake_minimum_required(VERSION 3.18)\nproject(GpuApp LANGUAGES CUDA CXX)\n";
            File.WriteAllText(rootCmake, original);
            File.WriteAllText(Path.Combine(pyDir, "pyproject.toml"), "[project]\nname = \"py_app\"\n");

            var sln = new SolutionModel(rootCmake, "GpuApp");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsFalse(form.RegisterCreatedProjectInSolution(pyDir),
                    "a Python project cannot join a CMake workspace");
                form.Close();
            }
            Assert.AreEqual(original, File.ReadAllText(rootCmake), "the root CMakeLists stays untouched");
        }

        [TestMethod]
        public void ReferenceManager_OffersProjectsAndLanguageAppropriatePackages()
        {
            // VS2022-style Reference Manager: categories on the left, project checklist +
            // package entry on the right; C++/CUDA (no package ecosystem) get Projects only.
            string root = Path.Combine(Path.GetTempPath(), "helix_refmgr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sln = new SolutionModel(Path.Combine(root, "pyproject.toml"), "App");
                var app = new ProjectModel(Path.Combine(root, "app", "pyproject.toml"), "App", sln)
                { Language = HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python };
                var lib = new ProjectModel(Path.Combine(root, "MyLib", "pyproject.toml"), "MyLib", sln)
                { Language = HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python };
                sln.Projects.Add(app);
                sln.Projects.Add(lib);

                using (var dlg = new HelixPythonNamespace.App.Dialogs.ReferenceManagerForm(sln, app))
                {
                    dlg.CreateControl();
                    Assert.AreEqual(2, dlg.CategoryCount, "Python offers Projects AND Packages");
                    Assert.IsTrue(dlg.CheckProject("MyLib"));
                    dlg.SelectCategory(1);
                    dlg.QueuePackage("requests", "2.31.0");

                    Assert.AreEqual(1, dlg.SelectedProjects.Count);
                    Assert.AreEqual("MyLib", dlg.SelectedProjects[0].Name);
                    Assert.AreEqual(1, dlg.PendingPackages.Count);
                    Assert.AreEqual("requests", dlg.PendingPackages[0].Key);
                }

                var cudaProj = new ProjectModel(Path.Combine(root, "gpu.cmake-target"), "gpu", sln)
                { Language = HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp };
                using (var dlg = new HelixPythonNamespace.App.Dialogs.ReferenceManagerForm(sln, cudaProj))
                {
                    dlg.CreateControl();
                    Assert.AreEqual(1, dlg.CategoryCount, "native projects have no package ecosystem — Projects only");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void AddCreatedCMakeProject_IsLinkedViaTheRootToml()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_anp_cu_" + Guid.NewGuid().ToString("N"));
            string libDir = Path.Combine(root, "gpu_lib");
            Directory.CreateDirectory(libDir);

            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml, "[project]\nname = \"ws\"\n");
            File.WriteAllText(Path.Combine(libDir, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.18)\nproject(gpu_lib LANGUAGES CUDA CXX)\n");

            var sln = new SolutionModel(rootToml, "ws");

            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                form._currentSolution = sln;
                Assert.IsTrue(form.RegisterCreatedProjectInSolution(libDir));

                string updated = File.ReadAllText(rootToml);
                StringAssert.Contains(updated, "linked-cmake-project = \"gpu_lib\"",
                    "a native CMake project links into the solution via [tool.helixstudio]");

                form.Close();
            }
        }
    }
}
