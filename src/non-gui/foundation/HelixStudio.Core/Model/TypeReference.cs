using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// A lazy-resolved reference to a type. Used for base types and relationships.
    /// </summary>
    public sealed class TypeReference
    {
        private TypeModel _resolvedType;
        private SolutionModel _solution;

        public TypeReference(string fullyQualifiedName, RelationshipKind relationshipKind)
        {
            FullyQualifiedName = fullyQualifiedName ?? string.Empty;
            RelationshipKind = relationshipKind;
        }

        /// <summary>
        /// Fully qualified name of the referenced type.
        /// </summary>
        public string FullyQualifiedName { get; set; }

        /// <summary>
        /// The kind of relationship this reference represents (Inherits, Implements, DependsOn).
        /// </summary>
        public RelationshipKind RelationshipKind { get; set; }

        /// <summary>
        /// Whether this reference points to a type outside the solution.
        /// </summary>
        public bool IsExternal
        {
            get
            {
                // Trigger lazy resolution first
                var resolved = ResolvedType;
                return _solution != null && resolved == null;
            }
        }

        /// <summary>
        /// Gets the resolved type, or null if the type is external or not found.
        /// </summary>
        public TypeModel ResolvedType
        {
            get
            {
                if (_resolvedType != null)
                    return _resolvedType;

                if (_solution != null)
                    _resolvedType = _solution.FindType(FullyQualifiedName);

                return _resolvedType;
            }
        }

        /// <summary>
        /// The FQN to use when matching this reference against the type/visual maps (or
        /// <see cref="SolutionModel.FindType"/>): a base is often written by short name
        /// (<c>class Circle(Shape)</c> stores "Shape"), while types and node visuals are keyed by
        /// the fully-qualified name (<c>pkg.mod.Shape</c>). Prefer the resolved type's FQN; fall back
        /// to the raw <see cref="FullyQualifiedName"/> when it can't be resolved. Use this for any
        /// node-/type-lookup so the short-vs-FQN mismatch (passes #28/#31/#32) can't recur.
        /// </summary>
        public string EffectiveFqn
        {
            get
            {
                TypeModel resolved = ResolvedType;
                return resolved != null ? resolved.FullyQualifiedName : FullyQualifiedName;
            }
        }

        /// <summary>
        /// Simple name of the referenced type (last part of FQN).
        /// </summary>
        public string SimpleName
        {
            get
            {
                if (string.IsNullOrEmpty(FullyQualifiedName))
                    return string.Empty;

                var lastDot = FullyQualifiedName.LastIndexOf('.');
                return lastDot >= 0 ? FullyQualifiedName.Substring(lastDot + 1) : FullyQualifiedName;
            }
        }

        /// <summary>
        /// Bind this reference to a solution for lazy resolution.
        /// </summary>
        public void Bind(SolutionModel solution)
        {
            _solution = solution;
            _resolvedType = null; // Clear cached resolution
        }

        /// <summary>
        /// Force resolution of the type reference.
        /// </summary>
        public TypeModel Resolve(SolutionModel solution)
        {
            _solution = solution;
            _resolvedType = null;
            return ResolvedType;
        }

        /// <summary>
        /// Invalidate the cached resolution (call after solution changes).
        /// </summary>
        public void Invalidate()
        {
            _resolvedType = null;
        }
    }
}
