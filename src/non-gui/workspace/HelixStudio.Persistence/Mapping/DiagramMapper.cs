namespace HelixPythonNamespace.Persistence.Mapping
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Persistence.Dto;

    public sealed class DiagramMapper
    {
        public DiagramFileDto ToDto(Diagram diagram, int formatVersion)
        {
            DiagramFileDto fileDto = new DiagramFileDto();
            fileDto.FormatVersion = formatVersion;
            fileDto.AppVersion = "1.0.0";
            fileDto.CreatedUtc = diagram.CreatedUtc.ToString("o");
            fileDto.ModifiedUtc = DateTime.UtcNow.ToString("o");

            DiagramDto diagramDto = new DiagramDto();
            diagramDto.Name = diagram.Name;
            diagramDto.DefaultNamespace = diagram.DefaultNamespace;

            List<NodeDto> nodeDtos = new List<NodeDto>();
            for (int i = 0; i < diagram.Nodes.Count; i++)
            {
                nodeDtos.Add(MapNodeToDto(diagram.Nodes[i]));
            }
            diagramDto.Nodes = nodeDtos;

            List<RelationshipDto> relDtos = new List<RelationshipDto>();
            for (int i = 0; i < diagram.Relationships.Count; i++)
            {
                relDtos.Add(MapRelationshipToDto(diagram.Relationships[i]));
            }
            diagramDto.Relationships = relDtos;

            fileDto.Diagram = diagramDto;
            return fileDto;
        }

        public Diagram ToDomain(DiagramFileDto dto)
        {
            DiagramDto diagramDto = dto.Diagram;

            // Phase 1: Create all TypeNodes and build an Id-to-node lookup
            Dictionary<string, TypeNode> nodeById = new Dictionary<string, TypeNode>();
            List<TypeNode> nodes = new List<TypeNode>();

            if (diagramDto.Nodes != null)
            {
                for (int i = 0; i < diagramDto.Nodes.Count; i++)
                {
                    NodeDto nodeDto = diagramDto.Nodes[i];
                    TypeNode node = MapNodeToDomain(nodeDto);
                    nodeById[nodeDto.Id] = node;
                    nodes.Add(node);
                }
            }

            // Phase 2: Create Relationships, resolving source/target by Id lookup
            List<Relationship> relationships = new List<Relationship>();

            if (diagramDto.Relationships != null)
            {
                for (int i = 0; i < diagramDto.Relationships.Count; i++)
                {
                    RelationshipDto relDto = diagramDto.Relationships[i];
                    Relationship rel = MapRelationshipToDomain(relDto, nodeById);
                    relationships.Add(rel);
                }
            }

            DateTime createdUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(dto.CreatedUtc))
            {
                createdUtc = DateTime.Parse(dto.CreatedUtc);
            }

            Diagram diagram = new Diagram(
                diagramDto.Name,
                diagramDto.DefaultNamespace,
                nodes,
                relationships,
                createdUtc);

            return diagram;
        }

        private NodeDto MapNodeToDto(TypeNode node)
        {
            NodeDto dto = new NodeDto();
            dto.Id = node.Id.ToString();
            dto.TypeKind = TypeKindToString(node.TypeKind);
            dto.Name = node.Name;
            dto.Namespace = node.Namespace;
            dto.PositionX = (double)node.PositionX;
            dto.PositionY = (double)node.PositionY;
            dto.Width = (double)node.Width;
            dto.Height = (double)node.Height;
            dto.IsCollapsed = node.IsCollapsed;

            List<GenericParameterDto> genericDtos = new List<GenericParameterDto>();
            for (int i = 0; i < node.GenericParameters.Count; i++)
            {
                genericDtos.Add(MapGenericParameterToDto(node.GenericParameters[i]));
            }
            dto.GenericParameters = genericDtos;

            List<MemberDto> memberDtos = new List<MemberDto>();
            for (int i = 0; i < node.Members.Count; i++)
            {
                memberDtos.Add(MapMemberToDto(node.Members[i]));
            }
            dto.Members = memberDtos;

            return dto;
        }

        private TypeNode MapNodeToDomain(NodeDto dto)
        {
            TypeKind typeKind = StringToTypeKind(dto.TypeKind);
            TypeNode node = new TypeNode(dto.Name, typeKind);
            node.Id = Guid.Parse(dto.Id);
            node.Namespace = dto.Namespace;
            node.PositionX = (float)dto.PositionX;
            node.PositionY = (float)dto.PositionY;
            node.Width = (float)dto.Width;
            node.Height = (float)dto.Height;
            node.IsCollapsed = dto.IsCollapsed;

            List<GenericParameter> genericParams = new List<GenericParameter>();
            if (dto.GenericParameters != null)
            {
                for (int i = 0; i < dto.GenericParameters.Count; i++)
                {
                    genericParams.Add(MapGenericParameterToDomain(dto.GenericParameters[i]));
                }
            }
            node.GenericParameters = genericParams;

            List<MemberDefinition> members = new List<MemberDefinition>();
            if (dto.Members != null)
            {
                for (int i = 0; i < dto.Members.Count; i++)
                {
                    members.Add(MapMemberToDomain(dto.Members[i]));
                }
            }
            node.Members = members;

            return node;
        }

        private MemberDto MapMemberToDto(MemberDefinition member)
        {
            MemberDto dto = new MemberDto();
            dto.MemberKind = MemberKindToString(member.MemberKind);
            dto.Name = member.Name;
            dto.ReturnType = member.ReturnType;
            dto.AccessModifier = AccessModifierToString(member.AccessModifier);
            dto.IsAbstract = member.IsAbstract;
            dto.IsVirtual = member.IsVirtual;
            dto.IsStatic = member.IsStatic;
            dto.IsOverride = member.IsOverride;
            dto.HasGetter = member.HasGetter;
            dto.HasSetter = member.HasSetter;

            List<MethodParameterDto> paramDtos = new List<MethodParameterDto>();
            for (int i = 0; i < member.Parameters.Count; i++)
            {
                paramDtos.Add(MapMethodParameterToDto(member.Parameters[i]));
            }
            dto.Parameters = paramDtos;

            return dto;
        }

        private MemberDefinition MapMemberToDomain(MemberDto dto)
        {
            MemberKind memberKind = StringToMemberKind(dto.MemberKind);
            MemberDefinition member = new MemberDefinition(dto.Name, memberKind);
            member.ReturnType = dto.ReturnType;
            member.AccessModifier = StringToAccessModifier(dto.AccessModifier);
            member.IsAbstract = dto.IsAbstract;
            member.IsVirtual = dto.IsVirtual;
            member.IsStatic = dto.IsStatic;
            member.IsOverride = dto.IsOverride;
            member.HasGetter = dto.HasGetter;
            member.HasSetter = dto.HasSetter;

            List<MethodParameter> parameters = new List<MethodParameter>();
            if (dto.Parameters != null)
            {
                for (int i = 0; i < dto.Parameters.Count; i++)
                {
                    parameters.Add(MapMethodParameterToDomain(dto.Parameters[i]));
                }
            }
            member.Parameters = parameters;

            return member;
        }

        private MethodParameterDto MapMethodParameterToDto(MethodParameter param)
        {
            MethodParameterDto dto = new MethodParameterDto();
            dto.Name = param.Name;
            dto.TypeName = param.TypeName;
            return dto;
        }

        private MethodParameter MapMethodParameterToDomain(MethodParameterDto dto)
        {
            return new MethodParameter(dto.Name, dto.TypeName);
        }

        private GenericParameterDto MapGenericParameterToDto(GenericParameter param)
        {
            GenericParameterDto dto = new GenericParameterDto();
            dto.Name = param.Name;
            dto.Constraints = new List<string>(param.Constraints);
            return dto;
        }

        private GenericParameter MapGenericParameterToDomain(GenericParameterDto dto)
        {
            List<string> constraints = new List<string>();
            if (dto.Constraints != null)
            {
                constraints = new List<string>(dto.Constraints);
            }
            return new GenericParameter(dto.Name, constraints);
        }

        private RelationshipDto MapRelationshipToDto(Relationship relationship)
        {
            RelationshipDto dto = new RelationshipDto();
            dto.Id = relationship.Id.ToString();
            dto.Kind = RelationshipKindToString(relationship.Kind);
            dto.SourceNodeId = relationship.Source.Id.ToString();
            dto.TargetNodeId = relationship.Target.Id.ToString();

            List<PointDto> pointDtos = new List<PointDto>();
            for (int i = 0; i < relationship.RoutingPoints.Count; i++)
            {
                PointDto pointDto = new PointDto();
                pointDto.X = (double)relationship.RoutingPoints[i].X;
                pointDto.Y = (double)relationship.RoutingPoints[i].Y;
                pointDtos.Add(pointDto);
            }
            dto.RoutingPoints = pointDtos;

            return dto;
        }

        private Relationship MapRelationshipToDomain(RelationshipDto dto, Dictionary<string, TypeNode> nodeById)
        {
            TypeNode source;
            if (!nodeById.TryGetValue(dto.SourceNodeId, out source))
            {
                throw new InvalidOperationException(
                    string.Format("Source node with Id '{0}' not found.", dto.SourceNodeId));
            }

            TypeNode target;
            if (!nodeById.TryGetValue(dto.TargetNodeId, out target))
            {
                throw new InvalidOperationException(
                    string.Format("Target node with Id '{0}' not found.", dto.TargetNodeId));
            }

            RelationshipKind kind = StringToRelationshipKind(dto.Kind);
            Relationship relationship = new Relationship(kind, source, target);
            relationship.Id = Guid.Parse(dto.Id);

            List<DiagramPoint> routingPoints = new List<DiagramPoint>();
            if (dto.RoutingPoints != null)
            {
                for (int i = 0; i < dto.RoutingPoints.Count; i++)
                {
                    PointDto pointDto = dto.RoutingPoints[i];
                    routingPoints.Add(new DiagramPoint((float)pointDto.X, (float)pointDto.Y));
                }
            }
            relationship.RoutingPoints = routingPoints;

            return relationship;
        }

        private static string TypeKindToString(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return "Interface";
                case TypeKind.AbstractClass:
                    return "AbstractClass";
                case TypeKind.ConcreteClass:
                    return "ConcreteClass";
                default:
                    throw new ArgumentOutOfRangeException("kind",
                        string.Format("Unknown TypeKind: {0}", kind));
            }
        }

        private static TypeKind StringToTypeKind(string value)
        {
            switch (value)
            {
                case "Interface":
                    return TypeKind.Interface;
                case "AbstractClass":
                    return TypeKind.AbstractClass;
                case "ConcreteClass":
                    return TypeKind.ConcreteClass;
                default:
                    throw new ArgumentException(
                        string.Format("Unknown TypeKind string: '{0}'", value), "value");
            }
        }

        private static string MemberKindToString(MemberKind kind)
        {
            switch (kind)
            {
                case MemberKind.Method:
                    return "Method";
                case MemberKind.Property:
                    return "Property";
                case MemberKind.Field:
                    return "Field";
                case MemberKind.Event:
                    return "Event";
                case MemberKind.Indexer:
                    return "Indexer";
                case MemberKind.Operator:
                    return "Operator";
                case MemberKind.Constructor:
                    return "Constructor";
                default:
                    return "Method";
            }
        }

        private static MemberKind StringToMemberKind(string value)
        {
            switch (value)
            {
                case "Method":
                    return MemberKind.Method;
                case "Property":
                    return MemberKind.Property;
                case "Field":
                    return MemberKind.Field;
                case "Event":
                    return MemberKind.Event;
                case "Indexer":
                    return MemberKind.Indexer;
                case "Operator":
                    return MemberKind.Operator;
                case "Constructor":
                    return MemberKind.Constructor;
                default:
                    return MemberKind.Method;
            }
        }

        private static string AccessModifierToString(AccessModifier modifier)
        {
            switch (modifier)
            {
                case AccessModifier.Public:
                    return "Public";
                case AccessModifier.Protected:
                    return "Protected";
                case AccessModifier.Internal:
                    return "Internal";
                case AccessModifier.Private:
                    return "Private";
                default:
                    throw new ArgumentOutOfRangeException("modifier",
                        string.Format("Unknown AccessModifier: {0}", modifier));
            }
        }

        private static AccessModifier StringToAccessModifier(string value)
        {
            switch (value)
            {
                case "Public":
                    return AccessModifier.Public;
                case "Protected":
                    return AccessModifier.Protected;
                case "Internal":
                    return AccessModifier.Internal;
                case "Private":
                    return AccessModifier.Private;
                default:
                    throw new ArgumentException(
                        string.Format("Unknown AccessModifier string: '{0}'", value), "value");
            }
        }

        private static string RelationshipKindToString(RelationshipKind kind)
        {
            switch (kind)
            {
                case RelationshipKind.Implements:
                    return "Implements";
                case RelationshipKind.Inherits:
                    return "Inherits";
                case RelationshipKind.DependsOn:
                    return "DependsOn";
                default:
                    throw new ArgumentOutOfRangeException("kind",
                        string.Format("Unknown RelationshipKind: {0}", kind));
            }
        }

        private static RelationshipKind StringToRelationshipKind(string value)
        {
            switch (value)
            {
                case "Implements":
                    return RelationshipKind.Implements;
                case "Inherits":
                    return RelationshipKind.Inherits;
                case "DependsOn":
                    return RelationshipKind.DependsOn;
                default:
                    throw new ArgumentException(
                        string.Format("Unknown RelationshipKind string: '{0}'", value), "value");
            }
        }
    }
}
