using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Cuda
{
    public enum CMakeLintSeverity { Error, Warning, Info }

    /// <summary>One CMake lint finding: rule code + message + position + severity.</summary>
    public sealed class CMakeLintFinding
    {
        public int Line;
        public int Column;
        public string Code = "";     // CML### / CMP### (parser)
        public string Message = "";
        public CMakeLintSeverity Severity = CMakeLintSeverity.Warning;

        public override string ToString()
        {
            return "(" + Line + "," + Column + ") " + Severity.ToString().ToLowerInvariant() + " " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// FR-LINT-04: a native CMake linter built on the <see cref="CMakeParser"/> AST — the CMake counterpart
    /// to <c>PythonLinter</c>. Native-first (no dependency on <c>cmake-lint</c>). Rules: parser errors
    /// (unbalanced/mismatched/malformed, folded in as errors), unknown command, deprecated command,
    /// insufficient arguments for common commands, unused <c>set()</c> variable, and discouraged
    /// <c>file(GLOB)</c> source globbing.
    /// </summary>
    public static class CMakeLinter
    {
        public static List<CMakeLintFinding> Lint(string source)
        {
            return Lint(CMakeParser.Parse(source ?? string.Empty));
        }

        public static List<CMakeLintFinding> Lint(CMakeFile file)
        {
            var findings = new List<CMakeLintFinding>();
            if (file == null) return findings;

            // CMP*** — surface the parser's structural diagnostics as lint errors.
            foreach (CMakeParseDiagnostic d in file.Diagnostics)
                findings.Add(new CMakeLintFinding
                {
                    Line = d.Line, Column = d.Column, Code = d.Code, Message = d.Message,
                    Severity = d.IsError ? CMakeLintSeverity.Error : CMakeLintSeverity.Warning,
                });

            // Names of functions/macros defined in-file are legitimate commands (not "unknown").
            var userDefined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CMakeCommand c in file.Commands)
                if ((Eq(c.Name, "function") || Eq(c.Name, "macro")) && c.Arguments.Count >= 1)
                    userDefined.Add(c.Arguments[0].Text);

            // All ${VAR} references anywhere (to detect unused set() variables).
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (CMakeCommand c in file.Commands)
                foreach (CMakeArgument a in c.Arguments)
                    foreach (CMakeReference r in a.References)
                        if (!r.IsGeneratorExpression && r.Scope == "" && !string.IsNullOrEmpty(r.Name))
                            referenced.Add(r.Name);

            // set()/option() variable definitions (name → the defining command) for the unused check.
            var defined = new List<CMakeCommand>();

            foreach (CMakeCommand c in file.Commands)
            {
                if (c.IsBlockClose || c.IsBlockMiddle) continue; // end*/else/elseif carry no target semantics

                // CML002 — unknown command.
                if (!KnownCommands.Contains(c.Name) && !userDefined.Contains(c.Name)
                    && !c.IsBlockOpen && !Deprecated.ContainsKey(c.Name))
                    Add(findings, c.Line, c.Column, "CML002", CMakeLintSeverity.Warning,
                        "Unknown command '" + c.Name + "' (typo, or a module not include()d?)");

                // CML003 — deprecated command.
                string replacement;
                if (Deprecated.TryGetValue(c.Name, out replacement))
                    Add(findings, c.Line, c.Column, "CML003", CMakeLintSeverity.Warning,
                        "Deprecated command '" + c.Name + "'" + (replacement.Length > 0 ? " — use " + replacement : ""));

                // CML004 — too few arguments for common commands.
                int min;
                if (MinArgs.TryGetValue(c.Name, out min) && c.Arguments.Count < min)
                    Add(findings, c.Line, c.Column, "CML004", CMakeLintSeverity.Warning,
                        c.Name + "() expects at least " + min + " argument(s), got " + c.Arguments.Count);

                // CML006 — file(GLOB ...) for sources is discouraged (globs don't re-run on new files).
                if (Eq(c.Name, "file") && c.Arguments.Count >= 1
                    && (Eq(c.Arguments[0].Text, "GLOB") || Eq(c.Arguments[0].Text, "GLOB_RECURSE")))
                    Add(findings, c.Line, c.Column, "CML006", CMakeLintSeverity.Info,
                        "file(" + c.Arguments[0].Text + ") for collecting sources is discouraged — list sources explicitly so new files trigger a re-configure");

                if (Eq(c.Name, "set") || Eq(c.Name, "option")) defined.Add(c);
            }

            // CML005 — a set()/option() variable never referenced via ${...} in this file.
            foreach (CMakeCommand c in defined)
            {
                if (c.Arguments.Count < 1) continue;
                string name = c.Arguments[0].Text;
                if (string.IsNullOrEmpty(name)) continue;
                if (IsCacheOrScope(c)) continue;                       // CACHE/option/PARENT_SCOPE are intentional public knobs
                if (name.StartsWith("CMAKE_", StringComparison.Ordinal)) continue; // read by CMake itself
                if (referenced.Contains(name)) continue;
                Add(findings, c.Line, c.Column, "CML005", CMakeLintSeverity.Warning,
                    "Variable '" + name + "' is set but never referenced (${" + name + "}) in this file");
            }

            findings.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return findings;
        }

        private static bool IsCacheOrScope(CMakeCommand c)
        {
            if (Eq(c.Name, "option")) return true;
            foreach (CMakeArgument a in c.Arguments)
                if (Eq(a.Text, "CACHE") || Eq(a.Text, "PARENT_SCOPE")) return true;
            return false;
        }

        private static void Add(List<CMakeLintFinding> list, int line, int col, string code, CMakeLintSeverity sev, string msg)
        {
            list.Add(new CMakeLintFinding { Line = line, Column = col, Code = code, Severity = sev, Message = msg });
        }

        private static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // Commands whose common form needs at least N arguments.
        private static readonly Dictionary<string, int> MinArgs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = 1, ["add_executable"] = 2, ["add_library"] = 2, ["add_subdirectory"] = 1,
            ["target_link_libraries"] = 2, ["target_include_directories"] = 2, ["target_compile_definitions"] = 2,
            ["target_compile_options"] = 2, ["target_sources"] = 2, ["set"] = 1, ["find_package"] = 1,
            ["cmake_minimum_required"] = 2, ["install"] = 1, ["add_test"] = 2,
        };

        // Real CMake commands removed/deprecated → their modern replacement.
        private static readonly Dictionary<string, string> Deprecated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["exec_program"] = "execute_process()",
            ["make_directory"] = "file(MAKE_DIRECTORY)",
            ["remove"] = "list(REMOVE_ITEM) or file(REMOVE)",
            ["write_file"] = "file(WRITE)",
            ["install_targets"] = "install(TARGETS)",
            ["install_files"] = "install(FILES)",
            ["install_programs"] = "install(PROGRAMS)",
            ["subdir_depends"] = "",
            ["output_required_files"] = "",
            ["use_mangled_mesa"] = "",
            ["variable_requires"] = "if()",
            ["build_name"] = "",
            ["utility_source"] = "",
            ["load_command"] = "",
        };

        private static readonly HashSet<string> KnownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // scripting / control (block keywords handled separately)
            "set","unset","list","string","math","option","include","include_guard","return","message","cmake_policy",
            "cmake_minimum_required","cmake_parse_arguments","cmake_language","set_property","get_property",
            "get_cmake_property","get_directory_property","get_filename_component","cmake_path","separate_arguments",
            "mark_as_advanced","find_file","find_path","find_library","find_program","find_package","site_name",
            // project / targets
            "project","add_executable","add_library","add_custom_command","add_custom_target","add_dependencies",
            "add_subdirectory","add_test","enable_testing","enable_language","set_target_properties","get_target_property",
            "set_tests_properties","set_directory_properties","set_source_files_properties",
            "target_link_libraries","target_include_directories","target_compile_definitions","target_compile_options",
            "target_compile_features","target_sources","target_link_directories","target_link_options","target_precompile_headers",
            "include_directories","link_directories","link_libraries","add_compile_options","add_compile_definitions",
            "add_definitions","remove_definitions","add_link_options",
            // files / install / config
            "file","configure_file","install","export","include_external_msproject","source_group","aux_source_directory",
            "create_test_sourcelist","define_property","fltk_wrap_ui","qt_wrap_cpp","qt_wrap_ui",
            "ctest_start","ctest_configure","ctest_build","ctest_test","ctest_submit","ctest_coverage","ctest_memcheck",
            "execute_process","try_compile","try_run","load_cache","build_command","fetchcontent_declare","fetchcontent_makeavailable",
            "fetchcontent_getproperties","fetchcontent_populate","find_dependency","write_basic_package_version_file",
            "configure_package_config_file","pkg_check_modules","pkg_search_module","check_include_file","check_function_exists",
            "check_symbol_exists","check_cxx_source_compiles","check_cxx_compiler_flag","doxygen_add_docs","cmake_host_system_information",
        };
    }
}
