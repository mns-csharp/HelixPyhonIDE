using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Services;

namespace HelixPythonNamespace.Export
{
    /// <summary>
    /// Exports a <see cref="Diagram"/> as Python source files. One class per
    /// file, file name matches the class name. Implements
    /// <see cref="ICodeExporter"/>; the legacy method name (ExportCSharp) is
    /// retained for API stability and now emits Python.
    /// </summary>
    public sealed class PythonExportService : ICodeExporter
    {
        public void ExportCSharp(Diagram diagram, ExportOptions options)
        {
            if (diagram == null) throw new ArgumentNullException(nameof(diagram));
            if (options == null) throw new ArgumentNullException(nameof(options));
            string outputDir = options.OutputPath ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDir);

            // Build inheritance / implementation lookups from relationships.
            Dictionary<Guid, List<string>> inherits = new Dictionary<Guid, List<string>>();
            Dictionary<Guid, List<string>> implements = new Dictionary<Guid, List<string>>();
            foreach (Relationship r in diagram.Relationships ?? Enumerable.Empty<Relationship>())
            {
                if (r.Source == null || r.Target == null) continue;
                Dictionary<Guid, List<string>> map = r.Kind == RelationshipKind.Implements ? implements : inherits;
                if (r.Kind == RelationshipKind.DependsOn) continue;
                if (!map.TryGetValue(r.Source.Id, out List<string> list))
                    map[r.Source.Id] = list = new List<string>();
                list.Add(r.Target.Name);
            }

            foreach (TypeNode node in diagram.Nodes ?? Enumerable.Empty<TypeNode>())
            {
                List<string> nodeInherits = inherits.ContainsKey(node.Id) ? inherits[node.Id] : new List<string>();
                List<string> nodeImplements = implements.ContainsKey(node.Id) ? implements[node.Id] : new List<string>();
                string body = Render(node, nodeInherits, nodeImplements);
                string path = Path.Combine(outputDir, node.Name + ".py");
                File.WriteAllText(path, body);
            }
        }

        private static string Render(TypeNode node, List<string> inherits, List<string> implements)
        {
            StringBuilder imports = new StringBuilder();
            StringBuilder body = new StringBuilder();
            bool needsAbc = node.TypeKind == TypeKind.AbstractClass || node.Members.Any(m => m.IsAbstract);
            bool needsProtocol = node.TypeKind == TypeKind.Interface;
            if (needsProtocol) imports.AppendLine("from typing import Protocol");
            if (needsAbc) imports.AppendLine("from abc import ABC, abstractmethod");
            if (node.TypeKind == TypeKind.Record) imports.AppendLine("from dataclasses import dataclass");
            if (node.TypeKind == TypeKind.Enum) imports.AppendLine("from enum import Enum");
            if (imports.Length > 0) imports.AppendLine();

            if (node.TypeKind == TypeKind.Record) body.AppendLine("@dataclass");

            // Base types in header
            List<string> bases = new List<string>();
            if (node.TypeKind == TypeKind.Interface) bases.Add("Protocol");
            else if (node.TypeKind == TypeKind.AbstractClass) bases.Add("ABC");
            else if (node.TypeKind == TypeKind.Enum) bases.Add("Enum");
            foreach (string b in inherits) if (!bases.Contains(b)) bases.Add(b);
            foreach (string b in implements) if (!bases.Contains(b)) bases.Add(b);

            body.Append("class ").Append(node.Name);
            if (node.GenericParameters != null && node.GenericParameters.Count > 0)
            {
                body.Append('[');
                for (int i = 0; i < node.GenericParameters.Count; i++)
                {
                    if (i > 0) body.Append(", ");
                    body.Append(node.GenericParameters[i].Name);
                }
                body.Append(']');
            }
            if (bases.Count > 0) body.Append('(').Append(string.Join(", ", bases)).Append(')');
            body.AppendLine(":");

            bool any = false;
            foreach (MemberDefinition m in node.Members ?? Enumerable.Empty<MemberDefinition>())
            {
                RenderMember(body, node, m);
                any = true;
            }
            if (!any) body.AppendLine("    pass");

            return imports.ToString() + body.ToString();
        }

        private static void RenderMember(StringBuilder body, TypeNode node, MemberDefinition m)
        {
            if (m.IsStatic && m.MemberKind == MemberKind.Method) body.AppendLine("    @staticmethod");
            if (m.MemberKind == MemberKind.Property) body.AppendLine("    @property");
            if (m.IsAbstract) body.AppendLine("    @abstractmethod");

            body.Append("    def ").Append(PythonName(m));
            body.Append('(');
            if (!m.IsStatic) body.Append("self");
            if (m.Parameters != null)
            {
                foreach (MethodParameter p in m.Parameters)
                {
                    if (body[body.Length - 1] != '(') body.Append(", ");
                    body.Append(p.Name);
                    if (!string.IsNullOrEmpty(p.TypeName)) body.Append(": ").Append(p.TypeName);
                }
            }
            body.Append(')');
            if (!string.IsNullOrEmpty(m.ReturnType)) body.Append(" -> ").Append(m.ReturnType);
            body.Append(':').AppendLine();
            if (m.IsAbstract) body.AppendLine("        ...");
            else body.AppendLine("        raise NotImplementedError");
        }

        private static string PythonName(MemberDefinition m)
        {
            if (m.MemberKind == MemberKind.Constructor) return "__init__";
            string name = m.Name ?? string.Empty;
            switch (m.AccessModifier)
            {
                case AccessModifier.Protected: return name.StartsWith("_") ? name : "_" + name;
                case AccessModifier.Private: return name.StartsWith("__") ? name : "__" + name.TrimStart('_');
                default: return name;
            }
        }
    }
}
