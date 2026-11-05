namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// The judgements that only exist when the whole submission is on screen at once. A per-file
    /// rule cannot see an import cycle, cannot know a block is a copy, and cannot tell that
    /// nothing tests a class.
    /// </summary>
    internal static class CrossFileRuleSet
    {
        public static IEnumerable<ICrossFileReviewRule> All()
        {
            yield return new ImportCycleRule();
            yield return new DuplicatedBlockRule();
            yield return new UntestedPublicTypeRule();
        }

        /// <summary>The module name a file would be imported by (its basename, no extension).</summary>
        internal static string ModuleNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string file = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        /// <summary>The dotted `package.module` form, when the file sits in a named directory.</summary>
        internal static string QualifiedModuleOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            if (slash <= 0) return ModuleNameOf(path);
            int previous = normalized.LastIndexOf('/', slash - 1);
            string package = normalized.Substring(previous + 1, slash - previous - 1);
            return package + "." + ModuleNameOf(path);
        }

        /// <summary>Every module path this file imports, as written.</summary>
        internal static List<string> ImportedModules(ModuleSyntax module)
        {
            var imports = new List<string>();
            if (module == null) return imports;

            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(module))
            {
                ImportStatement import = node as ImportStatement;
                if (import != null)
                {
                    foreach (ImportStatement.ImportedName name in import.Names)
                        if (name != null && name.Name != null) imports.Add(name.Name.AsString);
                    continue;
                }
                FromImportStatement from = node as FromImportStatement;
                if (from != null)
                {
                    if (from.Module != null) imports.Add(from.Module.AsString);
                    else
                        // `from . import sibling` names the module in the import list, not the module slot.
                        foreach (KeyValuePair<string, string> name in from.Names) imports.Add(name.Key);
                }
            }
            return imports;
        }
    }

    /// <summary>CR-XF-001 — two modules in the submission import each other.</summary>
    internal sealed class ImportCycleRule : ICrossFileReviewRule
    {
        public string RuleId { get { return "CR-XF-001"; } }
        public string Title { get { return "Import cycle between modules"; } }
        public ReviewCategory Category { get { return ReviewCategory.Design; } }

        public void Review(ReviewSolutionContext context, List<ReviewFinding> output)
        {
            // Only modules in THIS submission can be judged: an edge to a module that was not
            // submitted is unknown, not absent, and a cycle claimed through one would be a guess.
            var byModule = new Dictionary<string, ReviewContext>(StringComparer.Ordinal);
            var byQualified = new Dictionary<string, ReviewContext>(StringComparer.Ordinal);
            foreach (ReviewContext file in context.Files)
            {
                string simple = CrossFileRuleSet.ModuleNameOf(file.FilePath);
                if (simple.Length > 0 && !byModule.ContainsKey(simple)) byModule[simple] = file;
                string qualified = CrossFileRuleSet.QualifiedModuleOf(file.FilePath);
                if (qualified.Length > 0 && !byQualified.ContainsKey(qualified)) byQualified[qualified] = file;
            }

            var edges = new Dictionary<ReviewContext, HashSet<ReviewContext>>();
            foreach (ReviewContext file in context.Files)
            {
                var targets = new HashSet<ReviewContext>();
                foreach (string import in CrossFileRuleSet.ImportedModules(file.Module))
                {
                    ReviewContext target = Resolve(import, byModule, byQualified);
                    if (target != null && !ReferenceEquals(target, file)) targets.Add(target);
                }
                edges[file] = targets;
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (ReviewContext file in context.Files)
            {
                HashSet<ReviewContext> targets;
                if (!edges.TryGetValue(file, out targets)) continue;

                foreach (ReviewContext target in targets)
                {
                    HashSet<ReviewContext> back;
                    if (!edges.TryGetValue(target, out back) || !back.Contains(file)) continue;

                    string a = CrossFileRuleSet.ModuleNameOf(file.FilePath);
                    string b = CrossFileRuleSet.ModuleNameOf(target.FilePath);
                    string key = string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
                    if (!reported.Add(key)) continue;

                    output.Add(file.Report(file.Module, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "`" + a + "` and `" + b + "` import each other.",
                        "A cycle means neither module can be understood, tested or reused without the other, " +
                        "and the two are really one component wearing two names. It is also fragile at import " +
                        "time: whichever module is loaded first sees the other only half-initialised, so the " +
                        "failure is an AttributeError on a name that plainly exists.",
                        "Move what they share into a third module that both import, or invert one direction by " +
                        "passing the dependency in rather than importing it.",
                        ReviewEffort.Large));
                }
            }
        }

        private static ReviewContext Resolve(string import,
            Dictionary<string, ReviewContext> byModule, Dictionary<string, ReviewContext> byQualified)
        {
            if (string.IsNullOrEmpty(import)) return null;
            ReviewContext found;
            if (byQualified.TryGetValue(import, out found)) return found;

            string last = ReviewHelpers.LastSegment(import);
            if (byModule.TryGetValue(last, out found)) return found;

            // `package.module` where only the tail was submitted.
            int dot = import.IndexOf('.');
            if (dot > 0)
            {
                string tail = import.Substring(dot + 1);
                if (byQualified.TryGetValue(tail, out found)) return found;
            }
            return null;
        }
    }

    /// <summary>CR-XF-002 — the same block of statements in two places.</summary>
    internal sealed class DuplicatedBlockRule : ICrossFileReviewRule
    {
        public string RuleId { get { return "CR-XF-002"; } }
        public string Title { get { return "Duplicated block of code"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        private sealed class Occurrence
        {
            public ReviewContext File;
            public int StartLine;
            public int EndLine;
        }

        public void Review(ReviewSolutionContext context, List<ReviewFinding> output)
        {
            int window = context.Options.DuplicateBlockStatements;
            if (window < 2) return;

            var seen = new Dictionary<string, Occurrence>(StringComparer.Ordinal);

            foreach (ReviewContext file in context.Files)
            {
                foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(file.Module))
                {
                    SuiteSyntax suite = node as SuiteSyntax;
                    if (suite == null) continue;
                    IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(suite);
                    if (statements.Count < window) continue;

                    // One fingerprint per statement, reused by every window that contains it.
                    var fingerprints = new string[statements.Count];
                    for (int i = 0; i < statements.Count; i++)
                        fingerprints[i] = ReviewHelpers.StructuralFingerprint(statements[i]);

                    for (int start = 0; start + window <= statements.Count; start++)
                    {
                        if (!IsSubstantial(statements, start, window)) continue;

                        var builder = new StringBuilder();
                        for (int i = start; i < start + window; i++) builder.Append(fingerprints[i]).Append('|');
                        string key = builder.ToString();

                        var here = new Occurrence
                        {
                            File = file,
                            StartLine = statements[start].Span.Start.Line,
                            EndLine = statements[start + window - 1].Span.End.Line,
                        };

                        Occurrence first;
                        if (!seen.TryGetValue(key, out first)) { seen[key] = here; continue; }
                        // Overlapping windows inside one run are the same duplication reported twice.
                        if (ReferenceEquals(first.File, file) && here.StartLine <= first.EndLine) continue;

                        string where = ReferenceEquals(first.File, file)
                            ? "line " + first.StartLine + " of this file"
                            : System.IO.Path.GetFileName(first.File.FilePath ?? string.Empty) + ":" + first.StartLine;

                        output.Add(file.ReportAt(here.StartLine, here.EndLine, 1, RuleId, Title, Category,
                            ReviewSeverity.Minor, ReviewConfidence.Medium,
                            "These " + window + " statements repeat the block at " + where + ".",
                            "The copies will not be found together when one of them needs a fix — a bug fixed " +
                            "here stays live over there, and the two slowly diverge until it is no longer " +
                            "obvious they were ever the same. Matching is structural, so a rename does not " +
                            "hide it.",
                            "Extract the block into a function both places call. Where the two differ slightly, " +
                            "the difference is usually the parameter it should take.",
                            ReviewEffort.Moderate));

                        start += window - 1;   // do not re-report the tail of the same run
                    }
                }
            }
        }

        // A run of imports, or of `pass`, matches another run of imports and says nothing.
        private static bool IsSubstantial(IReadOnlyList<StatementSyntax> statements, int start, int window)
        {
            for (int i = start; i < start + window; i++)
            {
                StatementSyntax s = statements[i];
                if (s is ImportStatement || s is FromImportStatement || s is PassStatement) continue;
                if (s is ExpressionStatement && ((ExpressionStatement)s).Expression is LiteralExpression) continue;
                return true;
            }
            return false;
        }
    }

    /// <summary>CR-XF-003 — a public type no test in the submission mentions.</summary>
    internal sealed class UntestedPublicTypeRule : ICrossFileReviewRule
    {
        public string RuleId { get { return "CR-XF-003"; } }
        public string Title { get { return "Public class is not referenced by any test"; } }
        public ReviewCategory Category { get { return ReviewCategory.Testing; } }

        public void Review(ReviewSolutionContext context, List<ReviewFinding> output)
        {
            // With no tests in the submission the answer would be "nothing is tested", which is
            // not a review comment about any particular class. Say nothing rather than everything.
            var testNames = new HashSet<string>(StringComparer.Ordinal);
            bool anyTests = false;
            foreach (ReviewContext file in context.Files)
            {
                if (!file.IsTestFile) continue;
                anyTests = true;
                foreach (string name in ReviewHelpers.NamesIn(file.Module)) testNames.Add(name);
                // Public APIs are commonly tested through an imported package alias (`bm.Certify`,
                // `np.ndarray`). NamesIn records `bm` but the member is the type evidence this rule
                // needs; ignoring it produced a finding for every facade-exported class.
                foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(file.Module))
                {
                    var attribute = node as AttributeExpression;
                    if (attribute != null && !string.IsNullOrEmpty(attribute.Name))
                        testNames.Add(attribute.Name);
                }
            }
            if (!anyTests) return;

            foreach (ReviewContext file in context.Files)
            {
                if (file.IsTestFile) continue;
                foreach (ReviewClass cls in file.Classes)
                {
                    if (!ReviewHelpers.IsPublicName(cls.Name)) continue;
                    if (testNames.Contains(cls.Name)) continue;
                    // A type with no behaviour of its own does not need its own test.
                    if (cls.Methods.Count == 0) continue;

                    output.Add(file.Report(cls.Node, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.Low,
                        "`" + cls.Name + "` is public with " + cls.Methods.Count +
                        " method(s), and no submitted test mentions it by name.",
                        "This is a name-level check, not coverage: the class may well be exercised indirectly. " +
                        "What it does say is that no test names this type, so nothing pins its behaviour " +
                        "specifically — a change to it fails somewhere else, or nowhere.",
                        "Add a test that constructs it and asserts the behaviour its callers depend on. If it " +
                        "is genuinely internal, marking it `_" + cls.Name + "` says so.",
                        ReviewEffort.Moderate));
                }
            }
        }
    }
}
