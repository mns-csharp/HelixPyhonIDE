using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>Inputs to a generation run: where to create the project and the variable bindings.</summary>
    public sealed class GenerationOptions
    {
        public string TargetRoot = "";                        // the Location (parent) directory
        public bool Overwrite;                                // allow writing over existing files
        public readonly Dictionary<string, string> Variables = new Dictionary<string, string>();
    }

    /// <summary>Outcome of a generation run (SRS §8).</summary>
    public sealed class GenerationResult
    {
        public bool Success;
        public string Error = "";
        public readonly List<string> CreatedFiles = new List<string>();
        public readonly List<string> CreatedDirs = new List<string>();
    }

    /// <summary>
    /// Renders a <see cref="ProjectTemplate"/> to disk (SRS §8): resolves <c>{{Var}}</c> placeholders in every
    /// file path and body, creates directories and files. **Transactional** (NFR §14): it records every path it
    /// creates and, on any failure — a bad placeholder, an existing file without overwrite, an I/O error —
    /// rolls the whole thing back (deletes the files and the directories it made) so no partial project is left
    /// behind. UI-independent, so it is unit-testable on its own.
    /// </summary>
    public static class ProjectGenerator
    {
        public static GenerationResult Generate(ProjectTemplate template, GenerationOptions opt)
        {
            var r = new GenerationResult();
            if (template == null) { r.Error = "no template"; return r; }
            if (opt == null || string.IsNullOrEmpty(opt.TargetRoot)) { r.Error = "no target location"; return r; }

            // Fill in variable defaults for anything the caller didn't supply (declared variables + config fields).
            var vars = new Dictionary<string, string>(opt.Variables);
            foreach (var v in template.Variables)
                if (!vars.ContainsKey(v.Name)) vars[v.Name] = v.Default ?? "";
            foreach (var kv in template.ConfigDefaults())
                if (!vars.ContainsKey(kv.Key)) vars[kv.Key] = kv.Value;

            // Required-variable validation (SRS §5).
            foreach (var v in template.Variables)
                if (v.Required && string.IsNullOrEmpty(GetOrNull(vars, v.Name)))
                { r.Error = "missing required variable: " + v.Name; return r; }

            string root = Path.GetFullPath(opt.TargetRoot);
            try
            {
                EnsureDir(root, r);   // the Location itself (tracked only if we create it)
                foreach (var f in template.Files)
                {
                    string rel = Substitute(f.RelativePath, vars);
                    if (string.IsNullOrEmpty(rel)) { throw new InvalidOperationException("empty file path after substitution"); }
                    if (rel.IndexOf("{{", StringComparison.Ordinal) >= 0)
                        throw new InvalidOperationException("unresolved placeholder in path: " + rel);
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("path escapes target root: " + rel);

                    EnsureDir(Path.GetDirectoryName(full), r);
                    if (File.Exists(full) && !opt.Overwrite)
                        throw new IOException("file already exists: " + rel);

                    File.WriteAllText(full, Substitute(f.Content, vars), new UTF8Encoding(false));
                    r.CreatedFiles.Add(full);
                }
                r.Success = true;
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                Rollback(r);          // undo everything we created — no partial project (NFR §14)
                r.Success = false;
                return r;
            }
        }

        /// <summary>Replace every <c>{{Name}}</c> with its bound value; unknown tokens are left intact so the
        /// caller/validator can detect them.</summary>
        public static string Substitute(string text, IDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, open - i);
                string key = text.Substring(open + 2, close - (open + 2)).Trim();
                string val;
                if (vars != null && vars.TryGetValue(key, out val)) sb.Append(val);
                else sb.Append("{{").Append(key).Append("}}");   // leave unknown tokens for detection
                i = close + 2;
            }
            return sb.ToString();
        }

        private static void EnsureDir(string dir, GenerationResult r)
        {
            if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;
            // Create the whole missing chain, tracking each newly-created level for rollback.
            var toCreate = new List<string>();
            string cur = dir;
            while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur)) { toCreate.Add(cur); cur = Path.GetDirectoryName(cur); }
            toCreate.Reverse();
            foreach (string d in toCreate) { Directory.CreateDirectory(d); r.CreatedDirs.Add(d); }
        }

        private static void Rollback(GenerationResult r)
        {
            foreach (string f in r.CreatedFiles) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
            // Remove created dirs deepest-first so a parent is only removed after its children.
            var dirs = new List<string>(r.CreatedDirs);
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string d in dirs) { try { if (Directory.Exists(d) && IsEmpty(d)) Directory.Delete(d); } catch { } }
            r.CreatedFiles.Clear();
            r.CreatedDirs.Clear();
        }

        private static bool IsEmpty(string dir)
        {
            try { return Directory.GetFileSystemEntries(dir).Length == 0; } catch { return false; }
        }

        private static string GetOrNull(IDictionary<string, string> d, string k)
        {
            string v; return d.TryGetValue(k, out v) ? v : null;
        }
    }
}
