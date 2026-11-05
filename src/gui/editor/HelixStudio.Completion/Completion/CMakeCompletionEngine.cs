using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// CMake completion for <c>CMakeLists.txt</c> / <c>*.cmake</c> (FR-IC-05) — the CMake counterpart to
    /// <see cref="PythonCompletionEngine"/>/<see cref="CudaCompletionEngine"/>. Offers CMake commands
    /// (with a one-line signature), standard <c>CMAKE_*</c>/<c>PROJECT_*</c> variables, and — when the
    /// caret sits inside a <c>target_*</c> command's argument list — the visibility keywords
    /// <c>PUBLIC</c>/<c>PRIVATE</c>/<c>INTERFACE</c>.
    /// </summary>
    public sealed class CMakeCompletionEngine : ICompletionEngine
    {
        // command → one-line signature
        private static readonly (string cmd, string sig)[] Commands =
        {
            ("cmake_minimum_required", "cmake_minimum_required(VERSION <major.minor>)"),
            ("project", "project(<name> [VERSION <v>] [LANGUAGES <langs>])"),
            ("add_executable", "add_executable(<name> <source>...)"),
            ("add_library", "add_library(<name> [STATIC|SHARED|MODULE] <source>...)"),
            ("add_subdirectory", "add_subdirectory(<dir>)"),
            ("add_custom_command", "add_custom_command(...)"),
            ("add_custom_target", "add_custom_target(<name> ...)"),
            ("target_link_libraries", "target_link_libraries(<target> <PUBLIC|PRIVATE|INTERFACE> <lib>...)"),
            ("target_include_directories", "target_include_directories(<target> <PUBLIC|PRIVATE|INTERFACE> <dir>...)"),
            ("target_compile_definitions", "target_compile_definitions(<target> <PUBLIC|PRIVATE|INTERFACE> <def>...)"),
            ("target_compile_options", "target_compile_options(<target> <PUBLIC|PRIVATE|INTERFACE> <opt>...)"),
            ("target_compile_features", "target_compile_features(<target> <PUBLIC|PRIVATE|INTERFACE> <feature>...)"),
            ("target_sources", "target_sources(<target> <PUBLIC|PRIVATE|INTERFACE> <source>...)"),
            ("set", "set(<variable> <value>...)"),
            ("unset", "unset(<variable>)"),
            ("set_target_properties", "set_target_properties(<target>... PROPERTIES <prop> <value>...)"),
            ("set_property", "set_property(<scope> <name> PROPERTY <prop> <value>...)"),
            ("get_property", "get_property(<var> <scope> <name> PROPERTY <prop>)"),
            ("option", "option(<variable> \"<help>\" [ON|OFF])"),
            ("find_package", "find_package(<name> [version] [REQUIRED] [COMPONENTS <c>...])"),
            ("find_library", "find_library(<var> <name> [PATHS <p>...])"),
            ("include", "include(<module|file>)"),
            ("include_directories", "include_directories(<dir>...)"),
            ("enable_language", "enable_language(<CXX|CUDA|C>...)"),
            ("enable_testing", "enable_testing()"),
            ("add_test", "add_test(NAME <name> COMMAND <cmd>...)"),
            ("install", "install(TARGETS <target>... DESTINATION <dir>)"),
            ("file", "file(<GLOB|READ|WRITE|...> <args>...)"),
            ("configure_file", "configure_file(<input> <output> [@ONLY])"),
            ("message", "message([<mode>] \"<text>\"...)"),
            ("if", "if(<condition>)"),
            ("elseif", "elseif(<condition>)"),
            ("else", "else()"),
            ("endif", "endif()"),
            ("foreach", "foreach(<var> <items>...)"),
            ("endforeach", "endforeach()"),
            ("while", "while(<condition>)"),
            ("endwhile", "endwhile()"),
            ("function", "function(<name> [<arg>...])"),
            ("endfunction", "endfunction()"),
            ("macro", "macro(<name> [<arg>...])"),
            ("endmacro", "endmacro()"),
            ("list", "list(<APPEND|REMOVE_ITEM|LENGTH|...> <list> <args>...)"),
            ("string", "string(<SUBCOMMAND> <args>...)"),
            ("add_compile_options", "add_compile_options(<option>...)"),
            ("add_definitions", "add_definitions(-D<define>...)"),
        };

        private static readonly string[] Variables =
        {
            "CMAKE_SOURCE_DIR", "CMAKE_BINARY_DIR", "CMAKE_CURRENT_SOURCE_DIR", "CMAKE_CURRENT_BINARY_DIR",
            "CMAKE_CURRENT_LIST_DIR", "CMAKE_MODULE_PATH", "CMAKE_PREFIX_PATH", "CMAKE_INSTALL_PREFIX",
            "CMAKE_BUILD_TYPE", "CMAKE_CXX_STANDARD", "CMAKE_CXX_STANDARD_REQUIRED", "CMAKE_CXX_FLAGS",
            "CMAKE_CUDA_STANDARD", "CMAKE_CUDA_ARCHITECTURES", "CMAKE_CUDA_FLAGS", "CMAKE_CUDA_COMPILER",
            "CMAKE_RUNTIME_OUTPUT_DIRECTORY", "CMAKE_LIBRARY_OUTPUT_DIRECTORY", "CMAKE_ARCHIVE_OUTPUT_DIRECTORY",
            "PROJECT_NAME", "PROJECT_SOURCE_DIR", "PROJECT_BINARY_DIR", "PROJECT_VERSION",
        };

        private static readonly string[] VisibilityKeywords = { "PUBLIC", "PRIVATE", "INTERFACE" };

        public CompletionContext Classify(string source, int caretOffset)
        {
            var ctx = new CompletionContext
            {
                Kind = CompletionContextKind.General,
                Prefix = string.Empty,
                MemberBase = string.Empty,
                PrefixStart = caretOffset,
            };
            if (source == null) return ctx;
            int i = Math.Max(0, Math.Min(caretOffset, source.Length));
            int s = i;
            while (s > 0 && IsIdChar(source[s - 1])) s--;
            ctx.Prefix = source.Substring(s, i - s);
            ctx.PrefixStart = s;
            return ctx;
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution)
        {
            return GetCompletions(source, caretOffset, filePath, solution, Classify(source, caretOffset));
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution, CompletionContext ctx)
        {
            var items = new List<CompletionItem>();

            // Inside a target_* command's argument list, lead with the visibility keywords.
            string enclosing = EnclosingCommand(source, ctx.PrefixStart);
            if (enclosing != null && enclosing.StartsWith("target_", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string k in VisibilityKeywords)
                    items.Add(new CompletionItem(k, CompletionKind.Keyword, null, "scope keyword " + k));
            }

            // FR-IC-06: project-defined targets and variables parsed from the current buffer come first
            // when relevant (targets lead inside a target_* command's argument list).
            AddProjectSymbols(items, source, enclosing);

            foreach (var c in Commands)
                items.Add(new CompletionItem(c.cmd, CompletionKind.Function, null, c.sig, CMakeCatalog.GetDoc(c.cmd)));
            foreach (string v in Variables)
                items.Add(new CompletionItem(v, CompletionKind.Variable, null, "CMake variable ${" + v + "}", CMakeCatalog.GetDoc(v)));

            return items;
        }

        // FR-PM-05: feed completion from the SAME authoritative parser the project model uses
        // (<see cref="CMakeListsReader"/>) rather than a naive line regex. This picks up multi-line
        // target/source lists and variable-expanded sources robustly, and carries the target Kind and a
        // variable-value preview into the completion detail. Targets lead inside a target_* command.
        private static void AddProjectSymbols(List<CompletionItem> items, string source, string enclosing)
        {
            if (string.IsNullOrEmpty(source)) return;
            CMakeListsMetadata meta;
            try { meta = CMakeListsReader.Parse(source); }
            catch { return; }

            bool inTargetCmd = enclosing != null && enclosing.StartsWith("target_", StringComparison.OrdinalIgnoreCase);

            // Inside a target_* command, a project target is the most likely argument → offer first.
            if (inTargetCmd) AddTargetItems(items, meta);

            foreach (var kv in meta.Variables)
            {
                string preview = kv.Value != null && kv.Value.Count > 0
                    ? "  = " + string.Join(" ", kv.Value.ToArray()) : string.Empty;
                items.Add(new CompletionItem(kv.Key, CompletionKind.Variable, null,
                    "variable " + kv.Key + " (this project)  -> ${" + kv.Key + "}" + preview));
            }

            // Outside a target command, still offer targets (e.g. as a set_target_properties argument).
            if (!inTargetCmd) AddTargetItems(items, meta);
        }

        private static void AddTargetItems(List<CompletionItem> items, CMakeListsMetadata meta)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CMakeTarget t in meta.Targets)
            {
                if (string.IsNullOrEmpty(t.Name) || !seen.Add(t.Name)) continue;
                items.Add(new CompletionItem(t.Name, CompletionKind.Class, null,
                    "target " + t.Name + " [" + t.Kind + "] (this project)"));
            }
        }

        // Name of the command whose '(' most recently opened and hasn't closed before `pos`, or null.
        private static string EnclosingCommand(string source, int pos)
        {
            if (source == null) return null;
            int depth = 0;
            int p = Math.Min(pos, source.Length) - 1;
            for (; p >= 0; p--)
            {
                char c = source[p];
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth == 0)
                    {
                        // read the identifier immediately before '('
                        int e = p;
                        while (e > 0 && (source[e - 1] == ' ' || source[e - 1] == '\t')) e--;
                        int b = e;
                        while (b > 0 && IsIdChar(source[b - 1])) b--;
                        return e > b ? source.Substring(b, e - b) : null;
                    }
                    depth--;
                }
                else if (c == '\n' && depth == 0)
                {
                    // stay within the current statement's paren scope; a newline at depth 0 means no open call
                }
            }
            return null;
        }

        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
