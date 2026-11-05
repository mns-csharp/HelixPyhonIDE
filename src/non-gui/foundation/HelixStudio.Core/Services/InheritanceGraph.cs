using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Services
{
    /// <summary>
    /// Provides inheritance hierarchy navigation for a solution.
    /// Supports both direct and indirect relationships across C# and C++ types.
    /// </summary>
    public sealed class InheritanceGraph
    {
        private readonly SolutionModel _solution;

        // Cached mappings: FQN -> list of FQNs
        private Dictionary<string, List<string>> _directDescendants;
        private Dictionary<string, List<string>> _directAncestors;

        public InheritanceGraph(SolutionModel solution)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
            Rebuild();
        }

        /// <summary>
        /// Rebuild the graph caches from the current solution state.
        /// Call after any structural changes to types/inheritance.
        /// </summary>
        public void Rebuild()
        {
            _directDescendants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _directAncestors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in _solution.GetAllTypes())
            {
                var fqn = type.FullyQualifiedName;

                if (!_directAncestors.ContainsKey(fqn))
                    _directAncestors[fqn] = new List<string>();

                foreach (var baseRef in type.BaseTypes)
                {
                    var baseFqn = baseRef.FullyQualifiedName;

                    _directAncestors[fqn].Add(baseFqn);

                    if (!_directDescendants.ContainsKey(baseFqn))
                        _directDescendants[baseFqn] = new List<string>();

                    _directDescendants[baseFqn].Add(fqn);
                }
            }
        }

        /// <summary>
        /// Get all direct descendants (one level down) of the given type.
        /// </summary>
        public IReadOnlyList<TypeModel> GetDirectDescendants(TypeModel type)
        {
            List<string> fqns;
            if (!_directDescendants.TryGetValue(type.FullyQualifiedName, out fqns))
                return new TypeModel[0];

            return fqns
                .Select(fqn => _solution.FindType(fqn))
                .Where(t => t != null)
                .ToList();
        }

        /// <summary>
        /// Get all descendants (transitive closure) of the given type.
        /// Includes direct and indirect derived classes/implementers.
        /// </summary>
        public IReadOnlyList<TypeModel> GetAllDescendants(TypeModel type)
        {
            var result = new List<TypeModel>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectDescendants(type.FullyQualifiedName, result, visited);
            return result;
        }

        private void CollectDescendants(string fqn, List<TypeModel> result, HashSet<string> visited)
        {
            List<string> children;
            if (!_directDescendants.TryGetValue(fqn, out children))
                return;

            foreach (var childFqn in children)
            {
                if (!visited.Add(childFqn))
                    continue;

                var child = _solution.FindType(childFqn);
                if (child != null)
                {
                    result.Add(child);
                }
                CollectDescendants(childFqn, result, visited);
            }
        }

        /// <summary>
        /// Get all direct ancestors (one level up) of the given type.
        /// </summary>
        public IReadOnlyList<TypeModel> GetDirectAncestors(TypeModel type)
        {
            List<string> fqns;
            if (!_directAncestors.TryGetValue(type.FullyQualifiedName, out fqns))
                return new TypeModel[0];

            return fqns
                .Select(fqn => _solution.FindType(fqn))
                .Where(t => t != null)
                .ToList();
        }

        /// <summary>
        /// Get all ancestors (transitive closure) of the given type.
        /// </summary>
        public IReadOnlyList<TypeModel> GetAllAncestors(TypeModel type)
        {
            var result = new List<TypeModel>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectAncestors(type.FullyQualifiedName, result, visited);
            return result;
        }

        private void CollectAncestors(string fqn, List<TypeModel> result, HashSet<string> visited)
        {
            if (!visited.Add(fqn))
                return;

            List<string> parents;
            if (!_directAncestors.TryGetValue(fqn, out parents))
                return;

            foreach (var parentFqn in parents)
            {
                var parent = _solution.FindType(parentFqn);
                if (parent != null)
                {
                    result.Add(parent);
                }
                CollectAncestors(parentFqn, result, visited);
            }
        }

        /// <summary>
        /// Get all types that directly implement a given interface.
        /// </summary>
        public IReadOnlyList<TypeModel> GetDirectImplementers(TypeModel interfaceType)
        {
            if (interfaceType.TypeKind != TypeKind.Interface)
                return new TypeModel[0];

            var result = new List<TypeModel>();
            List<string> descendants;
            if (_directDescendants.TryGetValue(interfaceType.FullyQualifiedName, out descendants))
            {
                foreach (var fqn in descendants)
                {
                    var type = _solution.FindType(fqn);
                    if (type != null)
                    {
                        // Verify it's an Implements relationship, not Inherits
                        var baseRef = type.BaseTypes.FirstOrDefault(bt =>
                            bt.FullyQualifiedName == interfaceType.FullyQualifiedName &&
                            bt.RelationshipKind == RelationshipKind.Implements);
                        if (baseRef != null)
                        {
                            result.Add(type);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get the depth of the inheritance chain for a type.
        /// </summary>
        public int GetInheritanceDepth(TypeModel type)
        {
            return GetInheritanceDepthInternal(type.FullyQualifiedName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private int GetInheritanceDepthInternal(string fqn, HashSet<string> visited)
        {
            if (!visited.Add(fqn))
                return 0;

            List<string> parents;
            if (!_directAncestors.TryGetValue(fqn, out parents) || parents.Count == 0)
                return 0;

            int maxDepth = 0;
            foreach (var parentFqn in parents)
            {
                var depth = GetInheritanceDepthInternal(parentFqn, visited);
                if (depth + 1 > maxDepth)
                    maxDepth = depth + 1;
            }
            return maxDepth;
        }
    }
}
