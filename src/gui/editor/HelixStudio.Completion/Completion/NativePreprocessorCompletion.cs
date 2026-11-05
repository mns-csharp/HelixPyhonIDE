using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Editor.Completion
{
    internal static class NativePreprocessorCompletion
    {
        private static readonly string[] Directives =
        {
            "include", "define", "undef", "if", "ifdef", "ifndef", "elif", "else", "endif",
            "error", "warning", "pragma", "line"
        };

        private static readonly string[] StandardHeaders =
        {
            "algorithm", "any", "array", "atomic", "cassert", "chrono", "cmath", "cstdio",
            "cstdlib", "cstring", "filesystem", "functional", "iostream", "limits", "map",
            "memory", "mutex", "numbers", "optional", "set", "span", "stdexcept", "string",
            "string_view", "thread", "tuple", "type_traits", "typeinfo", "unordered_map", "utility",
            "variant", "vector", "cuda.h", "cuda_runtime.h", "cuda_runtime_api.h", "device_launch_parameters.h"
        };

        public static bool IsDirectiveContext(string source, int caretOffset)
        {
            if (source == null) return false;
            int caret = Math.Max(0, Math.Min(caretOffset, source.Length));
            int lineStart = caret;
            while (lineStart > 0 && source[lineStart - 1] != '\n' && source[lineStart - 1] != '\r') lineStart--;
            int hash = lineStart;
            while (hash < caret && (source[hash] == ' ' || source[hash] == '\t')) hash++;
            if (hash >= caret || source[hash] != '#') return false;
            for (int i = hash + 1; i < caret; i++)
                if (!(source[i] == '_' || char.IsLetterOrDigit(source[i]))) return false;
            return true;
        }

        public static List<CompletionItem> CreateItems()
        {
            var items = new List<CompletionItem>(Directives.Length);
            foreach (string directive in Directives)
                items.Add(new CompletionItem(directive, CompletionKind.Keyword, null,
                    "preprocessor directive #" + directive));
            return items;
        }

        public static bool TryClassifyInclude(string source, int caretOffset, out CompletionContext context)
        {
            context = new CompletionContext();
            if (source == null) return false;
            int caret = Math.Max(0, Math.Min(caretOffset, source.Length));
            int lineStart = caret;
            while (lineStart > 0 && source[lineStart - 1] != '\n' && source[lineStart - 1] != '\r') lineStart--;
            string line = source.Substring(lineStart, caret - lineStart);
            Match match = Regex.Match(line, "^\\s*#\\s*include\\s*([<\\\"])([^>\\\"]*)$");
            if (!match.Success) return false;

            string prefix = match.Groups[2].Value;
            context.Kind = CompletionContextKind.PreprocessorInclude;
            context.Prefix = prefix;
            context.PrefixStart = caret - prefix.Length;
            context.MemberBase = string.Empty;
            context.IncludeDelimiter = match.Groups[1].Value[0];
            return true;
        }

        public static List<CompletionItem> CreateHeaderItems(string filePath, SolutionModel solution, char delimiter)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (delimiter == '<')
                foreach (string header in StandardHeaders) names.Add(header);

            string root = ResolveSolutionRoot(filePath, solution);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) CollectProjectHeaders(root, names);

            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            var items = new List<CompletionItem>(sorted.Count);
            foreach (string name in sorted)
            {
                bool standard = delimiter == '<' && Array.IndexOf(StandardHeaders, name) >= 0;
                items.Add(new CompletionItem(name, CompletionKind.Module, null,
                    standard ? "standard/CUDA header <" + name + ">" : "project header \"" + name + "\""));
            }
            return items;
        }

        private static string ResolveSolutionRoot(string filePath, SolutionModel solution)
        {
            try
            {
                if (solution != null && !string.IsNullOrEmpty(solution.FilePath))
                    return Directory.Exists(solution.FilePath) ? solution.FilePath : Path.GetDirectoryName(solution.FilePath);
                string dir = string.IsNullOrEmpty(filePath) ? null : Path.GetDirectoryName(Path.GetFullPath(filePath));
                while (!string.IsNullOrEmpty(dir))
                {
                    if (File.Exists(Path.Combine(dir, "CMakeLists.txt"))) return dir;
                    DirectoryInfo parent = Directory.GetParent(dir);
                    dir = parent == null ? null : parent.FullName;
                }
            }
            catch { }
            return null;
        }

        private static void CollectProjectHeaders(string root, HashSet<string> names)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            int visited = 0;
            while (pending.Count > 0 && visited++ < 10000)
            {
                string dir = pending.Pop();
                string leaf = Path.GetFileName(dir);
                if (leaf == ".git" || leaf == "build" || leaf == "artifacts" || leaf == "CMakeFiles") continue;
                try
                {
                    foreach (string child in Directory.GetDirectories(dir)) pending.Push(child);
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".h" && ext != ".hh" && ext != ".hpp" && ext != ".hxx" && ext != ".cuh") continue;
                        names.Add(ToIncludeName(root, file));
                    }
                }
                catch { }
            }
        }

        private static string ToIncludeName(string root, string file)
        {
            string full = Path.GetFullPath(file);
            string marker = Path.DirectorySeparatorChar + "include" + Path.DirectorySeparatorChar;
            int include = full.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            string relative = include >= 0 ? full.Substring(include + marker.Length) : MakeRelative(root, full);
            return relative.Replace('\\', '/');
        }

        private static string MakeRelative(string root, string file)
        {
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Uri.UnescapeDataString(new Uri(basePath).MakeRelativeUri(new Uri(Path.GetFullPath(file))).ToString());
        }
    }
}
