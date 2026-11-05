using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Reads a PTVS <c>.pyproj</c> (MSBuild XML) and extracts the handful of
    /// fields HelixStudio cares about: project name, project home (absolute dir),
    /// and the <c>&lt;Compile Include="…"/&gt;</c> entries. Complements
    /// <see cref="PyProjectFileWriter"/>.
    /// </summary>
    public static class PyProjReader
    {
        public sealed class PyProjInfo
        {
            public string Name;
            public string ProjectHome;
            public List<string> CompileIncludes = new List<string>();
        }

        public static PyProjInfo Read(string pyProjPath)
        {
            if (string.IsNullOrWhiteSpace(pyProjPath)) throw new ArgumentException("pyProjPath");
            if (!File.Exists(pyProjPath)) throw new FileNotFoundException(pyProjPath);

            PyProjInfo info = new PyProjInfo();
            string pyProjDir = Path.GetDirectoryName(Path.GetFullPath(pyProjPath));
            info.ProjectHome = pyProjDir; // sensible default; <ProjectHome>.</ProjectHome> resolves to this

            XDocument doc;
            try { doc = XDocument.Load(pyProjPath); }
            catch (Exception ex)
            {
                throw new InvalidDataException("Failed to parse .pyproj XML: " + ex.Message);
            }
            if (doc.Root == null) return info;

            // The MSBuild namespace is "http://schemas.microsoft.com/developer/msbuild/2003"
            // but we match by LocalName so files without the namespace still work.
            string name = null, rootNs = null, projectHomeRel = null;
            foreach (XElement pg in Descendants(doc.Root, "PropertyGroup"))
            {
                foreach (XElement child in pg.Elements())
                {
                    string local = child.Name.LocalName;
                    if (string.Equals(local, "Name", StringComparison.Ordinal)) name = name ?? child.Value.Trim();
                    else if (string.Equals(local, "RootNamespace", StringComparison.Ordinal)) rootNs = rootNs ?? child.Value.Trim();
                    else if (string.Equals(local, "ProjectHome", StringComparison.Ordinal)) projectHomeRel = projectHomeRel ?? child.Value.Trim();
                }
            }
            info.Name = !string.IsNullOrEmpty(name) ? name
                : (!string.IsNullOrEmpty(rootNs) ? rootNs : Path.GetFileNameWithoutExtension(pyProjPath));

            if (!string.IsNullOrEmpty(projectHomeRel))
            {
                try { info.ProjectHome = Path.GetFullPath(Path.Combine(pyProjDir, projectHomeRel)); }
                catch { /* keep default */ }
            }

            foreach (XElement ig in Descendants(doc.Root, "ItemGroup"))
            {
                foreach (XElement compile in ig.Elements())
                {
                    if (!string.Equals(compile.Name.LocalName, "Compile", StringComparison.Ordinal)) continue;
                    string include = (string)compile.Attribute("Include");
                    if (!string.IsNullOrEmpty(include)) info.CompileIncludes.Add(include);
                }
            }
            return info;
        }

        private static IEnumerable<XElement> Descendants(XElement root, string localName)
        {
            foreach (XElement e in root.Elements())
            {
                if (string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal)) yield return e;
            }
        }
    }
}
