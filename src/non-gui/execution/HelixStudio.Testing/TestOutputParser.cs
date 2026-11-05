namespace HelixPythonNamespace.Testing
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Pure, deterministic line-by-line parser for pytest / unittest verbose output.
    /// Faithful port of the C++ <c>TestRunner::HandleLine</c> and its helpers, split out
    /// from the process orchestration so the parse logic is unit-testable in isolation.
    /// </summary>
    public sealed class TestOutputParser
    {
        private static readonly Regex PytestResultRe = new Regex(
            @"^(?<id>[^\s]+::[^\s]+)\s+(?<status>PASSED|FAILED|ERROR|SKIPPED|XFAIL|XPASS)\b(?:[^\[]*\[\s*(?<pct>\d+)%\])?",
            RegexOptions.Compiled);

        private static readonly Regex PytestHeaderRe = new Regex(
            @"^_{3,}\s*(?<name>.+?)\s*_{3,}$", RegexOptions.Compiled);

        private static readonly Regex PytestLocRe = new Regex(
            @"^(?<file>[^:]+\.py):(?<line>\d+):", RegexOptions.Compiled);

        private static readonly Regex UnittestRe = new Regex(
            @"^(?<test>\w+)\s+\((?<cls>[\w\.]+)\)\s+\.\.\.\s+(?<status>ok|FAIL|ERROR|skipped)",
            RegexOptions.Compiled);

        private readonly TestFramework _framework;
        private readonly string _workingDirectory;
        private string _currentFailureHeader = "";
        // Source lines per test file, read once per run: every result row resolves its
        // definition line against the same handful of files.
        private readonly Dictionary<string, string[]> _fileLinesCache =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public List<TestNode> Results { get; } = new List<TestNode>();
        public int LastEmittedPercent { get; private set; } = -1;

        /// <summary>Raised once per parsed result line, carrying a snapshot of the affected node.</summary>
        public event Action<TestNode> NodeBatched;

        /// <summary>Raised when a pytest result line carries a trailing <c>[ NN%]</c> indicator (percent, completed, totalEstimate).</summary>
        public event Action<int, int, int> ProgressReported;

        public TestOutputParser(TestFramework framework, string workingDirectory)
        {
            _framework = framework;
            _workingDirectory = workingDirectory ?? "";
        }

        public void HandleLine(string line)
        {
            if (_framework == TestFramework.Pytest)
            {
                Match m = PytestResultRe.Match(line);
                if (m.Success)
                {
                    string id = m.Groups["id"].Value;
                    string status = m.Groups["status"].Value;
                    TestNode node = FindOrCreate(id);
                    node.Display = id;
                    if (status == "PASSED" || status == "XFAIL" || status == "XPASS") node.Status = TestStatus.Passed;
                    else if (status == "FAILED") node.Status = TestStatus.Failed;
                    else if (status == "ERROR") node.Status = TestStatus.Error;
                    else node.Status = TestStatus.Skipped;

                    int sep = id.IndexOf("::", StringComparison.Ordinal);
                    if (sep >= 0)
                    {
                        string rel = id.Substring(0, sep);
                        node.FilePath = ResolveAgainst(_workingDirectory, rel);
                        // Every result gets the line its test FUNCTION is defined on — not just
                        // failures (whose traceback location overwrites this with the failing
                        // assert's line later, which is the better jump target for them).
                        if (node.Line == 0)
                            node.Line = ResolveDefinitionLine(node.FilePath, id.Substring(sep + 2), _fileLinesCache);
                    }
                    RaiseNode(node);

                    string pctStr = m.Groups["pct"].Value;
                    if (!string.IsNullOrEmpty(pctStr))
                    {
                        int pct = int.Parse(pctStr);
                        if (pct < 0) pct = 0;
                        if (pct > 100) pct = 100;
                        if (pct != LastEmittedPercent)
                        {
                            LastEmittedPercent = pct;
                            int completed = Results.Count;
                            int totalEst = pct > 0 ? (completed * 100 + pct - 1) / pct : completed;
                            Action<int, int, int> ph = ProgressReported;
                            if (ph != null) ph(pct, completed, totalEst);
                        }
                    }
                    return;
                }

                string trimmed = line.Trim();
                Match h = PytestHeaderRe.Match(trimmed);
                if (h.Success)
                {
                    _currentFailureHeader = h.Groups["name"].Value;
                    return;
                }

                Match l = PytestLocRe.Match(line);
                if (l.Success && _currentFailureHeader.Length > 0)
                {
                    string file = l.Groups["file"].Value;
                    int lineNo = int.Parse(l.Groups["line"].Value);
                    foreach (TestNode node in Results)
                    {
                        if ((node.Status == TestStatus.Failed || node.Status == TestStatus.Error) &&
                            IdMatchesHeader(node.Id, _currentFailureHeader))
                        {
                            node.Line = lineNo;
                            if (string.IsNullOrEmpty(node.FilePath))
                                node.FilePath = ResolveAgainst(_workingDirectory, file);
                            RaiseNode(node);
                            break;
                        }
                    }
                    return;
                }
                return;
            }

            // Unittest verbose: test_x (pkg.mod.Class) ... ok | FAIL | ERROR | skipped
            Match um = UnittestRe.Match(line);
            if (!um.Success) return;
            string uid = um.Groups["cls"].Value + "." + um.Groups["test"].Value;
            string ustatus = um.Groups["status"].Value;
            TestNode unode = FindOrCreate(uid);
            unode.Display = uid;
            if (ustatus == "ok") unode.Status = TestStatus.Passed;
            else if (ustatus == "FAIL") unode.Status = TestStatus.Failed;
            else if (ustatus == "ERROR") unode.Status = TestStatus.Error;
            else unode.Status = TestStatus.Skipped;
            RaiseNode(unode);
        }

        public List<string> FailedIds()
        {
            List<string> outList = new List<string>();
            foreach (TestNode n in Results)
                if (n.Status == TestStatus.Failed || n.Status == TestStatus.Error)
                    outList.Add(n.Id);
            return outList;
        }

        private TestNode FindOrCreate(string id)
        {
            foreach (TestNode n in Results) if (n.Id == id) return n;
            TestNode node = new TestNode { Id = id, Display = id };
            Results.Add(node);
            return node;
        }

        private void RaiseNode(TestNode node)
        {
            Action<TestNode> h = NodeBatched;
            if (h != null) h(node.Clone());
        }

        // ---- helpers (internal for unit testing) ----

        /// <summary>Resolve a relative path against the working dir and normalise; return input on failure.</summary>
        internal static string ResolveAgainst(string workingDir, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            try
            {
                string p = rel;
                if (!Path.IsPathRooted(p) && !string.IsNullOrEmpty(workingDir))
                    p = Path.Combine(workingDir, p);
                return Path.GetFullPath(p);
            }
            catch
            {
                return rel;
            }
        }

        /// <summary>Strip a parametrize suffix <c>[..]</c> from a test id tail.</summary>
        internal static string StripParametrize(string s)
        {
            int pos = s.IndexOf('[');
            return pos >= 0 ? s.Substring(0, pos) : s;
        }

        /// <summary>
        /// 1-based line the test function is defined on, found by scanning the source file for
        /// <c>def &lt;name&gt;</c> (searching below the owning <c>class</c> first when the id has
        /// one, so a method name reused across classes resolves to the right definition).
        /// 0 when the file or definition can't be found. Internal for deterministic testing.
        /// </summary>
        internal static int ResolveDefinitionLine(string filePath, string idTail,
            Dictionary<string, string[]> cache)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(idTail)) return 0;
            string[] lines;
            if (cache == null || !cache.TryGetValue(filePath, out lines))
            {
                try { lines = File.ReadAllLines(filePath); } catch { lines = new string[0]; }
                if (cache != null) cache[filePath] = lines;
            }
            if (lines.Length == 0) return 0;

            string[] segs = StripParametrize(idTail).Split(
                new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) return 0;
            string method = segs[segs.Length - 1];

            int from = 0;
            if (segs.Length > 1)
            {
                Regex classRe = new Regex(@"^\s*class\s+" + Regex.Escape(segs[0]) + @"\b");
                for (int i = 0; i < lines.Length; i++)
                    if (classRe.IsMatch(lines[i])) { from = i; break; }
            }
            Regex defRe = new Regex(@"^\s*(async\s+)?def\s+" + Regex.Escape(method) + @"\s*\(");
            for (int i = from; i < lines.Length; i++)
                if (defRe.IsMatch(lines[i])) return i + 1;
            // Not under the named class (e.g. inherited from a base class defined earlier in
            // the file): fall back to the part of the file above it.
            for (int i = 0; i < from; i++)
                if (defRe.IsMatch(lines[i])) return i + 1;
            return 0;
        }

        /// <summary>
        /// True if <paramref name="id"/> (e.g. "tests/test_sample.py::TestRectangle::test_x")
        /// matches the underlined-header <paramref name="header"/> (e.g. "TestRectangle.test_x").
        /// </summary>
        internal static bool IdMatchesHeader(string id, string header)
        {
            int sep = id.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0) return false;
            string idSuffix = id.Substring(sep + 2).Replace(':', '.');
            string normIdSuffix = CollapseDots(idSuffix);
            string normHeader = CollapseDots(header.Replace(':', '.'));
            return StripParametrize(normIdSuffix) == StripParametrize(normHeader);
        }

        private static string CollapseDots(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '.' && sb.Length > 0 && sb[sb.Length - 1] == '.') continue;
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
