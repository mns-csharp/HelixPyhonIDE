using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Pure text-surgery for SRS §5.1's "AST-level drag-rearrange": physically moving
    /// a module-level class's source text out of one file and into another (a tree
    /// drag onto a different file row). Deliberately scoped to top-level classes —
    /// a nested class dragged this way is refused rather than guessed at, since
    /// pulling it out of its enclosing class/function body is a different, riskier
    /// operation (indentation, `self`/outer-scope references) this pass does not
    /// attempt.
    /// </summary>
    public static class PythonTypeMover
    {
        public sealed class ExtractResult
        {
            public bool Success;
            public string ErrorMessage;
            public string RemainingSource;
            public string ExtractedText;

            public static ExtractResult Fail(string message)
            {
                return new ExtractResult { Success = false, ErrorMessage = message };
            }
        }

        /// <summary>
        /// Cuts the top-level class <paramref name="typeName"/> (decorators
        /// included) out of <paramref name="source"/>. The class's own text keeps
        /// its exact source formatting; the remaining file is <paramref name="source"/>
        /// with exactly that span removed.
        /// </summary>
        public static ExtractResult ExtractTopLevelClass(string source, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return ExtractResult.Fail("no type name given");
            source = source ?? string.Empty;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch (Exception ex) { return ExtractResult.Fail("could not parse source: " + ex.Message); }
            if (module == null) return ExtractResult.Fail("could not parse source");

            ClassDefSyntax found = null;
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is ClassDefSyntax c && c.Name == typeName) { found = c; break; }
            }
            if (found == null)
                return ExtractResult.Fail("'" + typeName + "' is not a top-level class in this file");

            int start = found.Decorators.Count > 0 ? found.Decorators[0].Span.Start.Offset : found.Span.Start.Offset;
            int end = found.Span.End.Offset;
            // Swallow the class's own trailing line break so removal doesn't leave
            // a stray blank line, and the extracted text ends cleanly for InsertClass.
            while (end < source.Length && (source[end] == '\r' || source[end] == '\n')) end++;

            string extracted = source.Substring(start, end - start);
            string remaining = source.Remove(start, end - start);
            return new ExtractResult
            {
                Success = true,
                RemainingSource = remaining,
                ExtractedText = extracted,
            };
        }

        /// <summary>
        /// Cuts the top-level (module-scope) function <paramref name="functionName"/> (decorators
        /// included) out of <paramref name="source"/>, for Solution Explorer's function "Delete".
        /// Mirrors <see cref="ExtractTopLevelClass"/> exactly, scoped to functions directly in the
        /// module body — a method of the same name nested inside a class is not a match, since that
        /// is a different node (member delete is a separate operation).
        /// </summary>
        public static ExtractResult ExtractTopLevelFunction(string source, string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return ExtractResult.Fail("no function name given");
            source = source ?? string.Empty;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch (Exception ex) { return ExtractResult.Fail("could not parse source: " + ex.Message); }
            if (module == null) return ExtractResult.Fail("could not parse source");

            FunctionDefSyntax found = null;
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FunctionDefSyntax f && f.Name == functionName) { found = f; break; }
            }
            if (found == null)
                return ExtractResult.Fail("'" + functionName + "' is not a top-level function in this file");

            int start = found.Decorators.Count > 0 ? found.Decorators[0].Span.Start.Offset : found.Span.Start.Offset;
            int end = found.Span.End.Offset;
            while (end < source.Length && (source[end] == '\r' || source[end] == '\n')) end++;

            string extracted = source.Substring(start, end - start);
            string remaining = source.Remove(start, end - start);
            return new ExtractResult
            {
                Success = true,
                RemainingSource = remaining,
                ExtractedText = extracted,
            };
        }

        /// <summary>
        /// Returns true when <paramref name="source"/>'s module body has exactly one top-level
        /// statement (of any kind — a function, a class, an import, a bare expression, ...). Used by
        /// function delete to decide whether removing the target function should delete the whole
        /// file (nothing else would be left) instead of leaving a hollowed-out module — mirrors
        /// TypeService.RemoveType's existing "delete the file when this was the only thing in it"
        /// rule for types, generalized off a real statement count rather than a text heuristic.
        /// </summary>
        public static bool IsOnlyTopLevelStatement(string source)
        {
            try
            {
                ModuleSyntax module = global::HelixPythonNamespace.Python.Python.ParseModule(source ?? string.Empty).Module;
                return module != null && module.Statements.Count <= 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// Cuts a method/property/constructor named <paramref name="methodName"/> (decorators
        /// included) directly out of class <paramref name="className"/> in
        /// <paramref name="source"/>, for Solution Explorer's member "Delete". Only matches a
        /// method declared DIRECTLY on that class — a nested class's method of the same name is a
        /// different node and is never touched.
        /// </summary>
        public static ExtractResult ExtractMethod(string source, string className, string methodName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(methodName))
                return ExtractResult.Fail("no class or method name given");
            source = source ?? string.Empty;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch (Exception ex) { return ExtractResult.Fail("could not parse source: " + ex.Message); }
            if (module == null) return ExtractResult.Fail("could not parse source");

            ClassDefSyntax cls = FindTopLevelClass(module, className);
            if (cls == null || cls.Body == null)
                return ExtractResult.Fail("'" + className + "' is not a top-level class in this file");

            FunctionDefSyntax found = null;
            foreach (StatementSyntax s in cls.Body.Statements)
            {
                if (s is FunctionDefSyntax f && f.Name == methodName) { found = f; break; }
            }
            if (found == null)
                return ExtractResult.Fail("'" + methodName + "' is not a method directly on '" + className + "'");

            int start = found.Decorators.Count > 0 ? found.Decorators[0].Span.Start.Offset : found.Span.Start.Offset;
            int end = found.Span.End.Offset;
            while (end < source.Length && (source[end] == '\r' || source[end] == '\n')) end++;

            string extracted = source.Substring(start, end - start);
            string remaining = source.Remove(start, end - start);
            return new ExtractResult { Success = true, RemainingSource = remaining, ExtractedText = extracted };
        }

        /// <summary>
        /// Locates every assignment statement inside class <paramref name="className"/> that
        /// targets attribute <paramref name="fieldName"/> — both a class-level declaration
        /// (<c>name = ...</c> / <c>name: T = ...</c>) and every <c>self.name = ...</c> inside any
        /// of the class's methods — and, when there is EXACTLY one, cuts that one statement out.
        /// <para/>
        /// A "field" as PythonTypeExtractor synthesizes it has no single authoritative source line
        /// (it can be declared at class level AND/OR assigned in one or more methods, deduplicated
        /// purely by name). Deleting is only safe when there is exactly one real assignment site to
        /// remove; zero or multiple sites fail with an explanatory message instead of guessing which
        /// one the user meant and silently corrupting an unrelated statement. A chained
        /// (<c>x = y = 0</c>) or tuple/list (<c>self.x, self.y = 1, 2</c>) assignment is never
        /// matched even when it targets the field, for the same reason — removing the whole
        /// statement would also delete the sibling target's assignment.
        /// </summary>
        public static ExtractResult TryLocateUniqueFieldAssignment(string source, string className, string fieldName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(fieldName))
                return ExtractResult.Fail("no class or field name given");
            source = source ?? string.Empty;

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch (Exception ex) { return ExtractResult.Fail("could not parse source: " + ex.Message); }
            if (module == null) return ExtractResult.Fail("could not parse source");

            ClassDefSyntax cls = FindTopLevelClass(module, className);
            if (cls == null || cls.Body == null)
                return ExtractResult.Fail("'" + className + "' is not a top-level class in this file");

            List<AssignmentStatement> matches = new List<AssignmentStatement>();
            foreach (StatementSyntax s in cls.Body.Statements)
            {
                if (s is AssignmentStatement asn && IsSoleTargetMatch(asn, fieldName, selfPrefixed: false))
                {
                    matches.Add(asn);
                }
                else if (s is FunctionDefSyntax fn)
                {
                    foreach (SyntaxNode node in fn.Descendants())
                    {
                        if (node is AssignmentStatement selfAsn && IsSoleTargetMatch(selfAsn, fieldName, selfPrefixed: true))
                            matches.Add(selfAsn);
                    }
                }
            }

            if (matches.Count == 0)
                return ExtractResult.Fail("'" + fieldName + "' was not found as a single, unambiguous assignment in '" + className + "'");
            if (matches.Count > 1)
                return ExtractResult.Fail(
                    "'" + fieldName + "' is assigned in " + matches.Count + " places in '" + className +
                    "' — cannot safely delete without knowing which one you mean");

            AssignmentStatement only = matches[0];
            int start = only.Span.Start.Offset;
            int end = only.Span.End.Offset;
            while (end < source.Length && (source[end] == '\r' || source[end] == '\n')) end++;

            string extracted = source.Substring(start, end - start);
            string remaining = source.Remove(start, end - start);
            return new ExtractResult { Success = true, RemainingSource = remaining, ExtractedText = extracted };
        }

        private static ClassDefSyntax FindTopLevelClass(ModuleSyntax module, string className)
        {
            foreach (StatementSyntax s in module.Statements)
                if (s is ClassDefSyntax c && c.Name == className) return c;
            return null;
        }

        // A single, un-chained, non-tuple assignment target naming exactly `fieldName`
        // (or `self.fieldName` when selfPrefixed) — see TryLocateUniqueFieldAssignment's doc for why
        // anything else (chained/tuple) is deliberately excluded.
        private static bool IsSoleTargetMatch(AssignmentStatement asn, string fieldName, bool selfPrefixed)
        {
            if (asn.Targets.Count != 1) return false;
            ExpressionSyntax t = asn.Targets[0];
            if (selfPrefixed)
                return t is AttributeExpression ae && ae.Name == fieldName &&
                       ae.Target is NameExpression baseName && baseName.Name == "self";
            return t is NameExpression ne && ne.Name == fieldName;
        }

        /// <summary>
        /// Appends <paramref name="extractedText"/> (as produced by
        /// <see cref="ExtractTopLevelClass"/>) to <paramref name="targetSource"/>,
        /// separated by a PEP 8 two-blank-line gap. Fails if the target already
        /// declares a top-level class of the same name — Python resolves duplicate
        /// definitions by the last one silently winning, which would delete the
        /// original class's behavior rather than reporting a conflict.
        /// </summary>
        public static ExtractResult InsertClass(string targetSource, string extractedText, string typeName)
        {
            targetSource = targetSource ?? string.Empty;
            if (string.IsNullOrEmpty(extractedText)) return ExtractResult.Fail("nothing to insert");

            if (!string.IsNullOrEmpty(typeName))
            {
                try
                {
                    ModuleSyntax targetModule = global::HelixPythonNamespace.Python.Python.ParseModule(targetSource).Module;
                    if (targetModule != null)
                    {
                        foreach (StatementSyntax s in targetModule.Statements)
                        {
                            if (s is ClassDefSyntax c && c.Name == typeName)
                                return ExtractResult.Fail(
                                    "the target file already defines a class named '" + typeName + "'");
                        }
                    }
                }
                catch { /* an unparsable target is caught by the caller before this point */ }
            }

            string trimmedTarget = targetSource.TrimEnd('\r', '\n');
            string trimmedExtract = extractedText.Trim('\r', '\n');
            string combined = trimmedTarget.Length == 0
                ? trimmedExtract + "\n"
                : trimmedTarget + "\n\n\n" + trimmedExtract + "\n";
            return new ExtractResult { Success = true, ExtractedText = combined };
        }
    }
}
