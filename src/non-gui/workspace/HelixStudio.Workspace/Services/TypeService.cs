using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Python.Emit;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Python-only type manipulation service. CRUD operations write to .py
    /// files via the <see cref="PythonWriter"/> emitter. For now the service
    /// creates one file per type; grouping related classes into a single module
    /// is a follow-up (TypePlacementDialog in the App).
    /// </summary>
    public sealed class TypeService : ITypeService
    {
        public ICodegenDowngradeNotifier DowngradeNotifier { get; set; }

        public TypeModel AddClass(ProjectModel project, string name, string ns, bool isAbstract = false)
        {
            TypeKind kind = isAbstract ? TypeKind.AbstractClass : TypeKind.ConcreteClass;
            return AddType(project, name, ns, kind);
        }

        public TypeModel AddInterface(ProjectModel project, string name, string ns)
        {
            return AddType(project, name, ns, TypeKind.Interface);
        }

        public TypeModel AddNestedType(TypeModel parent, string name, TypeKind kind)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            TypeModel nested = new TypeModel(name, parent.Namespace, kind, parent.Project);
            nested.ParentTypeFqn = parent.FullyQualifiedName;
            nested.SourceFilePath = parent.SourceFilePath;
            parent.Project.Types.Add(nested);
            RewriteModuleForType(nested);
            return nested;
        }

        public void RemoveType(ProjectModel project, TypeModel type)
        {
            if (project == null || type == null) return;
            project.Types.Remove(type);
            if (!string.IsNullOrEmpty(type.SourceFilePath) && File.Exists(type.SourceFilePath))
            {
                // If this is the only type in the file, delete the file. Otherwise rewrite.
                bool hasOthers = project.Types.Any(t =>
                    !ReferenceEquals(t, type)
                    && string.Equals(t.SourceFilePath, type.SourceFilePath, StringComparison.OrdinalIgnoreCase));
                if (!hasOthers)
                {
                    UpdatePackageExport(type.SourceFilePath, type.Name, null);
                    try { File.Delete(type.SourceFilePath); } catch { }
                }
                else
                {
                    // MVP: do nothing — the user will re-sync on next load.
                }
            }
        }

        public void RenameType(SolutionModel solution, TypeModel type, string newName)
        {
            if (type == null || string.IsNullOrWhiteSpace(newName)) return;
            string oldName = type.Name;
            type.Name = newName;
            // Rewrite the containing file.
            RewriteModuleForType(type);
            // Rename the file if it was named after the old type.
            if (!string.IsNullOrEmpty(type.SourceFilePath))
            {
                string dir = Path.GetDirectoryName(type.SourceFilePath);
                string stem = Path.GetFileNameWithoutExtension(type.SourceFilePath);
                if (string.Equals(stem, oldName, StringComparison.Ordinal))
                {
                    string renamed = Path.Combine(dir, newName + ".py");
                    try
                    {
                        if (!File.Exists(renamed)) File.Move(type.SourceFilePath, renamed);
                        type.SourceFilePath = renamed;
                    }
                    catch { }
                }
                UpdatePackageExport(type.SourceFilePath, oldName, newName);
            }
        }

        public void MoveToNamespace(SolutionModel solution, TypeModel type, string newNamespace)
        {
            if (type == null) return;
            type.Namespace = newNamespace ?? string.Empty;
        }

        public void AddInheritance(TypeModel derived, TypeModel baseType)
        {
            if (derived == null || baseType == null) return;
            RelationshipKind rel = baseType.TypeKind == TypeKind.Interface
                ? RelationshipKind.Implements
                : RelationshipKind.Inherits;
            derived.BaseTypes.Add(new TypeReference(baseType.FullyQualifiedName, rel));
            RewriteModuleForType(derived);
        }

        public void AddImplementation(TypeModel type, TypeModel interfaceType)
        {
            if (type == null || interfaceType == null) return;
            type.BaseTypes.Add(new TypeReference(interfaceType.FullyQualifiedName, RelationshipKind.Implements));
            RewriteModuleForType(type);
        }

        public void RemoveInheritance(TypeModel derived, TypeModel baseType)
        {
            if (derived == null || baseType == null) return;
            for (int i = derived.BaseTypes.Count - 1; i >= 0; i--)
            {
                if (string.Equals(derived.BaseTypes[i].FullyQualifiedName, baseType.FullyQualifiedName, StringComparison.Ordinal))
                    derived.BaseTypes.RemoveAt(i);
            }
            RewriteModuleForType(derived);
        }

        public void AddMember(TypeModel type, MemberModel member) { AddMember(type, member, addToModel: true); }
        public void AddMember(TypeModel type, MemberModel member, bool addToModel)
        {
            if (type == null || member == null) return;
            if (addToModel) type.Members.Add(member);
            RewriteModuleForType(type);
        }
        public void RemoveMember(TypeModel type, MemberModel member) { RemoveMember(type, member, removeFromModel: true); }
        public void RemoveMember(TypeModel type, MemberModel member, bool removeFromModel)
        {
            if (type == null || member == null) return;
            if (removeFromModel) type.Members.Remove(member);
            RewriteModuleForType(type);
        }

        public void ChangeTypeKind(TypeModel type, TypeKind newKind)
        {
            if (type == null) return;
            type.TypeKind = newKind;
            RewriteModuleForType(type);
        }

        public void UpdateGenericParameters(TypeModel type) { RewriteModuleForType(type); }
        public void UpdateGenericParameters(TypeModel type, SolutionModel solution) { RewriteModuleForType(type); }

        // ---------- helpers ----------

        /// <summary>
        /// The namespace whose <see cref="ComputeFilePath"/>-style directory contains
        /// <paramref name="filePath"/> — i.e. the package a new sibling type would join.
        /// Inverse of ComputeFilePath's mapping (ns → &lt;projectDir&gt;\src\&lt;ns dirs&gt;).
        /// Returns null when the file is not inside a package under the project's src tree;
        /// callers then fall back to the project's default namespace.
        /// </summary>
        public static string PackageNamespaceForFile(ProjectModel project, string filePath)
        {
            if (project == null || string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(project.FilePath))
                return null;
            try
            {
                string projectDir = Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
                if (string.IsNullOrEmpty(projectDir)) return null;
                string fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (string.IsNullOrEmpty(fileDir)) return null;
                string srcRoot = Path.Combine(projectDir, "src");
                if (string.Equals(fileDir.TrimEnd(Path.DirectorySeparatorChar),
                    srcRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
                string baseDir = IsInside(fileDir, srcRoot) ? srcRoot : projectDir;
                if (!IsInside(fileDir, baseDir)) return null;
                string rel = fileDir.Substring(baseDir.TrimEnd(Path.DirectorySeparatorChar).Length)
                    .Trim(Path.DirectorySeparatorChar);
                if (rel.Length == 0) return null;
                return rel.Replace(Path.DirectorySeparatorChar, '.');
            }
            catch
            {
                return null;
            }
        }

        private static bool IsInside(string path, string directory)
        {
            string prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private TypeModel AddType(ProjectModel project, string name, string ns, TypeKind kind)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
            TypeModel type = new TypeModel(name, ns ?? project.DefaultNamespace ?? string.Empty, kind, project);
            type.SourceFilePath = ComputeFilePath(project, ns, name);
            project.Types.Add(type);
            RewriteModuleForType(type);
            UpdatePackageExport(type.SourceFilePath, null, type.Name);
            return type;
        }

        private static void UpdatePackageExport(string sourceFilePath, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(sourceFilePath)) return;
            string packageDirectory = Path.GetDirectoryName(sourceFilePath);
            if (string.IsNullOrEmpty(packageDirectory)) return;

            string initPath = Path.Combine(packageDirectory, "__init__.py");
            string text = File.Exists(initPath) ? File.ReadAllText(initPath) : string.Empty;
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

            if (!string.IsNullOrEmpty(oldName))
            {
                string oldExport = "from ." + oldName + " import " + oldName;
                lines.RemoveAll(line => string.Equals(line.Trim(), oldExport, StringComparison.Ordinal));
            }

            if (!string.IsNullOrEmpty(newName))
            {
                string export = "from ." + newName + " import " + newName;
                if (!lines.Any(line => string.Equals(line.Trim(), export, StringComparison.Ordinal)))
                {
                    while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add(export);
                }
            }

            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            string updated = lines.Count == 0 ? string.Empty : string.Join(newline, lines) + newline;
            File.WriteAllText(initPath, updated, new UTF8Encoding(false));
        }

        private string ComputeFilePath(ProjectModel project, string ns, string typeName)
        {
            string projectDir = Path.GetDirectoryName(project.FilePath);
            string rel = string.IsNullOrEmpty(ns) ? "" : ns.Replace('.', Path.DirectorySeparatorChar);
            string defaultPkg = project.DefaultNamespace ?? Path.GetFileNameWithoutExtension(project.FilePath);
            string legacySrc = Path.Combine(projectDir, "src");
            string legacyPackage = Path.Combine(legacySrc, SafePackage(defaultPkg));
            string baseDir = Directory.Exists(legacyPackage) ? legacySrc : projectDir;
            string fullDir = string.IsNullOrEmpty(rel)
                ? Path.Combine(baseDir, SafePackage(defaultPkg))
                : Path.Combine(baseDir, rel);
            Directory.CreateDirectory(fullDir);
            return Path.Combine(fullDir, typeName + ".py");
        }

        private void RewriteModuleForType(TypeModel type)
        {
            if (type == null || string.IsNullOrEmpty(type.SourceFilePath)) return;
            CodegenPolicy policy = CodegenPolicy.Default;
            string body = PythonSourceEmitter.Render(type, policy);
            try { File.WriteAllText(type.SourceFilePath, body); } catch { }
        }

        private static string SafePackage(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '_' || c == '-' || c == '.') sb.Append('_');
            }
            return sb.Length == 0 ? "pkg" : sb.ToString();
        }
    }
}
