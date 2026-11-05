namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class NodeDto
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id { get; set; }

        [DataMember(Name = "typeKind", Order = 1)]
        public string TypeKind { get; set; }

        [DataMember(Name = "name", Order = 2)]
        public string Name { get; set; }

        [DataMember(Name = "namespace", Order = 3)]
        public string Namespace { get; set; }

        [DataMember(Name = "positionX", Order = 4)]
        public double PositionX { get; set; }

        [DataMember(Name = "positionY", Order = 5)]
        public double PositionY { get; set; }

        [DataMember(Name = "width", Order = 6)]
        public double Width { get; set; }

        [DataMember(Name = "height", Order = 7)]
        public double Height { get; set; }

        [DataMember(Name = "isCollapsed", Order = 8)]
        public bool IsCollapsed { get; set; }

        [DataMember(Name = "genericParameters", Order = 9)]
        public List<GenericParameterDto> GenericParameters { get; set; }

        [DataMember(Name = "members", Order = 10)]
        public List<MemberDto> Members { get; set; }
    }
}
