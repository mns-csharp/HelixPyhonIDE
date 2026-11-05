using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a generic type parameter (e.g., T in List&lt;T&gt;).
    /// </summary>
    public sealed class GenericParameterModel
    {
        public GenericParameterModel(string name)
        {
            Name = name ?? string.Empty;
            Constraints = new List<string>();
        }

        /// <summary>
        /// Name of the generic parameter (e.g., "T", "TKey").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Type constraints (e.g., "class", "struct", "IDisposable").
        /// </summary>
        public List<string> Constraints { get; }

        /// <summary>
        /// Whether this parameter has the "new()" constraint.
        /// </summary>
        public bool HasNewConstraint { get; set; }

        /// <summary>
        /// Whether this parameter is covariant (out T).
        /// </summary>
        public bool IsCovariant { get; set; }

        /// <summary>
        /// Whether this parameter is contravariant (in T).
        /// </summary>
        public bool IsContravariant { get; set; }
    }
}
