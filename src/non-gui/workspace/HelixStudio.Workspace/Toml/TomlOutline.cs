using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>Renders a <see cref="TomlDocument"/> as an indented outline (for display / diagnostics).</summary>
    public static class TomlOutline
    {
        public static string Render(TomlDocument doc)
        {
            var sb = new StringBuilder();
            if (doc == null) return "";
            RenderTable(sb, doc.Root, 0);
            if (doc.Diagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("diagnostics:");
                foreach (var d in doc.Diagnostics) sb.AppendLine("  " + d);
            }
            return sb.ToString();
        }

        private static void RenderTable(StringBuilder sb, TomlTable t, int depth)
        {
            string indent = new string(' ', depth * 2);
            foreach (string key in t.KeyOrder)
            {
                TomlItem item = t.Get(key);
                var val = item as TomlValue;
                var tbl = item as TomlTable;
                var arr = item as TomlTableArray;
                if (tbl != null)
                {
                    sb.AppendLine(indent + "[" + key + "]  (table)");
                    RenderTable(sb, tbl, depth + 1);
                }
                else if (arr != null)
                {
                    sb.AppendLine(indent + "[[" + key + "]]  (array-of-tables, " + arr.Elements.Count + ")");
                    for (int i = 0; i < arr.Elements.Count; i++)
                    {
                        sb.AppendLine(indent + "  - element " + i + ":");
                        RenderTable(sb, arr.Elements[i], depth + 2);
                    }
                }
                else if (val != null)
                {
                    sb.AppendLine(indent + key + " : " + val.Kind + " = " + ValuePreview(val));
                }
            }
        }

        private static string ValuePreview(TomlValue v)
        {
            switch (v.Kind)
            {
                case TomlValueKind.String: return "\"" + Truncate(v.StringValue) + "\"";
                case TomlValueKind.Integer: return v.IntValue.ToString();
                case TomlValueKind.Float: return v.FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case TomlValueKind.Boolean: return v.BoolValue ? "true" : "false";
                case TomlValueKind.Array:
                    var items = new List<string>();
                    if (v.Array != null) foreach (var e in v.Array) items.Add(ValuePreview(e));
                    return "[" + Truncate(string.Join(", ", items.ToArray())) + "]";
                case TomlValueKind.InlineTable:
                    return "{ " + (v.Inline != null ? v.Inline.KeyOrder.Count + " keys" : "0") + " }";
                default:
                    return v.StringValue ?? v.Raw;
            }
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Length > 70 ? s.Substring(0, 69) + "…" : s;
        }
    }
}
