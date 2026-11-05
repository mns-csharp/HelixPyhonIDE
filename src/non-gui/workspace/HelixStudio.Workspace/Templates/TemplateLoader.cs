using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>
    /// Loads data-driven templates from disk (SRS §1, §10, §14): a template is a directory containing a
    /// <c>template.json</c> manifest. Each entry becomes a <see cref="ProjectTemplate"/> — so users,
    /// organizations, and plugins add templates by dropping a folder, with **no IDE recompile**. File bodies
    /// may be inline (<c>content</c>) or referenced from a sibling file (<c>source</c>). Malformed manifests are
    /// skipped (collected in <see cref="Errors"/>), never thrown, so one bad template can't break discovery.
    /// </summary>
    public sealed class TemplateLoader
    {
        public readonly List<string> Errors = new List<string>();

        /// <summary>Scan <paramref name="root"/> for immediate subdirectories that contain a <c>template.json</c>
        /// (and the root itself if it has one). <paramref name="installedBy"/> labels the source (user/org/plugin).</summary>
        public List<ProjectTemplate> LoadFrom(string root, string installedBy = "user")
        {
            var result = new List<ProjectTemplate>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

            var manifests = new List<string>();
            string here = Path.Combine(root, "template.json");
            if (File.Exists(here)) manifests.Add(here);
            foreach (string dir in SafeDirs(root))
            {
                string m = Path.Combine(dir, "template.json");
                if (File.Exists(m)) manifests.Add(m);
            }

            foreach (string m in manifests)
            {
                try
                {
                    ProjectTemplate t = LoadFile(m, installedBy);
                    if (t != null) result.Add(t);
                }
                catch (Exception ex) { Errors.Add(m + ": " + ex.Message); }
            }
            return result;
        }

        /// <summary>Parse a single <c>template.json</c> into a <see cref="ProjectTemplate"/>.</summary>
        public ProjectTemplate LoadFile(string manifestPath, string installedBy = "user")
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            JObject o = JObject.Parse(File.ReadAllText(manifestPath));

            var t = new ProjectTemplate
            {
                Id = Str(o, "id", Path.GetFileName(dir)),
                Name = Str(o, "name", Path.GetFileName(dir)),
                Description = Str(o, "description", ""),
                Language = Str(o, "language", ""),
                Platform = Str(o, "platform", "Cross-platform"),
                ProjectType = Str(o, "projectType", "Console"),
                Framework = Str(o, "framework", ""),
                Category = Str(o, "category", "Community"),
                Author = Str(o, "author", ""),
                Version = Str(o, "version", "1.0.0"),
                Icon = Str(o, "icon", ""),
                InstalledBy = installedBy,
            };
            if (string.IsNullOrEmpty(t.Id)) throw new InvalidOperationException("template.json missing 'id'/'name'");

            foreach (var tag in Arr(o, "tags")) t.Tags.Add(tag.ToString());
            foreach (var dep in Arr(o, "dependencies")) t.Dependencies.Add(dep.ToString());

            foreach (JToken v in Arr(o, "variables"))
            {
                var vo = v as JObject; if (vo == null) continue;
                t.Variables.Add(new TemplateVariable
                {
                    Name = Str(vo, "name", ""), Prompt = Str(vo, "prompt", ""),
                    Default = Str(vo, "default", ""), Required = Bool(vo, "required", false),
                });
            }

            foreach (JToken p in Arr(o, "configPages"))
            {
                var po = p as JObject; if (po == null) continue;
                var page = new TemplateConfigPage { Title = Str(po, "title", "Options") };
                foreach (JToken f in Arr(po, "fields"))
                {
                    var fo = f as JObject; if (fo == null) continue;
                    var field = new TemplateConfigField
                    {
                        Name = Str(fo, "name", ""), Label = Str(fo, "label", ""),
                        Type = ParseType(Str(fo, "type", "text")), Default = Str(fo, "default", ""),
                        Required = Bool(fo, "required", false),
                    };
                    field.BoolTrue = Str(fo, "boolTrue", field.BoolTrue);
                    field.BoolFalse = Str(fo, "boolFalse", field.BoolFalse);
                    foreach (var ch in Arr(fo, "choices")) field.Choices.Add(ch.ToString());
                    page.Fields.Add(field);
                }
                t.ConfigPages.Add(page);
            }

            foreach (JToken f in Arr(o, "files"))
            {
                var fo = f as JObject; if (fo == null) continue;
                string rel = Str(fo, "path", "");
                if (string.IsNullOrEmpty(rel)) continue;
                string content;
                string source = Str(fo, "source", null);
                if (!string.IsNullOrEmpty(source))
                {
                    string src = Path.Combine(dir, source);
                    if (!File.Exists(src)) throw new FileNotFoundException("template file source not found: " + source);
                    content = File.ReadAllText(src);
                }
                else content = Str(fo, "content", "");
                t.Files.Add(new TemplateFile { RelativePath = rel, Content = content, Executable = Bool(fo, "executable", false) });
            }

            return t;
        }

        // ---- helpers ----
        private static ConfigFieldType ParseType(string s)
        {
            switch ((s ?? "text").ToLowerInvariant())
            {
                case "bool": case "boolean": case "checkbox": return ConfigFieldType.Bool;
                case "choice": case "select": case "combo": return ConfigFieldType.Choice;
                default: return ConfigFieldType.Text;
            }
        }
        private static string Str(JObject o, string key, string def)
        {
            JToken t; return o.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out t) && t.Type != JTokenType.Null ? t.ToString() : def;
        }
        private static bool Bool(JObject o, string key, bool def)
        {
            JToken t; if (!o.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out t)) return def;
            bool b; return bool.TryParse(t.ToString(), out b) ? b : def;
        }
        private static IEnumerable<JToken> Arr(JObject o, string key)
        {
            JToken t; if (o.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out t) && t is JArray) return (JArray)t;
            return new JToken[0];
        }
        private static IEnumerable<string> SafeDirs(string p) { try { return Directory.GetDirectories(p); } catch { return new string[0]; } }
    }
}
