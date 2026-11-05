using System;
using System.IO;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// Shared <see cref="ILanguageWorkspaceProvider"/> skeleton for the manifest-anchored
    /// languages (Rust/C#/Java/Julia): recognition comes straight from the language's
    /// <see cref="LanguageWorkspaceDescriptor"/> (manifest names + extensions), directory opens
    /// resolve to the contained manifest, and <see cref="OpenAsync"/> wraps the language's
    /// solution loader with the layer's never-throw contract. Python and C++ keep their own
    /// hand-written providers — their recognition rules predate this base and are pinned by
    /// tests; new languages implement <see cref="BuildSolution"/> and nothing else.
    /// </summary>
    public abstract class ManifestWorkspaceProvider : ILanguageWorkspaceProvider
    {
        public abstract ProjectLanguage Language { get; }

        public LanguageWorkspaceDescriptor Descriptor
        {
            get { return LanguageDescriptors.For(Language); }
        }

        /// <summary>Build the SolutionModel from a resolved, existing manifest file. May throw; the base catches.</summary>
        protected abstract SolutionModel BuildSolution(string manifestPath);

        public bool RecognizesManifest(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            foreach (string manifest in Descriptor.SolutionManifestFileNames)
                if (string.Equals(name, manifest, StringComparison.OrdinalIgnoreCase)) return true;
            string ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
                foreach (string known in Descriptor.SolutionFileExtensions)
                    if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool CanOpen(string path)
        {
            return ResolveManifestPath(path) != null;
        }

        /// <summary>
        /// The manifest file an accepted path maps to: the path itself when it is an existing
        /// recognized file, or the recognized manifest inside an accepted directory. Null when
        /// neither holds.
        /// </summary>
        public string ResolveManifestPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (File.Exists(path) && RecognizesManifest(path)) return path;
                if (Directory.Exists(path))
                {
                    foreach (string manifest in Descriptor.SolutionManifestFileNames)
                    {
                        string candidate = Path.Combine(path, manifest);
                        if (File.Exists(candidate)) return candidate;
                    }
                    foreach (string ext in Descriptor.SolutionFileExtensions)
                    {
                        string[] matches = Directory.GetFiles(path, "*" + ext);
                        if (matches.Length > 0)
                        {
                            // Deterministic pick when a folder holds several candidate files.
                            Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
                            return matches[0];
                        }
                    }
                }
            }
            catch { /* malformed path — not openable */ }
            return null;
        }

        public Task<WorkspaceOpenResult> OpenAsync(string path)
        {
            // Off-thread like every other provider: the load walks and parses real source trees.
            return Task.Run(() => Open(path));
        }

        /// <summary>Synchronous core, exposed for tests.</summary>
        public WorkspaceOpenResult Open(string path)
        {
            string manifest = ResolveManifestPath(path);
            if (manifest == null)
                return WorkspaceOpenResult.Failed(
                    "Not a " + Descriptor.DisplayName + " workspace (no " +
                    string.Join("/", Descriptor.SolutionManifestFileNames.Length > 0
                        ? Descriptor.SolutionManifestFileNames
                        : Descriptor.SolutionFileExtensions) + "): " + path);

            SolutionModel solution;
            try { solution = BuildSolution(manifest); }
            catch (Exception ex) { return WorkspaceOpenResult.Failed(ex.Message); }

            if (solution == null || solution.Projects.Count == 0)
                return WorkspaceOpenResult.Failed(
                    "The " + Descriptor.DisplayName + " workspace at " + manifest + " contains no projects.");
            return WorkspaceOpenResult.Opened(solution);
        }
    }

    // The Rust / C# / Java / Julia manifest providers that lived here in the combined build
    // ship in their own products (HelixRust, HelixCSharp, HelixJava, HelixJulia). HelixPython
    // opens Python workspaces via PyProjectWorkspaceProvider; the base class above stays
    // because it is the shared manifest-walking mechanism, not a per-language arm.
}
