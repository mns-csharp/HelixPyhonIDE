using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Export.Metadata
{
    internal sealed class MetadataMapper
    {
        private const string SchemaVersion = "1.0";

        public ArchitectureMetadataDto ToMetadata(Diagram diagram)
        {
            if (diagram == null)
            {
                throw new ArgumentNullException("diagram");
            }

            Dictionary<Guid, int> implementsCounts = new Dictionary<Guid, int>();
            Dictionary<Guid, int> dependencyCounts = new Dictionary<Guid, int>();

            foreach (Relationship rel in diagram.Relationships)
            {
                Guid sourceId = rel.Source.Id;

                if (rel.Kind == RelationshipKind.Implements)
                {
                    if (implementsCounts.ContainsKey(sourceId))
                    {
                        implementsCounts[sourceId] = implementsCounts[sourceId] + 1;
                    }
                    else
                    {
                        implementsCounts[sourceId] = 1;
                    }
                }
                else if (rel.Kind == RelationshipKind.DependsOn)
                {
                    if (dependencyCounts.ContainsKey(sourceId))
                    {
                        dependencyCounts[sourceId] = dependencyCounts[sourceId] + 1;
                    }
                    else
                    {
                        dependencyCounts[sourceId] = 1;
                    }
                }
            }

            List<TypeMetadataDto> types = new List<TypeMetadataDto>();
            foreach (TypeNode node in diagram.Nodes)
            {
                TypeMetadataDto dto = new TypeMetadataDto();
                dto.Id = node.Id.ToString();
                dto.Name = node.Name;
                dto.TypeKind = node.TypeKind.ToString();
                dto.MemberCount = node.Members != null ? node.Members.Count : 0;

                int implCount;
                if (implementsCounts.TryGetValue(node.Id, out implCount))
                {
                    dto.ImplementsCount = implCount;
                }
                else
                {
                    dto.ImplementsCount = 0;
                }

                int depCount;
                if (dependencyCounts.TryGetValue(node.Id, out depCount))
                {
                    dto.DependencyCount = depCount;
                }
                else
                {
                    dto.DependencyCount = 0;
                }

                List<string> genericParams = new List<string>();
                if (node.GenericParameters != null)
                {
                    foreach (GenericParameter gp in node.GenericParameters)
                    {
                        genericParams.Add(gp.Name);
                    }
                }
                dto.GenericParameters = genericParams;

                types.Add(dto);
            }

            List<RelationshipMetadataDto> relationships = new List<RelationshipMetadataDto>();
            foreach (Relationship rel in diagram.Relationships)
            {
                RelationshipMetadataDto dto = new RelationshipMetadataDto();
                dto.Id = rel.Id.ToString();
                dto.Kind = rel.Kind.ToString();
                dto.SourceTypeName = rel.Source.Name;
                dto.TargetTypeName = rel.Target.Name;
                relationships.Add(dto);
            }

            ArchitectureMetadataDto metadata = new ArchitectureMetadataDto();
            metadata.SchemaVersion = SchemaVersion;
            metadata.DiagramName = diagram.Name;
            metadata.Types = types;
            metadata.Relationships = relationships;

            return metadata;
        }
    }
}
