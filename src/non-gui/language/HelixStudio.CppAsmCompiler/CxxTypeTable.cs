using System;
using System.Collections.Generic;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;

namespace HelixPythonNamespace.CppAsmCompiler
{
    /// <summary>The two machine types this subset compiles. Everything else is refused.</summary>
    public enum VCxx
    {
        Int,      // C++ `int`: 32-bit two's complement, wrapping
        Double,   // C++ `double`: IEEE-754 binary64
    }

    /// <summary>Signature of a translatable function.</summary>
    public sealed class CxxFunctionType
    {
        public string Name { get; }
        public IReadOnlyList<VCxx> Parameters { get; }
        public VCxx Returns { get; }

        public CxxFunctionType(string name, IReadOnlyList<VCxx> parameters, VCxx returns)
        {
            Name = name;
            Parameters = parameters ?? new List<VCxx>();
            Returns = returns;
        }
    }

    /// <summary>
    /// Declared types of a function's parameters and locals.
    ///
    /// C++ declares its locals, so unlike the Python translator there is nothing to infer here —
    /// this walks the body, records each declaration, and refuses anything the emitter could not
    /// represent. Two rules are enforced that C++ itself allows:
    ///
    /// * a name may be declared only ONCE per function. The frame gives each name one slot, so an
    ///   inner block redeclaring an outer name would silently share its storage. Refusing is the
    ///   only honest option short of implementing scoped slots.
    /// * every local must have an initializer. C++ leaves an uninitialised local indeterminate, and
    ///   a translator that quietly zeroed it would produce a program that behaves DIFFERENTLY from
    ///   the same source under a real compiler — the one thing a translator must never do.
    /// </summary>
    public sealed class CxxTypeTable
    {
        private readonly Dictionary<string, VCxx> _locals = new Dictionary<string, VCxx>(StringComparer.Ordinal);

        public string Error { get; private set; }
        public int ErrorLine { get; private set; }

        public bool TryGetLocal(string name, out VCxx type)
        {
            return _locals.TryGetValue(name, out type);
        }

        public IEnumerable<KeyValuePair<string, VCxx>> Locals { get { return _locals; } }

        /// <summary>Maps a declared C++ type onto the subset, or fails with a reason.</summary>
        public static bool TryType(CxxType t, out VCxx v, out string why)
        {
            v = VCxx.Int;
            why = null;
            if (t == null || t.IsEmpty()) { why = "missing type"; return false; }
            if (t.IsPointer()) { why = "pointers and arrays are not supported (there is no heap or addressable memory model in this subset)"; return false; }
            if (t.IsReference) { why = "references are not supported (every value in this subset lives in a register or a frame slot, so there is nothing to alias)"; return false; }
            if (t.TemplateArgs != null && t.TemplateArgs.Count > 0) { why = "template types are not supported"; return false; }

            switch (t.Base)
            {
                case "int":
                    v = VCxx.Int;
                    return true;
                case "double":
                    v = VCxx.Double;
                    return true;
                case "float":
                    why = "'float' is not supported; use 'double'. Emitting binary64 for a "
                        + "binary32 declaration would give different results from a real compiler "
                        + "at the edges of precision";
                    return false;
                case "void":
                    why = "'void' functions are not supported: with no pointers, references or "
                        + "globals in this subset, a function returning void could have no "
                        + "observable effect";
                    return false;
                default:
                    why = "type '" + t + "' is not supported; this subset compiles 'int' and 'double'";
                    return false;
            }
        }

        public static string Describe(VCxx t) { return t == VCxx.Double ? "double" : "int"; }

        /// <summary>Collects parameter and local types for one function body.</summary>
        public static CxxTypeTable Build(FunctionDecl fn, CxxFunctionType signature)
        {
            var table = new CxxTypeTable();
            for (int i = 0; i < fn.Params.Count; i++)
                table._locals[fn.Params[i].Name] = signature.Parameters[i];

            table.Walk(fn.Body);
            return table;
        }

        private void Fail(string reason, int line)
        {
            if (Error == null) { Error = reason; ErrorLine = line; }
        }

        private void Walk(Stmt s)
        {
            if (s == null || Error != null) return;

            if (s is CompoundStmt block)
            {
                foreach (Stmt inner in block.Body) Walk(inner);
                return;
            }
            if (s is DeclStmt decl)
            {
                if (!(decl.Decl is VarDecl v))
                {
                    Fail("only variable declarations are supported inside a function; found "
                         + decl.Decl.GetType().Name, decl.Span.Start.Line);
                    return;
                }
                if (v.Storage != StorageClass.None)
                {
                    Fail("storage qualifiers ('static', 'extern', __device__ …) are not supported "
                         + "on a local; every local in this subset lives in the frame",
                         decl.Span.Start.Line);
                    return;
                }
                if (v.ArraySize != null)
                {
                    Fail("array locals are not supported", decl.Span.Start.Line);
                    return;
                }
                VCxx type;
                string why;
                if (!TryType(v.Type, out type, out why))
                {
                    Fail("local '" + v.Name + "': " + why, decl.Span.Start.Line);
                    return;
                }
                if (v.Initializer == null)
                {
                    Fail("local '" + v.Name + "' must be initialised where it is declared; an "
                         + "uninitialised local is indeterminate in C++, and this translator will "
                         + "not invent a value a real compiler does not guarantee",
                         decl.Span.Start.Line);
                    return;
                }
                if (_locals.ContainsKey(v.Name))
                {
                    Fail("'" + v.Name + "' is declared more than once in this function; each name "
                         + "gets one frame slot, so a redeclaration would share storage with the "
                         + "outer one instead of shadowing it", decl.Span.Start.Line);
                    return;
                }
                _locals[v.Name] = type;
                return;
            }
            if (s is IfStmt ifs) { Walk(ifs.Then); Walk(ifs.Else); return; }
            if (s is WhileStmt w) { Walk(w.Body); return; }
            if (s is DoStmt d) { Walk(d.Body); return; }
            if (s is ForStmt f) { Walk(f.Init); Walk(f.Body); return; }
            if (s is SwitchStmt sw)
            {
                Fail("'switch' is not supported; use if/else", sw.Span.Start.Line);
                return;
            }
            // Expression, return, break, continue and empty statements declare nothing.
        }
    }
}
