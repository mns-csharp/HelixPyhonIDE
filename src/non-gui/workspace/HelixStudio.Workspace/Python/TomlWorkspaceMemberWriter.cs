using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Writes a per-package <c>pyproject.toml</c> for one workspace member.
    /// Conflict semantics:
    /// <list type="bullet">
    /// <item><b>Keep</b> — the existing file is left untouched (no-op).</item>
    /// <item><b>Merge</b> — only the <c>[tool.helixstudio]</c> table is ensured.
    /// Every other line is preserved.</item>
    /// <item><b>Overwrite</b> — the existing file is replaced with a minimal
    /// PEP 621 document authored from scratch.</item>
    /// </list>
    /// When the target does not yet exist, the resolution is ignored and the
    /// file is created via <see cref="PyProjectWriter.WriteMinimal"/>.
    /// </summary>
    public static class TomlWorkspaceMemberWriter
    {
        public static string Write(PackageDescriptor package, string pythonVersion, PyProjectConflictResolution resolution)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            string path = Path.Combine(package.DirectoryPath, "pyproject.toml");

            if (!File.Exists(path))
            {
                PyProjectWriter.WriteMinimal(path, package.Name, pythonVersion ?? "3.12");
                return "created " + path;
            }

            switch (resolution)
            {
                case PyProjectConflictResolution.Keep:
                    return "kept existing " + path;

                case PyProjectConflictResolution.Merge:
                    Dictionary<string, string> desired = new Dictionary<string, string>
                    {
                        { "interface-style", "\"protocol\"" }
                    };
                    TomlTableMerger.EnsureTable(path, "tool.helixstudio", desired);
                    return "merged [tool.helixstudio] into " + path;

                case PyProjectConflictResolution.Overwrite:
                    PyProjectWriter.WriteMinimal(path, package.Name, pythonVersion ?? "3.12");
                    return "overwrote " + path;

                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution));
            }
        }
    }
}
