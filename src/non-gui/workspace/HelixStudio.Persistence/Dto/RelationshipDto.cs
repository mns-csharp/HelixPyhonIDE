namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class RelationshipDto
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id { get; set; }

        [DataMember(Name = "kind", Order = 1)]
        public string Kind { get; set; }

        [DataMember(Name = "sourceNodeId", Order = 2)]
        public string SourceNodeId { get; set; }

        [DataMember(Name = "targetNodeId", Order = 3)]
        public string TargetNodeId { get; set; }

        [DataMember(Name = "routingPoints", Order = 4)]
        public List<PointDto> RoutingPoints { get; set; }
    }
}
