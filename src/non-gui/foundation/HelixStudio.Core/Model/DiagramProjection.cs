using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Builds an old-style Diagram from SolutionModel + LayoutModel.
    /// Provides backward compatibility with existing analysis rules.
    /// </summary>
    public sealed class DiagramProjection
    {
        private readonly SolutionModel _solution;
        private readonly LayoutModel _layout;
        private readonly Dictionary<string, TypeNode> _fqnToNode;

        public DiagramProjection(SolutionModel solution, LayoutModel layout)
        {
            _solution = solution ?? throw new ArgumentNullException(nameof(solution));
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
            _fqnToNode = new Dictionary<string, TypeNode>();
        }

        /// <summary>
        /// Build a Diagram from the current SolutionModel and LayoutModel.
        /// </summary>
        public Diagram Build()
        {
            _fqnToNode.Clear();

            var nodes = new List<TypeNode>();
            var relationships = new List<Relationship>();

            // Create TypeNodes from TypeModels
            foreach (var typeModel in _solution.GetAllTypes())
            {
                var node = CreateTypeNode(typeModel);
                nodes.Add(node);
                _fqnToNode[typeModel.FullyQualifiedName] = node;
            }

            // Create Relationships from TypeReferences
            foreach (var typeModel in _solution.GetAllTypes())
            {
                TypeNode sourceNode;
                if (!_fqnToNode.TryGetValue(typeModel.FullyQualifiedName, out sourceNode))
                    continue;

                foreach (var baseType in typeModel.BaseTypes)
                {
                    // A base written by short name (`class Circle(Shape)`) stores "Shape"; the node
                    // map is keyed by the resolved FQN. Use EffectiveFqn so inheritance/implementation
                    // relationships are actually emitted (else `relationships` is silently empty).
                    string targetFqn = baseType.EffectiveFqn;
                    TypeNode targetNode;
                    if (string.IsNullOrEmpty(targetFqn) || !_fqnToNode.TryGetValue(targetFqn, out targetNode))
                        continue;

                    var relationship = CreateRelationship(baseType.RelationshipKind, sourceNode, targetNode);
                    ApplyEdgeLayout(relationship, typeModel.FullyQualifiedName, targetFqn);
                    relationships.Add(relationship);
                }
            }

            return new Diagram(_solution.Name, GetDefaultNamespace(), nodes, relationships, _solution.LastParsedUtc);
        }

        private TypeNode CreateTypeNode(TypeModel typeModel)
        {
            var node = new TypeNode(typeModel.Name, typeModel.TypeKind)
            {
                Namespace = typeModel.Namespace
            };

            // Copy generic parameters
            foreach (var gp in typeModel.GenericParameters)
            {
                node.GenericParameters.Add(new GenericParameter(gp.Name, new List<string>(gp.Constraints)));
            }

            // Copy members
            foreach (var member in typeModel.Members)
            {
                var memberDef = CreateMemberDefinition(member);
                node.Members.Add(memberDef);
            }

            // Apply layout from LayoutModel
            ApplyNodeLayout(node, typeModel.FullyQualifiedName);

            return node;
        }

        private MemberDefinition CreateMemberDefinition(MemberModel member)
        {
            var memberDef = new MemberDefinition(member.Name, member.MemberKind)
            {
                ReturnType = member.ReturnType,
                AccessModifier = member.AccessModifier,
                IsAbstract = member.IsAbstract,
                IsVirtual = member.IsVirtual,
                IsStatic = member.IsStatic,
                IsOverride = member.IsOverride,
                HasGetter = member.HasGetter,
                HasSetter = member.HasSetter
            };

            // Copy parameters
            foreach (var param in member.Parameters)
            {
                memberDef.Parameters.Add(new MethodParameter(param.Name, param.TypeName));
            }

            return memberDef;
        }

        private Relationship CreateRelationship(RelationshipKind kind, TypeNode source, TypeNode target)
        {
            return new Relationship(kind, source, target);
        }

        private void ApplyNodeLayout(TypeNode node, string fullyQualifiedName)
        {
            var layoutData = _layout.GetNodeLayout(fullyQualifiedName);
            if (layoutData != null)
            {
                node.PositionX = layoutData.PositionX;
                node.PositionY = layoutData.PositionY;
                node.Width = layoutData.Width;
                node.Height = layoutData.Height;
                node.IsCollapsed = layoutData.IsCollapsed;
            }
            else
            {
                // Default layout
                node.PositionX = 0;
                node.PositionY = 0;
                node.Width = 200f;
                node.Height = 150f;
                node.IsCollapsed = false;
            }
        }

        private void ApplyEdgeLayout(Relationship relationship, string sourceFQN, string targetFQN)
        {
            var edgeKey = LayoutModel.CreateEdgeKey(sourceFQN, targetFQN);
            EdgeLayoutData edgeLayout;
            if (_layout.EdgeLayouts.TryGetValue(edgeKey, out edgeLayout))
            {
                foreach (var point in edgeLayout.RoutingPoints)
                {
                    relationship.RoutingPoints.Add(new DiagramPoint(point.X, point.Y));
                }
            }
        }

        private string GetDefaultNamespace()
        {
            // Return the default namespace of the first project, or empty string
            var firstProject = _solution.Projects.FirstOrDefault();
            return firstProject?.DefaultNamespace ?? string.Empty;
        }

        /// <summary>
        /// Find the TypeNode corresponding to a TypeModel.
        /// </summary>
        public TypeNode FindNode(TypeModel typeModel)
        {
            if (typeModel == null)
                return null;

            TypeNode node;
            _fqnToNode.TryGetValue(typeModel.FullyQualifiedName, out node);
            return node;
        }

        /// <summary>
        /// Find the TypeModel corresponding to a TypeNode.
        /// </summary>
        public TypeModel FindTypeModel(TypeNode node)
        {
            if (node == null)
                return null;

            var fqn = string.IsNullOrEmpty(node.Namespace)
                ? node.Name
                : node.Namespace + "." + node.Name;

            return _solution.FindType(fqn);
        }
    }
}
