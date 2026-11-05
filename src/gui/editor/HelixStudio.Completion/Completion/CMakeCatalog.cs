using System.Collections.Generic;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// FR-KB-07: documentation catalog for CMake commands and standard variables — the CMake counterpart
    /// to <see cref="CudaCatalog"/>. Feeds <see cref="CompletionItem.Documentation"/> so completion and
    /// Quick Info show a real purpose + key-argument description rather than a bare signature.
    /// </summary>
    public static class CMakeCatalog
    {
        public static string GetDoc(string name)
        {
            return string.IsNullOrEmpty(name) ? null : (Docs.TryGetValue(name, out string d) ? d : null);
        }

        public static bool Has(string name) => !string.IsNullOrEmpty(name) && Docs.ContainsKey(name);

        private static readonly Dictionary<string, string> Docs = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["cmake_minimum_required"] =
                "Declares the minimum CMake version a project requires.\n\n" +
                "VERSION <major.minor[.patch]>  - the lowest supported CMake version.",
            ["project"] =
                "Names the project and (optionally) its version and enabled languages.\n\n" +
                "  <name>              - project name (sets PROJECT_NAME)\n" +
                "  VERSION <v>         - project version (sets PROJECT_VERSION)\n" +
                "  LANGUAGES <langs>   - e.g. CXX CUDA C. Enables the toolchains for those languages.",
            ["add_executable"] =
                "Adds an executable target built from the given source files.\n\n" +
                "  <name>       - target name\n" +
                "  <source>...  - source files (.cu/.cpp/.c) compiled into the executable.",
            ["add_library"] =
                "Adds a library target (STATIC, SHARED, or MODULE) from the given sources.\n\n" +
                "  <name>                 - target name\n" +
                "  STATIC|SHARED|MODULE   - library kind (default depends on BUILD_SHARED_LIBS)\n" +
                "  <source>...            - source files.",
            ["add_subdirectory"] =
                "Adds a subdirectory containing its own CMakeLists.txt to the build.",
            ["target_link_libraries"] =
                "Links libraries (and propagates their usage requirements) to a target.\n\n" +
                "  <target>                   - the consuming target\n" +
                "  PUBLIC|PRIVATE|INTERFACE   - visibility scope of each dependency:\n" +
                "     PRIVATE   - used only to build <target>\n" +
                "     INTERFACE - imposed only on consumers of <target>\n" +
                "     PUBLIC    - both\n" +
                "  <lib>...                   - libraries / targets to link.",
            ["target_include_directories"] =
                "Adds include directories to a target with PUBLIC/PRIVATE/INTERFACE scope\n" +
                "(same propagation semantics as target_link_libraries).",
            ["target_compile_definitions"] =
                "Adds -D preprocessor definitions to a target with PUBLIC/PRIVATE/INTERFACE scope.",
            ["target_compile_options"] =
                "Adds compiler flags to a target with PUBLIC/PRIVATE/INTERFACE scope.",
            ["target_compile_features"] =
                "Requests compiler features (e.g. cxx_std_17) for a target with scope.",
            ["target_sources"] =
                "Adds source files to an already-declared target, with PUBLIC/PRIVATE/INTERFACE scope.",
            ["set"] =
                "Sets a normal, cache, or environment variable.\n\n" +
                "  set(<var> <value>...)                     - normal variable\n" +
                "  set(<var> <value> CACHE <type> <docstr>)  - cache variable.",
            ["option"] =
                "Defines a boolean cache option shown in the CMake GUI.\n\n" +
                "  option(<var> \"<help text>\" [ON|OFF])   - default is OFF.",
            ["find_package"] =
                "Locates and loads an external package's settings.\n\n" +
                "  <name>        - package name\n" +
                "  [version]     - minimum version\n" +
                "  REQUIRED      - fail the configure if not found\n" +
                "  COMPONENTS .. - specific components to require.",
            ["include"] =
                "Loads and runs CMake code from a file or module.",
            ["enable_language"] =
                "Enables a language's toolchain for the project (e.g. enable_language(CUDA)).",
            ["install"] =
                "Specifies install rules (targets, files, directories) for `cmake --install`.",
            ["file"] =
                "Filesystem / content command: GLOB, READ, WRITE, COPY, MAKE_DIRECTORY, etc.\n" +
                "Note: GLOB is discouraged for sources (globs don't re-run on new files).",
            ["configure_file"] =
                "Copies <input> to <output>, substituting @VAR@ / ${VAR} references.\n" +
                "  [@ONLY]  - only substitute @VAR@ form.",
            ["message"] =
                "Prints a diagnostic message.\n\n" +
                "  [<mode>]  - STATUS, WARNING, AUTHOR_WARNING, SEND_ERROR, FATAL_ERROR, DEBUG.",
            ["add_compile_options"] =
                "Adds compiler flags to all targets in the current directory and below.",
            ["set_target_properties"] =
                "Sets properties (e.g. CUDA_ARCHITECTURES, CXX_STANDARD, OUTPUT_NAME) on targets.",
            // A few standard variables.
            ["CMAKE_CXX_STANDARD"] =
                "The C++ standard to compile with for targets (e.g. 11, 14, 17, 20).",
            ["CMAKE_CUDA_ARCHITECTURES"] =
                "The CUDA architectures to generate code for (e.g. 75;86;89, or 'native').",
            ["CMAKE_SOURCE_DIR"] =
                "The top-level source directory (where the top CMakeLists.txt lives).",
            ["CMAKE_BINARY_DIR"] =
                "The top-level build (binary) directory.",
            ["PROJECT_NAME"] =
                "The name given to the most recent project() call.",
        };
    }
}
