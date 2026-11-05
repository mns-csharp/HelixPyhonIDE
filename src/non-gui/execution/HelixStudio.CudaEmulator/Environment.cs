namespace HelixPythonNamespace.CudaEmulator
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// A local variable slot. Memory stores narrow by element type; local values
    /// keep full numeric precision. Port of helix::cuda_emulator::Cell. Class (not
    /// struct) so Scope.Find returns a mutable reference the way the C++ Cell* does.
    /// </summary>
    public sealed class Cell
    {
        public Value Value = new Value();
        public CxxType DeclType;

        // If the local's address is taken (&x), it is promoted to an arena slot so
        // the pointer and the variable share one storage location. Once AddressTaken,
        // reads/writes route through Backing (see LValue.Get/Set) to stay coherent
        // with any writes made through the pointer (e.g. cudaMemcpy(&x, d, n)).
        public bool AddressTaken;
        public Pointer Backing;
        public ElemType BackingElem;
    }

    /// <summary>
    /// One lexical block scope; lookup walks the parent chain. Port of
    /// helix::cuda_emulator::Scope (cuda_emulator/environment.h).
    /// </summary>
    public sealed class Scope
    {
        private readonly Scope parent_;
        private readonly Dictionary<string, Cell> locals_ = new Dictionary<string, Cell>();

        public Scope(Scope parent)
        {
            parent_ = parent;
        }

        public Cell Find(string name)
        {
            for (Scope s = this; s != null; s = s.parent_)
            {
                Cell c;
                if (s.locals_.TryGetValue(name, out c)) return c;
            }
            return null;
        }

        public Cell FindLocal(string name)
        {
            Cell c;
            return locals_.TryGetValue(name, out c) ? c : null;
        }

        public Cell Declare(string name, Value v, CxxType declType)
        {
            Cell c;
            if (!locals_.TryGetValue(name, out c))
            {
                c = new Cell();
                locals_[name] = c;
            }
            c.Value = v;
            c.DeclType = declType;
            return c;
        }

        public Scope Parent { get { return parent_; } }
        public IReadOnlyDictionary<string, Cell> Locals { get { return locals_; } }
    }

    /// <summary>
    /// A writable location: a local cell, a typed memory address (*p, a[i]), or a
    /// lane of a dim3/vector aggregate (b.x). Port of helix::cuda_emulator::LValue.
    /// </summary>
    public struct LValue
    {
        public enum Kind { None, Cell, Memory, Dim3Lane, VecLane }

        public Kind LValueKind;
        public Cell Cell;          // Cell / Dim3Lane / VecLane container
        public Pointer Mem;        // Memory
        public ElemType Elem;      // Memory
        public int Lane;           // Dim3Lane (0=x,1=y,2=z) / VecLane

        public LValue(Kind kind, Cell cell, Pointer mem, ElemType elem, int lane)
        {
            LValueKind = kind;
            Cell = cell;
            Mem = mem;
            Elem = elem;
            Lane = lane;
        }

        public Value Get(Memory memory, SourceSpan span)
        {
            switch (LValueKind)
            {
                case Kind.Cell:
                    return Cell.AddressTaken ? memory.Load(Cell.Backing, Cell.BackingElem, span) : Cell.Value;
                case Kind.Memory: return memory.Load(Mem, Elem, span);
                case Kind.Dim3Lane:
                    return new Value((long)LaneOf(Cell.Value.AsDim3, Lane));
                case Kind.VecLane:
                    return new Value(Cell.Value.AsVec.Comps[Lane]);
                case Kind.None:
                    throw new RuntimeError(RuntimeError.Kind.Other, "read of non-lvalue", span);
            }
            return new Value();
        }

        public void Set(Memory memory, Value v, SourceSpan span)
        {
            switch (LValueKind)
            {
                case Kind.Cell:
                    if (Cell.AddressTaken) memory.Store(Cell.Backing, Cell.BackingElem, v, span);
                    else Cell.Value = v;
                    return;
                case Kind.Memory: memory.Store(Mem, Elem, v, span); return;
                case Kind.Dim3Lane:
                    {
                        Dim3 d = Cell.Value.AsDim3;
                        SetLane(ref d, Lane, (ulong)v.ToInt64());
                        Cell.Value = new Value(d);
                        return;
                    }
                case Kind.VecLane:
                    Cell.Value.AsVec.Comps[Lane] = v.ToDouble();
                    return;
                case Kind.None:
                    throw new RuntimeError(RuntimeError.Kind.Other, "assignment to non-lvalue", span);
            }
        }

        private static ulong LaneOf(Dim3 d, int lane)
        {
            return lane == 0 ? d.X : lane == 1 ? d.Y : d.Z;
        }

        private static void SetLane(ref Dim3 d, int lane, ulong v)
        {
            if (lane == 0) d.X = v; else if (lane == 1) d.Y = v; else d.Z = v;
        }
    }

    /// <summary>
    /// One call frame: a stack of block scopes plus kernel thread context. Port of
    /// helix::cuda_emulator::Frame (cuda_emulator/environment.h).
    /// </summary>
    public sealed class Frame
    {
        public FunctionDecl Func;
        public readonly List<Scope> Scopes = new List<Scope>();
        public ThreadContext Thread;       // non-null inside a kernel
        public Stmt CurrentStmt;
        public int CallDepth;

        public Scope Current()
        {
            return Scopes[Scopes.Count - 1];
        }

        public Scope Push()
        {
            Scope parent = Scopes.Count == 0 ? null : Scopes[Scopes.Count - 1];
            Scope s = new Scope(parent);
            Scopes.Add(s);
            return s;
        }

        public void Pop()
        {
            Scopes.RemoveAt(Scopes.Count - 1);
        }
    }
}
