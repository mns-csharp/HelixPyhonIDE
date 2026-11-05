using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    public class MemberDefinition
    {
        public MemberDefinition(string name, MemberKind memberKind)
        {
            Name = name;
            MemberKind = memberKind;
            Parameters = new List<MethodParameter>();
            AccessModifier = AccessModifier.Public;
            HasGetter = true;
        }

        public string Name { get; set; }
        public MemberKind MemberKind { get; set; }
        public string ReturnType { get; set; }
        public AccessModifier AccessModifier { get; set; }

        public bool IsAbstract { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsStatic { get; set; }
        public bool IsOverride { get; set; }

        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }

        public List<MethodParameter> Parameters { get; set; }
    }
}
