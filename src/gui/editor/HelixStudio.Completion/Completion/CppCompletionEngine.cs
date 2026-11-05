using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>C/C++98-26 completion with canonical signatures and current-file symbols.</summary>
    public sealed class CppCompletionEngine : ICompletionEngine
    {
        private static readonly string[] Keywords =
        {
            "alignas", "alignof", "asm", "auto", "bool", "break", "case", "catch", "char",
            "char8_t", "char16_t", "char32_t", "class", "concept", "const", "consteval",
            "constexpr", "constinit", "const_cast", "continue", "co_await", "co_return",
            "co_yield", "decltype", "default", "delete", "do", "double", "dynamic_cast",
            "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend",
            "goto", "if", "inline", "int", "long", "module", "mutable", "namespace", "new",
            "noexcept", "nullptr", "operator", "private", "protected", "public", "register",
            "reinterpret_cast", "requires", "return", "short", "signed", "sizeof", "static",
            "static_assert", "static_cast", "struct", "switch", "template", "this",
            "thread_local", "throw", "true", "try", "typedef", "typeid", "typename", "union",
            "unsigned", "using", "virtual", "void", "volatile", "wchar_t", "while"
        };

        private static readonly string[] LibraryTypes =
        {
            "string", "string_view", "vector", "array", "span", "map", "unordered_map", "set",
            "optional", "variant", "tuple", "unique_ptr", "shared_ptr", "weak_ptr", "function",
            "filesystem", "thread", "mutex", "atomic", "size_t", "ptrdiff_t"
        };

        public CompletionContext Classify(string source, int caretOffset)
        {
            CompletionContext includeContext;
            if (NativePreprocessorCompletion.TryClassifyInclude(source, caretOffset, out includeContext))
                return includeContext;
            source = source ?? string.Empty;
            int caret = Math.Max(0, Math.Min(caretOffset, source.Length));
            int start = caret;
            while (start > 0 && IsIdentifier(source[start - 1])) start--;
            var context = new CompletionContext
            {
                Kind = CompletionContextKind.General,
                Prefix = source.Substring(start, caret - start),
                PrefixStart = start,
                MemberBase = string.Empty
            };
            int separator = start - 1;
            if (separator >= 0 && source[separator] == '.')
                context.Kind = CompletionContextKind.Member;
            else if (separator >= 1 && source[separator] == '>' && source[separator - 1] == '-')
                context.Kind = CompletionContextKind.Member;
            else if (separator >= 1 && source[separator] == ':' && source[separator - 1] == ':')
                context.Kind = CompletionContextKind.Member;
            return context;
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution)
        {
            return GetCompletions(source, caretOffset, filePath, solution, Classify(source, caretOffset));
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath,
            SolutionModel solution, CompletionContext context)
        {
            if (context.Kind == CompletionContextKind.PreprocessorInclude)
                return NativePreprocessorCompletion.CreateHeaderItems(filePath, solution, context.IncludeDelimiter);
            if (NativePreprocessorCompletion.IsDirectiveContext(source, caretOffset))
                return NativePreprocessorCompletion.CreateItems();

            var items = new List<CompletionItem>();
            foreach (string keyword in Keywords)
                items.Add(new CompletionItem(keyword, CompletionKind.Keyword, null, "C++ keyword " + keyword));
            foreach (string type in LibraryTypes)
                items.Add(new CompletionItem(type, CompletionKind.Class, null, "std::" + type));
            foreach (string name in new[] { "printf", "malloc", "free", "memcpy", "move", "make_unique",
                "make_shared", "sort", "begin", "end" })
            {
                CppCatalog.Entry entry = CppCatalog.Get(name);
                items.Add(new CompletionItem(name, CompletionKind.Function, null,
                    entry.Signature, entry.Documentation));
            }
            AddFileSymbols(items, source ?? string.Empty);
            return items;
        }

        private static void AddFileSymbols(List<CompletionItem> items, string source)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(source,
                @"(?m)^\s*(?:template\s*<[^;{}]+>\s*)?(?:[\w:<>~*&]+\s+)+(?<name>[A-Za-z_]\w*)\s*\((?<args>[^;{}()]*)\)"))
            {
                string name = match.Groups["name"].Value;
                if (!seen.Add(name) || IsControlWord(name)) continue;
                string signature = match.Value.Trim();
                items.Add(new CompletionItem(name, CompletionKind.Function, null, signature,
                    "Declared in the current translation unit."));
            }
            foreach (Match match in Regex.Matches(source, @"\b(?:class|struct|enum\s+class|enum)\s+(?<name>[A-Za-z_]\w*)"))
            {
                string name = match.Groups["name"].Value;
                if (seen.Add(name))
                    items.Add(new CompletionItem(name, CompletionKind.Class, null, "type " + name,
                        "Declared in the current translation unit."));
            }
        }

        private static bool IsControlWord(string name)
        {
            return name == "if" || name == "for" || name == "while" || name == "switch" || name == "catch";
        }

        private static bool IsIdentifier(char value)
        {
            return value == '_' || char.IsLetterOrDigit(value);
        }
    }
}
