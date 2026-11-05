namespace HelixPythonNamespace.Editor.Doc
{
    using System.Collections.Generic;

    /// <summary>
    /// Resolves stdlib symbols to their docs.python.org page. Port of the C++
    /// <c>doc_url_resolver</c>. Only known stdlib modules resolve; everything else
    /// returns an empty URL.
    /// </summary>
    public static class DocUrlResolver
    {
        private static readonly HashSet<string> KnownStdlibModules = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "os", "os.path", "sys", "io", "json", "re", "math", "cmath",
            "pathlib", "subprocess", "typing", "collections", "collections.abc",
            "itertools", "functools", "operator", "datetime", "time", "calendar",
            "zoneinfo", "shutil", "glob", "tempfile", "argparse", "logging",
            "threading", "multiprocessing", "asyncio", "queue", "sched", "socket",
            "ssl", "http", "http.client", "http.server", "urllib", "urllib.parse",
            "urllib.request", "urllib.error", "urllib.response", "csv", "sqlite3",
            "hashlib", "hmac", "base64", "uuid", "random", "secrets", "dataclasses",
            "enum", "abc", "contextlib", "warnings", "traceback", "inspect", "ast",
            "copy", "pickle", "string", "textwrap", "unicodedata", "struct", "array",
            "weakref", "types", "__future__", "numbers", "decimal", "fractions",
            "statistics", "xml", "xml.etree.ElementTree", "html", "email",
            "unittest", "doctest", "pdb", "profile", "tracemalloc", "gc",
            "platform", "locale", "gettext",
        };

        public static bool IsStdlibModule(string moduleFqn)
        {
            if (string.IsNullOrEmpty(moduleFqn)) return false;
            return KnownStdlibModules.Contains(moduleFqn);
        }

        /// <summary>Returns the docs URL, or empty string when <paramref name="moduleFqn"/> isn't stdlib.</summary>
        public static string ResolvePythonDocsUrl(string moduleFqn, string symbolName)
        {
            if (!IsStdlibModule(moduleFqn)) return "";
            string url = "https://docs.python.org/3/library/" + moduleFqn + ".html";
            if (!string.IsNullOrEmpty(symbolName))
                url += "#" + moduleFqn + "." + symbolName;
            return url;
        }
    }
}
