using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>
    /// Projects a <see cref="TomlDocument"/> onto the shared GDI+ OOP class canvas (<c>DiagramCanvas</c>) — the
    /// TOML counterpart to <see cref="HelixPythonNamespace.Workspace.Python.PythonTypeExtractor"/> and
    /// <see cref="Cuda.CppTypeExtractor"/>. Per the design mandate
    /// every FR-DES surface is the GDI+ graphical canvas, so a config file is browsed as a diagram, not a tree:
    /// each TOML <b>table</b> becomes a class box (its scalar keys are fields typed by their value kind), each
    /// <b>sub-table</b> is its own box linked to its parent by a <em>DependsOn</em> containment edge, and each
    /// <b>array-of-tables</b> becomes a box (stereotype-like <c>[]</c> suffix) linked from its parent. The
    /// dotted table path is the node name so nodes are unique and the hierarchy reads left-to-right.
    /// </summary>
    public static class TomlTypeExtractor
    {
        public static void ExtractInto(ProjectModel project, TomlDocument doc, string filePath)
        {
            if (project == null || doc == null) return;
            EmitTable(project, doc.Root, "(root)", null, filePath);
        }

        private static void EmitTable(ProjectModel project, TomlTable table, string nodeName, string parentName, string filePath)
        {
            var model = new TypeModel(nodeName, "", TypeKind.ConcreteClass, project);
            model.SourceFilePath = filePath;
            model.SourceLineNumber = table.Line;
            // A containment edge from the parent table box to this one.
            if (parentName != null)
                model.BaseTypes.Add(new TypeReference(parentName, RelationshipKind.DependsOn));

            var childTables = new List<KeyValuePair<string, TomlTable>>();
            var childArrays = new List<KeyValuePair<string, TomlTableArray>>();

            foreach (string key in table.KeyOrder)
            {
                TomlItem item = table.Get(key);
                if (item is TomlTable sub && !sub.FromInlineTable)
                {
                    childTables.Add(new KeyValuePair<string, TomlTable>(key, sub));
                }
                else if (item is TomlTableArray arr)
                {
                    childArrays.Add(new KeyValuePair<string, TomlTableArray>(key, arr));
                    var f = new MemberModel(key + "[]", MemberKind.Field);
                    f.ReturnType = "table[" + arr.Elements.Count + "]";
                    f.AccessModifier = AccessModifier.Public;
                    model.Members.Add(f);
                }
                else
                {
                    // Scalar / array / inline-table value → a typed field on this box.
                    var f = new MemberModel(key, MemberKind.Field);
                    f.ReturnType = ValueKindLabel(item);
                    f.AccessModifier = AccessModifier.Public;
                    if (item != null) f.SourceLineNumber = item.Line;
                    model.Members.Add(f);
                }
            }

            project.Types.Add(model);

            // Recurse into sub-tables (each its own box, linked back to this one).
            foreach (var kv in childTables)
            {
                string childName = nodeName == "(root)" ? kv.Key : nodeName + "." + kv.Key;
                EmitTable(project, kv.Value, childName, nodeName, filePath);
            }
            // Array-of-tables: one box per array using the first element's schema (keeps the diagram compact).
            foreach (var kv in childArrays)
            {
                string childName = (nodeName == "(root)" ? kv.Key : nodeName + "." + kv.Key) + "[]";
                TomlTable schema = kv.Value.Elements.Count > 0 ? kv.Value.Elements[0] : new TomlTable();
                EmitArrayOfTables(project, schema, kv.Value.Elements.Count, childName, nodeName, filePath);
            }
        }

        private static void EmitArrayOfTables(ProjectModel project, TomlTable schema, int count, string nodeName, string parentName, string filePath)
        {
            var model = new TypeModel(nodeName, "", TypeKind.Struct, project); // Struct kind to visually distinguish AoT
            model.SourceFilePath = filePath;
            model.SourceLineNumber = schema.Line;
            model.BaseTypes.Add(new TypeReference(parentName, RelationshipKind.DependsOn));

            var countField = new MemberModel("«count»", MemberKind.Field);
            countField.ReturnType = count.ToString();
            countField.AccessModifier = AccessModifier.Public;
            model.Members.Add(countField);

            foreach (string key in schema.KeyOrder)
            {
                TomlItem item = schema.Get(key);
                if (item is TomlTable || item is TomlTableArray) continue; // keep AoT boxes flat/compact
                var f = new MemberModel(key, MemberKind.Field);
                f.ReturnType = ValueKindLabel(item);
                f.AccessModifier = AccessModifier.Public;
                model.Members.Add(f);
            }
            project.Types.Add(model);
        }

        private static string ValueKindLabel(TomlItem item)
        {
            var v = item as TomlValue;
            if (v == null) return item is TomlTable ? "inline-table" : "value";
            switch (v.Kind)
            {
                case TomlValueKind.String: return "string";
                case TomlValueKind.Integer: return "int";
                case TomlValueKind.Float: return "float";
                case TomlValueKind.Boolean: return "bool";
                case TomlValueKind.Array: return "array[" + (v.Array != null ? v.Array.Count : 0) + "]";
                case TomlValueKind.InlineTable: return "inline-table";
                default: return v.Kind.ToString().ToLowerInvariant();
            }
        }
    }
}
