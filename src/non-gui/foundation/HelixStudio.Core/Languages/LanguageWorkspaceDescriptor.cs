using System;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Languages
{
    /// <summary>
    /// Language-intrinsic workspace rules for one <see cref="ProjectLanguage"/> — the data half
    /// of the language-services layer (the behavioral half is
    /// <c>HelixPythonNamespace.Workspace.LanguageServices.ILanguageWorkspaceProvider</c>, which Tier-1 GUI
    /// assemblies cannot reference).
    ///
    /// WHY THIS EXISTS: solution format (pyproject.toml vs CMakeLists.txt), context-menu verbs,
    /// package-management rules and type-system traits are properties of the LANGUAGE, not of the
    /// user or of the calling control. Before this type, each of those decisions lived as an inline
    /// <c>if (Language == ProjectLanguage.Cpp)</c> somewhere in MainForm / SolutionCoordinator /
    /// ProjectTreePanel — so fixing the C++ arm routinely broke the Python arm and vice versa.
    /// GUI code must consult <see cref="LanguageDescriptors.For"/> instead of hard-coding a
    /// language test; adding a language means registering a descriptor, not hunting call sites.
    ///
    /// Deliberately NOT here: anything the languages do not actually share as a concept. The
    /// codebase already established (see <c>CppTypeService</c>'s doc comment) that forcing one
    /// language through another language's service interface silently drops semantics — this
    /// descriptor therefore carries capabilities and labels, never behavior.
    /// </summary>
    public sealed class LanguageWorkspaceDescriptor
    {
        public LanguageWorkspaceDescriptor(
            ProjectLanguage language,
            string displayName,
            string[] solutionManifestFileNames,
            string[] solutionFileExtensions,
            bool supportsPackageRename,
            bool supportsPackageManager,
            string restorePackagesMenuLabel,
            string managePackagesMenuLabel,
            bool supportsGuiFormsDesigner,
            bool allowsMultipleBaseClasses,
            // ---- explorer-connector traits (CodebaseSolutionToSolutionExplorerPanelConnector).
            // Optional so the two pre-layer registrations and every existing test keep compiling;
            // the defaults reproduce the tree's historical Python-era behavior exactly.
            string startupVerbLabel = "Set as Startup Package",
            bool supportsTypeScaffolding = true,
            bool supportsCodeModelEdits = true,
            bool supportsRunTests = true,
            bool supportsDebugTests = true,
            string[] sourceFileExtensions = null,
            string[] testFileExtensions = null,
            string[] testFileNamePrefixes = null,
            string[] testFileNameSuffixes = null,
            string[] testFileNames = null,
            string[] testDirectoryNames = null,
            ExplorerIconSet explorerIcons = null)
        {
            Language = language;
            DisplayName = displayName ?? language.ToString();
            SolutionManifestFileNames = solutionManifestFileNames ?? new string[0];
            SolutionFileExtensions = solutionFileExtensions ?? new string[0];
            SupportsPackageRename = supportsPackageRename;
            SupportsPackageManager = supportsPackageManager;
            RestorePackagesMenuLabel = restorePackagesMenuLabel;
            ManagePackagesMenuLabel = managePackagesMenuLabel;
            SupportsGuiFormsDesigner = supportsGuiFormsDesigner;
            AllowsMultipleBaseClasses = allowsMultipleBaseClasses;
            StartupVerbLabel = startupVerbLabel;
            SupportsTypeScaffolding = supportsTypeScaffolding;
            SupportsCodeModelEdits = supportsCodeModelEdits;
            SupportsRunTests = supportsRunTests;
            SupportsDebugTests = supportsDebugTests;
            SourceFileExtensions = sourceFileExtensions ?? new string[0];
            // Defaults mirror ProjectTreePanel's historical hard-coded heuristic
            // (TestFilePatterns = *.py/*.cpp/*.cxx/*.cc with test_* / *_test names).
            TestFileExtensions = testFileExtensions ?? new[] { ".py", ".cpp", ".cxx", ".cc" };
            TestFileNamePrefixes = testFileNamePrefixes ?? new[] { "test_" };
            TestFileNameSuffixes = testFileNameSuffixes ?? new[] { "_test" };
            TestFileNames = testFileNames ?? new string[0];
            TestDirectoryNames = testDirectoryNames ?? new string[0];
            ExplorerIcons = explorerIcons;
        }

        public ProjectLanguage Language { get; }

        /// <summary>Human-readable name, e.g. "Python" or "C++/CUDA".</summary>
        public string DisplayName { get; }

        // ---------------------------------------------------------------- solution format

        /// <summary>
        /// Exact file names (no path) that anchor a workspace of this language, e.g.
        /// <c>pyproject.toml</c> or <c>CMakeLists.txt</c>. Name-only — existence checks are the
        /// provider's job.
        /// </summary>
        public string[] SolutionManifestFileNames { get; }

        /// <summary>
        /// Solution-file extensions (with leading dot) this language can open, e.g.
        /// <c>.sln</c>/<c>.slnx</c> for the Python front end's PTVS-compatibility path.
        /// </summary>
        public string[] SolutionFileExtensions { get; }

        // ------------------------------------------- explorer verbs (language-dependent, not user-dependent)

        /// <summary>
        /// True when "Rename Package…" is meaningful: the language maps a namespace to a
        /// directory (Python's dotted-import ↔ folder convention). A CMake target has no such
        /// equivalent — see <c>CppSymbolRenameService.CppRenameScope</c>.
        /// </summary>
        public bool SupportsPackageRename { get; }

        /// <summary>
        /// True when the IDE offers package-manager verbs (restore / manage) on a project node
        /// of this language. False hides the verbs entirely — showing another language's package
        /// manager on a project is precisely the cross-language leakage this layer removes.
        /// </summary>
        public bool SupportsPackageManager { get; }

        /// <summary>Menu text for the restore verb; null when <see cref="SupportsPackageManager"/> is false.</summary>
        public string RestorePackagesMenuLabel { get; }

        /// <summary>Menu text for the manage verb; null when <see cref="SupportsPackageManager"/> is false.</summary>
        public string ManagePackagesMenuLabel { get; }

        // ---------------------------------------------------------------- language traits

        /// <summary>True when the GUI-forms designer pipeline supports this language's projects.</summary>
        public bool SupportsGuiFormsDesigner { get; }

        /// <summary>
        /// Type-system trait: whether a class may list multiple base CLASSES (Python MRO,
        /// C++ multiple inheritance). A single-base language would register false and the
        /// canvas/scaffolding layers would read it from here instead of guessing.
        /// </summary>
        public bool AllowsMultipleBaseClasses { get; }

        // ------------------------------------- explorer-connector traits (menus, icons, tests)

        /// <summary>
        /// Menu text for the startup-selection verb on a project node ("Set as Startup
        /// Package" for Python/CMake parity; "Set as Startup Project" reads better for the
        /// project-per-manifest languages). Null hides the verb entirely.
        /// </summary>
        public string StartupVerbLabel { get; }

        /// <summary>
        /// True when the IDE's type-scaffolding services (Add Class / Add Interface / Add
        /// Abstract Class) can actually create code for this language. Only Python
        /// (<c>ITypeService</c>) and C++ (<c>CppTypeService</c>) have scaffolding engines —
        /// showing the verbs on a language without one would route its project into the
        /// Python scaffolder.
        /// </summary>
        public bool SupportsTypeScaffolding { get; }

        /// <summary>
        /// True when symbol-level edits from the tree (Rename Symbol…, Edit Signature…, Delete
        /// on a type/function/member node) are backed by a real rename/rewrite service for this
        /// language. Same rationale as <see cref="SupportsTypeScaffolding"/>: Python and C++
        /// have rename services; the other languages get read/navigate-only symbol nodes until
        /// theirs exist.
        /// </summary>
        public bool SupportsCodeModelEdits { get; }

        /// <summary>True when the project context menu offers "Run Tests" (a per-language test runner exists).</summary>
        public bool SupportsRunTests { get; }

        /// <summary>
        /// True when "Debug Tests" is offered. Only Python's debugpy-backed pytest launch
        /// exists today; the verb previously appeared on C++ test packages too and launched
        /// pytest against C++ sources — hidden now instead of lying (SRS §2.3).
        /// </summary>
        public bool SupportsDebugTests { get; }

        /// <summary>Source-file extensions (with dot) of this language, e.g. [".py"].</summary>
        public string[] SourceFileExtensions { get; }

        /// <summary>File extensions considered when sniffing for test files in a project directory.</summary>
        public string[] TestFileExtensions { get; }

        /// <summary>File-name (sans extension) prefixes that mark a test file, e.g. "test_".</summary>
        public string[] TestFileNamePrefixes { get; }

        /// <summary>File-name (sans extension) suffixes that mark a test file, e.g. "_test", "Tests".</summary>
        public string[] TestFileNameSuffixes { get; }

        /// <summary>Exact file names that mark a test entry point, e.g. "conftest.py".</summary>
        public string[] TestFileNames { get; }

        /// <summary>
        /// Project-relative directories whose contents ARE tests by convention, regardless of
        /// file name — pytest's <c>tests/</c>, a GTest target's <c>test/</c>. A test target whose
        /// files carry no test-marked name at all would otherwise read as plain source,
        /// which is what this list prevents.
        /// </summary>
        public string[] TestDirectoryNames { get; }

        /// <summary>
        /// Per-language Solution Explorer icon set, or null to use the tree's historical
        /// (Python-asset) icon keys — Python and C++/CUDA register null so their pixels are
        /// bit-identical to the pre-connector tree.
        /// </summary>
        public ExplorerIconSet ExplorerIcons { get; }
    }

    /// <summary>
    /// The Solution Explorer icons for one language: asset file names resolved against
    /// <c>assets/product/tree</c> / <c>assets/product/app</c> (same loader as every existing tree
    /// icon), each with the language's accent color as the coloured-square fallback so a
    /// missing asset still yields a visually distinct, language-consistent tree.
    /// </summary>
    public sealed class ExplorerIconSet
    {
        public ExplorerIconSet(int accentArgb,
            string solutionIconFileName = null,
            string executableProjectIconFileName = null,
            string libraryProjectIconFileName = null,
            string unitTestProjectIconFileName = null,
            string sourceFileIconFileName = null)
        {
            AccentArgb = accentArgb;
            SolutionIconFileName = solutionIconFileName;
            ExecutableProjectIconFileName = executableProjectIconFileName;
            LibraryProjectIconFileName = libraryProjectIconFileName;
            UnitTestProjectIconFileName = unitTestProjectIconFileName;
            SourceFileIconFileName = sourceFileIconFileName;
        }

        /// <summary>Language accent color (ARGB) used for generated fallback squares.</summary>
        public int AccentArgb { get; }

        public string SolutionIconFileName { get; }
        public string ExecutableProjectIconFileName { get; }
        public string LibraryProjectIconFileName { get; }
        public string UnitTestProjectIconFileName { get; }
        public string SourceFileIconFileName { get; }
    }
}
