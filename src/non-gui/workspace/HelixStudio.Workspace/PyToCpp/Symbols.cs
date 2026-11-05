using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    public sealed class ParamInfo
    {
        public string Name;
        public CppType Type;
        public bool HasDefault;
        public bool IsVarPositional;  // *args -> element type List[Type]
        public bool IsVarKeyword;     // **kwargs -> element type Dict[str, Type]
    }

    public sealed class FunctionInfo
    {
        public string Name;
        public IReadOnlyList<ParamInfo> Parameters;
        public CppType ReturnType;
        public bool IsStatic;     // @staticmethod
        public bool IsClassMethod; // @classmethod
        public bool IsProperty;   // @property
        public string OwnerClass; // null for a free function
        public FunctionDefSyntaxRef Syntax; // original AST, for Pass 2 to re-walk the body

        public FunctionType AsType() => new FunctionType(GetParamTypesForCallSignature(), ReturnType);

        private List<CppType> GetParamTypesForCallSignature()
        {
            var list = new List<CppType>();
            foreach (var p in Parameters)
            {
                if (p.IsVarPositional) list.Add(new ListType(p.Type));
                else if (p.IsVarKeyword) list.Add(new DictType(CppType.Str, p.Type));
                else list.Add(p.Type);
            }
            return list;
        }
    }

    public sealed class FieldInfo
    {
        public string Name;
        public CppType Type;
    }

    public sealed class ClassInfo
    {
        public string Name;
        public string BaseClassName; // null for no base
        public Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();
        public Dictionary<string, FunctionInfo> Methods = new Dictionary<string, FunctionInfo>();
        public bool IsExceptionType; // derives (transitively) from BaseException/Exception or a builtin exception
    }

    /// <summary>Thin wrapper so Symbols.cs doesn't need to reference the full Syntax namespace type by type.</summary>
    public sealed class FunctionDefSyntaxRef
    {
        public HelixPythonNamespace.Python.Syntax.FunctionDefSyntax Node;
        public FunctionDefSyntaxRef(HelixPythonNamespace.Python.Syntax.FunctionDefSyntax node) { Node = node; }
    }

    public sealed class Scope
    {
        public readonly Scope Parent;
        public readonly Dictionary<string, CppType> Variables = new Dictionary<string, CppType>();
        // Names this scope's nested functions must NOT reassign via `nonlocal` (v1 boundary —
        // see PythonToCppTypeChecker's class comment: nonlocal capture is rejected outright).
        public Scope(Scope parent) { Parent = parent; }

        public bool TryLookup(string name, out CppType type)
        {
            for (Scope s = this; s != null; s = s.Parent)
                if (s.Variables.TryGetValue(name, out type)) return true;
            type = null;
            return false;
        }

        public void Declare(string name, CppType type) => Variables[name] = type;
    }
}
