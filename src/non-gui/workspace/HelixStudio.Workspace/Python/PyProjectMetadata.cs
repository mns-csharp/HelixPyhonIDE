using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Python
{
    public sealed class PyProjectMetadata
    {
        public string Name;
        public string Version;
        public string PythonVersion;
        public List<string> Dependencies = new List<string>();
        public Dictionary<string, string> ToolHelixStudio = new Dictionary<string, string>();

        /// <summary>
        /// Member paths from <c>[tool.uv.workspace].members</c>. Empty when the TOML
        /// does not declare a uv workspace. Paths are relative to the pyproject.toml's
        /// directory and forward-slash normalized.
        /// </summary>
        public List<string> WorkspaceMembers = new List<string>();

        /// <summary>Explicit Solution Explorer order, including the implicit workspace-root
        /// project when present, from <c>[tool.helixstudio].project-order</c>.</summary>
        public List<string> ProjectOrder = new List<string>();

        /// <summary>True when <see cref="WorkspaceMembers"/> has any entries.</summary>
        public bool IsWorkspace { get { return WorkspaceMembers != null && WorkspaceMembers.Count > 0; } }

        /// <summary>
        /// Files/folders excluded from the project (SRS §5.9 "Exclude from Project"),
        /// from <c>[tool.helixstudio].excluded</c>. Paths are relative to this
        /// pyproject.toml's directory and forward-slash normalized. A directory entry
        /// excludes everything beneath it.
        /// </summary>
        public List<string> Excluded = new List<string>();

        /// <summary>Sibling workspace-member names this project references (PSM-41…46), from
        /// <c>[tool.helixstudio].references</c> — resolved to ProjectReferences edges at load.</summary>
        public List<string> References = new List<string>();
    }
}
