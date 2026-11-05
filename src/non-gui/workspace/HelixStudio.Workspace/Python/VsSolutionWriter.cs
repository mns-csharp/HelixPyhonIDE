using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Emits a Visual Studio <c>.sln</c> referencing one or more PTVS <c>.pyproj</c> files.
    /// </summary>
    public static class VsSolutionWriter
    {
        /// <summary>PTVS project-type GUID. Every .pyproj project line in the .sln uses this.</summary>
        public const string PtvsProjectTypeGuid = "{888888A0-9F3D-457C-B088-3A5042F75D52}";

        /// <summary>Solution-folder project-type GUID (for grouping nested packages).</summary>
        public const string SolutionFolderTypeGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

        public static void Write(string slnPath, IReadOnlyList<PackageDescriptor> packages)
        {
            if (string.IsNullOrWhiteSpace(slnPath)) throw new ArgumentException("slnPath");
            if (packages == null) throw new ArgumentNullException(nameof(packages));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 16");
            sb.AppendLine("VisualStudioVersion = 16.0.33529.622");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

            string slnDir = Path.GetDirectoryName(slnPath);

            foreach (PackageDescriptor pkg in packages)
            {
                string rel = GetRelativePath(slnDir, pkg.PyProjFilePath);
                sb.Append("Project(\"").Append(PtvsProjectTypeGuid).Append("\") = \"")
                  .Append(pkg.Name).Append("\", \"").Append(rel).Append("\", \"")
                  .Append(FormatGuid(pkg.ProjectGuid)).AppendLine("\"");
                sb.AppendLine("EndProject");
            }

            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (PackageDescriptor pkg in packages)
            {
                string g = FormatGuid(pkg.ProjectGuid);
                sb.Append("\t\t").Append(g).AppendLine(".Debug|Any CPU.ActiveCfg = Debug|Any CPU");
                sb.Append("\t\t").Append(g).AppendLine(".Release|Any CPU.ActiveCfg = Release|Any CPU");
            }
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("\t\tHideSolutionNode = FALSE");
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("\tGlobalSection(ExtensibilityGlobals) = postSolution");
            sb.Append("\t\tSolutionGuid = ").AppendLine(FormatGuid(Guid.NewGuid()));
            sb.AppendLine("\tEndGlobalSection");

            sb.AppendLine("EndGlobal");

            Directory.CreateDirectory(slnDir);
            File.WriteAllText(slnPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string FormatGuid(Guid g)
        {
            return "{" + g.ToString("D").ToUpperInvariant() + "}";
        }

        private static string GetRelativePath(string fromDir, string toPath)
        {
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toPath)) return toPath;
            Uri fromUri = new Uri(EnsureTrailingSeparator(fromDir));
            Uri toUri = new Uri(toPath);
            if (fromUri.Scheme != toUri.Scheme) return toPath;
            Uri rel = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(rel.ToString().Replace('/', '\\'));
        }

        private static string EnsureTrailingSeparator(string p)
        {
            if (p.EndsWith(Path.DirectorySeparatorChar.ToString()) || p.EndsWith("/")) return p;
            return p + Path.DirectorySeparatorChar;
        }
    }
}
