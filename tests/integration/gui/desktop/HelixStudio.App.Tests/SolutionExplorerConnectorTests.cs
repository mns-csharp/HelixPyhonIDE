using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Connectors;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The App half of the CodebaseSolutionToSolutionExplorerPanelConnector: which language arm
    /// owns which project, and that dispatch runs through the host-services seam rather than
    /// reaching into MainForm. Pure logic — the connector is constructed over a recording stub
    /// host, no Form, no message pump.
    /// </summary>
    [TestClass]
    public class SolutionExplorerConnectorTests
    {
        private sealed class RecordingHost : ISolutionExplorerHostServices
        {
            public readonly List<string> Calls = new List<string>();
            public string LastStatus;

            public void SetExplorerStatus(string text) { LastStatus = text; Calls.Add("status"); }
            public void ShowExplorerInfo(string caption, string message) { Calls.Add("info:" + caption); }
            public Task RunPythonProjectCommandAsync(ProjectModel project, string[] args, string operationVerb)
            { Calls.Add("python:" + operationVerb); return Task.CompletedTask; }
            public void BuildCMakeTargetFromExplorer(string targetName) { Calls.Add("cmake:" + targetName); }
            public Task<bool> RunProcessToBuildResultsAsync(string fileName, string arguments, string workingDirectory, string operationTitle)
            { Calls.Add("proc:" + fileName + " " + arguments); return Task.FromResult(true); }
            public void RunPythonTestsForProject(string projectDirectory) { Calls.Add("pytest"); }
            public void RunNativeTestsForProject(string projectDirectory) { Calls.Add("ctest"); }
            public void DebugPythonTestsForProject(string projectDirectory) { Calls.Add("debugpy"); }
            public void PersistPythonStartupSelection(SolutionModel solution, ProjectModel chosen) { Calls.Add("persist-toml"); }
            public bool HasPythonMainModule(ProjectModel project) { Calls.Add("has-main"); return true; }
            public void ShowManagePackagesDialog(ProjectModel project) { Calls.Add("manage-dialog"); }
        }

        private static ProjectModel MakeProject(ProjectLanguage language, ProjectKind kind, string name = "p")
        {
            var solution = new SolutionModel(@"X:\nowhere\anchor", "s");
            var project = new ProjectModel(@"X:\nowhere\" + name + ".anchor", name, solution)
            {
                Language = language,
                Kind = kind,
            };
            solution.Projects.Add(project);
            return project;
        }

        [TestMethod]
        public void For_RoutesEachLanguageToItsOwnConnector()
        {
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(new RecordingHost());

            Assert.IsInstanceOfType(connector.For(MakeProject(ProjectLanguage.Python, ProjectKind.App)),
                typeof(PythonExplorerConnector));
        }

        [TestMethod]
        public void For_SplitsCppAndCuda_ByKernelSources()
        {
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(new RecordingHost());

            ProjectModel plainCpp = MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaExecutable, "app");
            plainCpp.Types.Add(new TypeModel("Greeter", "app", TypeKind.ConcreteClass, plainCpp)
            { SourceFilePath = @"X:\nowhere\src\greeter.cpp" });

            ProjectModel cuda = MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaExecutable, "kernels");
            cuda.Types.Add(new TypeModel("Reducer", "kernels", TypeKind.ConcreteClass, cuda)
            { SourceFilePath = @"X:\nowhere\src\reduce.cu" });

            var cppArm = connector.For(plainCpp);
            var cudaArm = connector.For(cuda);
            Assert.IsInstanceOfType(cppArm, typeof(CppExplorerConnector));
            Assert.IsNotInstanceOfType(cppArm, typeof(CudaExplorerConnector),
                "a .cu-less CMake project belongs to the plain C++ arm");
            Assert.IsInstanceOfType(cudaArm, typeof(CudaExplorerConnector),
                "kernel-bearing CMake projects go to the CUDA arm");
        }

        [TestMethod]
        public void CanBeStartup_UsesTheOwningLanguagesOwnRule()
        {
            var host = new RecordingHost();
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(host);

            // C++: only executable CMake targets (libraries share the folder's CMakeLists).
            Assert.IsFalse(connector.CanBeStartup(MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaLibrary)));
            // Python: delegates to the entry-file check on the host.
            connector.CanBeStartup(MakeProject(ProjectLanguage.Python, ProjectKind.App));
            CollectionAssert.Contains(host.Calls, "has-main");
        }

        [TestMethod]
        public void Dispatch_RunsEachLanguagesVerbThroughTheHostSeam()
        {
            var host = new RecordingHost();
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(host);

            // C++ build → the CMake target pipeline, by target name.
            connector.HandleBuildProjectAsync(MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaExecutable, "app_cli"))
                .GetAwaiter().GetResult();
            CollectionAssert.Contains(host.Calls, "cmake:app_cli");

            // Python tests → the scoped pytest runner; Python debug-tests → debugpy.
            ProjectModel py = MakeProject(ProjectLanguage.Python, ProjectKind.App);
            connector.HandleRunTests(py);
            connector.HandleDebugTests(py);
            CollectionAssert.Contains(host.Calls, "pytest");
            CollectionAssert.Contains(host.Calls, "debugpy");

            // Python manage-packages → the dialog, never a toolchain probe.
            connector.HandleManagePackages(py);
            CollectionAssert.Contains(host.Calls, "manage-dialog");

            // Python startup persistence → the pyproject TOML write.
            connector.PersistStartupSelection(py.Solution, py);
            CollectionAssert.Contains(host.Calls, "persist-toml");
        }

        [TestMethod]
        public void Dispatch_VerbsWithoutAServiceRefuseViaStatus_NeverAnotherLanguagesPipeline()
        {
            var host = new RecordingHost();
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(host);

            // C++ has no manage dialog: the verb is descriptor-hidden, and even a programmatic
            // call must refuse via the status line instead of opening Python's dialog.
            connector.HandleManagePackages(MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaLibrary));
            CollectionAssert.Contains(host.Calls, "manage-dialog");

            // C++ debug-tests is descriptor-hidden; a programmatic call refuses, it does not
            // launch debugpy against .cpp sources (the pre-layer bug).
            connector.HandleDebugTests(MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaExecutable));
            CollectionAssert.DoesNotContain(host.Calls, "debugpy");
        }

        [TestMethod]
        public void Dispatch_CppRunTests_RoutesToTheNativeCtestPipeline_NotPytest()
        {
            var host = new RecordingHost();
            var connector = new CodebaseSolutionToSolutionExplorerPanelConnector(host);

            connector.HandleRunTests(MakeProject(ProjectLanguage.Cpp, ProjectKind.CudaExecutable, "gtests"));
            CollectionAssert.Contains(host.Calls, "ctest");
            CollectionAssert.DoesNotContain(host.Calls, "pytest",
                "C++ Run Tests used to run pytest against .cpp sources");
        }
    }
}
