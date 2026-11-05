using System.Runtime.Serialization;

namespace HelixPythonNamespace.Export.Metadata
{
    [DataContract]
    public class RelationshipMetadataDto
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id { get; set; }

        [DataMember(Name = "kind", Order = 1)]
        public string Kind { get; set; }

        [DataMember(Name = "sourceTypeName", Order = 2)]
        public string SourceTypeName { get; set; }

        [DataMember(Name = "targetTypeName", Order = 3)]
        public string TargetTypeName { get; set; }
    }
}
