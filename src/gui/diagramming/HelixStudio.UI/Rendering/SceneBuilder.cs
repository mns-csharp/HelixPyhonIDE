namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Text;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class SceneBuilder
    {
        private readonly SceneGraph _scene;
        private readonly EdgeRouter _edgeRouter;

        public SceneBuilder(SceneGraph scene)
        {
            if (scene == null)
            {
                throw new ArgumentNullException("scene");
            }
            _scene = scene;
            _edgeRouter = new EdgeRouter();
        }

        public void RebuildScene(Diagram diagram)
        {
            if (diagram == null)
            {
                throw new ArgumentNullException("diagram");
            }

            _scene.Clear();

            // Add grid background
            GridVisual grid = new GridVisual(Color.FromArgb(230, 230, 230));
            _scene.AddRenderable(grid);

            // Create NodeVisual for each TypeNode
            for (int i = 0; i < diagram.Nodes.Count; i++)
            {
                TypeNode typeNode = diagram.Nodes[i];
                string nodeId = typeNode.Id.ToString();
                RectangleF bounds = new RectangleF(
                    typeNode.PositionX,
                    typeNode.PositionY,
                    typeNode.Width,
                    typeNode.Height);

                INodeStyleStrategy style = NodeStyleFactory.GetStyle(typeNode.TypeKind);
                NodeVisual nodeVisual = new NodeVisual(nodeId, bounds, style);

                string typeKindLabel = FormatTypeKindLabel(typeNode.TypeKind);
                string genericParams = FormatGenericParameters(typeNode.GenericParameters);
                List<string> memberLines = FormatMemberLines(typeNode);

                nodeVisual.UpdateFromModel(typeNode.Name, typeKindLabel, genericParams, memberLines);
                _scene.AddNode(nodeVisual);
            }

            // Create EdgeVisual for each Relationship
            for (int i = 0; i < diagram.Relationships.Count; i++)
            {
                Relationship rel = diagram.Relationships[i];
                string edgeId = rel.Id.ToString();
                string sourceId = rel.Source.Id.ToString();
                string targetId = rel.Target.Id.ToString();

                IEdgeStyleStrategy style = EdgeStyleFactory.GetStyle(rel.Kind);
                EdgeVisual edgeVisual = new EdgeVisual(edgeId, sourceId, targetId, style);
                _scene.AddEdge(edgeVisual);
            }

            // Route all edges
            _edgeRouter.RouteAllEdges(_scene);
        }

        public void UpdateNodeFromDomain(TypeNode node)
        {
            if (node == null)
            {
                return;
            }

            string nodeId = node.Id.ToString();
            NodeVisual visual = _scene.FindNodeById(nodeId);
            if (visual == null)
            {
                return;
            }

            string typeKindLabel = FormatTypeKindLabel(node.TypeKind);
            string genericParams = FormatGenericParameters(node.GenericParameters);
            List<string> memberLines = FormatMemberLines(node);

            visual.UpdateFromModel(node.Name, typeKindLabel, genericParams, memberLines);
        }

        private static string FormatTypeKindLabel(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return "<<interface>>";
                case TypeKind.AbstractClass:
                    return "<<abstract>>";
                default:
                    return "";
            }
        }

        private static string FormatGenericParameters(List<GenericParameter> genericParams)
        {
            if (genericParams == null || genericParams.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < genericParams.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(genericParams[i].Name);
            }
            return sb.ToString();
        }

        private static List<string> FormatMemberLines(TypeNode node)
        {
            List<string> lines = new List<string>();

            if (node.Members == null)
            {
                return lines;
            }

            for (int i = 0; i < node.Members.Count; i++)
            {
                MemberDefinition member = node.Members[i];
                string accessPrefix = FormatAccessModifier(member.AccessModifier);

                if (member.MemberKind == MemberKind.Method)
                {
                    string parameters = FormatMethodParameters(member.Parameters);
                    // Python has no "void"; only a real return annotation produces a UML suffix.
                    // (Matches SolutionProjectionBuilder — see iter 355.)
                    string returnSuffix = string.IsNullOrEmpty(member.ReturnType)
                        ? string.Empty
                        : " : " + member.ReturnType;
                    string line = string.Format("{0} {1}({2}){3}", accessPrefix, member.Name, parameters, returnSuffix);
                    lines.Add(line);
                }
                else if (member.MemberKind == MemberKind.Property)
                {
                    // No invented "object" type for an unannotated Python attribute (iter 357).
                    string accessors = FormatPropertyAccessors(member.HasGetter, member.HasSetter);
                    string typeSuffix = string.IsNullOrEmpty(member.ReturnType)
                        ? string.Empty
                        : " : " + member.ReturnType;
                    string accessorSuffix = string.IsNullOrEmpty(accessors) ? string.Empty : " " + accessors;
                    string line = string.Format("{0} {1}{2}{3}", accessPrefix, member.Name, typeSuffix, accessorSuffix);
                    lines.Add(line);
                }
            }

            return lines;
        }

        private static string FormatAccessModifier(AccessModifier modifier)
        {
            switch (modifier)
            {
                case AccessModifier.Public:
                    return "+";
                case AccessModifier.Protected:
                    return "#";
                case AccessModifier.Internal:
                    return "~";
                case AccessModifier.Private:
                    return "-";
                default:
                    return "+";
            }
        }

        private static string FormatMethodParameters(List<MethodParameter> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                // No dangling colon on an unannotated Python parameter (iter 355).
                sb.Append(string.IsNullOrEmpty(parameters[i].TypeName)
                    ? parameters[i].Name
                    : string.Format("{0}: {1}", parameters[i].Name, parameters[i].TypeName));
            }
            return sb.ToString();
        }

        private static string FormatPropertyAccessors(bool hasGetter, bool hasSetter)
        {
            if (hasGetter && hasSetter)
            {
                return "{ get; set; }";
            }
            if (hasGetter)
            {
                return "{ get; }";
            }
            if (hasSetter)
            {
                return "{ set; }";
            }
            return "";
        }
    }
}
