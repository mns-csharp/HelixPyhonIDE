using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    public class Relationship
    {
        public Relationship(RelationshipKind kind, TypeNode source, TypeNode target)
        {
            Id = Guid.NewGuid();
            Kind = kind;
            Source = source;
            Target = target;
            RoutingPoints = new List<DiagramPoint>();
        }

        public Guid Id { get; set; }
        public RelationshipKind Kind { get; set; }
        public TypeNode Source { get; set; }
        public TypeNode Target { get; set; }
        public List<DiagramPoint> RoutingPoints { get; set; }
    }
}
