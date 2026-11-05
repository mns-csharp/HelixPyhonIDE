using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// TOML completion for <c>*.toml</c> / <c>pyproject.toml</c> (FR-CORE-07) — the TOML counterpart to
    /// <see cref="CMakeCompletionEngine"/>. Context-aware: at a line beginning with <c>[</c> it offers the
    /// standard pyproject table headers; inside <c>[project]</c> it offers the PEP-621 keys (with docs),
    /// inside <c>[build-system]</c> the build keys, and inside <c>[tool.helixstudio]</c> the tool keys.
    /// </summary>
    public sealed class TomlCompletionEngine : ICompletionEngine
    {
        // (key, documentation) — PEP 621 [project] fields.
        private static readonly (string key, string doc)[] Pep621Keys =
        {
            ("name", "Distribution name (required). e.g. \"my-package\"."),
            ("version", "Project version (required unless in [project].dynamic). e.g. \"1.0.0\"."),
            ("description", "One-line summary of the project."),
            ("readme", "Path to the README (or an inline table with file/text + content-type)."),
            ("requires-python", "Python version constraint, e.g. \">=3.10\"."),
            ("license", "SPDX license expression string, or a table with file/text."),
            ("license-files", "Array of glob patterns for license files."),
            ("authors", "Array of tables: { name = \"...\", email = \"...\" }."),
            ("maintainers", "Array of maintainer tables (same shape as authors)."),
            ("keywords", "Array of keyword strings for discovery."),
            ("classifiers", "Array of Trove classifier strings."),
            ("urls", "Table of label -> URL (Homepage, Repository, Documentation, …)."),
            ("scripts", "Table of console-script name -> \"module:function\"."),
            ("gui-scripts", "Table of GUI-script name -> \"module:function\"."),
            ("entry-points", "Table of entry-point groups."),
            ("dependencies", "Array of PEP 508 requirement strings, e.g. [\"numpy>=1.24\"]."),
            ("optional-dependencies", "Table of extra-name -> array of PEP 508 requirements."),
            ("dynamic", "Array of field names computed by the build backend at build time."),
        };

        private static readonly (string key, string doc)[] BuildSystemKeys =
        {
            ("requires", "Array of build-time requirements, e.g. [\"setuptools>=61\"]."),
            ("build-backend", "The build backend, e.g. \"setuptools.build_meta\" or \"hatchling.build\"."),
            ("backend-path", "Array of paths added to sys.path for an in-tree backend."),
        };

        private static readonly (string key, string doc)[] ToolHelixstudioKeys =
        {
            ("kind", "Project template: \"app\", \"library\", \"flask\", \"gui\", \"cuda-exe\", \"cuda-lib\"."),
            ("interface-style", "OOP interface style: \"protocol\" or \"abc\"."),
            ("gui-framework", "GUI toolkit for a gui project: tkinter / pyqt6 / pyside6 / wxpython."),
        };

        // Standard top-level tables offered when typing a header.
        private static readonly (string name, string doc)[] Tables =
        {
            ("project", "PEP 621 project metadata table."),
            ("build-system", "PEP 518 build-system requirements."),
            ("tool", "Tool-specific configuration namespace."),
            ("tool.helixstudio", "HelixStudio project settings."),
            ("tool.uv.workspace", "uv workspace members."),
            ("dependency-groups", "PEP 735 dependency groups."),
        };

        public CompletionContext Classify(string source, int caretOffset)
        {
            var ctx = new CompletionContext
            {
                Kind = CompletionContextKind.General,
                Prefix = string.Empty,
                MemberBase = string.Empty,
                PrefixStart = caretOffset,
            };
            if (source == null) return ctx;
            int i = Math.Max(0, Math.Min(caretOffset, source.Length));
            int s = i;
            while (s > 0 && IsKeyChar(source[s - 1])) s--;
            ctx.Prefix = source.Substring(s, i - s);
            ctx.PrefixStart = s;
            return ctx;
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution)
        {
            return GetCompletions(source, caretOffset, filePath, solution, Classify(source, caretOffset));
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution, CompletionContext ctx)
        {
            var items = new List<CompletionItem>();
            if (source == null) source = string.Empty;

            // Typing a table header ("[..." on the current line) → offer table names.
            if (LineStartsWithBracket(source, ctx.PrefixStart))
            {
                foreach (var t in Tables)
                    items.Add(new CompletionItem(t.name, CompletionKind.Class, t.name, "table [" + t.name + "]", t.doc));
                return items;
            }

            string table = EnclosingTable(source, caretOffset);
            if (Eq(table, "project")) AddKeys(items, Pep621Keys);
            else if (Eq(table, "build-system")) AddKeys(items, BuildSystemKeys);
            else if (Eq(table, "tool.helixstudio")) AddKeys(items, ToolHelixstudioKeys);
            else
            {
                // Unknown / no table: offer both the header tables and the PEP-621 keys (common case at top).
                foreach (var t in Tables)
                    items.Add(new CompletionItem(t.name, CompletionKind.Class, null, "table [" + t.name + "]", t.doc));
                if (string.IsNullOrEmpty(table)) AddKeys(items, Pep621Keys);
            }
            return items;
        }

        private static void AddKeys(List<CompletionItem> items, (string key, string doc)[] keys)
        {
            foreach (var k in keys)
                items.Add(new CompletionItem(k.key, CompletionKind.Property, null, "key " + k.key, k.doc));
        }

        // The name of the [table] / [[array]] header most recently opened before `pos`, or "" if none.
        private static string EnclosingTable(string source, int pos)
        {
            int p = Math.Min(pos, source.Length);
            // Walk backwards line by line.
            int lineEnd = p;
            while (lineEnd > 0)
            {
                int lineStart = lineEnd;
                while (lineStart > 0 && source[lineStart - 1] != '\n') lineStart--;
                string line = source.Substring(lineStart, lineEnd - lineStart).Trim();
                if (line.StartsWith("[") )
                {
                    int close = line.IndexOf(']');
                    int open = line[1] == '[' ? 2 : 1;
                    if (close > open) return line.Substring(open, close - open).Trim();
                }
                if (lineStart == 0) break;
                lineEnd = lineStart - 1; // move to previous line (skip the '\n')
            }
            return "";
        }

        private static bool LineStartsWithBracket(string source, int prefixStart)
        {
            int ls = prefixStart;
            while (ls > 0 && source[ls - 1] != '\n') ls--;
            int i = ls;
            while (i < source.Length && (source[i] == ' ' || source[i] == '\t')) i++;
            return i < source.Length && source[i] == '[';
        }

        private static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool IsKeyChar(char c) { return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'; }
    }
}
