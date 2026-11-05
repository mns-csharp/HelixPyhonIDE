using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    public class TypeNode
    {
        public TypeNode(string name, TypeKind typeKind)
        {
            Id = Guid.NewGuid();
            Name = name;
            TypeKind = typeKind;
            GenericParameters = new List<GenericParameter>();
            Members = new List<MemberDefinition>();
            Width = 200f;
            Height = 150f;
        }

        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Namespace { get; set; }
        public TypeKind TypeKind { get; set; }

        public List<GenericParameter> GenericParameters { get; set; }
        public List<MemberDefinition> Members { get; set; }

        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }

        public bool IsCollapsed { get; set; }
    }
}
