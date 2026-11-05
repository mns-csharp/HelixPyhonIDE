namespace HelixPythonNamespace.CodeReview
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>One <c>def</c>, with the context a reviewer needs to judge it.</summary>
    public sealed class ReviewFunction
    {
        public FunctionDefSyntax Node;
        /// <summary>The class that owns this def, or null for a module-level function.</summary>
        public ClassDefSyntax OwnerClass;
        /// <summary>The enclosing def, for a closure/nested helper; null otherwise.</summary>
        public FunctionDefSyntax OwnerFunction;
        /// <summary>Dotted name — <c>Class.method</c>, <c>outer.inner</c>, or just the name.</summary>
        public string QualifiedName;
        public int Line;

        public bool IsMethod { get { return OwnerClass != null; } }
        public string Name { get { return Node != null ? Node.Name : string.Empty; } }
    }

    /// <summary>One <c>class</c>, with its own methods already collected.</summary>
    public sealed class ReviewClass
    {
        public ClassDefSyntax Node;
        public string Name;
        public int Line;
        public readonly List<FunctionDefSyntax> Methods = new List<FunctionDefSyntax>();
    }

    /// <summary>
    /// Everything a per-file rule sees: the source, its parse tree, and the pre-walked
    /// function/class tables so forty rules do not each re-walk the module.
    ///
    /// Rules never construct <see cref="ReviewFinding"/>s directly — <see cref="Report"/> fills in
    /// the file, the snippet, the owning member and whether the line is inside the reviewed
    /// change, so no rule can accidentally emit a finding that the diff scope cannot place.
    /// </summary>
    public sealed class ReviewContext
    {
        public readonly ReviewSource Source;
        public readonly ModuleSyntax Module;
        public readonly ReviewOptions Options;

        /// <summary>The file split into lines, 0-based (line N is <c>Lines[N - 1]</c>).</summary>
        public readonly string[] Lines;

        /// <summary>Every def in the file, module-level and nested, in source order.</summary>
        public readonly List<ReviewFunction> Functions = new List<ReviewFunction>();

        /// <summary>Every class in the file, in source order.</summary>
        public readonly List<ReviewClass> Classes = new List<ReviewClass>();

        /// <summary>True when the path looks like test code — several rules invert there.</summary>
        public readonly bool IsTestFile;

        /// <summary>
        /// Every name this module binds by importing it — module aliases and imported symbols
        /// alike. Rules that reason about OBJECTS need this: `np.zeros(...)` and `Order(...)` are
        /// attribute accesses on a name, but neither is a collaborator you can move a method onto,
        /// and treating them as one was measured producing 154 of 282 findings on a real corpus.
        /// </summary>
        public readonly HashSet<string> ImportedNames = new HashSet<string>(StringComparer.Ordinal);

        public string FilePath { get { return Source != null ? Source.FilePath : null; } }
        public string Text { get { return Source != null ? (Source.Text ?? string.Empty) : string.Empty; } }

        // Comments are trivia: the parser keeps them out of the tree, so the two rules that judge
        // them (review-debt markers, commented-out code) re-lex the file. Done once, lazily, and
        // shared — scanning the raw lines for '#' instead would mistake every '#' inside a string
        // literal for a comment.
        private List<HelixPythonNamespace.Python.Syntax.Token> _comments;

        public IReadOnlyList<HelixPythonNamespace.Python.Syntax.Token> Comments
        {
            get
            {
                if (_comments != null) return _comments;
                _comments = new List<HelixPythonNamespace.Python.Syntax.Token>();
                try
                {
                    foreach (HelixPythonNamespace.Python.Syntax.Token token in HelixPythonNamespace.Python.Python.Tokenize(Text, FilePath))
                        if (token.Kind == HelixPythonNamespace.Python.Syntax.TokenKind.Comment) _comments.Add(token);
                }
                catch { /* a file the lexer cannot finish contributes no comment findings */ }
                return _comments;
            }
        }

        public ReviewContext(ReviewSource source, ModuleSyntax module, ReviewOptions options)
        {
            Source = source ?? new ReviewSource();
            Module = module;
            Options = options ?? ReviewOptions.Default;
            Lines = (Source.Text ?? string.Empty).Split('\n');
            for (int i = 0; i < Lines.Length; i++) Lines[i] = Lines[i].TrimEnd('\r');
            IsTestFile = ReviewHelpers.LooksLikeTestPath(Source.FilePath);
            if (module != null)
            {
                Collect(module.Statements, null, null, string.Empty);
                CollectImportedNames(module);
            }
        }

        private void CollectImportedNames(ModuleSyntax module)
        {
            foreach (SyntaxNode node in module.Descendants())
            {
                ImportStatement import = node as ImportStatement;
                if (import != null)
                {
                    foreach (ImportStatement.ImportedName imported in import.Names)
                    {
                        if (imported == null) continue;
                        // `import a.b.c` binds `a`; `import a.b as ab` binds `ab`.
                        if (!string.IsNullOrEmpty(imported.Alias)) { ImportedNames.Add(imported.Alias); continue; }
                        if (imported.Name != null && imported.Name.Parts.Count > 0)
                            ImportedNames.Add(imported.Name.Parts[0]);
                    }
                    continue;
                }
                FromImportStatement from = node as FromImportStatement;
                if (from == null) continue;
                foreach (KeyValuePair<string, string> name in from.Names)
                    ImportedNames.Add(string.IsNullOrEmpty(name.Value) ? name.Key : name.Value);
            }
        }

        private void Collect(IReadOnlyList<StatementSyntax> statements, ClassDefSyntax ownerClass,
            FunctionDefSyntax ownerFunction, string prefix)
        {
            if (statements == null) return;
            foreach (StatementSyntax s in statements)
            {
                FunctionDefSyntax fn = s as FunctionDefSyntax;
                if (fn != null)
                {
                    string qualified = prefix.Length == 0 ? fn.Name : prefix + "." + fn.Name;
                    Functions.Add(new ReviewFunction
                    {
                        Node = fn,
                        OwnerClass = ownerClass,
                        OwnerFunction = ownerFunction,
                        QualifiedName = qualified,
                        Line = fn.Span.Start.Line,
                    });
                    if (ownerClass != null)
                    {
                        ReviewClass owner = FindClass(ownerClass);
                        if (owner != null) owner.Methods.Add(fn);
                    }
                    // A nested def's owner class is the def, not the outer class: a closure inside a
                    // method is not itself a method, and rules that special-case `self` must not
                    // treat it as one.
                    if (fn.Body != null) Collect(fn.Body.Statements, null, fn, qualified);
                    continue;
                }

                ClassDefSyntax cls = s as ClassDefSyntax;
                if (cls != null)
                {
                    string qualified = prefix.Length == 0 ? cls.Name : prefix + "." + cls.Name;
                    Classes.Add(new ReviewClass { Node = cls, Name = cls.Name, Line = cls.Span.Start.Line });
                    if (cls.Body != null) Collect(cls.Body.Statements, cls, ownerFunction, qualified);
                }
            }
        }

        private ReviewClass FindClass(ClassDefSyntax node)
        {
            for (int i = Classes.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Classes[i].Node, node)) return Classes[i];
            return null;
        }

        /// <summary>The text of a 1-based line, trimmed; empty when out of range.</summary>
        public string LineText(int line)
        {
            if (line < 1 || line > Lines.Length) return string.Empty;
            return Lines[line - 1].Trim();
        }

        /// <summary>The dotted member that encloses a line, or empty at module level.</summary>
        public string MemberAt(int line)
        {
            string best = string.Empty;
            int bestStart = -1;
            foreach (ReviewFunction f in Functions)
            {
                if (f.Node == null) continue;
                int start = f.Node.Span.Start.Line, end = f.Node.Span.End.Line;
                if (line < start || line > end) continue;
                if (start > bestStart) { bestStart = start; best = f.QualifiedName; }
            }
            if (best.Length > 0) return best;
            foreach (ReviewClass c in Classes)
            {
                if (c.Node == null) continue;
                if (line >= c.Node.Span.Start.Line && line <= c.Node.Span.End.Line) return c.Name;
            }
            return string.Empty;
        }

        /// <summary>
        /// Builds a finding anchored at <paramref name="node"/>. Every positional and contextual
        /// field is derived here so a rule can only get the judgement wrong, never the location.
        /// </summary>
        public ReviewFinding Report(SyntaxNode node, string ruleId, string title, ReviewCategory category,
            ReviewSeverity severity, ReviewConfidence confidence, string message, string rationale,
            string suggestion, ReviewEffort effort = ReviewEffort.Small)
        {
            int line = node != null ? node.Span.Start.Line : 1;
            int endLine = node != null ? node.Span.End.Line : line;
            int column = node != null ? node.Span.Start.Column : 1;
            return ReportAt(line, endLine, column, ruleId, title, category, severity, confidence,
                message, rationale, suggestion, effort);
        }

        /// <summary>As <see cref="Report"/>, for a rule whose evidence is raw text rather than a node.</summary>
        public ReviewFinding ReportAt(int line, int endLine, int column, string ruleId, string title,
            ReviewCategory category, ReviewSeverity severity, ReviewConfidence confidence, string message,
            string rationale, string suggestion, ReviewEffort effort = ReviewEffort.Small)
        {
            if (line < 1) line = 1;
            if (endLine < line) endLine = line;
            return new ReviewFinding
            {
                RuleId = ruleId,
                Title = title,
                Category = category,
                Severity = severity,
                Confidence = confidence,
                Effort = effort,
                FilePath = Source.FilePath,
                Line = line,
                EndLine = endLine,
                Column = column < 1 ? 1 : column,
                MemberName = MemberAt(line),
                Message = message,
                Rationale = rationale,
                Suggestion = suggestion,
                Snippet = LineText(line),
                InReviewedLines = Source.IsChanged(line),
            };
        }
    }
}
