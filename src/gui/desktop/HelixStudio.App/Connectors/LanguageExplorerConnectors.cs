using System;
using System.IO;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Connectors
{
    /// <summary>
    /// Shared plumbing for the per-language connectors: each holds the host-services seam and
    /// inherits safe no-op defaults that refuse via the status line, so a verb that somehow
    /// reaches a language without that capability (stale menu, programmatic call) degrades
    /// loudly-but-harmlessly instead of running another language's pipeline — the pre-layer
    /// failure mode this class exists to remove.
    /// </summary>
    internal abstract class LanguageExplorerConnectorBase : ILanguageExplorerConnector
    {
        protected LanguageExplorerConnectorBase(ISolutionExplorerHostServices host)
        {
            Host = host;
        }

        protected ISolutionExplorerHostServices Host { get; }

        public abstract ProjectLanguage Language { get; }

        public virtual bool Owns(ProjectModel project)
        {
            return project != null && project.Language == Language;
        }

        public virtual bool CanBeStartup(ProjectModel project)
        {
            return project != null && project.IsExecutable;
        }

        public virtual void PersistStartupSelection(SolutionModel solution, ProjectModel chosen)
        {
            // No manifest-level startup marker for this language; the per-solution user
            // setting written by MainForm is the persistence.
        }

        public virtual Task BuildProjectAsync(ProjectModel project)
        {
            Refuse("Build");
            return Task.CompletedTask;
        }

        public virtual void RunTests(ProjectModel project) { Refuse("Run Tests"); }

        public virtual void DebugTests(ProjectModel project) { Refuse("Debug Tests"); }

        public virtual Task RestorePackagesAsync(ProjectModel project)
        {
            Refuse("Restore packages");
            return Task.CompletedTask;
        }

        public virtual void ManagePackages(ProjectModel project) { Host.ShowManagePackagesDialog(project); }

        private void Refuse(string verb)
        {
            Host.SetExplorerStatus(verb + " is not available for " +
                HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(Language).DisplayName + " projects.");
        }

        // Directory a project's toolchain commands run in.
        protected static string ProjectDir(ProjectModel project)
        {
            return project != null ? project.ProjectDirectory : null;
        }
    }

    /// <summary>
    /// Python — every verb wraps the pre-layer MainForm behavior verbatim, including the
    /// historical fallback of `dotnet build`/`dotnet restore` for a Python-language project
    /// whose anchor is not a pyproject.toml (legacy .sln-imported projects).
    /// </summary>
    internal sealed class PythonExplorerConnector : LanguageExplorerConnectorBase
    {
        public PythonExplorerConnector(ISolutionExplorerHostServices host) : base(host) { }

        public override ProjectLanguage Language { get { return ProjectLanguage.Python; } }

        public override bool CanBeStartup(ProjectModel project)
        {
            return Host.HasPythonMainModule(project);
        }

        public override void PersistStartupSelection(SolutionModel solution, ProjectModel chosen)
        {
            Host.PersistPythonStartupSelection(solution, chosen);
        }

        public override async Task BuildProjectAsync(ProjectModel project)
        {
            if (MainForm.IsPythonProjectFile(project.FilePath))
            {
                await Host.RunPythonProjectCommandAsync(project, new[] { "-m", "build" }, "build");
                return;
            }
            Host.ShowExplorerInfo("Not a Python Project",
                "Build is available for Python projects (pyproject.toml/.pyproj). "
                + project.Name + " is not one.");
        }

        public override void RunTests(ProjectModel project)
        {
            string dir = ProjectDir(project);
            if (!string.IsNullOrEmpty(dir)) Host.RunPythonTestsForProject(dir);
        }

        public override void DebugTests(ProjectModel project)
        {
            string dir = ProjectDir(project);
            if (!string.IsNullOrEmpty(dir)) Host.DebugPythonTestsForProject(dir);
        }

        public override async Task RestorePackagesAsync(ProjectModel project)
        {
            if (MainForm.IsPythonProjectFile(project.FilePath))
            {
                await Host.RunPythonProjectCommandAsync(project, new[] { "-m", "pip", "install", "-e", "." }, "restore");
                return;
            }
            Host.ShowExplorerInfo("Not a Python Project",
                "Restore is available for Python projects (pyproject.toml/.pyproj). "
                + project.Name + " is not one.");
        }

        public override void ManagePackages(ProjectModel project)
        {
            Host.ShowManagePackagesDialog(project);
        }

    }

    /// <summary>
    /// C++ (CMake) — build routes to the native CMake pipeline for exactly the clicked target.
    /// Run Tests keeps the historical routing (the shared scoped test-runner path); Debug
    /// Tests / package verbs are descriptor-hidden and refuse defensively here.
    /// </summary>
    internal class CppExplorerConnector : LanguageExplorerConnectorBase
    {
        public CppExplorerConnector(ISolutionExplorerHostServices host) : base(host) { }

        public override ProjectLanguage Language { get { return ProjectLanguage.Cpp; } }

        public override bool CanBeStartup(ProjectModel project)
        {
            // Kind must say Executable specifically — several sibling targets share one
            // folder's CMakeLists.txt, and libraries in it are not startable (see
            // ProjectTreePanel.IsAppProject's native branch, which this mirrors).
            return project != null && project.Kind == ProjectKind.CudaExecutable;
        }

        public override Task BuildProjectAsync(ProjectModel project)
        {
            // Each CMake target is its own ProjectModel (CMakeSolutionLoader) — Name is the
            // real CMake target name, so this builds exactly that one target.
            Host.BuildCMakeTargetFromExplorer(project.Name);
            return Task.CompletedTask;
        }

        public override void RunTests(ProjectModel project)
        {
            // Real ctest pipeline: build the workspace, then ctest scoped to this project's
            // build subdirectory (GTest cases registered by gtest_discover_tests). This used to
            // route to the pytest runner, which collected nothing from .cpp sources.
            string dir = ProjectDir(project);
            if (!string.IsNullOrEmpty(dir)) Host.RunNativeTestsForProject(dir);
        }
    }

    /// <summary>
    /// CUDA — a CMake project that actually carries kernel sources (.cu/.cuh). Subsystem
    /// routing is the C++ pipeline today (nvcc enters through the same CMake build); the
    /// separate class is the seam where CUDA-only explorer behavior lands without touching
    /// the plain-C++ arm. Registered ahead of <see cref="CppExplorerConnector"/> so it wins
    /// for kernel-bearing projects.
    /// </summary>
    internal sealed class CudaExplorerConnector : CppExplorerConnector
    {
        public CudaExplorerConnector(ISolutionExplorerHostServices host) : base(host) { }

        public override bool Owns(ProjectModel project)
        {
            return base.Owns(project) && HasCudaSources(project);
        }

        internal static bool HasCudaSources(ProjectModel project)
        {
            if (project == null) return false;
            foreach (TypeModel type in project.Types)
                if (IsCudaFile(type.SourceFilePath)) return true;
            foreach (FunctionModel fn in project.Functions)
                if (IsCudaFile(fn.SourceFilePath)) return true;
            return false;
        }

        private static bool IsCudaFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase);
        }
    }

}
