using System;
using System.IO;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// Python workspace provider — wraps <see cref="SolutionService"/> (pyproject.toml solutions,
    /// PSM rules, and the PTVS <c>.sln</c>/<c>.slnx</c> compatibility path). This class adds no
    /// loading logic of its own; it is the Python arm's single registration point in the
    /// language-services layer.
    /// </summary>
    public sealed class PythonWorkspaceProvider : ILanguageWorkspaceProvider
    {
        private readonly ISolutionService _solutionService;

        public PythonWorkspaceProvider() : this(new SolutionService()) { }

        /// <summary>Test seam: inject a fake ISolutionService.</summary>
        public PythonWorkspaceProvider(ISolutionService solutionService)
        {
            _solutionService = solutionService ?? new SolutionService();
        }

        public ProjectLanguage Language { get { return ProjectLanguage.Python; } }

        public LanguageWorkspaceDescriptor Descriptor
        {
            get { return LanguageDescriptors.For(ProjectLanguage.Python); }
        }

        public bool RecognizesManifest(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string manifest in Descriptor.SolutionManifestFileNames)
                if (string.Equals(name, manifest, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string ext in Descriptor.SolutionFileExtensions)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool CanOpen(string path)
        {
            // Files only: a bare directory is Import-wizard territory for Python, never a
            // direct open (unlike C++, where the CMakeLists.txt-bearing folder IS the workspace).
            return RecognizesManifest(path) && File.Exists(path);
        }

        public async Task<WorkspaceOpenResult> OpenAsync(string path)
        {
            try
            {
                SolutionLoadResult result = await _solutionService.LoadAsync(path).ConfigureAwait(false);
                return result.Success
                    ? WorkspaceOpenResult.Opened(result.Solution)
                    : WorkspaceOpenResult.Failed(result.ErrorMessage);
            }
            catch (Exception ex)
            {
                return WorkspaceOpenResult.Failed("Failed to load solution: " + ex.Message);
            }
        }
    }
}
