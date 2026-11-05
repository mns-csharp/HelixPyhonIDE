using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Reads a Visual Studio solution file (legacy INI-like <c>.sln</c> from
    /// Visual Studio solution formats and XML-based <c>.slnx</c> files) and returns the
    /// list of <c>.pyproj</c> references it contains. Non-PTVS references are
    /// skipped. Relative paths are resolved against the solution's directory
    /// and verified to exist on disk; orphan references are dropped.
    /// </summary>
    public static class VsSolutionReader
    {
        /// <summary>PTVS project-type GUID used by .pyproj entries.</summary>
        public const string PtvsProjectTypeGuid = "888888A0-9F3D-457C-B088-3A5042F75D52";

        /// <summary>A single .pyproj reference extracted from a solution file.</summary>
        public sealed class ProjectReference
        {
            public string Name;
            public string PyProjAbsolutePath;
        }

        /// <summary>The <c>.pyproj</c> entries only — the historical behaviour, kept for callers
        /// that specifically want PTVS projects.</summary>
        public static List<ProjectReference> Read(string solutionPath)
        {
            List<ProjectReference> all = ReadAll(solutionPath);
            List<ProjectReference> pyproj = new List<ProjectReference>();
            foreach (ProjectReference r in all)
                if (r.PyProjAbsolutePath.EndsWith(".pyproj", StringComparison.OrdinalIgnoreCase))
                    pyproj.Add(r);
            return pyproj;
        }

        /// <summary>
        /// EVERY project entry the solution lists, whatever its type. Reading only the
        /// <c>.pyproj</c> ones is why an ordinary Visual Studio solution used to fail to open
        /// with "contains no .pyproj references" — the loader could not see what it held.
        /// </summary>
        public static List<ProjectReference> ReadAll(string solutionPath)
        {
            if (string.IsNullOrWhiteSpace(solutionPath)) throw new ArgumentException("solutionPath");
            if (!File.Exists(solutionPath)) throw new FileNotFoundException(solutionPath);

            string ext = Path.GetExtension(solutionPath).ToLowerInvariant();
            string solDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
            if (ext == ".slnx") return ReadSlnx(solutionPath, solDir);
            if (ext == ".sln") return ReadLegacySln(solutionPath, solDir);
            throw new NotSupportedException("Unsupported solution extension: " + ext);
        }

        private static List<ProjectReference> ReadSlnx(string path, string solDir)
        {
            List<ProjectReference> result = new List<ProjectReference>();
            XDocument doc = XDocument.Load(path);
            if (doc.Root == null) return result;

            // .slnx nests project entries inside <Folder> elements, so descendants — not just
            // direct children — must be walked or a solution-folder-organised tree reads empty.
            foreach (XElement proj in doc.Root.Descendants())
            {
                if (!string.Equals(proj.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
                    continue;
                string relPath = (string)proj.Attribute("Path");
                if (string.IsNullOrEmpty(relPath)) continue;

                ProjectReference r = ResolveReference(solDir, relPath);
                if (r != null) result.Add(r);
            }
            return result;
        }

        private static List<ProjectReference> ReadLegacySln(string path, string solDir)
        {
            // Project("{type-guid}") = "Name", "Relative\Path.csproj", "{proj-guid}"
            Regex rx = new Regex(
                "Project\\(\"\\{(?<type>[^}]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"\\{(?<pid>[^}]+)\\}\"",
                RegexOptions.Compiled);

            List<ProjectReference> result = new List<ProjectReference>();
            foreach (string raw in File.ReadAllLines(path))
            {
                Match m = rx.Match(raw);
                if (!m.Success) continue;

                // A solution FOLDER is also a `Project(...)` line, but its "path" is just the
                // folder's display name — ResolveReference drops it because no such file exists.
                ProjectReference r = ResolveReference(solDir, m.Groups["path"].Value);
                if (r != null)
                {
                    // Prefer the name recorded in the .sln over the file stem fallback.
                    r.Name = m.Groups["name"].Value;
                    result.Add(r);
                }
            }
            return result;
        }

        private static ProjectReference ResolveReference(string solDir, string relPath)
        {
            if (string.IsNullOrEmpty(solDir)) return null;
            // .sln paths use backslashes; .slnx paths may use forward slashes on disk.
            string normalized = relPath.Replace('\\', Path.DirectorySeparatorChar)
                                       .Replace('/', Path.DirectorySeparatorChar);
            string abs;
            try { abs = Path.GetFullPath(Path.Combine(solDir, normalized)); }
            catch { return null; }
            if (!File.Exists(abs)) return null;
            return new ProjectReference
            {
                Name = Path.GetFileNameWithoutExtension(abs),
                PyProjAbsolutePath = abs,
            };
        }

        /// <summary>True when a solution entry's type GUID is the PTVS one.</summary>
        public static bool IsPtvsGuid(string guidLike)
        {
            if (string.IsNullOrEmpty(guidLike)) return false;
            string g = guidLike.Trim().Trim('{', '}').ToUpperInvariant();
            return g == PtvsProjectTypeGuid;
        }
    }
}
