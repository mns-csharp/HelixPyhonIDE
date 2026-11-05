using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Model
{
    public class Diagram
    {
        public Diagram()
        {
            Name = "Untitled";
            Nodes = new List<TypeNode>();
            Relationships = new List<Relationship>();
            CreatedUtc = DateTime.UtcNow;
        }

        public Diagram(string name, string defaultNamespace, List<TypeNode> nodes,
            List<Relationship> relationships, DateTime createdUtc)
        {
            Name = name;
            DefaultNamespace = defaultNamespace;
            Nodes = nodes;
            Relationships = relationships;
            CreatedUtc = createdUtc;
        }

        public string Name { get; set; }
        public string DefaultNamespace { get; set; }
        public List<TypeNode> Nodes { get; set; }
        public List<Relationship> Relationships { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
