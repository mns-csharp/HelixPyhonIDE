using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HelixPythonNamespace.Ai.Abstractions;

namespace HelixPythonNamespace.Ai.Tools
{
    /// <summary>How dangerous a tool call is, which decides whether the user is asked first.</summary>
    public enum AgentToolRisk
    {
        /// <summary>Reads only. Cannot change the solution or the machine.</summary>
        ReadOnly = 0,

        /// <summary>Writes a file inside the workspace. Reversible via the edit journal.</summary>
        Write = 1,

        /// <summary>Runs an external process. Arbitrary effect; always confirmed.</summary>
        Execute = 2,
    }

    /// <summary>The outcome of running one tool.</summary>
    public sealed class AgentToolResult
    {
        private AgentToolResult(string content, bool isError)
        {
            Content = content ?? string.Empty;
            IsError = isError;
        }

        public string Content { get; }
        public bool IsError { get; }

        public static AgentToolResult Ok(string content) { return new AgentToolResult(content, false); }

        /// <summary>
        /// A failure the model should see and recover from. Returned as a normal tool result with
        /// the error flag set — never thrown — because a missing result desynchronizes the turn.
        /// </summary>
        public static AgentToolResult Error(string message) { return new AgentToolResult(message, true); }
    }

    /// <summary>
    /// The sandbox every tool is confined to, plus the workspace-relative path arithmetic they all
    /// need.
    ///
    /// <b>This type is the security boundary.</b> Tool arguments are model output and are treated
    /// as untrusted: every path a tool touches is resolved to its canonical form and verified to
    /// remain inside <see cref="RootDirectory"/>. Rejecting traversal here, once, is what keeps
    /// each individual tool from having to get it right.
    /// </summary>
    public sealed class AgentWorkspace
    {
        public AgentWorkspace(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("An agent workspace needs a root directory.", "rootDirectory");
            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        /// <summary>The solution directory. Nothing outside it is readable or writable.</summary>
        public string RootDirectory { get; }

        /// <summary>
        /// Resolve a model-supplied path against the workspace root, or explain why it is refused.
        ///
        /// Handles the traversal cases that matter: <c>..</c> segments, absolute paths pointing
        /// elsewhere, and a sibling directory whose name merely starts with the root's name
        /// (<c>C:\sol_backup</c> vs <c>C:\sol</c>) — the separator is appended before comparing so
        /// the prefix test cannot match a sibling.
        ///
        /// It does NOT resolve symlinks: .NET Framework has no portable API for that, so a symlink
        /// planted inside the workspace could still point outside it. That residual is documented
        /// in AiProviders.md rather than papered over — it requires an attacker who can already
        /// write to the solution directory.
        /// </summary>
        public bool TryResolve(string relativeOrAbsolute, out string fullPath, out string refusal)
        {
            fullPath = null;
            refusal = null;

            if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            {
                refusal = "No path was given.";
                return false;
            }

            string candidate;
            try
            {
                candidate = Path.IsPathRooted(relativeOrAbsolute)
                    ? Path.GetFullPath(relativeOrAbsolute)
                    : Path.GetFullPath(Path.Combine(RootDirectory, relativeOrAbsolute));
            }
            catch (Exception ex)
            {
                refusal = "That path could not be interpreted: " + ex.Message;
                return false;
            }

            string root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar);
            bool inside = string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!inside)
            {
                refusal = "Refused: '" + relativeOrAbsolute + "' resolves outside the solution "
                        + "directory. The agent may only touch files under " + RootDirectory + ".";
                return false;
            }

            fullPath = candidate;
            return true;
        }

        /// <summary>The path as the user recognizes it — relative to the solution root.</summary>
        public string Display(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            string prefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(prefix.Length)
                : fullPath;
        }
    }

    /// <summary>
    /// One capability exposed to the model.
    ///
    /// Implementations must be pure with respect to approval: they run only after the agent loop
    /// has cleared them with the permission policy. A tool never prompts the user itself — that
    /// keeps the approval path in one place instead of scattered across every capability.
    /// </summary>
    public interface IAgentTool
    {
        /// <summary>Wire name the model calls. Must be unique and stable.</summary>
        string Name { get; }

        /// <summary>What it does AND when to call it — the trigger condition drives usage rate.</summary>
        string Description { get; }

        /// <summary>JSON Schema for the arguments.</summary>
        string InputSchemaJson { get; }

        /// <summary>Decides whether this call needs confirmation.</summary>
        AgentToolRisk Risk { get; }

        /// <summary>
        /// A one-line, human-readable summary of what THIS call would do, shown in the approval
        /// prompt. Must be derived from the actual arguments — "Run: pytest -q" is reviewable,
        /// "Run a command" is not.
        /// </summary>
        string Describe(string argumentsJson);

        Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken);

        /// <summary>The provider-neutral declaration handed to the model.</summary>
        AiToolDefinition ToDefinition();
    }

    /// <summary>Shared plumbing so each tool only writes its own logic.</summary>
    public abstract class AgentToolBase : IAgentTool
    {
        protected AgentToolBase(AgentWorkspace workspace)
        {
            Workspace = workspace ?? throw new ArgumentNullException("workspace");
        }

        protected AgentWorkspace Workspace { get; }

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string InputSchemaJson { get; }
        public abstract AgentToolRisk Risk { get; }
        public abstract string Describe(string argumentsJson);
        public abstract Task<AgentToolResult> RunAsync(string argumentsJson, CancellationToken cancellationToken);

        public AiToolDefinition ToDefinition()
        {
            return new AiToolDefinition(Name, Description, InputSchemaJson);
        }

        /// <summary>
        /// Read a string argument. Tool input is model output, so a missing or wrong-typed field is
        /// an ordinary condition to report, not an exception to throw.
        /// </summary>
        protected static string ReadString(Newtonsoft.Json.Linq.JObject args, string name)
        {
            if (args == null) return null;
            Newtonsoft.Json.Linq.JToken token = args[name];
            if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null) return null;
            return token.ToString();
        }

        protected static int ReadInt(Newtonsoft.Json.Linq.JObject args, string name, int fallback)
        {
            if (args == null) return fallback;
            Newtonsoft.Json.Linq.JToken token = args[name];
            if (token == null) return fallback;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : fallback;
        }

        protected static Newtonsoft.Json.Linq.JObject ParseArgs(string argumentsJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(argumentsJson)
                    ? new Newtonsoft.Json.Linq.JObject()
                    : Newtonsoft.Json.Linq.JObject.Parse(argumentsJson);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }
    }
}
