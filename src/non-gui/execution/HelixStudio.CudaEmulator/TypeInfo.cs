namespace HelixPythonNamespace.CudaEmulator
{
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;

    /// <summary>
    /// Runtime element descriptor for a declared C++/CUDA type. Port of
    /// helix::cuda_emulator::TypeInfo (cuda_emulator/value.h).
    /// </summary>
    public sealed class TypeInfo
    {
        public ElemType Elem = ElemType.Byte;
        public uint Size = 1;
        public bool IsFloat;
        public bool IsSigned = true;
    }

    /// <summary>
    /// Maps declared CxxType to runtime element descriptors. Port of the C++
    /// free functions MapType / PointeeInfo / ScalarInfo (cuda_emulator/value.cpp).
    /// </summary>
    public static class TypeMapping
    {
        // Base-name → element descriptor for non-pointer scalar types.
        private static TypeInfo ScalarInfo(string b)
        {
            if (b == "float") return T(ElemType.Float32, 4, true, true);
            if (b == "double") return T(ElemType.Float64, 8, true, true);
            if (b == "bool") return T(ElemType.Bool, 1, false, false);
            if (b == "char" || b == "signed char" || b == "int8_t")
                return T(ElemType.Int8, 1, false, true);
            if (b == "unsigned char" || b == "uint8_t")
                return T(ElemType.UInt8, 1, false, false);
            if (b == "short" || b == "int16_t") return T(ElemType.Int16, 2, false, true);
            if (b == "unsigned short" || b == "uint16_t")
                return T(ElemType.UInt16, 2, false, false);
            if (b == "int" || b == "int32_t" || b == "signed")
                return T(ElemType.Int32, 4, false, true);
            if (b == "unsigned" || b == "unsigned int" || b == "uint32_t")
                return T(ElemType.UInt32, 4, false, false);
            if (b == "long long" || b == "int64_t" || b == "long")
                return T(ElemType.Int64, 8, false, true);
            if (b == "unsigned long long" || b == "uint64_t" || b == "size_t" ||
                b == "unsigned long")
                return T(ElemType.UInt64, 8, false, false);
            // Unknown base: treat as a 4-byte int so simple code still runs.
            return T(ElemType.Int32, 4, false, true);
        }

        /// <summary>Element descriptor for the pointee of a pointer/array type.</summary>
        public static TypeInfo PointeeInfo(CxxType pointerType)
        {
            return ScalarInfo(pointerType != null ? pointerType.Base : string.Empty);
        }

        /// <summary>
        /// Map a declared C++/CUDA type to a runtime element descriptor. For pointers
        /// the returned info describes the POINTEE element.
        /// </summary>
        public static TypeInfo MapType(CxxType type)
        {
            if (type != null && type.IsPointer())
            {
                TypeInfo info = new TypeInfo();
                info.Elem = ElemType.Pointer;
                info.Size = 8;
                info.IsFloat = false;
                info.IsSigned = false;
                return info;
            }
            return ScalarInfo(type != null ? type.Base : string.Empty);
        }

        private static TypeInfo T(ElemType e, uint s, bool f, bool sg)
        {
            TypeInfo ti = new TypeInfo();
            ti.Elem = e;
            ti.Size = s;
            ti.IsFloat = f;
            ti.IsSigned = sg;
            return ti;
        }
    }
}
