using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>One template-declared variable substituted during generation (SRS §9 "Variables").</summary>
    public sealed class TemplateVariable
    {
        public string Name = "";          // token substituted as {{Name}}
        public string Prompt = "";        // UI label
        public string Default = "";
        public bool Required;
        public override string ToString() { return "{{" + Name + "}}"; }
    }

    /// <summary>One file the template emits. <see cref="RelativePath"/> and <see cref="Content"/> may both
    /// contain <c>{{Var}}</c> placeholders (SRS §8 "copy template files / placeholder substitution").</summary>
    public sealed class TemplateFile
    {
        public string RelativePath = "";
        public string Content = "";
        public bool Executable;
    }

    /// <summary>The kind of input control a config field renders as (SRS §6).</summary>
    public enum ConfigFieldType { Text, Bool, Choice }

    /// <summary>One field on a template configuration page (SRS §6). Its <see cref="Name"/> becomes a
    /// <c>{{Name}}</c> variable available to file substitution and post-generation actions.</summary>
    public sealed class TemplateConfigField
    {
        public string Name = "";
        public string Label = "";
        public ConfigFieldType Type = ConfigFieldType.Text;
        public string Default = "";
        public bool Required;
        public readonly List<string> Choices = new List<string>();   // Type == Choice

        public string BoolTrue = "true";
        public string BoolFalse = "false";
    }

    /// <summary>A template-declared configuration page (SRS §6): a titled group of fields. A template may
    /// declare zero or more; the wizard renders one dynamic page each, between Configure and Summary.</summary>
    public sealed class TemplateConfigPage
    {
        public string Title = "";
        public readonly List<TemplateConfigField> Fields = new List<TemplateConfigField>();
    }

    /// <summary>
    /// A fully data-driven project template (SRS §9): metadata + files + variables + dependencies. The wizard
    /// contains NO project-specific code — every project type is one of these records, so new languages/types
    /// need no wizard change (SRS §2 "No hardcoded project types"). Language-independent (NFR §14).
    /// </summary>
    public sealed class ProjectTemplate
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Language = "";       // Python, CUDA C, C++, C
        public string Platform = "";       // Windows, Cross-platform, GPU, ...
        public string ProjectType = "";    // Console, GUI, Library, Scientific, ...
        public string Framework = "";      // CPython, CUDA Runtime, NumPy, ...
        public string Category = "";       // Official, Samples, Community, ...
        public string Author = "";
        public string Version = "1.0.0";
        public string Icon = "";           // emoji/tag for the card
        public string InstalledBy = "built-in";

        public readonly List<string> Tags = new List<string>();
        public readonly List<TemplateVariable> Variables = new List<TemplateVariable>();
        public readonly List<TemplateFile> Files = new List<TemplateFile>();
        public readonly List<string> Dependencies = new List<string>();
        public readonly List<TemplateConfigPage> ConfigPages = new List<TemplateConfigPage>();   // SRS §6

        /// <summary>Default variable bindings from every config field (so generation works even without the UI).</summary>
        public Dictionary<string, string> ConfigDefaults()
        {
            var d = new Dictionary<string, string>();
            foreach (var page in ConfigPages)
                foreach (var f in page.Fields)
                    d[f.Name] = f.Type == ConfigFieldType.Bool ? (string.Equals(f.Default, "true", System.StringComparison.OrdinalIgnoreCase) ? f.BoolTrue : f.BoolFalse) : f.Default;
            return d;
        }

        /// <summary>All searchable text for this template, lower-cased (name/desc/lang/framework/type/tags/author).</summary>
        public string SearchCorpus()
        {
            var parts = new List<string> { Name, Description, Language, Platform, ProjectType, Framework, Category, Author };
            parts.AddRange(Tags);
            return string.Join(" ", parts.ToArray()).ToLowerInvariant();
        }

        /// <summary>Acronym of the display name (e.g. "CUDA Molecular Dynamics" -> "cmd"), for acronym search.</summary>
        public string Acronym()
        {
            var sb = new System.Text.StringBuilder();
            foreach (string w in Name.Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length > 0) sb.Append(char.ToLowerInvariant(w[0]));
            return sb.ToString();
        }

        public override string ToString() { return Id + " (" + Language + " / " + ProjectType + ")"; }
    }
}
