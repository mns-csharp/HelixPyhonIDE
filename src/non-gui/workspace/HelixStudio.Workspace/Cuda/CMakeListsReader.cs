using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// One parsed CMake command invocation: the command name plus its whitespace-split
    /// argument tokens (quotes removed, <c>${...}</c> left intact).
    /// </summary>
    internal sealed class CMakeRawCommand
    {
        public string Name;
        public List<string> Args = new List<string>();
    }

    /// <summary>
    /// A pragmatic <c>CMakeLists.txt</c> reader — the CUDA/native counterpart to
    /// <see cref="HelixPythonNamespace.Workspace.Python.PyProjectReader"/>. Tokenizes the file into
    /// command invocations (handling <c>#</c> comments and paren scopes that span lines),
    /// then extracts the subset an IDE models into a <see cref="CMakeListsMetadata"/>.
    /// Not a full CMake evaluator: variable expansion is a single, non-recursive pass over
    /// the <c>set()</c> table, which covers the overwhelmingly common source-list idiom.
    /// </summary>
    public static class CMakeListsReader
    {
        public static CMakeListsMetadata Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException(path ?? "(null)");
            return Parse(File.ReadAllText(path));
        }

        public static CMakeListsMetadata Parse(string text)
        {
            var meta = new CMakeListsMetadata();
            if (string.IsNullOrEmpty(text)) return meta;

            // Pass 0: pull out function(name p1 p2...) ... endfunction() definitions — common for a
            // "one target per item" idiom (e.g. a small helper that add_executable()s + wires up
            // link libraries per named kernel, called once per item instead of a hand-written
            // add_executable per target). Function bodies are removed from the flat command list so
            // they aren't ALSO misread as literal top-level calls; call-sites are expanded in pass 2
            // below with the call's positional arguments bound as local (function-scoped, matching
            // real CMake — a plain function() does not leak set() into the parent scope) variables.
            var allCommands = Tokenize(text);
            var functions = new Dictionary<string, CMakeFunctionDef>(StringComparer.OrdinalIgnoreCase);
            var commands = ExtractFunctionDefinitions(allCommands, functions);

            // Pass 1: collect variables so later source lists can be expanded.
            foreach (var cmd in commands)
            {
                if (Eq(cmd.Name, "set") && cmd.Args.Count >= 1)
                {
                    string var = cmd.Args[0];
                    var vals = new List<string>();
                    for (int i = 1; i < cmd.Args.Count; i++)
                    {
                        // Stop at CACHE/PARENT_SCOPE modifiers — they are not values.
                        if (Eq(cmd.Args[i], "CACHE") || Eq(cmd.Args[i], "PARENT_SCOPE")) break;
                        vals.Add(cmd.Args[i]);
                    }
                    meta.Variables[var] = vals;

                    if (Eq(var, "CMAKE_CXX_STANDARD") && vals.Count > 0) meta.CxxStandard = vals[0];
                    else if (Eq(var, "CMAKE_CUDA_STANDARD") && vals.Count > 0) meta.CudaStandard = vals[0];
                    else if (Eq(var, "CMAKE_CUDA_ARCHITECTURES") && vals.Count > 0)
                        meta.CudaArchitectures = string.Join(";", vals);
                }
            }

            // Pass 2: the rest of the model. A command whose name matches a registered function is a
            // CALL, not a literal command — expand the function's body (positional params bound to
            // the call's arguments, ${...} substituted, its own set()s kept function-local) into
            // real commands and dispatch each of those instead of the call itself.
            foreach (var cmd in commands)
            {
                CMakeFunctionDef fn;
                if (functions.TryGetValue(cmd.Name, out fn))
                {
                    foreach (var expanded in ExpandFunctionCall(fn, cmd.Args))
                        DispatchCommand(meta, expanded);
                    continue;
                }
                DispatchCommand(meta, cmd);
            }

            return meta;
        }

        // One function(name p1 p2 ...) ... endfunction() definition.
        private sealed class CMakeFunctionDef
        {
            public List<string> Parameters = new List<string>();
            public List<CMakeRawCommand> Body = new List<CMakeRawCommand>();
        }

        // Scans the flat command list for function(...)/endfunction() pairs, registers each by name
        // in `functions`, and returns the remaining commands with those spans removed (so the body
        // is never ALSO read as literal top-level calls).
        private static List<CMakeRawCommand> ExtractFunctionDefinitions(
            List<CMakeRawCommand> allCommands, Dictionary<string, CMakeFunctionDef> functions)
        {
            var top = new List<CMakeRawCommand>();
            int i = 0;
            while (i < allCommands.Count)
            {
                CMakeRawCommand cmd = allCommands[i];
                if ((Eq(cmd.Name, "function") || Eq(cmd.Name, "macro")) && cmd.Args.Count >= 1)
                {
                    string fname = cmd.Args[0];
                    string endName = Eq(cmd.Name, "function") ? "endfunction" : "endmacro";
                    var def = new CMakeFunctionDef();
                    for (int p = 1; p < cmd.Args.Count; p++) def.Parameters.Add(cmd.Args[p]);
                    i++;
                    while (i < allCommands.Count && !Eq(allCommands[i].Name, endName))
                    {
                        def.Body.Add(allCommands[i]);
                        i++;
                    }
                    if (i < allCommands.Count) i++; // past endfunction()/endmacro()
                    functions[fname] = def;
                    continue;
                }
                top.Add(cmd);
                i++;
            }
            return top;
        }

        // Binds `fn`'s parameters positionally to `callArgs`, then replays the body: each command's
        // arguments get ${...} substituted from the CURRENT local scope (params plus any set()s seen
        // so far in the body — CMake resolves top-to-bottom, so a set() after a use doesn't apply to
        // earlier uses, matching real semantics), and a set(VAR value) command updates the local
        // scope instead of being dispatched as a real command (function-local, not global — a plain
        // function() does not leak into the parent/global scope in real CMake).
        private static List<CMakeRawCommand> ExpandFunctionCall(CMakeFunctionDef fn, List<string> callArgs)
        {
            var scope = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int p = 0; p < fn.Parameters.Count && p < callArgs.Count; p++)
                scope[fn.Parameters[p]] = callArgs[p];

            var expanded = new List<CMakeRawCommand>();
            foreach (CMakeRawCommand bodyCmd in fn.Body)
            {
                var subArgs = new List<string>();
                foreach (string a in bodyCmd.Args) subArgs.Add(Substitute(a, scope));

                if (Eq(bodyCmd.Name, "set") && subArgs.Count >= 2)
                {
                    scope[subArgs[0]] = subArgs[1];
                    continue;
                }
                expanded.Add(new CMakeRawCommand { Name = bodyCmd.Name, Args = subArgs });
            }
            return expanded;
        }

        // General ${VAR} substitution ANYWHERE within a string (e.g. "debug_${kernel_name}" or
        // "apps/main_${kernel_name}.cpp") — unlike ExpandInto below (which only handles a whole
        // argument that IS exactly "${VAR}", the source-list idiom), a function body routinely
        // interpolates a variable into the middle of a larger literal. Unknown variables are left as
        // literal "${...}" text rather than silently dropped, so a real gap in this reader shows up
        // as a visibly-wrong name instead of vanishing quietly.
        private static string Substitute(string s, Dictionary<string, string> scope)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("${", StringComparison.Ordinal) < 0) return s;
            var sb = new StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '$' && i + 1 < s.Length && s[i + 1] == '{')
                {
                    int close = s.IndexOf('}', i + 2);
                    if (close < 0) { sb.Append(s[i]); i++; continue; }
                    string varName = s.Substring(i + 2, close - i - 2);
                    string val;
                    sb.Append(scope != null && scope.TryGetValue(varName, out val) ? val : s.Substring(i, close - i + 1));
                    i = close + 1;
                }
                else { sb.Append(s[i]); i++; }
            }
            return sb.ToString();
        }

        private static void DispatchCommand(CMakeListsMetadata meta, CMakeRawCommand cmd)
        {
            {
                if (Eq(cmd.Name, "cmake_minimum_required"))
                {
                    int vi = IndexOf(cmd.Args, "VERSION");
                    if (vi >= 0 && vi + 1 < cmd.Args.Count) meta.CMakeMinimumVersion = cmd.Args[vi + 1];
                }
                else if (Eq(cmd.Name, "project") && cmd.Args.Count >= 1)
                {
                    meta.ProjectName = cmd.Args[0];
                    int lv = IndexOf(cmd.Args, "VERSION");
                    if (lv >= 0 && lv + 1 < cmd.Args.Count) meta.ProjectVersion = cmd.Args[lv + 1];
                    int li = IndexOf(cmd.Args, "LANGUAGES");
                    if (li >= 0)
                        for (int i = li + 1; i < cmd.Args.Count; i++) AddLang(meta, cmd.Args[i]);
                }
                else if (Eq(cmd.Name, "enable_language"))
                {
                    for (int i = 0; i < cmd.Args.Count; i++) AddLang(meta, cmd.Args[i]);
                }
                else if (Eq(cmd.Name, "include_directories") || Eq(cmd.Name, "target_include_directories"))
                {
                    int start = Eq(cmd.Name, "target_include_directories") ? 1 : 0;
                    for (int i = start; i < cmd.Args.Count; i++)
                    {
                        string a = cmd.Args[i];
                        if (Eq(a, "PUBLIC") || Eq(a, "PRIVATE") || Eq(a, "INTERFACE") || Eq(a, "SYSTEM") || Eq(a, "BEFORE"))
                            continue;
                        if (!meta.IncludeDirectories.Contains(a)) meta.IncludeDirectories.Add(a);
                    }
                }
                else if (Eq(cmd.Name, "add_subdirectory") && cmd.Args.Count >= 1)
                {
                    if (!meta.Subdirectories.Contains(cmd.Args[0])) meta.Subdirectories.Add(cmd.Args[0]);
                }
                else if (Eq(cmd.Name, "add_executable") && cmd.Args.Count >= 1)
                {
                    AddTarget(meta, cmd.Args[0], CMakeTargetKind.Executable, cmd.Args, 1);
                }
                else if (Eq(cmd.Name, "add_library") && cmd.Args.Count >= 1)
                {
                    int srcStart = 1;
                    CMakeTargetKind kind = CMakeTargetKind.StaticLibrary;
                    if (cmd.Args.Count >= 2)
                    {
                        string k = cmd.Args[1].ToUpperInvariant();
                        if (k == "STATIC") { kind = CMakeTargetKind.StaticLibrary; srcStart = 2; }
                        else if (k == "SHARED") { kind = CMakeTargetKind.SharedLibrary; srcStart = 2; }
                        else if (k == "MODULE") { kind = CMakeTargetKind.ModuleLibrary; srcStart = 2; }
                        else if (k == "INTERFACE") { kind = CMakeTargetKind.InterfaceLibrary; srcStart = 2; }
                    }
                    AddTarget(meta, cmd.Args[0], kind, cmd.Args, srcStart);
                }
                else if (Eq(cmd.Name, "add_custom_target") && cmd.Args.Count >= 1)
                {
                    AddTarget(meta, cmd.Args[0], CMakeTargetKind.Custom, cmd.Args, cmd.Args.Count);
                }
                else if (Eq(cmd.Name, "target_link_libraries") && cmd.Args.Count >= 2)
                {
                    CMakeTarget target = FindTarget(meta, cmd.Args[0]);
                    if (target != null)
                        for (int i = 1; i < cmd.Args.Count; i++)
                        {
                            string a = cmd.Args[i];
                            if (Eq(a, "PUBLIC") || Eq(a, "PRIVATE") || Eq(a, "INTERFACE")) continue;
                            if (!target.LinkDependencies.Contains(a)) target.LinkDependencies.Add(a);
                        }
                }
            }
        }

        private static CMakeTarget FindTarget(CMakeListsMetadata meta, string name)
        {
            for (int i = 0; i < meta.Targets.Count; i++)
                if (Eq(meta.Targets[i].Name, name)) return meta.Targets[i];
            return null;
        }

        private static void AddTarget(CMakeListsMetadata meta, string name, CMakeTargetKind kind,
            List<string> args, int srcStart)
        {
            var t = new CMakeTarget(name, kind);
            for (int i = srcStart; i < args.Count; i++)
            {
                t.Sources.Add(args[i]);
                ExpandInto(meta, args[i], t.ResolvedSources);
            }
            meta.Targets.Add(t);
        }

        // Expand a single argument: if it's exactly ${VAR}, splice VAR's tokens (one level); else keep as-is.
        private static void ExpandInto(CMakeListsMetadata meta, string arg, List<string> into)
        {
            if (arg != null && arg.Length > 3 && arg[0] == '$' && arg[1] == '{' && arg[arg.Length - 1] == '}'
                && arg.IndexOf('}') == arg.Length - 1)
            {
                string var = arg.Substring(2, arg.Length - 3);
                if (meta.Variables.TryGetValue(var, out var vals))
                {
                    for (int i = 0; i < vals.Count; i++) into.Add(vals[i]);
                    return;
                }
            }
            into.Add(arg);
        }

        private static void AddLang(CMakeListsMetadata meta, string lang)
        {
            if (string.IsNullOrEmpty(lang)) return;
            for (int i = 0; i < meta.Languages.Count; i++)
                if (Eq(meta.Languages[i], lang)) return;
            meta.Languages.Add(lang);
        }

        private static int IndexOf(List<string> args, string keyword)
        {
            for (int i = 0; i < args.Count; i++) if (Eq(args[i], keyword)) return i;
            return -1;
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Split the file into command invocations. Recognizes <c>#</c> line comments (outside quotes),
        /// bracket-comments are not handled (rare), and gathers arguments across newlines until the
        /// matching close paren.
        /// </summary>
        private static List<CMakeRawCommand> Tokenize(string text)
        {
            var result = new List<CMakeRawCommand>();
            int n = text.Length;
            int i = 0;
            while (i < n)
            {
                char c = text[i];
                if (c == '#') { while (i < n && text[i] != '\n') i++; continue; }
                if (!IsIdStart(c)) { i++; continue; }

                int idStart = i;
                while (i < n && IsIdChar(text[i])) i++;
                string name = text.Substring(idStart, i - idStart);

                // Skip whitespace between the command name and '('.
                int j = i;
                while (j < n && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r' || text[j] == '\n')) j++;
                if (j >= n || text[j] != '(') { i = idStart + name.Length; continue; } // not a command call

                // Collect the argument text up to the matching ')'.
                j++; // past '('
                int depth = 1;
                var sb = new StringBuilder();
                bool inQuote = false;
                while (j < n && depth > 0)
                {
                    char d = text[j];
                    if (inQuote)
                    {
                        if (d == '"' && text[j - 1] != '\\') inQuote = false;
                        sb.Append(d);
                    }
                    else if (d == '"') { inQuote = true; sb.Append(d); }
                    else if (d == '#') { while (j < n && text[j] != '\n') j++; sb.Append(' '); continue; }
                    else if (d == '(') { depth++; sb.Append(d); }
                    else if (d == ')') { depth--; if (depth > 0) sb.Append(d); }
                    else sb.Append(d);
                    j++;
                }

                var cmd = new CMakeRawCommand { Name = name };
                SplitArgs(sb.ToString(), cmd.Args);
                result.Add(cmd);
                i = j;
            }
            return result;
        }

        private static void SplitArgs(string s, List<string> into)
        {
            int n = s.Length;
            int i = 0;
            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < n && s[i] != '"') { if (s[i] == '\\' && i + 1 < n) i++; sb.Append(s[i]); i++; }
                    i++; // closing quote
                    into.Add(sb.ToString());
                }
                else
                {
                    int start = i;
                    while (i < n && s[i] != ' ' && s[i] != '\t' && s[i] != '\r' && s[i] != '\n') i++;
                    into.Add(s.Substring(start, i - start));
                }
            }
        }

        private static bool IsIdStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
