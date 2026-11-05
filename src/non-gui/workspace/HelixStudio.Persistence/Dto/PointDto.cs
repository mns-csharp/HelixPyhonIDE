namespace HelixPythonNamespace.Persistence.Dto
{
    using System.Runtime.Serialization;

    [DataContract]
    public class PointDto
    {
        [DataMember(Name = "x", Order = 0)]
        public double X { get; set; }

        [DataMember(Name = "y", Order = 1)]
        public double Y { get; set; }
    }
}
