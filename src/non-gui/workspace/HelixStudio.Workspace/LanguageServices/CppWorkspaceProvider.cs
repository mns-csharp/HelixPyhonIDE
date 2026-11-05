using System;
using System.IO;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// C++/CUDA workspace provider — wraps <see cref="CMakeProjectImporter"/> (parse + target
    /// discovery) and <see cref="CMakeSolutionLoader"/> (one <c>ProjectModel</c> per CMake target,
    /// types populated via <c>CppTypeExtractor</c>). The returned
    /// <see cref="WorkspaceOpenResult.NativeProject"/> is the <see cref="CMakeProjectDescriptor"/>
    /// the App layer needs for real-toolchain configure/build/run, exactly as
    /// MainForm.LoadCMakeFolderAsNativeProject historically obtained it inline.
    /// </summary>
    public sealed class CppWorkspaceProvider : ILanguageWorkspaceProvider
    {
        public ProjectLanguage Language { get { return ProjectLanguage.Cpp; } }

        public LanguageWorkspaceDescriptor Descriptor
        {
            get { return LanguageDescriptors.For(ProjectLanguage.Cpp); }
        }

        public bool RecognizesManifest(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            foreach (string manifest in Descriptor.SolutionManifestFileNames)
                if (string.Equals(name, manifest, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool CanOpen(string path)
        {
            string folder = ResolveWorkspaceFolder(path);
            return folder != null;
        }

        /// <summary>
        /// Maps an accepted path to the CMake workspace folder: the directory itself when it
        /// contains a CMakeLists.txt, or the containing directory of an existing CMakeLists.txt
        /// file. Null when neither holds — mirrors the historical sniffing in
        /// SolutionCoordinator.OpenSolutionAsync verbatim.
        /// </summary>
        public string ResolveWorkspaceFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "CMakeLists.txt")))
                    return path;
                if (File.Exists(path) && RecognizesManifest(path))
                    return Path.GetDirectoryName(path);
            }
            catch { /* malformed path — not openable */ }
            return null;
        }

        public Task<WorkspaceOpenResult> OpenAsync(string path)
        {
            // Off-thread like SolutionService.LoadAsync: reload paths await this from the UI
            // thread, and the import walks every target's sources through CppTypeExtractor.
            return Task.Run(() => Open(path));
        }

        /// <summary>Synchronous core; the import is parser work plus local file reads.</summary>
        public WorkspaceOpenResult Open(string path)
        {
            string folder = ResolveWorkspaceFolder(path);
            if (folder == null)
                return WorkspaceOpenResult.Failed("Not a CMake workspace (no CMakeLists.txt): " + path);

            CMakeImportResult res;
            try
            {
                res = new CMakeProjectImporter().Import(folder);
            }
            catch (Exception ex)
            {
                return WorkspaceOpenResult.Failed(ex.Message);
            }

            if (res == null || !res.Success || res.Project == null)
                return WorkspaceOpenResult.Failed(res != null ? res.Error : "unknown");

            SolutionModel solution = CMakeSolutionLoader.BuildSolution(res.Project);
            return WorkspaceOpenResult.Opened(solution, res.Project);
        }
    }
}
