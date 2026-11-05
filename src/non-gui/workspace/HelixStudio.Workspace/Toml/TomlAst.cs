using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>The concrete type of a TOML value (TOML v1.0.0).</summary>
    public enum TomlValueKind
    {
        String, Integer, Float, Boolean,
        OffsetDateTime, LocalDateTime, LocalDate, LocalTime,
        Array, InlineTable,
    }

    /// <summary>Base for anything that can live under a key: a scalar/array/inline value, a table, or an array-of-tables.</summary>
    public abstract class TomlItem
    {
        public int Line;
        public int Column;

        /// <summary>Whole-line <c># comments</c> that immediately precede this item (each entry includes the <c>#</c>).
        /// Null until any are captured — so trivia is opt-in and never allocated for comment-free files.</summary>
        public List<string> LeadingComments;

        /// <summary>A same-line trailing <c># comment</c> after this item's value/header (includes the <c>#</c>), or null.</summary>
        public string TrailingComment;

        /// <summary>Attach a leading comment line, allocating the list on first use.</summary>
        public void AddLeadingComment(string comment)
        {
            if (string.IsNullOrEmpty(comment)) return;
            if (LeadingComments == null) LeadingComments = new List<string>();
            LeadingComments.Add(comment);
        }
    }

    /// <summary>A scalar, array, or inline-table value.</summary>
    public sealed class TomlValue : TomlItem
    {
        public TomlValueKind Kind;
        public string Raw = "";           // exactly as written (for round-trip / display)

        public string StringValue;        // Kind == String
        public long IntValue;             // Kind == Integer
        public double FloatValue;         // Kind == Float
        public bool BoolValue;            // Kind == Boolean
        public List<TomlValue> Array;     // Kind == Array
        public TomlTable Inline;          // Kind == InlineTable

        public override string ToString() { return Raw; }
    }

    /// <summary>A TOML table: ordered key → item map. Items are values, sub-tables, or arrays-of-tables.</summary>
    public sealed class TomlTable : TomlItem
    {
        public readonly List<string> KeyOrder = new List<string>();
        public readonly Dictionary<string, TomlItem> Items = new Dictionary<string, TomlItem>();

        public bool ExplicitlyDefined;    // via a [header]
        public bool FromInlineTable;      // { ... } — closed, cannot be extended
        public bool DefinedByDottedKey;   // implicitly created by a dotted key a.b.c = v

        public TomlItem Get(string key)
        {
            TomlItem v;
            return Items.TryGetValue(key, out v) ? v : null;
        }

        public void Set(string key, TomlItem item)
        {
            if (!Items.ContainsKey(key)) KeyOrder.Add(key);
            Items[key] = item;
        }
    }

    /// <summary>An array-of-tables node (<c>[[a.b]]</c>) — an ordered list of table elements.</summary>
    public sealed class TomlTableArray : TomlItem
    {
        public readonly List<TomlTable> Elements = new List<TomlTable>();
    }

    /// <summary>A parse diagnostic (syntax error, duplicate key, redefined table, …).</summary>
    public sealed class TomlDiagnostic
    {
        public int Line;
        public int Column;
        public string Code = "";
        public string Message = "";
        public bool IsError = true;

        public override string ToString()
        {
            return "(" + Line + "," + Column + ") " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// A parsed TOML document (FR-CORE-05): the root table tree plus parse diagnostics — the real TOML
    /// front-end that supersedes the regex subset in <c>PyProjectReader</c> and underpins the TOML linter
    /// (FR-LINT-03) and analyzer (FR-ANA-04).
    /// </summary>
    public sealed class TomlDocument
    {
        public readonly TomlTable Root = new TomlTable();
        public readonly List<TomlDiagnostic> Diagnostics = new List<TomlDiagnostic>();

        public bool HasErrors
        {
            get { for (int i = 0; i < Diagnostics.Count; i++) if (Diagnostics[i].IsError) return true; return false; }
        }

        /// <summary>Resolve a dotted path (e.g. <c>tool.helixstudio.kind</c>) to its item, or null.</summary>
        public TomlItem Find(params string[] path)
        {
            TomlItem cur = Root;
            foreach (string seg in path)
            {
                var tbl = cur as TomlTable;
                if (tbl == null) return null;
                cur = tbl.Get(seg);
                if (cur == null) return null;
            }
            return cur;
        }
    }
}
