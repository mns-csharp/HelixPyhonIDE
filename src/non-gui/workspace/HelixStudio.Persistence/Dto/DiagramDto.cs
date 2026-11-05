namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class DiagramDto
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; }

        [DataMember(Name = "defaultNamespace", Order = 1)]
        public string DefaultNamespace { get; set; }

        [DataMember(Name = "nodes", Order = 2)]
        public List<NodeDto> Nodes { get; set; }

        [DataMember(Name = "relationships", Order = 3)]
        public List<RelationshipDto> Relationships { get; set; }
    }
}
