namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Collections.Generic;
    using System.Runtime.Serialization;

    [DataContract]
    public class GenericParameterDto
    {
        [DataMember(Name = "name", Order = 0)]
        public string Name { get; set; }

        [DataMember(Name = "constraints", Order = 1)]
        public List<string> Constraints { get; set; }
    }
}
