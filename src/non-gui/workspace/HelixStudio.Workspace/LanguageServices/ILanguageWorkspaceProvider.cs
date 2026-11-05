using System.Threading.Tasks;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// The behavioral half of the language-services layer: one implementation per language that
    /// owns HOW a workspace of that language is recognized and loaded into a
    /// <see cref="HelixPythonNamespace.Core.Model.SolutionModel"/>. The data half (capability flags, verb
    /// labels, traits) is the <see cref="Descriptor"/>, defined in HelixPythonNamespace.Core so Tier-1 GUI
    /// assemblies that cannot reference Workspace still read the same rules.
    ///
    /// Contract notes:
    /// <list type="bullet">
    /// <item><see cref="RecognizesManifest"/> is a pure file-NAME test — no disk I/O — because
    /// callers like MainForm.IsProjectPath classify paths that may not exist yet (drag-drop,
    /// recent-files pruning).</item>
    /// <item><see cref="CanOpen"/> is disk-aware and may accept directories (a folder containing
    /// CMakeLists.txt is an openable C++ workspace; a bare folder is not an openable Python
    /// one — it goes to the Import wizard instead).</item>
    /// <item><see cref="OpenAsync"/> builds the model only. GUI adoption (canvas projection,
    /// panels, title, terminal cwd, recents) stays in the App layer, which is language-neutral
    /// once it holds a SolutionModel.</item>
    /// </list>
    /// </summary>
    public interface ILanguageWorkspaceProvider
    {
        ProjectLanguage Language { get; }

        /// <summary>Language-intrinsic rules shared with the GUI via HelixPythonNamespace.Core.</summary>
        LanguageWorkspaceDescriptor Descriptor { get; }

        /// <summary>Name-only recognition of a workspace manifest / solution file. No disk I/O.</summary>
        bool RecognizesManifest(string path);

        /// <summary>Disk-aware: true when <paramref name="path"/> (file or directory) is openable by this provider.</summary>
        bool CanOpen(string path);

        /// <summary>Load the workspace into a SolutionModel (+ optional native payload). Never throws for bad input — returns a failed result.</summary>
        Task<WorkspaceOpenResult> OpenAsync(string path);
    }
}
