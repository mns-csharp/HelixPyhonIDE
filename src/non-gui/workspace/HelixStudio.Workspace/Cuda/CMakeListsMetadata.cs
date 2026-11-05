using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>The kind of build target declared by an <c>add_*</c> command.</summary>
    public enum CMakeTargetKind
    {
        /// <summary><c>add_executable(&lt;name&gt; ...)</c>.</summary>
        Executable = 0,
        /// <summary><c>add_library(&lt;name&gt; STATIC ...)</c> (or the default when BUILD_SHARED_LIBS is off).</summary>
        StaticLibrary = 1,
        /// <summary><c>add_library(&lt;name&gt; SHARED ...)</c>.</summary>
        SharedLibrary = 2,
        /// <summary><c>add_library(&lt;name&gt; MODULE ...)</c>.</summary>
        ModuleLibrary = 3,
        /// <summary><c>add_library(&lt;name&gt; INTERFACE)</c> — header-only.</summary>
        InterfaceLibrary = 4,
        /// <summary><c>add_custom_target(&lt;name&gt; ...)</c>.</summary>
        Custom = 5,
    }

    /// <summary>
    /// A single build target parsed from a CMakeLists.txt. Sources are recorded as written
    /// (including <c>${VAR}</c> references); <see cref="ResolvedSources"/> holds the
    /// variable-expanded list computed by <see cref="CMakeListsReader"/>.
    /// </summary>
    public sealed class CMakeTarget
    {
        public CMakeTarget(string name, CMakeTargetKind kind)
        {
            Name = name;
            Kind = kind;
            Sources = new List<string>();
            ResolvedSources = new List<string>();
            LinkDependencies = new List<string>();
        }

        public string Name { get; }
        public CMakeTargetKind Kind { get; set; }

        /// <summary>Source arguments exactly as written, e.g. <c>${SOURCE_FILES}</c>, <c>main.cpp</c>.</summary>
        public List<string> Sources { get; }

        /// <summary>Sources with <c>${VAR}</c> references expanded against the file's <c>set()</c> variables.</summary>
        public List<string> ResolvedSources { get; }

        /// <summary>Libraries/targets this target links (from <c>target_link_libraries</c>); emitted back by the writer.</summary>
        public List<string> LinkDependencies { get; }
    }

    /// <summary>
    /// Structured view of a <c>CMakeLists.txt</c> — the CUDA/native counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Python.PyProjectMetadata"/>. Populated by
    /// <see cref="CMakeListsReader"/> and re-emitted by <see cref="CMakeListsWriter"/>.
    /// Intentionally a pragmatic subset: the fields an IDE needs to model a project
    /// (name, languages, standards, targets and their sources), not a full CMake AST.
    /// </summary>
    public sealed class CMakeListsMetadata
    {
        public CMakeListsMetadata()
        {
            Languages = new List<string>();
            Variables = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
            IncludeDirectories = new List<string>();
            Subdirectories = new List<string>();
            Targets = new List<CMakeTarget>();
        }

        /// <summary><c>cmake_minimum_required(VERSION x.y)</c>, e.g. "3.20". Null if absent.</summary>
        public string CMakeMinimumVersion { get; set; }

        /// <summary>The <c>project(&lt;name&gt; ...)</c> name. Null if absent.</summary>
        public string ProjectName { get; set; }

        /// <summary>Version from <c>project(... VERSION v)</c>. Null if absent.</summary>
        public string ProjectVersion { get; set; }

        /// <summary>Languages from <c>project(... LANGUAGES ...)</c> and/or <c>enable_language()</c>, e.g. CXX, CUDA.</summary>
        public List<string> Languages { get; }

        /// <summary><c>set(CMAKE_CXX_STANDARD n)</c>. Null if absent.</summary>
        public string CxxStandard { get; set; }

        /// <summary><c>set(CMAKE_CUDA_STANDARD n)</c>. Null if absent.</summary>
        public string CudaStandard { get; set; }

        /// <summary><c>set(CMAKE_CUDA_ARCHITECTURES ...)</c>. Null if absent.</summary>
        public string CudaArchitectures { get; set; }

        /// <summary>All <c>set()</c> variables (name → token list), e.g. SOURCE_FILES → [src/a.cpp, src/b.cpp].</summary>
        public Dictionary<string, List<string>> Variables { get; }

        /// <summary>Directories from <c>include_directories(...)</c> / <c>target_include_directories(...)</c>.</summary>
        public List<string> IncludeDirectories { get; }

        /// <summary>Directories from <c>add_subdirectory(...)</c>.</summary>
        public List<string> Subdirectories { get; }

        /// <summary>Executable / library / custom targets, in declaration order.</summary>
        public List<CMakeTarget> Targets { get; }

        /// <summary>True when CUDA is an enabled language (drives CUDA project modelling).</summary>
        public bool UsesCuda
        {
            get
            {
                for (int i = 0; i < Languages.Count; i++)
                    if (string.Equals(Languages[i], "CUDA", System.StringComparison.OrdinalIgnoreCase))
                        return true;
                // A .cu source in any target also implies CUDA.
                for (int t = 0; t < Targets.Count; t++)
                    for (int s = 0; s < Targets[t].ResolvedSources.Count; s++)
                        if (Targets[t].ResolvedSources[s].EndsWith(".cu", System.StringComparison.OrdinalIgnoreCase))
                            return true;
                return false;
            }
        }
    }
}
