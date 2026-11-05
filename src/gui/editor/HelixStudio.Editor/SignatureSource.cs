namespace HelixPythonNamespace.Editor
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Catalog;

    /// <summary>A parameter's [start, end) byte span within a <see cref="SignatureInfo.Text"/>.</summary>
    public struct ParamSpan
    {
        public int Start;
        public int End;
        public ParamSpan(int start, int end) { Start = start; End = end; }
    }

    /// <summary>Curated call-tip body plus per-parameter spans. Port of the C++ <c>SignatureInfo</c>.</summary>
    public sealed class SignatureInfo
    {
        public string Text = "";
        public List<ParamSpan> ParamSpans = new List<ParamSpan>();
        public List<string> ParamSummaries = new List<string>();
        public string Summary = "";
        public string ReturnSummary = "";
    }

    /// <summary>
    /// Signature lookups for call-tips / quick-info. Priority: project catalog → system
    /// catalog → curated builtin table (cold-start fallback). Port of the C++
    /// <c>SignatureSource</c>.
    /// </summary>
    public sealed class SignatureSource
    {
        private PythonCatalog _catalog;
        private PythonCatalog _projectCatalog;

        public void SetCatalog(PythonCatalog catalog) { _catalog = catalog; }
        public void SetProjectCatalog(PythonCatalog catalog) { _projectCatalog = catalog; }

        public SignatureInfo Lookup(string functionName)
        {
            CatalogSymbol hit = CatalogHit(functionName);
            if (hit != null) return BuildFromCatalog(hit, false);
            SignatureInfo builtin;
            return BuiltinTable().TryGetValue(functionName, out builtin) ? builtin : null;
        }

        public SignatureInfo LookupVerbose(string functionName)
        {
            CatalogSymbol hit = CatalogHit(functionName);
            if (hit != null) return BuildFromCatalog(hit, true);
            return Lookup(functionName);
        }

        public CatalogSymbol FindSymbol(string bareName)
        {
            return CatalogHit(bareName);
        }

        private CatalogSymbol CatalogHit(string name)
        {
            if (_projectCatalog != null)
            {
                CatalogSymbol hit = _projectCatalog.Lookup(name) ?? _projectCatalog.LookupByName(name);
                if (hit != null) return hit;
            }
            if (_catalog != null)
            {
                CatalogSymbol hit = _catalog.Lookup(name) ?? _catalog.LookupByName(name);
                if (hit != null) return hit;
            }
            return null;
        }

        public static SignatureInfo Build(string funcName, IList<string> parms, string returnType)
        {
            SignatureInfo info = new SignatureInfo();
            StringBuilder text = new StringBuilder();
            text.Append(funcName).Append("(");
            for (int i = 0; i < parms.Count; i++)
            {
                int start = text.Length;
                text.Append(parms[i]);
                int end = text.Length;
                info.ParamSpans.Add(new ParamSpan(start, end));
                if (i + 1 < parms.Count) text.Append(", ");
            }
            text.Append(")");
            if (!string.IsNullOrEmpty(returnType)) text.Append(" -> ").Append(returnType);
            info.Text = text.ToString();
            return info;
        }

        private static SignatureInfo BuildFromCatalog(CatalogSymbol s, bool verbose)
        {
            SignatureInfo info = new SignatureInfo();
            string sigLine = !string.IsNullOrEmpty(s.Signature) ? s.Signature : s.Name + "(...)";
            info.Text = sigLine;
            info.Summary = s.Summary;
            info.ReturnSummary = s.ReturnSummary;

            int open = sigLine.IndexOf('(');
            int close = sigLine.LastIndexOf(')');
            if (open >= 0 && close >= 0 && close > open)
            {
                int start = open + 1;
                int depth = 0;
                int paramIdx = 0;
                for (int i = start; i <= close; i++)
                {
                    char c = (i == close) ? ',' : sigLine[i];
                    if (c == '(' || c == '[' || c == '{') depth++;
                    else if (c == ')' || c == ']' || c == '}') depth--;
                    if (c == ',' && depth <= 0)
                    {
                        int end = i;
                        if (end > start)
                        {
                            int s2 = start;
                            while (s2 < end && sigLine[s2] == ' ') s2++;
                            info.ParamSpans.Add(new ParamSpan(s2, end));
                            info.ParamSummaries.Add(paramIdx < s.Params.Count ? s.Params[paramIdx].Description : "");
                            paramIdx++;
                        }
                        start = i + 1;
                    }
                }
            }

            string body = (verbose && !string.IsNullOrEmpty(s.FullDoc)) ? s.FullDoc : s.Summary;
            if (!string.IsNullOrEmpty(body)) info.Text += "\n" + WrapPreservingLines(body, 72);
            if (!verbose && !string.IsNullOrEmpty(s.ReturnSummary))
                info.Text += "\nReturns: " + WrapPreservingLines(s.ReturnSummary, 64);
            return info;
        }

        internal static string WrapPreservingLines(string s, int maxCols)
        {
            if (maxCols < 16) maxCols = 16;
            StringBuilder outSb = new StringBuilder();
            int lineStart = 0;
            bool firstLine = true;
            while (lineStart <= s.Length)
            {
                int nl = s.IndexOf('\n', lineStart);
                string line = nl < 0 ? s.Substring(lineStart) : s.Substring(lineStart, nl - lineStart);
                int i = 0;
                while (i < line.Length)
                {
                    int remaining = line.Length - i;
                    if (remaining <= maxCols)
                    {
                        if (!firstLine) outSb.Append('\n');
                        firstLine = false;
                        outSb.Append(line.Substring(i));
                        break;
                    }
                    int breakPos = -1;
                    for (int k = i + maxCols; k > i; k--)
                        if (k < line.Length && line[k] == ' ') { breakPos = k; break; }
                    if (!firstLine) outSb.Append('\n');
                    firstLine = false;
                    if (breakPos < 0)
                    {
                        int nextSpace = line.IndexOf(' ', i);
                        if (nextSpace < 0) { outSb.Append(line.Substring(i)); break; }
                        outSb.Append(line.Substring(i, nextSpace - i));
                        i = nextSpace + 1;
                    }
                    else
                    {
                        outSb.Append(line.Substring(i, breakPos - i));
                        i = breakPos + 1;
                    }
                }
                if (line.Length == 0)
                {
                    if (!firstLine) outSb.Append('\n');
                    firstLine = false;
                }
                if (nl < 0) break;
                lineStart = nl + 1;
            }
            return outSb.ToString();
        }

        private static Dictionary<string, SignatureInfo> _builtinTable;

        private static Dictionary<string, SignatureInfo> BuiltinTable()
        {
            if (_builtinTable != null) return _builtinTable;
            Dictionary<string, SignatureInfo> m = new Dictionary<string, SignatureInfo>();
            System.Action<string, string[], string> add = (name, parms, ret) => m[name] = Build(name, parms, ret);

            add("print", new[] { "*objects", "sep=' '", "end='\\n'", "file=sys.stdout", "flush=False" }, "None");
            add("range", new[] { "start", "stop", "step=1" }, "range");
            add("len", new[] { "obj" }, "int");
            add("open", new[] { "file", "mode='r'", "buffering=-1", "encoding=None", "errors=None", "newline=None" }, "IO");
            add("enumerate", new[] { "iterable", "start=0" }, "enumerate");
            add("sorted", new[] { "iterable", "*", "key=None", "reverse=False" }, "list");
            add("zip", new[] { "*iterables", "strict=False" }, "zip");
            add("map", new[] { "function", "*iterables" }, "map");
            add("filter", new[] { "function", "iterable" }, "filter");
            add("min", new[] { "iterable", "*", "key=None", "default=...", "/" }, "Any");
            add("max", new[] { "iterable", "*", "key=None", "default=..." }, "Any");
            add("sum", new[] { "iterable", "start=0" }, "Any");
            add("abs", new[] { "x" }, "Any");
            add("round", new[] { "number", "ndigits=None" }, "Any");
            add("isinstance", new[] { "obj", "classinfo" }, "bool");
            add("issubclass", new[] { "cls", "classinfo" }, "bool");
            add("hasattr", new[] { "obj", "name" }, "bool");
            add("getattr", new[] { "obj", "name", "default=..." }, "Any");
            add("setattr", new[] { "obj", "name", "value" }, "None");
            add("delattr", new[] { "obj", "name" }, "None");
            add("input", new[] { "prompt=''" }, "str");
            add("int", new[] { "value=0", "base=10" }, "int");
            add("float", new[] { "value=0.0" }, "float");
            add("str", new[] { "object=''", "encoding='utf-8'", "errors='strict'" }, "str");
            add("bytes", new[] { "source=b''", "encoding=None", "errors=None" }, "bytes");
            add("list", new[] { "iterable=()" }, "list");
            add("dict", new[] { "**kwargs" }, "dict");
            add("set", new[] { "iterable=()" }, "set");
            add("tuple", new[] { "iterable=()" }, "tuple");
            add("type", new[] { "object_or_name", "bases=...", "dict=..." }, "type");
            add("repr", new[] { "obj" }, "str");
            add("format", new[] { "value", "format_spec=''" }, "str");
            add("any", new[] { "iterable" }, "bool");
            add("all", new[] { "iterable" }, "bool");
            add("iter", new[] { "iterable", "sentinel=...?" }, "iterator");
            add("next", new[] { "iterator", "default=...?" }, "Any");
            add("reversed", new[] { "sequence" }, "iterator");
            add("hash", new[] { "obj" }, "int");
            add("id", new[] { "obj" }, "int");
            add("chr", new[] { "i" }, "str");
            add("ord", new[] { "c" }, "int");
            add("hex", new[] { "number" }, "str");
            add("oct", new[] { "number" }, "str");
            add("bin", new[] { "number" }, "str");
            add("divmod", new[] { "x", "y" }, "tuple");
            add("pow", new[] { "base", "exp", "mod=None" }, "Any");
            add("join", new[] { "path", "*paths" }, "str");
            add("exists", new[] { "path" }, "bool");
            add("isfile", new[] { "path" }, "bool");
            add("isdir", new[] { "path" }, "bool");

            _builtinTable = m;
            return m;
        }
    }
}
