using System.Collections.Generic;
using System.Runtime.Serialization;

namespace HelixPythonNamespace.Export.Metadata
{
    [DataContract]
    public class ArchitectureMetadataDto
    {
        [DataMember(Name = "schemaVersion", Order = 0)]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "diagramName", Order = 1)]
        public string DiagramName { get; set; }

        [DataMember(Name = "types", Order = 2)]
        public List<TypeMetadataDto> Types { get; set; }

        [DataMember(Name = "relationships", Order = 3)]
        public List<RelationshipMetadataDto> Relationships { get; set; }
    }
}
