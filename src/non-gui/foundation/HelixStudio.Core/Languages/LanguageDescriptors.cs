using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Languages
{
    /// <summary>
    /// Registry of <see cref="LanguageWorkspaceDescriptor"/>s, keyed by
    /// <see cref="ProjectLanguage"/>. Tier-1 GUI code (ProjectTreePanel, MainForm, coordinators)
    /// asks this registry instead of writing inline language conditionals, so per-language
    /// explorer/menu/packaging rules have exactly one home.
    ///
    /// <see cref="For"/> never returns null: an unregistered language yields a conservative
    /// descriptor with every capability off, which degrades to "show nothing language-specific"
    /// rather than showing another language's verbs.
    /// </summary>
    public static class LanguageDescriptors
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<ProjectLanguage, LanguageWorkspaceDescriptor> Registered =
            new Dictionary<ProjectLanguage, LanguageWorkspaceDescriptor>();

        static LanguageDescriptors()
        {
            // Python — TOML-anchored workspace; namespace == package directory, so package
            // rename is a real operation; package manager verbs are live. The verb labels keep
            // the historical VS-parity strings ProjectTreePanel always showed.
            Register(new LanguageWorkspaceDescriptor(
                ProjectLanguage.Python,
                "Python",
                new[] { "pyproject.toml" },
                new[] { ".sln", ".slnx" },
                supportsPackageRename: true,
                supportsPackageManager: true,
                restorePackagesMenuLabel: "Restore NuGet Packages",
                managePackagesMenuLabel: "Manage NuGet Packages...",
                supportsGuiFormsDesigner: true,
                allowsMultipleBaseClasses: true,
                sourceFileExtensions: new[] { ".py" },
                // Spelled out rather than left to the constructor defaults, which hand every
                // language `.py/.cpp/.cxx/.cc` — a Python project's C++ files are not its tests.
                // conftest.py and tests/ are pytest's own conventions and carry no test_ prefix,
                // so without them a `tests/` folder full of fixtures reads as ordinary source.
                testFileExtensions: new[] { ".py" },
                testFileNamePrefixes: new[] { "test_" },
                testFileNameSuffixes: new[] { "_test" },
                testFileNames: new[] { "conftest.py" },
                testDirectoryNames: new[] { "tests", "test" }));

            // C++/CUDA — CMakeLists.txt-anchored workspace, one ProjectModel per CMake target.
            // No namespace↔directory convention (headers are addressed by include path), so no
            // package rename; no package-manager integration is wired, so the verbs are hidden
            // instead of lying. Covers plain C++ AND CUDA targets (kernel-bearing projects are a
            // per-project property, not a separate language value). supportsDebugTests is false
            // on purpose: the only debug-tests pipeline is Python's debugpy pytest launch, and
            // the verb used to appear on C++ test packages and run pytest against .cpp sources.
            Register(new LanguageWorkspaceDescriptor(
                ProjectLanguage.Cpp,
                "C++/CUDA",
                new[] { "CMakeLists.txt" },
                new string[0],
                supportsPackageRename: false,
                supportsPackageManager: false,
                restorePackagesMenuLabel: null,
                managePackagesMenuLabel: null,
                supportsGuiFormsDesigner: false,
                allowsMultipleBaseClasses: true,
                supportsDebugTests: false,
                sourceFileExtensions: new[] { ".cpp", ".cxx", ".cc", ".c", ".cu", ".cuh", ".h", ".hpp", ".hxx", ".hh" },
                testFileExtensions: new[] { ".cpp", ".cxx", ".cc", ".cu" },
                testDirectoryNames: new[] { "tests", "test" }));   // the usual GTest target layout
        }

        /// <summary>
        /// Register (or replace) the descriptor for a language. Adding a language to the IDE
        /// starts here — GUI call sites pick the new rules up without modification.
        /// </summary>
        public static void Register(LanguageWorkspaceDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            lock (Sync) Registered[descriptor.Language] = descriptor;
        }

        /// <summary>Descriptor for <paramref name="language"/>; never null (see class doc).</summary>
        public static LanguageWorkspaceDescriptor For(ProjectLanguage language)
        {
            lock (Sync)
            {
                LanguageWorkspaceDescriptor d;
                if (Registered.TryGetValue(language, out d)) return d;
            }
            return new LanguageWorkspaceDescriptor(
                language, language.ToString(),
                new string[0], new string[0],
                supportsPackageRename: false,
                supportsPackageManager: false,
                restorePackagesMenuLabel: null,
                managePackagesMenuLabel: null,
                supportsGuiFormsDesigner: false,
                allowsMultipleBaseClasses: false);
        }

        /// <summary>Snapshot of every registered descriptor.</summary>
        public static IReadOnlyList<LanguageWorkspaceDescriptor> All
        {
            get
            {
                lock (Sync) return new List<LanguageWorkspaceDescriptor>(Registered.Values);
            }
        }
    }
}
