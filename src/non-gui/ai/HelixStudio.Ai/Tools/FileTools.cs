using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Ai.Tools
{
    /// <summary>Read one file from the solution.</summary>
    public sealed class ReadFileTool : AgentToolBase
    {
        public const int DefaultMaxLines = 2000;

        public ReadFileTool(AgentWorkspace workspace) : base(workspace) { }

        public override string Name { get { return "read_file"; } }
        public override AgentToolRisk Risk { get { return AgentToolRisk.ReadOnly; } }

        public override string Description
        {
            get
            {
                return "Read a text file from the solution. Call this before editing any file, and "
                     + "whenever the answer depends on what the code actually says rather than on "
                     + "what you remember. Paths are relative to the solution root. Returns the "
                     + "content with 1-based line numbers so you can cite and edit precisely.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""File path relative to the solution root."" },
    ""start_line"": { ""type"": ""integer"", ""description"": ""Optional 1-based first line to read."" },
    ""max_lines"": { ""type"": ""integer"", ""description"": ""Optional cap on lines returned (default 2000)."" }
  },
  ""required"": [""path""],
  ""additionalProperties"": false
}";
            }
        }

        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            return "Read " + (ReadString(args, "path") ?? "?");
        }

        public override Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return Task.FromResult(AgentToolResult.Error("Arguments were not valid JSON."));

            string path = ReadString(args, "path");
            string full, refusal;
            if (!Workspace.TryResolve(path, out full, out refusal))
                return Task.FromResult(AgentToolResult.Error(refusal));

            if (!File.Exists(full))
                return Task.FromResult(AgentToolResult.Error("No such file: " + Workspace.Display(full)));

            int start = Math.Max(1, ReadInt(args, "start_line", 1));
            int max = Math.Max(1, ReadInt(args, "max_lines", DefaultMaxLines));

            try
            {
                string[] lines = File.ReadAllLines(full);
                var sb = new StringBuilder();
                int emitted = 0;
                for (int i = start - 1; i < lines.Length && emitted < max; i++, emitted++)
                    sb.Append(i + 1).Append('\t').AppendLine(lines[i]);

                if (emitted == 0)
                    return Task.FromResult(AgentToolResult.Ok("(file has " + lines.Length + " lines; nothing at that offset)"));

                int lastLine = start + emitted - 1;
                if (lastLine < lines.Length)
                {
                    sb.AppendLine();
                    sb.Append("… truncated at line ").Append(lastLine).Append(" of ")
                      .Append(lines.Length).Append("; call again with start_line=")
                      .Append(lastLine + 1).AppendLine(" to continue.");
                }
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Error("Could not read the file: " + ex.Message));
            }
        }
    }

    /// <summary>Create or overwrite a file.</summary>
    public sealed class WriteFileTool : AgentToolBase
    {
        private readonly EditJournal _journal;

        public WriteFileTool(AgentWorkspace workspace, EditJournal journal) : base(workspace)
        {
            _journal = journal;
        }

        public override string Name { get { return "write_file"; } }
        public override AgentToolRisk Risk { get { return AgentToolRisk.Write; } }

        public override string Description
        {
            get
            {
                return "Create a file, or replace an existing file's entire contents. Use edit_file "
                     + "instead when changing part of a file you have already read — this tool "
                     + "discards everything not in `content`. The previous contents are journaled "
                     + "so the user can undo.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""File path relative to the solution root."" },
    ""content"": { ""type"": ""string"", ""description"": ""The complete new contents of the file."" }
  },
  ""required"": [""path"", ""content""],
  ""additionalProperties"": false
}";
            }
        }

        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            string path = ReadString(args, "path") ?? "?";
            string content = ReadString(args, "content") ?? string.Empty;
            int lines = content.Length == 0 ? 0 : content.Split('\n').Length;
            return "Write " + path + " (" + lines + " lines)";
        }

        public override Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return Task.FromResult(AgentToolResult.Error("Arguments were not valid JSON."));

            string path = ReadString(args, "path");
            string content = ReadString(args, "content");
            if (content == null) return Task.FromResult(AgentToolResult.Error("No content was given."));

            string full, refusal;
            if (!Workspace.TryResolve(path, out full, out refusal))
                return Task.FromResult(AgentToolResult.Error(refusal));

            try
            {
                bool existed = File.Exists(full);
                if (_journal != null) _journal.Record(full);

                string directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                // Preserve the file's existing line ending rather than imposing the host's — a
                // CRLF-ified Python file is a whole-file diff for anyone sharing the repo.
                File.WriteAllText(full, Normalize(content, full, existed));
                return Task.FromResult(AgentToolResult.Ok(
                    (existed ? "Overwrote " : "Created ") + Workspace.Display(full)));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Error("Could not write the file: " + ex.Message));
            }
        }

        private static string Normalize(string content, string path, bool existed)
        {
            if (!existed) return content;
            try
            {
                string original = File.ReadAllText(path);
                bool crlf = original.Contains("\r\n");
                string lf = content.Replace("\r\n", "\n");
                return crlf ? lf.Replace("\n", "\r\n") : lf;
            }
            catch (IOException)
            {
                return content;
            }
        }
    }

    /// <summary>Replace an exact string in a file.</summary>
    public sealed class EditFileTool : AgentToolBase
    {
        private readonly EditJournal _journal;

        public EditFileTool(AgentWorkspace workspace, EditJournal journal) : base(workspace)
        {
            _journal = journal;
        }

        public override string Name { get { return "edit_file"; } }
        public override AgentToolRisk Risk { get { return AgentToolRisk.Write; } }

        public override string Description
        {
            get
            {
                return "Replace an exact block of text in a file. This is the preferred way to "
                     + "change existing code. `old_text` must appear EXACTLY once — include "
                     + "enough surrounding context to make it unique. Read the file first; the "
                     + "edit is refused rather than guessed at if the match is missing or ambiguous.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""File path relative to the solution root."" },
    ""old_text"": { ""type"": ""string"", ""description"": ""Exact text to replace. Must occur exactly once."" },
    ""new_text"": { ""type"": ""string"", ""description"": ""Replacement text."" }
  },
  ""required"": [""path"", ""old_text"", ""new_text""],
  ""additionalProperties"": false
}";
            }
        }

        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            return "Edit " + (ReadString(args, "path") ?? "?");
        }

        public override Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return Task.FromResult(AgentToolResult.Error("Arguments were not valid JSON."));

            string path = ReadString(args, "path");
            string oldText = ReadString(args, "old_text");
            string newText = ReadString(args, "new_text") ?? string.Empty;
            if (string.IsNullOrEmpty(oldText))
                return Task.FromResult(AgentToolResult.Error("old_text was empty."));

            string full, refusal;
            if (!Workspace.TryResolve(path, out full, out refusal))
                return Task.FromResult(AgentToolResult.Error(refusal));
            if (!File.Exists(full))
                return Task.FromResult(AgentToolResult.Error("No such file: " + Workspace.Display(full)));

            try
            {
                string original = File.ReadAllText(full);
                int first = original.IndexOf(oldText, StringComparison.Ordinal);
                if (first < 0)
                {
                    return Task.FromResult(AgentToolResult.Error(
                        "old_text was not found in " + Workspace.Display(full)
                        + ". Read the file again — it may have changed, or the whitespace may differ."));
                }

                int second = original.IndexOf(oldText, first + 1, StringComparison.Ordinal);
                if (second >= 0)
                {
                    return Task.FromResult(AgentToolResult.Error(
                        "old_text appears more than once in " + Workspace.Display(full)
                        + ". Include more surrounding context so the match is unique."));
                }

                if (_journal != null) _journal.Record(full);
                File.WriteAllText(full, original.Substring(0, first) + newText
                    + original.Substring(first + oldText.Length));

                int line = 1;
                for (int i = 0; i < first; i++) if (original[i] == '\n') line++;
                return Task.FromResult(AgentToolResult.Ok(
                    "Edited " + Workspace.Display(full) + " at line " + line + "."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Error("Could not edit the file: " + ex.Message));
            }
        }
    }

    /// <summary>Find files by glob pattern.</summary>
    public sealed class GlobTool : AgentToolBase
    {
        public GlobTool(AgentWorkspace workspace) : base(workspace) { }

        public override string Name { get { return "glob"; } }
        public override AgentToolRisk Risk { get { return AgentToolRisk.ReadOnly; } }

        public override string Description
        {
            get
            {
                return "List files in the solution matching a wildcard pattern such as "
                     + "\"src/**/*.py\" or \"*.toml\". Call this to discover what exists before "
                     + "guessing at paths.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""pattern"": { ""type"": ""string"", ""description"": ""Wildcard pattern, e.g. src/**/*.py"" }
  },
  ""required"": [""pattern""],
  ""additionalProperties"": false
}";
            }
        }

        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            return "Find files matching " + (ReadString(args, "pattern") ?? "?");
        }

        public override Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return Task.FromResult(AgentToolResult.Error("Arguments were not valid JSON."));

            string pattern = ReadString(args, "pattern");
            if (string.IsNullOrWhiteSpace(pattern))
                return Task.FromResult(AgentToolResult.Error("No pattern was given."));

            try
            {
                Regex regex = GlobToRegex(pattern);
                var matches = new List<string>();
                foreach (string file in EnumerateFiles(Workspace.RootDirectory, cancellationToken))
                {
                    string relative = Workspace.Display(file).Replace('\\', '/');
                    if (regex.IsMatch(relative)) matches.Add(relative);
                    if (matches.Count >= 500) break;
                }
                matches.Sort(StringComparer.OrdinalIgnoreCase);
                return Task.FromResult(AgentToolResult.Ok(matches.Count == 0
                    ? "No files matched."
                    : string.Join("\n", matches.ToArray())));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Error("Search failed: " + ex.Message));
            }
        }

        /// <summary>`**` spans directories, `*` does not, `?` is one character.</summary>
        internal static Regex GlobToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            string normalized = pattern.Replace('\\', '/');
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == '*')
                {
                    if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                        if (i + 1 < normalized.Length && normalized[i + 1] == '/') i++;
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append("$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }

        internal static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dir = stack.Pop();

                string[] subdirectories;
                try { subdirectories = Directory.GetDirectories(dir); }
                catch (Exception) { continue; }
                foreach (string sub in subdirectories)
                {
                    string name = Path.GetFileName(sub);
                    // Skipping these is not just speed: a venv or .git tree would otherwise
                    // dominate every search and flood the model's context with vendored code.
                    if (name.StartsWith(".", StringComparison.Ordinal)
                        || string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "venv", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "artifacts", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                        continue;
                    stack.Push(sub);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { continue; }
                foreach (string file in files) yield return file;
            }
        }
    }

    /// <summary>Search file contents by regex.</summary>
    public sealed class GrepTool : AgentToolBase
    {
        public GrepTool(AgentWorkspace workspace) : base(workspace) { }

        public override string Name { get { return "grep"; } }
        public override AgentToolRisk Risk { get { return AgentToolRisk.ReadOnly; } }

        public override string Description
        {
            get
            {
                return "Search the solution's files for a regular expression and return matching "
                     + "lines with their file and line number. Use this to locate a symbol, a "
                     + "call site, or an error message before reading whole files.";
            }
        }

        public override string InputSchemaJson
        {
            get
            {
                return @"{
  ""type"": ""object"",
  ""properties"": {
    ""pattern"": { ""type"": ""string"", ""description"": ""Regular expression to search for."" },
    ""file_pattern"": { ""type"": ""string"", ""description"": ""Optional glob limiting which files are searched, e.g. **/*.py"" }
  },
  ""required"": [""pattern""],
  ""additionalProperties"": false
}";
            }
        }

        public override string Describe(string argumentsJson)
        {
            JObject args = ParseArgs(argumentsJson);
            return "Search for /" + (ReadString(args, "pattern") ?? "?") + "/";
        }

        public override Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            JObject args = ParseArgs(argumentsJson);
            if (args == null) return Task.FromResult(AgentToolResult.Error("Arguments were not valid JSON."));

            string pattern = ReadString(args, "pattern");
            if (string.IsNullOrWhiteSpace(pattern))
                return Task.FromResult(AgentToolResult.Error("No pattern was given."));

            Regex regex;
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException ex)
            {
                return Task.FromResult(AgentToolResult.Error("That is not a valid regex: " + ex.Message));
            }

            string filePattern = ReadString(args, "file_pattern");
            Regex fileRegex = string.IsNullOrWhiteSpace(filePattern) ? null : GlobTool.GlobToRegex(filePattern);

            try
            {
                var hits = new List<string>();
                foreach (string file in GlobTool.EnumerateFiles(Workspace.RootDirectory, cancellationToken))
                {
                    string relative = Workspace.Display(file).Replace('\\', '/');
                    if (fileRegex != null && !fileRegex.IsMatch(relative)) continue;
                    if (LooksBinary(file)) continue;

                    string[] lines;
                    try { lines = File.ReadAllLines(file); }
                    catch (Exception) { continue; }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!regex.IsMatch(lines[i])) continue;
                        hits.Add(relative + ":" + (i + 1) + ": " + lines[i].Trim());
                        if (hits.Count >= 200) break;
                    }
                    if (hits.Count >= 200) break;
                }
                return Task.FromResult(AgentToolResult.Ok(hits.Count == 0
                    ? "No matches."
                    : string.Join("\n", hits.ToArray())
                      + (hits.Count >= 200 ? "\n… stopped at 200 matches; narrow the pattern." : string.Empty)));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Error("Search failed: " + ex.Message));
            }
        }

        private static bool LooksBinary(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            switch (ext.ToLowerInvariant())
            {
                case ".dll": case ".exe": case ".pdb": case ".png": case ".jpg": case ".jpeg":
                case ".gif": case ".ico": case ".zip": case ".gz": case ".pyc": case ".so":
                    return true;
                default:
                    return false;
            }
        }
    }
}
