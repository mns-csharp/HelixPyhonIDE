namespace HelixPythonNamespace.Metrics
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Python;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// AST-based metrics computation. Pure — no subprocess. Walks the
    /// HelixPythonNamespace.Python CST for each input source and emits per-file, per-class,
    /// and per-function records. Port of the C++ <c>helix::metrics::MetricsEngine</c>.
    /// </summary>
    public sealed class MetricsEngine
    {
        public MetricsReport Analyze(IReadOnlyList<MetricsSource> sources, CancellationToken cancel = default(CancellationToken))
        {
            MetricsReport report = new MetricsReport();
            if (sources == null)
            {
                return report;
            }

            foreach (MetricsSource src in sources)
            {
                if (cancel.IsCancellationRequested)
                {
                    break;
                }
                FileMetric fm = new FileMetric { FilePath = src.FilePath };
                ScanLineCounts(src.Text ?? string.Empty, fm);

                ParseResult parsed = Python.ParseModule(src.Text ?? string.Empty, src.FilePath);
                foreach (StatementSyntax s in parsed.Module.Statements)
                {
                    FunctionDefSyntax fn = s as FunctionDefSyntax;
                    if (fn != null)
                    {
                        CollectFromFunction(fn, src.FilePath, string.Empty, report.Functions, report.Classes, fm);
                        continue;
                    }
                    ClassDefSyntax cls = s as ClassDefSyntax;
                    if (cls != null)
                    {
                        CollectFromClass(cls, src.FilePath, report.Functions, report.Classes, fm);
                    }
                }

                report.Files.Add(fm);
            }
            return report;
        }

        // ---- cyclomatic complexity + nesting ----

        // Counts every And/Or BinaryExpression node within the tree rooted at node
        // (inclusive), added into cyclomatic complexity.
        private static int CountBoolOps(SyntaxNode node)
        {
            if (node == null)
            {
                return 0;
            }
            int n = 0;
            if (IsBoolOp(node))
            {
                n++;
            }
            foreach (SyntaxNode d in node.Descendants())
            {
                if (IsBoolOp(d))
                {
                    n++;
                }
            }
            return n;
        }

        private static bool IsBoolOp(SyntaxNode node)
        {
            BinaryExpression be = node as BinaryExpression;
            return be != null && (be.Operator == BinaryOperator.And || be.Operator == BinaryOperator.Or);
        }

        private static int ComputeBranchCount(IReadOnlyList<StatementSyntax> body, ref int maxNesting, int currentDepth)
        {
            int n = 0;
            if (body == null)
            {
                return 0;
            }
            foreach (StatementSyntax s in body)
            {
                n += ComputeBranchCount(s, ref maxNesting, currentDepth);
            }
            return n;
        }

        private static int ComputeBranchCount(StatementSyntax stmt, ref int maxNesting, int depth)
        {
            int n = 0;
            if (stmt is IfStatement iff)
            {
                n += 1;
                if (iff.Test != null) n += CountBoolOps(iff.Test);
                int newDepth = depth + 1;
                if (newDepth > maxNesting) maxNesting = newDepth;
                if (iff.Body != null) n += ComputeBranchCount(iff.Body.Statements, ref maxNesting, newDepth);
                foreach (IfStatement elf in iff.Elifs)
                {
                    n += 1;
                    if (elf.Test != null) n += CountBoolOps(elf.Test);
                    if (elf.Body != null) n += ComputeBranchCount(elf.Body.Statements, ref maxNesting, newDepth);
                }
                if (iff.Else != null) n += ComputeBranchCount(iff.Else.Statements, ref maxNesting, newDepth);
            }
            else if (stmt is WhileStatement w)
            {
                n += 1;
                if (w.Test != null) n += CountBoolOps(w.Test);
                int newDepth = depth + 1;
                if (newDepth > maxNesting) maxNesting = newDepth;
                if (w.Body != null) n += ComputeBranchCount(w.Body.Statements, ref maxNesting, newDepth);
                if (w.Else != null) n += ComputeBranchCount(w.Else.Statements, ref maxNesting, newDepth);
            }
            else if (stmt is ForStatement fo)
            {
                n += 1;
                int newDepth = depth + 1;
                if (newDepth > maxNesting) maxNesting = newDepth;
                if (fo.Body != null) n += ComputeBranchCount(fo.Body.Statements, ref maxNesting, newDepth);
                if (fo.Else != null) n += ComputeBranchCount(fo.Else.Statements, ref maxNesting, newDepth);
            }
            else if (stmt is TryStatement tr)
            {
                int newDepth = depth + 1;
                if (newDepth > maxNesting) maxNesting = newDepth;
                if (tr.Body != null) n += ComputeBranchCount(tr.Body.Statements, ref maxNesting, newDepth);
                foreach (ExceptHandler h in tr.Handlers)
                {
                    n += 1; // each except clause is a branching path
                    if (h.Body != null) n += ComputeBranchCount(h.Body.Statements, ref maxNesting, newDepth);
                }
                if (tr.Else != null) n += ComputeBranchCount(tr.Else.Statements, ref maxNesting, newDepth);
                if (tr.Finally != null) n += ComputeBranchCount(tr.Finally.Statements, ref maxNesting, newDepth);
            }
            else if (stmt is WithStatement wi)
            {
                int newDepth = depth + 1;
                if (newDepth > maxNesting) maxNesting = newDepth;
                if (wi.Body != null) n += ComputeBranchCount(wi.Body.Statements, ref maxNesting, newDepth);
            }
            // Nested FunctionDef / ClassDef get their own records — don't recurse here.
            return n;
        }

        // ---- statement counting ----

        private static int CountStatements(IReadOnlyList<StatementSyntax> body)
        {
            int n = 0;
            if (body == null)
            {
                return 0;
            }
            foreach (StatementSyntax s in body)
            {
                n += CountStatementsIn(s);
            }
            return n;
        }

        private static int CountStatementsIn(StatementSyntax stmt)
        {
            if (stmt is FunctionDefSyntax || stmt is ClassDefSyntax)
            {
                return 1; // independent — counted by their own records
            }
            if (stmt is IfStatement iff)
            {
                int n = 1;
                if (iff.Body != null) n += CountStatements(iff.Body.Statements);
                foreach (IfStatement elf in iff.Elifs)
                {
                    if (elf.Body != null) n += CountStatements(elf.Body.Statements);
                }
                if (iff.Else != null) n += CountStatements(iff.Else.Statements);
                return n;
            }
            if (stmt is WhileStatement w)
            {
                int n = 1;
                if (w.Body != null) n += CountStatements(w.Body.Statements);
                if (w.Else != null) n += CountStatements(w.Else.Statements);
                return n;
            }
            if (stmt is ForStatement fo)
            {
                int n = 1;
                if (fo.Body != null) n += CountStatements(fo.Body.Statements);
                if (fo.Else != null) n += CountStatements(fo.Else.Statements);
                return n;
            }
            if (stmt is TryStatement tr)
            {
                int n = 1;
                if (tr.Body != null) n += CountStatements(tr.Body.Statements);
                foreach (ExceptHandler h in tr.Handlers)
                {
                    if (h.Body != null) n += CountStatements(h.Body.Statements);
                }
                if (tr.Else != null) n += CountStatements(tr.Else.Statements);
                if (tr.Finally != null) n += CountStatements(tr.Finally.Statements);
                return n;
            }
            if (stmt is WithStatement wi)
            {
                int n = 1;
                if (wi.Body != null) n += CountStatements(wi.Body.Statements);
                return n;
            }
            return 1;
        }

        // ---- collectors ----

        private static void CollectFromFunction(
            FunctionDefSyntax fn, string filePath, string qualifierPrefix,
            List<FunctionMetric> funcsOut, List<ClassMetric> classesOut, FileMetric fileMetric)
        {
            FunctionMetric m = new FunctionMetric();
            m.Name = string.IsNullOrEmpty(qualifierPrefix) ? fn.Name : qualifierPrefix + "." + fn.Name;
            m.FilePath = filePath;
            m.Line = fn.Span.Start.Line;
            m.Loc = fn.Span.End.Line - fn.Span.Start.Line + 1;
            m.ParameterCount = fn.Parameters.Count;

            int maxNesting = 0;
            int branches = 0;
            int stmts = 0;
            if (fn.Body != null)
            {
                branches = ComputeBranchCount(fn.Body.Statements, ref maxNesting, 0);
                stmts = CountStatements(fn.Body.Statements);
            }
            m.CyclomaticComplexity = 1 + branches;
            m.MaxNestingDepth = maxNesting;
            m.StatementCount = stmts;

            funcsOut.Add(m);
            fileMetric.FunctionCount++;

            if (fn.Body != null)
            {
                foreach (StatementSyntax s in fn.Body.Statements)
                {
                    FunctionDefSyntax nfn = s as FunctionDefSyntax;
                    if (nfn != null)
                    {
                        CollectFromFunction(nfn, filePath, m.Name, funcsOut, classesOut, fileMetric);
                        continue;
                    }
                    ClassDefSyntax ncls = s as ClassDefSyntax;
                    if (ncls != null)
                    {
                        ClassMetric cm = new ClassMetric();
                        cm.Name = m.Name + "." + ncls.Name;
                        cm.FilePath = filePath;
                        cm.Line = ncls.Span.Start.Line;
                        cm.Loc = ncls.Span.End.Line - ncls.Span.Start.Line + 1;
                        cm.MethodCount = CountMethods(ncls);
                        classesOut.Add(cm);
                        fileMetric.ClassCount++;
                    }
                }
            }
        }

        private static void CollectFromClass(
            ClassDefSyntax cls, string filePath,
            List<FunctionMetric> funcsOut, List<ClassMetric> classesOut, FileMetric fileMetric)
        {
            ClassMetric cm = new ClassMetric();
            cm.Name = cls.Name;
            cm.FilePath = filePath;
            cm.Line = cls.Span.Start.Line;
            cm.Loc = cls.Span.End.Line - cls.Span.Start.Line + 1;

            if (cls.Body != null)
            {
                foreach (StatementSyntax s in cls.Body.Statements)
                {
                    FunctionDefSyntax fn = s as FunctionDefSyntax;
                    if (fn != null)
                    {
                        cm.MethodCount++;
                        CollectFromFunction(fn, filePath, cm.Name, funcsOut, classesOut, fileMetric);
                        continue;
                    }
                    ClassDefSyntax nc = s as ClassDefSyntax;
                    if (nc != null)
                    {
                        ClassMetric nm = new ClassMetric();
                        nm.Name = cm.Name + "." + nc.Name;
                        nm.FilePath = filePath;
                        nm.Line = nc.Span.Start.Line;
                        nm.Loc = nc.Span.End.Line - nc.Span.Start.Line + 1;
                        nm.MethodCount = CountMethods(nc);
                        classesOut.Add(nm);
                        fileMetric.ClassCount++;
                    }
                }
            }
            classesOut.Add(cm);
            fileMetric.ClassCount++;
        }

        private static int CountMethods(ClassDefSyntax cls)
        {
            int n = 0;
            if (cls.Body != null)
            {
                foreach (StatementSyntax cs in cls.Body.Statements)
                {
                    if (cs is FunctionDefSyntax) n++;
                }
            }
            return n;
        }

        // ---- line counting ----

        private static void ScanLineCounts(string text, FileMetric m)
        {
            int lines = 0, blank = 0, comment = 0;
            int start = 0;
            while (start <= text.Length)
            {
                int nl = text.IndexOf('\n', start);
                string line = nl < 0 ? text.Substring(start) : text.Substring(start, nl - start);
                lines++;

                string trimmed = line.Trim();
                if (trimmed.Length == 0) blank++;
                else if (trimmed[0] == '#') comment++;

                if (nl < 0) break;
                start = nl + 1;
            }
            m.TotalLines = lines;
            m.BlankLines = blank;
            m.CommentLines = comment;
            int code = lines - blank - comment;
            m.CodeLines = code < 0 ? 0 : code;
        }
    }
}
