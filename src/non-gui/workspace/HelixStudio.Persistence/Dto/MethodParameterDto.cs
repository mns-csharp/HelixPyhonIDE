namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Runtime.Serialization;

    [DataContract]
    public class MethodParameterDto
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; }

        [DataMember(Name = "typeName", Order = 1)]
        public string TypeName { get; set; }
    }
}
