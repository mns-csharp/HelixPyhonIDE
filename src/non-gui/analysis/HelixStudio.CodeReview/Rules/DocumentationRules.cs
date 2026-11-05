namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// What the code tells its callers about itself. Scoped to PUBLIC API on purpose — demanding
    /// a docstring on every private helper produces noise that trains reviewers to skip the whole
    /// category, and the helper's name is usually documentation enough.
    /// </summary>
    internal static class DocumentationRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new MissingDocstringRule();
            yield return new MissingTypeAnnotationsRule();
            yield return new DocstringParameterMismatchRule();
        }

        /// <summary>A def that forms part of the module's public surface.</summary>
        internal static bool IsPublicApi(ReviewFunction function)
        {
            if (function.OwnerFunction != null) return false;                 // a closure is private by construction
            if (!ReviewHelpers.IsPublicName(function.Name)) return false;
            if (ReviewHelpers.IsDunder(function.Name)) return false;
            if (function.OwnerClass != null && !ReviewHelpers.IsPublicName(function.OwnerClass.Name)) return false;
            return true;
        }
    }

    /// <summary>CR-DOC-001 — a public definition with no docstring.</summary>
    internal sealed class MissingDocstringRule : IReviewRule
    {
        public string RuleId { get { return "CR-DOC-001"; } }
        public string Title { get { return "Public definition has no docstring"; } }
        public ReviewCategory Category { get { return ReviewCategory.Documentation; } }

        /// <summary>Below this, the signature really is the whole story.</summary>
        private const int MinimumStatements = 5;

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.IsTestFile) return;   // a test's name is its documentation

            foreach (ReviewClass cls in context.Classes)
            {
                if (!ReviewHelpers.IsPublicName(cls.Name)) continue;
                if (Docstrings.Of(cls.Node.Body) != null) continue;

                output.Add(context.Report(cls.Node, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.High,
                    "Public class `" + cls.Name + "` has no docstring.",
                    "A class docstring is where the invariant lives — what this type guarantees about itself " +
                    "between calls. Without it every caller has to infer the contract from the implementation, " +
                    "and each infers a slightly different one.",
                    "One sentence on what the type represents, plus anything a caller must not do, is enough.",
                    ReviewEffort.Trivial));
            }

            foreach (ReviewFunction function in context.Functions)
            {
                if (!DocumentationRuleSet.IsPublicApi(function)) continue;
                if (Docstrings.Of(function.Node.Body) != null) continue;
                if (ReviewHelpers.StatementCount(function.Node.Body) < MinimumStatements) continue;
                if (ReviewHelpers.HasDecorator(function.Node, "override", "overrides")) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.High,
                    "Public function `" + function.QualifiedName + "` has no docstring.",
                    "The parts a caller cannot see from the signature are exactly the parts that need writing " +
                    "down: what it does when given nothing, what it raises, whether it mutates its arguments, " +
                    "and whether it is safe to call twice.",
                    "Document the contract rather than the implementation — the body already says how.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-DOC-002 — an unannotated public signature.</summary>
    internal sealed class MissingTypeAnnotationsRule : IReviewRule
    {
        public string RuleId { get { return "CR-DOC-002"; } }
        public string Title { get { return "Public signature has no type annotations"; } }
        public ReviewCategory Category { get { return ReviewCategory.Documentation; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.IsTestFile) return;

            foreach (ReviewFunction function in context.Functions)
            {
                if (!DocumentationRuleSet.IsPublicApi(function)) continue;

                List<ParameterSyntax> parameters = ReviewHelpers.DeclaredParameters(function.Node, function.IsMethod);
                if (parameters.Count == 0 && function.Node.ReturnAnnotation != null) continue;
                if (parameters.Count == 0) continue;

                int annotated = 0;
                foreach (ParameterSyntax p in parameters) if (p.Annotation != null) annotated++;
                // Partially annotated is a different (smaller) problem than not annotated at all;
                // only the second is reported, so the comment is never "you nearly did it".
                if (annotated > 0 || function.Node.ReturnAnnotation != null) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.High,
                    "`" + function.QualifiedName + "` declares " + parameters.Count +
                    " parameter(s) and a return value with no types.",
                    "Annotations are the only part of the contract a tool can check. Without them a caller " +
                    "passing the wrong shape finds out at runtime, in whatever code first touches the value — " +
                    "and the editor cannot offer completion on the result either.",
                    "Annotate the parameters and the return: `def " + function.Name +
                    "(path: str) -> dict:`. Start with the public surface; the internals can follow.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-DOC-003 — the docstring documents a parameter that does not exist.</summary>
    internal sealed class DocstringParameterMismatchRule : IReviewRule
    {
        public string RuleId { get { return "CR-DOC-003"; } }
        public string Title { get { return "Docstring describes a parameter the function does not have"; } }
        public ReviewCategory Category { get { return ReviewCategory.Documentation; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                string docstring = Docstrings.Of(function.Node.Body);
                if (docstring == null) continue;

                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (ParameterSyntax p in function.Node.Parameters) declared.Add(p.Name);
                if (declared.Count == 0) continue;

                foreach (string documented in DocumentedParameterNames(docstring))
                {
                    if (declared.Contains(documented)) continue;

                    output.Add(context.Report(function.Node, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.Medium,
                        "The docstring of `" + function.QualifiedName + "` documents `" + documented +
                        "`, which is not a parameter of this function.",
                        "The signature was changed and the prose was not. A stale docstring is worse than a " +
                        "missing one: it is read as current, so callers pass the argument it describes and " +
                        "get a TypeError — or, worse, believe a behaviour that no longer exists.",
                        "Rename or remove the entry so the docstring matches the signature.",
                        ReviewEffort.Trivial));
                }
            }
        }

        /// <summary>
        /// Parameter names a docstring claims to document. Handles the two conventions that can be
        /// recognised without guessing: Sphinx `:param name:` and a Google-style `Args:` block.
        /// Numpy-style underlined sections are deliberately not parsed — the shape is ambiguous
        /// enough that it would invent names.
        /// </summary>
        internal static IEnumerable<string> DocumentedParameterNames(string docstring)
        {
            string[] lines = docstring.Split('\n');
            bool inArgs = false;
            int argsIndent = -1;

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0) continue;
                int indent = line.Length - trimmed.Length;

                if (trimmed.StartsWith(":param ", StringComparison.Ordinal))
                {
                    int colon = trimmed.IndexOf(':', 7);
                    if (colon > 7)
                    {
                        string name = trimmed.Substring(7, colon - 7).Trim();
                        // ":param int count:" — the type is written first.
                        int space = name.LastIndexOf(' ');
                        if (space >= 0) name = name.Substring(space + 1);
                        if (IsIdentifier(name)) yield return name.TrimStart('*');
                    }
                    continue;
                }

                if (!inArgs)
                {
                    if (string.Equals(trimmed, "Args:", StringComparison.Ordinal) ||
                        string.Equals(trimmed, "Arguments:", StringComparison.Ordinal) ||
                        string.Equals(trimmed, "Parameters:", StringComparison.Ordinal))
                    { inArgs = true; argsIndent = indent; }
                    continue;
                }

                // The block ends at the next section heading or at anything dedented back to it.
                if (indent <= argsIndent) { inArgs = false; continue; }

                int separator = trimmed.IndexOf(':');
                if (separator <= 0) continue;
                string entry = trimmed.Substring(0, separator).Trim();
                int paren = entry.IndexOf('(');           // "count (int): ..."
                if (paren > 0) entry = entry.Substring(0, paren).Trim();
                // `*args` / `**kwargs` are documented with their stars but declared without them.
                if (IsIdentifier(entry)) yield return entry.TrimStart('*');
            }
        }

        private static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (char.IsDigit(text[0])) return false;
            foreach (char c in text)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '*') return false;
            return true;
        }
    }
}
