namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Runtime.Serialization;

    [DataContract]
    public class DiagramFileDto
    {
        [DataMember(Name = "formatVersion", Order = 0)]
        public int FormatVersion { get; set; }

        [DataMember(Name = "appVersion", Order = 1)]
        public string AppVersion { get; set; }

        [DataMember(Name = "createdUtc", Order = 2)]
        public string CreatedUtc { get; set; }

        [DataMember(Name = "modifiedUtc", Order = 3)]
        public string ModifiedUtc { get; set; }

        [DataMember(Name = "diagram", Order = 4)]
        public DiagramDto Diagram { get; set; }
    }
}
