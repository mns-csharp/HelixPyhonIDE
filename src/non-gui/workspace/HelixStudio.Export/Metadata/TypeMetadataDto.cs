using System.Collections.Generic;
using System.Runtime.Serialization;

namespace HelixPythonNamespace.Export.Metadata
{
    [DataContract]
    public class TypeMetadataDto
    {
        [DataMember(Name = "id", Order = 0)]
        public string Id { get; set; }

        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; }

        [DataMember(Name = "typeKind", Order = 2)]
        public string TypeKind { get; set; }

        [DataMember(Name = "memberCount", Order = 3)]
        public int MemberCount { get; set; }

        [DataMember(Name = "implementsCount", Order = 4)]
        public int ImplementsCount { get; set; }

        [DataMember(Name = "dependencyCount", Order = 5)]
        public int DependencyCount { get; set; }

        [DataMember(Name = "genericParameters", Order = 6)]
        public List<string> GenericParameters { get; set; }
    }
}
