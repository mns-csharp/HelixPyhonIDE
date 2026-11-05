using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>Everything a consumer file needs to know about one converted module.</summary>
    public sealed class OopModuleConversion
    {
        public OopModuleConversion(string filePath, string dottedPath, string className,
                                   IEnumerable<string> movedFunctions)
        {
            FilePath = filePath != null ? Path.GetFullPath(filePath) : null;
            DottedPath = dottedPath ?? string.Empty;
            ClassName = className ?? string.Empty;
            MovedFunctions = new HashSet<string>(movedFunctions ?? Enumerable.Empty<string>(),
                                                 StringComparer.Ordinal);
        }

        /// <summary>Absolute path of the converted .py file.</summary>
        public string FilePath { get; }

        /// <summary>Importable dotted path, e.g. "pkg.sub.utils" (or "utils" for a root module).</summary>
        public string DottedPath { get; }

        public string ClassName { get; }

        public HashSet<string> MovedFunctions { get; }

        public string ModuleBaseName
        {
            get { return FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : string.Empty; }
        }
    }

    /// <summary>
    /// Rewrites the OTHER files of an imported tree after
    /// <see cref="PythonOopConverter"/> encapsulated a module's functions:
    /// <c>from m import f</c> becomes <c>from m import ClassName</c> with bare
    /// uses requalified to <c>ClassName.f</c>, and attribute references through a
    /// module object or alias (<c>m.f</c>, <c>pkg.m.f</c>) gain the class
    /// segment (<c>m.ClassName.f</c>). Strings and comments are never touched;
    /// unparsable consumers are left unchanged.
    /// </summary>
    public static class PythonOopReferenceRewriter
    {
        public readonly struct ConsumerRewriteResult
        {
            public readonly string Text;
            public readonly int Count;
            public ConsumerRewriteResult(string text, int count) { Text = text; Count = count; }
        }

        /// <summary>
        /// Rewrite one consumer file's references into <paramref name="conv"/>.
        /// <paramref name="consumerFilePath"/> resolves relative imports.
        /// </summary>
        public static ConsumerRewriteResult RewriteConsumer(string source, string consumerFilePath,
                                                            OopModuleConversion conv)
        {
            if (source == null || conv == null || conv.MovedFunctions.Count == 0)
                return new ConsumerRewriteResult(source, 0);

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { module = null; }
            if (module == null) return new ConsumerRewriteResult(source, 0);

            var edits = new List<(int start, int end, string text)>();
            var bareMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var moduleChains = new List<string[]>();

            CollectImports(module, consumerFilePath, conv, source, edits, bareMap, moduleChains);

            // Bare uses introduced by from-imports (and star imports).
            if (bareMap.Count > 0)
                edits.AddRange(PythonOopConverter.ComputeBareReferenceEdits(source, module, bareMap));

            // Attribute chains through module objects/aliases: m.f -> m.ClassName.f
            if (moduleChains.Count > 0)
                edits.AddRange(ComputeChainEdits(source, conv, moduleChains));

            if (edits.Count == 0) return new ConsumerRewriteResult(source, 0);

            string result = PythonOopConverter.ApplyEdits(source, edits);

            // Defensive: never emit a consumer that no longer parses.
            try
            {
                if (global::HelixPythonNamespace.Python.Python.ParseModule(result).Module == null)
                    return new ConsumerRewriteResult(source, 0);
            }
            catch { return new ConsumerRewriteResult(source, 0); }

            return new ConsumerRewriteResult(result, edits.Count);
        }

        // ---- import analysis ----

        private static void CollectImports(ModuleSyntax module, string consumerFilePath,
            OopModuleConversion conv, string source,
            List<(int start, int end, string text)> edits,
            Dictionary<string, string> bareMap, List<string[]> moduleChains)
        {
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FromImportStatement fi)
                {
                    if (fi.IsStar)
                    {
                        if (TargetsConvertedModule(fi, consumerFilePath, conv))
                        {
                            foreach (string f in conv.MovedFunctions)
                                bareMap[f] = conv.ClassName + "." + f;
                        }
                        continue;
                    }

                    if (TargetsConvertedModule(fi, consumerFilePath, conv))
                    {
                        // from m import f, g as h  ->  from m import ClassName (+ survivors)
                        var survivors = new List<string>();
                        bool classAdded = false;
                        bool anyMoved = false;
                        foreach (KeyValuePair<string, string> n in fi.Names)
                        {
                            if (conv.MovedFunctions.Contains(n.Key))
                            {
                                anyMoved = true;
                                string useName = n.Value ?? n.Key;  // alias binds the use sites
                                bareMap[useName] = conv.ClassName + "." + n.Key;
                                if (!classAdded)
                                {
                                    survivors.Add(conv.ClassName);
                                    classAdded = true;
                                }
                            }
                            else
                            {
                                survivors.Add(n.Value != null ? n.Key + " as " + n.Value : n.Key);
                            }
                        }
                        if (anyMoved)
                        {
                            string moduleText = ModuleTextOf(fi);
                            edits.Add((s.Span.Start.Offset, s.Span.End.Offset,
                                "from " + moduleText + " import " + string.Join(", ", survivors.Distinct())));
                        }
                        continue;
                    }

                    // from <parent-pkg> import m [as alias]  ->  module object; uses are m.f
                    foreach (KeyValuePair<string, string> n in fi.Names)
                    {
                        if (ResolvesToConvertedFile(fi, n.Key, consumerFilePath, conv))
                            moduleChains.Add(new[] { n.Value ?? n.Key });
                    }
                }
                else if (s is ImportStatement imp)
                {
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        if (n?.Name == null) continue;
                        if (!string.Equals(n.Name.AsString, conv.DottedPath, StringComparison.Ordinal))
                            continue;
                        moduleChains.Add(n.Alias != null
                            ? new[] { n.Alias }
                            : conv.DottedPath.Split('.'));
                    }
                }
            }
        }

        /// <summary>True when this from-import's module IS the converted module.</summary>
        private static bool TargetsConvertedModule(FromImportStatement fi, string consumerFilePath,
                                                   OopModuleConversion conv)
        {
            if (fi.RelativeDots == 0)
            {
                return fi.Module != null &&
                       string.Equals(fi.Module.AsString, conv.DottedPath, StringComparison.Ordinal);
            }
            if (fi.Module == null) return false;  // 'from . import x' targets a package, not this module
            string resolved = ResolveRelative(consumerFilePath, fi.RelativeDots, fi.Module.Parts);
            return resolved != null && PathsEqual(resolved, conv.FilePath);
        }

        /// <summary>True when import entry <paramref name="name"/> of this from-import resolves to the converted FILE (module-object import).</summary>
        private static bool ResolvesToConvertedFile(FromImportStatement fi, string name,
                                                    string consumerFilePath, OopModuleConversion conv)
        {
            if (!string.Equals(name, conv.ModuleBaseName, StringComparison.Ordinal)) return false;
            if (fi.RelativeDots == 0)
            {
                // from pkg.sub import m  — dotted parent + name must equal the dotted path.
                if (fi.Module == null) return false;
                return string.Equals(fi.Module.AsString + "." + name, conv.DottedPath, StringComparison.Ordinal);
            }
            var parts = new List<string>();
            if (fi.Module != null) parts.AddRange(fi.Module.Parts);
            parts.Add(name);
            string resolved = ResolveRelative(consumerFilePath, fi.RelativeDots, parts);
            return resolved != null && PathsEqual(resolved, conv.FilePath);
        }

        /// <summary>Resolve a PEP 328 relative import to an absolute .py path (null when it leaves the tree).</summary>
        private static string ResolveRelative(string consumerFilePath, int dots, IReadOnlyList<string> parts)
        {
            if (string.IsNullOrEmpty(consumerFilePath)) return null;
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(consumerFilePath));
                for (int i = 1; i < dots; i++)
                {
                    dir = Path.GetDirectoryName(dir);
                    if (dir == null) return null;
                }
                foreach (string p in parts) dir = Path.Combine(dir, p);
                return dir + ".py";
            }
            catch { return null; }
        }

        private static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static string ModuleTextOf(FromImportStatement fi)
        {
            var sb = new StringBuilder();
            sb.Append('.', fi.RelativeDots);
            if (fi.Module != null) sb.Append(fi.Module.AsString);
            return sb.ToString();
        }

        // ---- attribute-chain rewriting ----

        /// <summary>
        /// Token scan for <c>chain.f</c> where <c>chain</c> exactly matches a known
        /// module alias/path and <c>f</c> is a moved function: the <c>f</c> token
        /// becomes <c>ClassName.f</c>.
        /// </summary>
        private static List<(int start, int end, string text)> ComputeChainEdits(
            string source, OopModuleConversion conv, List<string[]> moduleChains)
        {
            var edits = new List<(int start, int end, string text)>();
            IReadOnlyList<Token> tokens;
            try { tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source); }
            catch { return edits; }

            var significant = new List<Token>();
            foreach (Token t in tokens)
            {
                if (t.Kind == TokenKind.Whitespace || t.Kind == TokenKind.Comment ||
                    t.Kind == TokenKind.NewLine || t.Kind == TokenKind.Indent ||
                    t.Kind == TokenKind.Dedent || t.Kind == TokenKind.LineContinuation)
                    continue;
                significant.Add(t);
            }

            for (int i = 0; i < significant.Count; i++)
            {
                Token t = significant[i];
                if (t.Kind != TokenKind.Name || !conv.MovedFunctions.Contains(t.Text)) continue;
                if (i < 1 || significant[i - 1].Kind != TokenKind.Dot) continue;

                // Walk the dotted chain leftward: Name (Dot Name)* directly before the dot.
                var chain = new List<string>();
                int j = i - 1;  // points at the Dot before f
                while (j >= 1 && significant[j].Kind == TokenKind.Dot &&
                       significant[j - 1].Kind == TokenKind.Name)
                {
                    chain.Insert(0, significant[j - 1].Text);
                    j -= 2;
                }
                if (chain.Count == 0) continue;
                // The chain root must not itself be an attribute/call/index result.
                if (j >= 0 && (significant[j].Kind == TokenKind.Dot ||
                               significant[j].Kind == TokenKind.RightParen ||
                               significant[j].Kind == TokenKind.RightBracket))
                    continue;

                bool matches = false;
                foreach (string[] known in moduleChains)
                {
                    if (known.Length == chain.Count && known.SequenceEqual(chain, StringComparer.Ordinal))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches) continue;

                int off = t.Span.Start.Offset;
                int end = t.Span.End.Offset;
                if (off < 0 || end > source.Length || end <= off) continue;
                edits.Add((off, end, conv.ClassName + "." + t.Text));
            }
            return edits;
        }
    }
}
