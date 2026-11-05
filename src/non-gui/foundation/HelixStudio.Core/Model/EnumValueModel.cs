namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a named value inside an enum type.
    /// </summary>
    public sealed class EnumValueModel
    {
        public EnumValueModel(string name)
        {
            Name = name ?? string.Empty;
        }

        /// <summary>
        /// Name of the enum member.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Optional explicit value expression (e.g., "1", "0x10", "None").
        /// Null when the value is implicit.
        /// </summary>
        public string ValueExpression { get; set; }
    }
}
