namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a parameter of a method.
    /// </summary>
    public sealed class MethodParameterModel
    {
        public MethodParameterModel(string name, string typeName)
        {
            Name = name ?? string.Empty;
            TypeName = typeName ?? string.Empty;
        }

        /// <summary>
        /// Name of the parameter.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Type of the parameter as a string.
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>
        /// Whether this parameter has a default value.
        /// </summary>
        public bool HasDefaultValue { get; set; }

        /// <summary>
        /// The default value expression (if any).
        /// </summary>
        public string DefaultValue { get; set; }

        /// <summary>
        /// Whether this is a params array parameter.
        /// </summary>
        public bool IsParams { get; set; }

        /// <summary>
        /// Whether this is a ref parameter.
        /// </summary>
        public bool IsRef { get; set; }

        /// <summary>
        /// Whether this is an out parameter.
        /// </summary>
        public bool IsOut { get; set; }

        /// <summary>
        /// Whether this is an "in" parameter (read-only reference).
        /// </summary>
        public bool IsIn { get; set; }
    }
}
