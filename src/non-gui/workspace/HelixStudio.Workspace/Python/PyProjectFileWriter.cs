using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Emits a Python Tools for Visual Studio (PTVS) <c>.pyproj</c> MSBuild file.
    /// Schema reference: https://github.com/microsoft/PTVS (Microsoft.PythonTools.targets).
    /// </summary>
    public static class PyProjectFileWriter
    {
        public const string MsBuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

        public static void Write(PackageDescriptor package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (string.IsNullOrEmpty(package.PyProjFilePath))
                throw new InvalidOperationException("PackageDescriptor.PyProjFilePath must be set before writing.");

            XmlWriterSettings settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = false,
                NewLineChars = "\n"
            };

            string dir = Path.GetDirectoryName(package.PyProjFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (FileStream fs = new FileStream(package.PyProjFilePath, FileMode.Create, FileAccess.Write))
            using (XmlWriter w = XmlWriter.Create(fs, settings))
            {
                w.WriteStartDocument();
                w.WriteStartElement("Project", MsBuildNamespace);
                w.WriteAttributeString("DefaultTargets", "Build");
                w.WriteAttributeString("ToolsVersion", "4.0");

                WriteMainProperties(w, package);
                WriteCompileItems(w, package);
                WriteFolderItems(w, package);
                WritePtvsTargetsImport(w);

                w.WriteEndElement();
                w.WriteEndDocument();
            }
        }

        private static void WriteMainProperties(XmlWriter w, PackageDescriptor package)
        {
            w.WriteStartElement("PropertyGroup", MsBuildNamespace);

            w.WriteStartElement("Configuration", MsBuildNamespace);
            w.WriteAttributeString("Condition", " '$(Configuration)' == '' ");
            w.WriteString("Debug");
            w.WriteEndElement();

            WriteSimpleElement(w, "SchemaVersion", "2.0");
            WriteSimpleElement(w, "ProjectGuid", FormatGuid(package.ProjectGuid));
            WriteSimpleElement(w, "ProjectHome", ".");
            WriteSimpleElement(w, "StartupFile", PickStartupFile(package));
            WriteSimpleElement(w, "SearchPath", string.Empty);
            WriteSimpleElement(w, "WorkingDirectory", ".");
            WriteSimpleElement(w, "OutputPath", ".");
            WriteSimpleElement(w, "Name", package.Name);
            WriteSimpleElement(w, "RootNamespace", package.Name);
            WriteSimpleElement(w, "InterpreterId", "MSBuild|env|$(MSBuildProjectFullPath)");

            w.WriteEndElement();
        }

        private static void WriteCompileItems(XmlWriter w, PackageDescriptor package)
        {
            if (package.PyFiles.Count == 0) return;
            w.WriteStartElement("ItemGroup", MsBuildNamespace);
            List<string> sorted = new List<string>(package.PyFiles);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in sorted)
            {
                w.WriteStartElement("Compile", MsBuildNamespace);
                w.WriteAttributeString("Include", NormalizeWindowsPath(rel));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private static void WriteFolderItems(XmlWriter w, PackageDescriptor package)
        {
            if (package.SubDirectories.Count == 0) return;
            w.WriteStartElement("ItemGroup", MsBuildNamespace);
            List<string> sorted = new List<string>(package.SubDirectories);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in sorted)
            {
                w.WriteStartElement("Folder", MsBuildNamespace);
                string include = NormalizeWindowsPath(rel);
                if (!include.EndsWith("\\")) include += "\\";
                w.WriteAttributeString("Include", include);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }

        private static void WritePtvsTargetsImport(XmlWriter w)
        {
            w.WriteStartElement("Import", MsBuildNamespace);
            w.WriteAttributeString("Project",
                "$(MSBuildExtensionsPath32)\\Microsoft\\VisualStudio\\v$(VisualStudioVersion)\\Python Tools\\Microsoft.PythonTools.targets");
            w.WriteEndElement();
        }

        private static void WriteSimpleElement(XmlWriter w, string name, string value)
        {
            w.WriteStartElement(name, MsBuildNamespace);
            if (!string.IsNullOrEmpty(value)) w.WriteString(value);
            w.WriteEndElement();
        }

        private static string PickStartupFile(PackageDescriptor package)
        {
            foreach (string f in package.PyFiles)
                if (string.Equals(f, "__init__.py", StringComparison.OrdinalIgnoreCase)) return f;
            return package.PyFiles.Count > 0 ? package.PyFiles[0] : "__init__.py";
        }

        private static string FormatGuid(Guid g)
        {
            // PTVS writes GUIDs in braces, upper-case.
            return "{" + g.ToString("D").ToUpperInvariant() + "}";
        }

        private static string NormalizeWindowsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('/', '\\');
        }
    }
}
