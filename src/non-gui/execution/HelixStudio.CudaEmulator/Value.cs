namespace HelixPythonNamespace.CudaEmulator
{
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// How bytes in the memory arena are interpreted at a typed load/store. Port of
    /// helix::cuda_emulator::ElemType (cuda_emulator/value.h).
    /// </summary>
    public enum ElemType
    {
        Byte, Int8, UInt8, Int16, UInt16, Int32, UInt32,
        Int64, UInt64, Float32, Float64, Bool, Pointer, Struct,
    }

    /// <summary>
    /// A pointer value: an address into a Memory arena buffer. ElemSize/ElemType
    /// describe what the pointer "sees" (from its static type at creation/cast).
    /// C++ value struct → C# value struct (matches the interpreter's copy-then-wrap
    /// pointer-arithmetic idiom). Port of helix::cuda_emulator::Pointer.
    /// </summary>
    public struct Pointer
    {
        public uint Buffer;    // 0 == null
        public long Offset;    // byte offset within the buffer
        public uint ElemSize;
        public ElemType ElemType;

        public bool IsNull { get { return Buffer == 0; } }

        /// <summary>A null pointer with the C++ default ElemSize=1/ElemType=Byte (default(Pointer) would give ElemSize=0).</summary>
        public static Pointer Null()
        {
            Pointer p = new Pointer();
            p.ElemSize = 1;
            p.ElemType = ElemType.Byte;
            return p;
        }
    }

    /// <summary>
    /// dim3 / uint3 / threadIdx / blockIdx / blockDim / gridDim. C++ default is
    /// (1,1,1); use the constructor everywhere (default(Dim3) is (0,0,0)). Port of
    /// helix::cuda_emulator::Dim3.
    /// </summary>
    public struct Dim3
    {
        public ulong X, Y, Z;

        public Dim3(ulong x, ulong y, ulong z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Dim3 One()
        {
            return new Dim3(1, 1, 1);
        }
    }

    /// <summary>
    /// Small fixed vector (float2/float4/int4...). Heap-shared so a lane is a
    /// mutable lvalue (matches the C++ std::shared_ptr&lt;VecValue&gt;). Port of
    /// helix::cuda_emulator::VecValue.
    /// </summary>
    public sealed class VecValue
    {
        public ElemType Elem = ElemType.Float32;
        public int Count;
        public readonly double[] Comps = new double[4];
    }

    /// <summary>std::cout / std::cerr identity for a Stream-kind Value.</summary>
    public enum StreamKind { Cout, Cerr }

    /// <summary>I/O manipulators recognized in a `<<` chain (currently just std::endl).</summary>
    public enum ManipKind { EndL }

    internal enum ValueKind { Void, Int, Double, Bool, Pointer, Dim3, Vec, Stream, Manipulator }

    /// <summary>
    /// A runtime value. Two numeric kinds (int64 / double) keep arithmetic small;
    /// the declared type at a store narrows width (e.g. float store rounds through
    /// float). Immutable — assignment replaces the reference (Cell.Value = v); the
    /// only in-place mutation is through a shared VecValue. Port of the C++
    /// helix::cuda_emulator::Value (cuda_emulator/value.{h,cpp}).
    /// </summary>
    public sealed class Value
    {
        private readonly ValueKind kind_;
        private readonly long int_;
        private readonly double double_;
        private readonly bool bool_;
        private readonly Pointer ptr_;
        private readonly Dim3 dim3_;
        private readonly VecValue vec_;
        private readonly StreamKind stream_;
        private readonly ManipKind manip_;

        public Value() { kind_ = ValueKind.Void; }
        public Value(long i) { kind_ = ValueKind.Int; int_ = i; }
        public Value(double d) { kind_ = ValueKind.Double; double_ = d; }
        public Value(bool b) { kind_ = ValueKind.Bool; bool_ = b; }
        public Value(Pointer p) { kind_ = ValueKind.Pointer; ptr_ = p; }
        public Value(Dim3 d) { kind_ = ValueKind.Dim3; dim3_ = d; }
        public Value(VecValue vec) { kind_ = ValueKind.Vec; vec_ = vec; }
        public Value(StreamKind s) { kind_ = ValueKind.Stream; stream_ = s; }
        public Value(ManipKind m) { kind_ = ValueKind.Manipulator; manip_ = m; }

        public static Value Void() { return new Value(); }

        public bool IsVoid { get { return kind_ == ValueKind.Void; } }
        public bool IsInt { get { return kind_ == ValueKind.Int; } }
        public bool IsDouble { get { return kind_ == ValueKind.Double; } }
        public bool IsBool { get { return kind_ == ValueKind.Bool; } }
        public bool IsPointer { get { return kind_ == ValueKind.Pointer; } }
        public bool IsDim3 { get { return kind_ == ValueKind.Dim3; } }
        public bool IsVec { get { return kind_ == ValueKind.Vec; } }
        public bool IsStream { get { return kind_ == ValueKind.Stream; } }
        public bool IsManipulator { get { return kind_ == ValueKind.Manipulator; } }

        public long AsInt { get { return int_; } }
        public double AsDouble { get { return double_; } }
        public bool AsBool { get { return bool_; } }
        public Pointer AsPointer { get { return ptr_; } }
        public Dim3 AsDim3 { get { return dim3_; } }
        public VecValue AsVec { get { return vec_; } }
        public StreamKind AsStream { get { return stream_; } }
        public ManipKind AsManipulator { get { return manip_; } }

        public long ToInt64()
        {
            if (IsInt) return int_;
            if (IsDouble) return (long)double_;
            if (IsBool) return bool_ ? 1 : 0;
            if (IsPointer) return ptr_.Offset;
            return 0;
        }

        public double ToDouble()
        {
            if (IsDouble) return double_;
            if (IsInt) return (double)int_;
            if (IsBool) return bool_ ? 1.0 : 0.0;
            return 0.0;
        }

        public bool ToBool()
        {
            if (IsBool) return bool_;
            if (IsInt) return int_ != 0;
            if (IsDouble) return double_ != 0.0;
            if (IsPointer) return !ptr_.IsNull;
            return false;
        }

        public override string ToString()
        {
            if (IsVoid) return "void";
            if (IsBool) return bool_ ? "true" : "false";
            if (IsInt) return int_.ToString(CultureInfo.InvariantCulture);
            if (IsDouble) return CFormat.G(double_);
            if (IsPointer)
            {
                if (ptr_.IsNull) return "nullptr";
                return "0x" + ptr_.Buffer.ToString(CultureInfo.InvariantCulture) + "+" +
                       ptr_.Offset.ToString(CultureInfo.InvariantCulture);
            }
            if (IsDim3)
            {
                return "(" + dim3_.X.ToString(CultureInfo.InvariantCulture) + "," +
                       dim3_.Y.ToString(CultureInfo.InvariantCulture) + "," +
                       dim3_.Z.ToString(CultureInfo.InvariantCulture) + ")";
            }
            if (IsVec)
            {
                StringBuilder s = new StringBuilder("{");
                for (int i = 0; i < vec_.Count; ++i)
                {
                    if (i != 0) s.Append(",");
                    s.Append(CFormat.G(vec_.Comps[i]));
                }
                return s.Append("}").ToString();
            }
            if (IsStream) return stream_ == StreamKind.Cout ? "std::cout" : "std::cerr";
            if (IsManipulator) return "std::endl";
            return "?";
        }
    }
}
