namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using HelixPythonNamespace.Python.Text;

    /// <summary>Memory space a buffer lives in. Port of helix::cuda_emulator::MemSpace.</summary>
    public enum MemSpace { Host, Device, Shared, Constant }

    /// <summary>An allocation in the arena. Port of helix::cuda_emulator::Buffer.</summary>
    public sealed class Buffer
    {
        public uint Id;
        public byte[] Bytes;
        public string Label = string.Empty;
        public MemSpace Space = MemSpace.Host;
        public bool Freed;
    }

    /// <summary>
    /// Flat byte arena shared by all emulated host + device memory. cudaMalloc, host
    /// arrays, and __shared__ all allocate Buffers here; pointers are (buffer,offset).
    /// Typed Load/Store bounds-check against the buffer and report RuntimeError with
    /// the offending node's span. Allocation is lock-guarded (host is single-threaded
    /// but blocks run in parallel); atomics lock per buffer. Port of the C++
    /// helix::cuda_emulator::Memory (cuda_emulator/memory.{h,cpp}).
    /// </summary>
    public sealed class Memory
    {
        private readonly List<Buffer> buffers_ = new List<Buffer>();          // index 0 reserved → null
        private readonly List<object> bufferMutexes_ = new List<object>();
        private readonly object allocMutex_ = new object();
        private bool initialized_;

        /// <summary>
        /// Top-level program globals (__device__ / __constant__ vars and file-scope
        /// const/var), keyed by name -> their arena Pointer. Shared across the host
        /// interpreter and every per-block/per-thread kernel interpreter (they all
        /// share this Memory), so a __device__ global is genuinely one storage
        /// location visible to all threads. Populated once at program start.
        /// </summary>
        public readonly Dictionary<string, Pointer> Globals = new Dictionary<string, Pointer>();

        public static uint SizeOfElem(ElemType e)
        {
            switch (e)
            {
                case ElemType.Byte:
                case ElemType.Int8:
                case ElemType.UInt8:
                case ElemType.Bool: return 1;
                case ElemType.Int16:
                case ElemType.UInt16: return 2;
                case ElemType.Int32:
                case ElemType.UInt32:
                case ElemType.Float32: return 4;
                case ElemType.Int64:
                case ElemType.UInt64:
                case ElemType.Float64:
                case ElemType.Pointer: return 8;
                case ElemType.Struct: return 1;
            }
            return 1;
        }

        private void EnsureInit()
        {
            if (initialized_) return;
            // Reserve index 0 as the null buffer.
            buffers_.Add(null);
            bufferMutexes_.Add(new object());
            initialized_ = true;
        }

        private Buffer Get(uint id)
        {
            if (id == 0 || id >= buffers_.Count) return null;
            return buffers_[(int)id];
        }

        public Buffer Find(uint id)
        {
            return Get(id);
        }

        public object BufferMutex(uint buffer)
        {
            lock (allocMutex_)
            {
                EnsureInit();
                if (buffer < bufferMutexes_.Count && bufferMutexes_[(int)buffer] != null)
                    return bufferMutexes_[(int)buffer];
                return bufferMutexes_[0];
            }
        }

        public Pointer Allocate(long numBytes, MemSpace space, ElemType elem, uint elemSize, string label)
        {
            lock (allocMutex_)
            {
                EnsureInit();
                Buffer buf = new Buffer();
                buf.Id = (uint)buffers_.Count;
                buf.Bytes = new byte[numBytes < 0 ? 0 : numBytes];
                buf.Label = label ?? string.Empty;
                buf.Space = space;
                Pointer p = new Pointer();
                p.Buffer = buf.Id;
                p.Offset = 0;
                p.ElemSize = elemSize != 0 ? elemSize : SizeOfElem(elem);
                p.ElemType = elem;
                buffers_.Add(buf);
                bufferMutexes_.Add(new object());
                return p;
            }
        }

        public void Free(Pointer p, SourceSpan span)
        {
            lock (allocMutex_)
            {
                Buffer b = Get(p.Buffer);
                if (b == null) throw new RuntimeError(RuntimeError.Kind.NullDeref, "cudaFree/free of invalid pointer", span);
                if (b.Freed) throw new RuntimeError(RuntimeError.Kind.DoubleFree, "double free of '" + b.Label + "'", span);
                b.Freed = true;
            }
        }

        // Bounds-checked buffer + validated offset for a window of accessBytes.
        private Buffer Checked(Pointer p, long accessBytes, SourceSpan span)
        {
            Buffer b = Get(p.Buffer);
            if (b == null) throw new RuntimeError(RuntimeError.Kind.NullDeref, "dereference of null/invalid pointer", span);
            if (b.Freed) throw new RuntimeError(RuntimeError.Kind.UseAfterFree, "use after free of '" + b.Label + "'", span);
            if (p.Offset < 0 || p.Offset + accessBytes > b.Bytes.Length)
            {
                throw new RuntimeError(RuntimeError.Kind.OutOfBounds,
                    "out-of-bounds access on '" + b.Label + "' (offset " +
                    p.Offset.ToString(CultureInfo.InvariantCulture) + ", size " +
                    accessBytes.ToString(CultureInfo.InvariantCulture) + ", capacity " +
                    b.Bytes.Length.ToString(CultureInfo.InvariantCulture) + ")", span);
            }
            return b;
        }

        public void Memset(Pointer dst, int byteValue, long n, SourceSpan span)
        {
            Buffer b = Checked(dst, n, span);
            int off = (int)dst.Offset;
            byte v = (byte)byteValue;
            for (long i = 0; i < n; ++i) b.Bytes[off + i] = v;
        }

        public void Copy(Pointer dst, Pointer src, long n, SourceSpan span)
        {
            Buffer bd = Checked(dst, n, span);
            Buffer bs = Checked(src, n, span);
            Array.Copy(bs.Bytes, (int)src.Offset, bd.Bytes, (int)dst.Offset, (int)n);
        }

        public Value Load(Pointer p, ElemType elem, SourceSpan span)
        {
            Buffer b = Checked(p, SizeOfElem(elem), span);
            return Decode(b.Bytes, (int)p.Offset, elem);
        }

        public void Store(Pointer p, ElemType elem, Value v, SourceSpan span)
        {
            Buffer b = Checked(p, SizeOfElem(elem), span);
            Encode(b.Bytes, (int)p.Offset, elem, v);
        }

        public Value ProbeElement(Pointer baseP, long index, ElemType elem)
        {
            Pointer p = baseP;
            p.Offset += index * (long)SizeOfElem(elem);
            return Load(p, elem, SourceSpan.Empty);
        }

        // Walk a NUL-terminated Int8 arena buffer and return it as a managed string.
        // Used by printf's %s and by std::cout << (char*)/string-literal formatting.
        public string ReadCString(Pointer strPtr, SourceSpan span)
        {
            StringBuilder s = new StringBuilder();
            Pointer p = strPtr;
            for (int i = 0; i < (1 << 16); ++i)  // guard against unterminated
            {
                Value c = Load(p, ElemType.Int8, span);
                char ch = (char)(byte)c.ToInt64();
                if (ch == '\0') break;
                s.Append(ch);
                p.Offset += 1;
            }
            return s.ToString();
        }

        private static Value Decode(byte[] a, int off, ElemType elem)
        {
            switch (elem)
            {
                case ElemType.Bool: return new Value(a[off] != 0);
                case ElemType.Int8: return new Value((long)(sbyte)a[off]);
                case ElemType.UInt8:
                case ElemType.Byte: return new Value((long)a[off]);
                case ElemType.Int16: return new Value((long)BitConverter.ToInt16(a, off));
                case ElemType.UInt16: return new Value((long)BitConverter.ToUInt16(a, off));
                case ElemType.Int32: return new Value((long)BitConverter.ToInt32(a, off));
                case ElemType.UInt32: return new Value((long)BitConverter.ToUInt32(a, off));
                case ElemType.Int64: return new Value(BitConverter.ToInt64(a, off));
                case ElemType.UInt64: return new Value((long)BitConverter.ToUInt64(a, off));
                case ElemType.Float32: return new Value((double)BitConverter.ToSingle(a, off));
                case ElemType.Float64: return new Value(BitConverter.ToDouble(a, off));
                case ElemType.Pointer:
                    {
                        ulong packed = BitConverter.ToUInt64(a, off);
                        Pointer p = new Pointer();
                        p.ElemSize = 1;
                        p.ElemType = ElemType.Byte;
                        p.Buffer = (uint)(packed >> 32);
                        p.Offset = (long)(int)(uint)(packed & 0xFFFFFFFFu);
                        return new Value(p);
                    }
                case ElemType.Struct: return new Value();
            }
            return new Value();
        }

        private static void Encode(byte[] a, int off, ElemType elem, Value v)
        {
            switch (elem)
            {
                case ElemType.Bool: a[off] = (byte)(v.ToBool() ? 1 : 0); return;
                case ElemType.Int8: a[off] = (byte)(sbyte)v.ToInt64(); return;
                case ElemType.UInt8:
                case ElemType.Byte: a[off] = (byte)v.ToInt64(); return;
                case ElemType.Int16: WriteBytes(a, off, BitConverter.GetBytes((short)v.ToInt64())); return;
                case ElemType.UInt16: WriteBytes(a, off, BitConverter.GetBytes((ushort)v.ToInt64())); return;
                case ElemType.Int32: WriteBytes(a, off, BitConverter.GetBytes((int)v.ToInt64())); return;
                case ElemType.UInt32: WriteBytes(a, off, BitConverter.GetBytes((uint)v.ToInt64())); return;
                case ElemType.Int64: WriteBytes(a, off, BitConverter.GetBytes(v.ToInt64())); return;
                case ElemType.UInt64: WriteBytes(a, off, BitConverter.GetBytes((ulong)v.ToInt64())); return;
                case ElemType.Float32: WriteBytes(a, off, BitConverter.GetBytes((float)v.ToDouble())); return;
                case ElemType.Float64: WriteBytes(a, off, BitConverter.GetBytes(v.ToDouble())); return;
                case ElemType.Pointer:
                    {
                        Pointer p = v.IsPointer ? v.AsPointer : Pointer.Null();
                        ulong packed = ((ulong)p.Buffer << 32) | ((uint)p.Offset & 0xFFFFFFFFu);
                        WriteBytes(a, off, BitConverter.GetBytes(packed));
                        return;
                    }
                case ElemType.Struct: return;
            }
        }

        private static void WriteBytes(byte[] a, int off, byte[] src)
        {
            for (int i = 0; i < src.Length; ++i) a[off + i] = src[i];
        }
    }
}
