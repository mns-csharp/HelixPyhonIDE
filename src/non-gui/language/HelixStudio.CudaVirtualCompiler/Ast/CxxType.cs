namespace HelixPythonNamespace.CudaVirtualCompiler.Ast
{
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// A deliberately coarse C++ type descriptor — enough for the CUDA analyses
    /// (which care about "is this a pointer", "what is its base name", "is it
    /// const", "what memory space was it declared in") without modeling full C++
    /// type semantics. Template arguments are kept as nested CxxType for display and
    /// shallow matching but are not instantiated. Port of the C++
    /// helix::cuda_vcompiler::ast::CxxType (cuda_vcompiler/ast/cxx_type.h).
    ///
    /// Examples:
    ///   float*            -> Base="float", PointerDepth=1
    ///   const int&amp;         -> Base="int",   IsConst=true, IsReference=true
    ///   dim3              -> Base="dim3"
    ///   thrust::device_vector&lt;int&gt; -> Base="thrust::device_vector", TemplateArgs=[int]
    /// </summary>
    public sealed class CxxType
    {
        public string Base;                    // qualified base name, e.g. "std::size_t"
        public int PointerDepth;               // number of '*'
        public bool IsReference;               // trailing '&'
        public bool IsConst;                   // top-level const
        public bool IsVolatile;
        public bool IsRestrict;                // __restrict__
        public bool IsArray;                   // declared with [] (decays to pointer)
        public readonly List<CxxType> TemplateArgs;

        public CxxType()
        {
            Base = string.Empty;
            TemplateArgs = new List<CxxType>();
        }

        public CxxType(string b)
        {
            Base = b ?? string.Empty;
            TemplateArgs = new List<CxxType>();
        }

        /// <summary>
        /// Deep copy. The C++ CxxType is a value struct copied by assignment (e.g.
        /// once per declarator on top of a shared base type); C# reference semantics
        /// require an explicit clone to reproduce that.
        /// </summary>
        public CxxType Clone()
        {
            CxxType c = new CxxType(Base);
            c.PointerDepth = PointerDepth;
            c.IsReference = IsReference;
            c.IsConst = IsConst;
            c.IsVolatile = IsVolatile;
            c.IsRestrict = IsRestrict;
            c.IsArray = IsArray;
            for (int i = 0; i < TemplateArgs.Count; ++i) c.TemplateArgs.Add(TemplateArgs[i].Clone());
            return c;
        }

        public bool IsPointer()
        {
            return PointerDepth > 0 || IsArray;
        }

        public bool IsVoid()
        {
            return Base == "void" && PointerDepth == 0;
        }

        public bool IsEmpty()
        {
            return string.IsNullOrEmpty(Base) && PointerDepth == 0;
        }

        public override string ToString()
        {
            StringBuilder s = new StringBuilder();
            if (IsConst) s.Append("const ");
            s.Append(string.IsNullOrEmpty(Base) ? "<auto>" : Base);
            for (int i = 0; i < PointerDepth; ++i) s.Append("*");
            if (IsArray) s.Append("[]");
            if (IsReference) s.Append("&");
            return s.ToString();
        }
    }
}
