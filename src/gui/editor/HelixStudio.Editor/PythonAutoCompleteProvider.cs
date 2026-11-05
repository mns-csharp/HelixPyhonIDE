using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// Provides Python auto-complete suggestions including keywords, builtins, and context-aware completions.
    /// </summary>
    public class PythonAutoCompleteProvider
    {
        // Python 3 keywords
        private static readonly string[] PythonKeywords = new[]
        {
            "False", "None", "True", "and", "as", "assert", "async", "await",
            "break", "class", "continue", "def", "del", "elif", "else", "except",
            "finally", "for", "from", "global", "if", "import", "in", "is",
            "lambda", "nonlocal", "not", "or", "pass", "raise", "return",
            "try", "while", "with", "yield"
        };

        // Python builtin functions
        private static readonly string[] PythonBuiltins = new[]
        {
            "abs", "aiter", "all", "any", "anext", "ascii",
            "bin", "bool", "breakpoint", "bytearray", "bytes",
            "callable", "chr", "classmethod", "compile", "complex",
            "delattr", "dict", "dir", "divmod",
            "enumerate", "eval", "exec",
            "filter", "float", "format", "frozenset",
            "getattr", "globals",
            "hasattr", "hash", "help", "hex",
            "id", "input", "int", "isinstance", "issubclass", "iter",
            "len", "list", "locals",
            "map", "max", "memoryview", "min",
            "next",
            "object", "oct", "open", "ord",
            "pow", "print", "property",
            "range", "repr", "reversed", "round",
            "set", "setattr", "slice", "sorted", "staticmethod", "str", "sum", "super",
            "tuple", "type",
            "vars",
            "zip",
            "__import__"
        };

        // Common Python modules
        private static readonly string[] CommonModules = new[]
        {
            "os", "sys", "math", "json", "re", "datetime", "collections",
            "itertools", "functools", "io", "pathlib", "subprocess",
            "threading", "multiprocessing", "socket", "http", "urllib",
            "logging", "unittest", "argparse", "csv", "sqlite3",
            "hashlib", "random", "string", "time", "copy", "pickle",
            "struct", "array", "queue", "typing", "abc", "enum",
            "dataclasses", "contextlib", "operator", "shutil", "glob",
            "tempfile", "traceback", "inspect", "pprint", "textwrap",
            "warnings", "weakref", "heapq", "bisect", "statistics",
            "decimal", "fractions", "numbers", "cmath",
            "numpy", "pandas", "matplotlib", "scipy", "sklearn",
            "requests", "flask", "django", "sqlalchemy", "pytest",
            "tkinter", "PIL", "cv2", "tensorflow", "torch"
        };

        // Common module members
        private static readonly Dictionary<string, string[]> ModuleMembers = new Dictionary<string, string[]>
        {
            { "os", new[] { "path", "getcwd", "listdir", "mkdir", "makedirs", "remove", "rmdir",
                           "rename", "stat", "walk", "environ", "sep", "linesep", "name",
                           "system", "popen", "getenv", "putenv", "chdir", "access" } },
            { "os.path", new[] { "join", "exists", "isfile", "isdir", "basename", "dirname",
                                "split", "splitext", "abspath", "realpath", "getsize",
                                "expanduser", "normpath", "relpath" } },
            { "sys", new[] { "argv", "path", "exit", "stdin", "stdout", "stderr", "version",
                           "platform", "modules", "executable", "getdefaultencoding",
                           "getfilesystemencoding", "maxsize", "prefix" } },
            { "json", new[] { "dump", "dumps", "load", "loads", "JSONEncoder", "JSONDecoder",
                            "JSONDecodeError" } },
            { "math", new[] { "pi", "e", "inf", "nan", "ceil", "floor", "sqrt", "pow", "log",
                            "log2", "log10", "sin", "cos", "tan", "asin", "acos", "atan",
                            "degrees", "radians", "factorial", "gcd", "fabs", "isnan", "isinf" } },
            { "re", new[] { "match", "search", "findall", "finditer", "sub", "subn", "split",
                          "compile", "IGNORECASE", "MULTILINE", "DOTALL", "VERBOSE", "Pattern" } },
            { "datetime", new[] { "datetime", "date", "time", "timedelta", "timezone",
                                 "MINYEAR", "MAXYEAR" } },
            { "collections", new[] { "OrderedDict", "defaultdict", "Counter", "deque",
                                    "namedtuple", "ChainMap", "UserDict", "UserList", "UserString" } },
            { "pathlib", new[] { "Path", "PurePath", "PosixPath", "WindowsPath", "PurePosixPath",
                               "PureWindowsPath" } },
            { "string", new[] { "ascii_letters", "ascii_lowercase", "ascii_uppercase", "digits",
                              "hexdigits", "octdigits", "punctuation", "whitespace",
                              "Formatter", "Template", "capwords" } },
            { "random", new[] { "random", "randint", "randrange", "choice", "choices", "shuffle",
                              "sample", "uniform", "gauss", "seed" } },
            { "typing", new[] { "Any", "Union", "Optional", "List", "Dict", "Tuple", "Set",
                              "FrozenSet", "Callable", "Iterator", "Generator", "Type",
                              "ClassVar", "Final", "Literal", "TypeVar", "Generic", "Protocol" } },
        };

        // String method completions
        private static readonly string[] StringMethods = new[]
        {
            "capitalize", "casefold", "center", "count", "encode", "endswith",
            "expandtabs", "find", "format", "format_map", "index", "isalnum",
            "isalpha", "isascii", "isdecimal", "isdigit", "isidentifier",
            "islower", "isnumeric", "isprintable", "isspace", "istitle",
            "isupper", "join", "ljust", "lower", "lstrip", "maketrans",
            "partition", "removeprefix", "removesuffix", "replace", "rfind",
            "rindex", "rjust", "rpartition", "rsplit", "rstrip", "split",
            "splitlines", "startswith", "strip", "swapcase", "title",
            "translate", "upper", "zfill"
        };

        // List method completions
        private static readonly string[] ListMethods = new[]
        {
            "append", "clear", "copy", "count", "extend", "index",
            "insert", "pop", "remove", "reverse", "sort"
        };

        // Dict method completions
        private static readonly string[] DictMethods = new[]
        {
            "clear", "copy", "fromkeys", "get", "items", "keys",
            "pop", "popitem", "setdefault", "update", "values"
        };

        // Common dunder methods
        private static readonly string[] DunderMethods = new[]
        {
            "__init__", "__str__", "__repr__", "__len__", "__getitem__", "__setitem__",
            "__delitem__", "__contains__", "__iter__", "__next__", "__call__",
            "__enter__", "__exit__", "__eq__", "__ne__", "__lt__", "__gt__",
            "__le__", "__ge__", "__add__", "__sub__", "__mul__", "__truediv__",
            "__floordiv__", "__mod__", "__pow__", "__and__", "__or__", "__xor__",
            "__hash__", "__bool__", "__new__", "__del__", "__getattr__",
            "__setattr__", "__delattr__", "__class__", "__dict__", "__doc__",
            "__name__", "__module__", "__bases__", "__slots__"
        };

        /// <summary>
        /// Gets auto-complete suggestions based on the current text and cursor position.
        /// </summary>
        public List<string> GetSuggestions(string fullText, int currentPos, string currentWord)
        {
            var suggestions = new List<string>();

            if (string.IsNullOrEmpty(currentWord))
                return suggestions;

            // Check if we're after a dot (member access)
            string lineText = GetCurrentLine(fullText, currentPos);
            string beforeDot = GetTextBeforeDot(lineText, currentWord);

            if (beforeDot != null)
            {
                // Module member completions
                suggestions.AddRange(GetMemberCompletions(beforeDot, currentWord));
            }
            else
            {
                // Check for import context
                if (IsImportContext(lineText))
                {
                    suggestions.AddRange(CommonModules.Where(m =>
                        m.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)));
                }
                else
                {
                    // General completions: keywords + builtins + extracted identifiers
                    suggestions.AddRange(PythonKeywords.Where(k =>
                        k.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)));
                    suggestions.AddRange(PythonBuiltins.Where(b =>
                        b.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)));

                    // Dunder methods if starting with __
                    if (currentWord.StartsWith("__"))
                    {
                        suggestions.AddRange(DunderMethods.Where(d =>
                            d.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)));
                    }

                    // Extract user-defined identifiers from the document
                    var userIdentifiers = ExtractIdentifiers(fullText);
                    suggestions.AddRange(userIdentifiers.Where(id =>
                        id.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase) &&
                        !id.Equals(currentWord, StringComparison.OrdinalIgnoreCase)));

                    // Extract imported module names
                    var importedModules = ExtractImportedNames(fullText);
                    suggestions.AddRange(importedModules.Where(m =>
                        m.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return suggestions.Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();
        }

        /// <summary>
        /// Gets call tip / signature help for a function.
        /// </summary>
        public string GetCallTip(string functionName)
        {
            var builtinTips = new Dictionary<string, string>
            {
                { "print", "print(*objects, sep=' ', end='\\n', file=sys.stdout, flush=False)" },
                { "range", "range(stop) | range(start, stop[, step])" },
                { "len", "len(s) -> int" },
                { "open", "open(file, mode='r', buffering=-1, encoding=None, errors=None, newline=None, closefd=True, opener=None)" },
                { "input", "input([prompt]) -> string" },
                { "int", "int(x=0) | int(x, base=10)" },
                { "float", "float(x=0)" },
                { "str", "str(object='') | str(object=b'', encoding='utf-8', errors='strict')" },
                { "list", "list() | list(iterable)" },
                { "dict", "dict() | dict(mapping) | dict(iterable) | dict(**kwargs)" },
                { "set", "set() | set(iterable)" },
                { "tuple", "tuple() | tuple(iterable)" },
                { "sorted", "sorted(iterable, /, *, key=None, reverse=False)" },
                { "map", "map(func, *iterables) -> map object" },
                { "filter", "filter(function, iterable) -> filter object" },
                { "enumerate", "enumerate(iterable, start=0)" },
                { "zip", "zip(*iterables) -> zip object" },
                { "isinstance", "isinstance(object, classinfo) -> bool" },
                { "type", "type(object) | type(name, bases, dict)" },
                { "super", "super() -> same as super(__class__, <first argument>)" },
                { "getattr", "getattr(object, name[, default])" },
                { "setattr", "setattr(object, name, value)" },
                { "hasattr", "hasattr(object, name) -> bool" },
                { "format", "format(value[, format_spec]) -> str" },
                { "max", "max(iterable, *[, default, key]) | max(arg1, arg2, *args[, key])" },
                { "min", "min(iterable, *[, default, key]) | min(arg1, arg2, *args[, key])" },
                { "abs", "abs(x) -> number" },
                { "round", "round(number[, ndigits]) -> number" },
                { "sum", "sum(iterable, /, start=0)" },
                { "all", "all(iterable) -> bool" },
                { "any", "any(iterable) -> bool" },
                { "hex", "hex(number) -> string" },
                { "oct", "oct(number) -> string" },
                { "bin", "bin(number) -> string" },
                { "ord", "ord(c) -> integer" },
                { "chr", "chr(i) -> character" },
                { "eval", "eval(expression[, globals[, locals]])" },
                { "exec", "exec(object[, globals[, locals]])" },
            };

            string tip;
            if (builtinTips.TryGetValue(functionName, out tip))
                return tip;

            return null;
        }

        private List<string> GetMemberCompletions(string objectName, string prefix)
        {
            var results = new List<string>();

            // Check known module members
            string[] members;
            if (ModuleMembers.TryGetValue(objectName, out members))
            {
                results.AddRange(members.Where(m =>
                    string.IsNullOrEmpty(prefix) || m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }

            // Common type methods
            string lowerObj = objectName.ToLower();
            if (lowerObj == "str" || lowerObj == "string" || lowerObj.StartsWith("\"") || lowerObj.StartsWith("'"))
            {
                results.AddRange(StringMethods.Where(m =>
                    string.IsNullOrEmpty(prefix) || m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            else if (lowerObj == "list" || lowerObj == "[]")
            {
                results.AddRange(ListMethods.Where(m =>
                    string.IsNullOrEmpty(prefix) || m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            else if (lowerObj == "dict" || lowerObj == "{}")
            {
                results.AddRange(DictMethods.Where(m =>
                    string.IsNullOrEmpty(prefix) || m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }
            else if (lowerObj == "self")
            {
                // For self., suggest dunder methods
                results.AddRange(DunderMethods.Where(d =>
                    string.IsNullOrEmpty(prefix) || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }

            return results;
        }

        private string GetCurrentLine(string text, int pos)
        {
            if (string.IsNullOrEmpty(text) || pos <= 0)
                return "";

            int safePos = Math.Min(pos, text.Length);
            int lineStart = text.LastIndexOf('\n', safePos - 1);
            if (lineStart < 0) lineStart = 0;
            else lineStart++;

            return text.Substring(lineStart, safePos - lineStart);
        }

        private string GetTextBeforeDot(string line, string currentWord)
        {
            // Find pattern like "module.prefix" in the line
            int dotPos = line.LastIndexOf('.');
            if (dotPos < 0) return null;

            string beforeDot = line.Substring(0, dotPos).TrimEnd();
            // Extract the last identifier before the dot
            var match = Regex.Match(beforeDot, @"([\w\.]+)\s*$");
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }

        private bool IsImportContext(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("import ") || trimmed.StartsWith("from ") ||
                   Regex.IsMatch(trimmed, @"from\s+\S+\s+import\s+");
        }

        private HashSet<string> ExtractIdentifiers(string text)
        {
            var identifiers = new HashSet<string>();

            // Extract function names
            foreach (Match m in Regex.Matches(text, @"def\s+(\w+)"))
                identifiers.Add(m.Groups[1].Value);

            // Extract class names
            foreach (Match m in Regex.Matches(text, @"class\s+(\w+)"))
                identifiers.Add(m.Groups[1].Value);

            // Extract variable assignments
            foreach (Match m in Regex.Matches(text, @"^(\w+)\s*=", RegexOptions.Multiline))
            {
                var name = m.Groups[1].Value;
                if (!PythonKeywords.Contains(name))
                    identifiers.Add(name);
            }

            // Extract function parameters
            foreach (Match m in Regex.Matches(text, @"def\s+\w+\(([^)]*)\)"))
            {
                var paramsStr = m.Groups[1].Value;
                foreach (var param in paramsStr.Split(','))
                {
                    var p = param.Trim().Split('=')[0].Split(':')[0].Trim()
                        .Replace("*", "").Replace("**", "");
                    if (!string.IsNullOrEmpty(p) && p != "self" && p != "cls")
                        identifiers.Add(p);
                }
            }

            return identifiers;
        }

        private HashSet<string> ExtractImportedNames(string text)
        {
            var names = new HashSet<string>();

            // import module
            foreach (Match m in Regex.Matches(text, @"^\s*import\s+([\w,\s]+)", RegexOptions.Multiline))
            {
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    var mod = part.Trim();
                    // handle "import x as y"
                    var asParts = mod.Split(new[] { " as " }, StringSplitOptions.None);
                    names.Add(asParts.Length > 1 ? asParts[1].Trim() : asParts[0].Trim());
                }
            }

            // from module import name
            foreach (Match m in Regex.Matches(text, @"^\s*from\s+\S+\s+import\s+([\w,\s*]+)", RegexOptions.Multiline))
            {
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    var name = part.Trim();
                    if (name == "*") continue;
                    var asParts = name.Split(new[] { " as " }, StringSplitOptions.None);
                    names.Add(asParts.Length > 1 ? asParts[1].Trim() : asParts[0].Trim());
                }
            }

            return names;
        }
    }
}
