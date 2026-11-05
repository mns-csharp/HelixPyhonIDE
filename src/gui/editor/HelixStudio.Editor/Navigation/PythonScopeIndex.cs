namespace HelixPythonNamespace.Editor.Navigation
{
    using System;
    using System.Collections.Generic;

    public enum ScopeKind { Module, Function, Class }

    /// <summary>A lexical scope span over a source buffer. Port of the C++ scope_index <c>Scope</c>.</summary>
    public sealed class Scope
    {
        public ScopeKind Kind = ScopeKind.Module;
        public int StartByte;     // inclusive (char offset)
        public int EndByte;       // exclusive
        public string Name = "";  // empty for Module
    }

    /// <summary>
    /// Lightweight, parser-free scope analyser for a single Python buffer. Walks line by
    /// line tracking indent and triple-quoted strings; pushes a Function/Class scope on each
    /// <c>def NAME</c> / <c>class NAME</c>. Port of the C++ <c>scope_index::ScopeIndex</c>.
    /// Not a full parser — global/nonlocal/comprehension/lambda scopes are ignored.
    /// </summary>
    public sealed class ScopeIndex
    {
        private const int TabWidth = 4;

        private readonly List<Scope> _scopes = new List<Scope>();
        private readonly HashSet<string> _moduleNames = new HashSet<string>(StringComparer.Ordinal);
        private bool _wildcardImport;

        public IReadOnlyList<Scope> AllScopes { get { return _scopes; } }

        public ScopeIndex(string source)
        {
            if (source == null) source = "";

            _scopes.Add(new Scope { Kind = ScopeKind.Module, StartByte = 0, EndByte = source.Length, Name = "" });

            List<int> openScopeIndex = new List<int>();
            List<int> openBodyIndent = new List<int>();

            bool inTriple = false;
            char tripleQuote = '"';

            int lineStart = 0;
            while (lineStart < source.Length)
            {
                int lineEnd = lineStart;
                while (lineEnd < source.Length && source[lineEnd] != '\n') lineEnd++;
                string line = source.Substring(lineStart, lineEnd - lineStart);
                bool startedInString = inTriple;

                bool endsInString = LineEndsInTriple(line, startedInString, ref tripleQuote);
                int nextLineStart = (lineEnd < source.Length) ? lineEnd + 1 : lineEnd;

                if (startedInString) { inTriple = endsInString; lineStart = nextLineStart; continue; }

                int indent = LineIndent(line);
                if (indent < 0) { inTriple = endsInString; lineStart = nextLineStart; continue; }

                while (openScopeIndex.Count > 0 && indent < openBodyIndent[openBodyIndent.Count - 1])
                {
                    _scopes[openScopeIndex[openScopeIndex.Count - 1]].EndByte = lineStart;
                    openScopeIndex.RemoveAt(openScopeIndex.Count - 1);
                    openBodyIndent.RemoveAt(openBodyIndent.Count - 1);
                }

                int firstChar = FirstNonSpace(line);
                string body = RTrim(StripInlineComment(line.Substring(firstChar)));

                bool isDef = StartsWith(body, "def ") || StartsWith(body, "async def ");
                bool isClass = StartsWith(body, "class ");
                if (isDef || isClass)
                {
                    int kwLen = isClass ? 6 : (StartsWith(body, "async ") ? 10 : 4);
                    int after = kwLen;
                    while (after < body.Length && (body[after] == ' ' || body[after] == '\t')) after++;
                    string name = ReadIdentifier(body, after);
                    if (name.Length > 0)
                    {
                        _scopes.Add(new Scope
                        {
                            Kind = isClass ? ScopeKind.Class : ScopeKind.Function,
                            Name = name,
                            StartByte = nextLineStart,
                            EndByte = source.Length,
                        });
                        openScopeIndex.Add(_scopes.Count - 1);
                        openBodyIndent.Add(indent + 1);
                        if (indent == 0) _moduleNames.Add(name);
                    }
                }
                else if (indent == 0)
                {
                    if (StartsWith(body, "import "))
                    {
                        CollectImportNames(body.Substring(7), _moduleNames);
                    }
                    else if (StartsWith(body, "from "))
                    {
                        if (CollectFromImportNames(body.Substring(5), _moduleNames)) _wildcardImport = true;
                    }
                    else
                    {
                        string lhs;
                        if (TryParseAssignmentLhs(body, out lhs)) CollectAssignTargets(lhs, _moduleNames);
                    }
                }

                inTriple = endsInString;
                lineStart = nextLineStart;
            }

            while (openScopeIndex.Count > 0)
            {
                _scopes[openScopeIndex[openScopeIndex.Count - 1]].EndByte = source.Length;
                openScopeIndex.RemoveAt(openScopeIndex.Count - 1);
                openBodyIndent.RemoveAt(openBodyIndent.Count - 1);
            }
        }

        public Scope EnclosingScope(int byteOffset)
        {
            Scope best = _scopes[0];
            int bestSpan = _scopes[0].EndByte - _scopes[0].StartByte;
            for (int i = 1; i < _scopes.Count; i++)
            {
                Scope s = _scopes[i];
                if (byteOffset < s.StartByte || byteOffset >= s.EndByte) continue;
                int span = s.EndByte - s.StartByte;
                if (span < bestSpan) { best = s; bestSpan = span; }
            }
            return best;
        }

        public bool IsModuleScopeName(string name)
        {
            if (_wildcardImport) return true;
            return _moduleNames.Contains(name);
        }

        // ---- helpers (ports of the anonymous-namespace functions) ----

        private static bool IsIdStart(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }
        private static bool IsIdContinue(char c) { return IsIdStart(c) || (c >= '0' && c <= '9'); }

        private static int LineIndent(string line)
        {
            int col = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ' ') col++;
                else if (c == '\t') col += TabWidth;
                else if (c == '#' || c == '\r' || c == '\n') return -1;
                else return col;
            }
            return -1;
        }

        private static int FirstNonSpace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        private static string ReadIdentifier(string line, int start)
        {
            if (start >= line.Length || !IsIdStart(line[start])) return "";
            int i = start + 1;
            while (i < line.Length && IsIdContinue(line[i])) i++;
            return line.Substring(start, i - start);
        }

        private static int CountTriple(string s, int pos, char q)
        {
            int n = 0;
            while (pos + n < s.Length && s[pos + n] == q && n < 3) n++;
            return n;
        }

        private static bool LineEndsInTriple(string line, bool lineStartsInsideString, ref char tripleQuote)
        {
            bool inside = lineStartsInsideString;
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (inside)
                {
                    if (c == tripleQuote)
                    {
                        int n = CountTriple(line, i, tripleQuote);
                        if (n >= 3) { inside = false; i += 3; continue; }
                    }
                    i++;
                }
                else
                {
                    if (c == '#') break;
                    if (c == '"' || c == '\'')
                    {
                        int n = CountTriple(line, i, c);
                        if (n >= 3) { inside = true; tripleQuote = c; i += 3; continue; }
                        char q = c;
                        i++;
                        while (i < line.Length && line[i] != q)
                        {
                            if (line[i] == '\\' && i + 1 < line.Length) i += 2;
                            else i++;
                        }
                        if (i < line.Length) i++;
                        continue;
                    }
                    i++;
                }
            }
            return inside;
        }

        private static string StripInlineComment(string line)
        {
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '#') return line.Substring(0, i);
                if (c == '"' || c == '\'')
                {
                    char q = c;
                    i++;
                    while (i < line.Length && line[i] != q)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length) i += 2;
                        else i++;
                    }
                    if (i < line.Length) i++;
                }
                else i++;
            }
            return line;
        }

        private static string RTrim(string s)
        {
            int end = s.Length;
            while (end > 0 && (s[end - 1] == ' ' || s[end - 1] == '\t' || s[end - 1] == '\r')) end--;
            return end == s.Length ? s : s.Substring(0, end);
        }

        private static bool StartsWith(string s, string prefix)
        {
            return s.Length >= prefix.Length && string.CompareOrdinal(s, 0, prefix, 0, prefix.Length) == 0;
        }

        private static void CollectAssignTargets(string lhs, HashSet<string> outSet)
        {
            int i = 0;
            while (i < lhs.Length)
            {
                if (IsIdStart(lhs[i]))
                {
                    int start = i;
                    while (i < lhs.Length && IsIdContinue(lhs[i])) i++;
                    int look = i;
                    while (look < lhs.Length && (lhs[look] == ' ' || lhs[look] == '\t')) look++;
                    if (look < lhs.Length && (lhs[look] == '.' || lhs[look] == '[')) continue;
                    outSet.Add(lhs.Substring(start, i - start));
                }
                else i++;
            }
        }

        private static bool TryParseAssignmentLhs(string line, out string outLhs)
        {
            outLhs = "";
            int depth = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                if (c == ')' || c == ']' || c == '}') { depth--; continue; }
                if (depth != 0) continue;
                if (c == '"' || c == '\'')
                {
                    char q = c;
                    i++;
                    while (i < line.Length && line[i] != q)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length) i++;
                        i++;
                    }
                    continue;
                }
                if (c == '=')
                {
                    if (i + 1 < line.Length && line[i + 1] == '=') continue;
                    if (i > 0 && (line[i - 1] == '!' || line[i - 1] == '<' || line[i - 1] == '>')) continue;
                    int lhsEnd = i;
                    while (lhsEnd > 0)
                    {
                        char p = line[lhsEnd - 1];
                        if (p == '+' || p == '-' || p == '*' || p == '/' || p == '%' || p == '&' ||
                            p == '|' || p == '^' || p == ':' || p == '@' || p == '<' || p == '>') lhsEnd--;
                        else break;
                    }
                    outLhs = line.Substring(0, lhsEnd);
                    return true;
                }
            }
            return false;
        }

        private static void CollectImportNames(string rest, HashSet<string> outSet)
        {
            int i = 0;
            while (i < rest.Length)
            {
                while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t' || rest[i] == ',')) i++;
                if (i >= rest.Length) break;
                string first = ReadIdentifier(rest, i);
                if (first.Length == 0) { i++; continue; }
                i += first.Length;
                while (i < rest.Length && rest[i] == '.')
                {
                    i++;
                    string chain = ReadIdentifier(rest, i);
                    if (chain.Length == 0) break;
                    i += chain.Length;
                }
                int look = i;
                while (look < rest.Length && (rest[look] == ' ' || rest[look] == '\t')) look++;
                if (look + 2 <= rest.Length && rest[look] == 'a' && rest[look + 1] == 's' &&
                    (look + 2 == rest.Length || rest[look + 2] == ' ' || rest[look + 2] == '\t'))
                {
                    i = look + 2;
                    while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
                    string alias = ReadIdentifier(rest, i);
                    if (alias.Length > 0) { outSet.Add(alias); i += alias.Length; continue; }
                }
                outSet.Add(first);
            }
        }

        private static bool CollectFromImportNames(string rest, HashSet<string> outSet)
        {
            int i = 0;
            if (i < rest.Length && IsIdStart(rest[i]))
            {
                string m = ReadIdentifier(rest, i);
                i += m.Length;
                while (i < rest.Length && rest[i] == '.')
                {
                    i++;
                    string m2 = ReadIdentifier(rest, i);
                    if (m2.Length == 0) break;
                    i += m2.Length;
                }
            }
            while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
            if (i + 6 > rest.Length || !StartsWith(rest.Substring(i), "import")) return false;
            i += 6;
            while (i < rest.Length && (rest[i] == ' ' || rest[i] == '\t')) i++;
            if (i < rest.Length && rest[i] == '*') return true;
            if (i < rest.Length && rest[i] == '(') i++;
            CollectImportNames(rest.Substring(i), outSet);
            return false;
        }
    }
}
