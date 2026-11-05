namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class MemberDto
    {
        [DataMember(Name = "memberKind", Order = 0)]
        public string MemberKind { get; set; }

        [DataMember(Name = "name", Order = 1)]
        public string Name { get; set; }

        [DataMember(Name = "returnType", Order = 2)]
        public string ReturnType { get; set; }

        [DataMember(Name = "accessModifier", Order = 3)]
        public string AccessModifier { get; set; }

        [DataMember(Name = "isAbstract", Order = 4)]
        public bool IsAbstract { get; set; }

        [DataMember(Name = "isVirtual", Order = 5)]
        public bool IsVirtual { get; set; }

        [DataMember(Name = "isStatic", Order = 6)]
        public bool IsStatic { get; set; }

        [DataMember(Name = "isOverride", Order = 7)]
        public bool IsOverride { get; set; }

        [DataMember(Name = "hasGetter", Order = 8)]
        public bool HasGetter { get; set; }

        [DataMember(Name = "hasSetter", Order = 9)]
        public bool HasSetter { get; set; }

        [DataMember(Name = "parameters", Order = 10)]
        public List<MethodParameterDto> Parameters { get; set; }
    }
}
