using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// CUDA-C completion for <c>.cu</c>/<c>.cuh</c> files — the CUDA counterpart to
    /// <see cref="PythonCompletionEngine"/>. Offers: C/CUDA keywords and qualifiers
    /// (<c>__global__</c>, <c>__device__</c>, <c>__shared__</c>, …), scalar + vector types
    /// (<c>dim3</c>, <c>float4</c>, …), kernel-launch built-ins (<c>threadIdx</c>, <c>blockIdx</c>,
    /// <c>blockDim</c>, <c>gridDim</c>, <c>warpSize</c>), the CUDA runtime / device-intrinsic API
    /// (<c>cudaMalloc</c>, <c>cudaMemcpy</c>, <c>__syncthreads</c>, <c>atomicAdd</c>, …), and the
    /// functions/kernels declared in the current file. Member completion on a coordinate built-in
    /// (<c>threadIdx.</c>, …) offers <c>x</c>/<c>y</c>/<c>z</c>.
    /// </summary>
    public sealed class CudaCompletionEngine : ICompletionEngine
    {
        private static readonly string[] Keywords =
        {
            "__global__", "__device__", "__host__", "__shared__", "__constant__", "__managed__", "__restrict__",
            "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "goto",
            "struct", "union", "enum", "typedef", "class", "template", "typename", "namespace", "using",
            "public", "private", "protected", "const", "static", "extern", "volatile", "inline", "auto",
            "register", "sizeof", "new", "delete", "this", "nullptr", "true", "false",
        };

        private static readonly string[] Types =
        {
            "void", "bool", "char", "short", "int", "long", "float", "double", "unsigned", "signed", "size_t",
            "int2", "int3", "int4", "uint2", "uint3", "uint4", "float2", "float3", "float4",
            "double2", "double3", "double4", "dim3", "cudaError_t", "cudaStream_t", "cudaEvent_t",
        };

        private static readonly string[] Builtins = { "threadIdx", "blockIdx", "blockDim", "gridDim", "warpSize" };

        private static readonly string[] MemcpyKinds =
        {
            "cudaMemcpyHostToDevice", "cudaMemcpyDeviceToHost", "cudaMemcpyDeviceToDevice",
            "cudaMemcpyHostToHost", "cudaMemcpyDefault",
        };

        public CompletionContext Classify(string source, int caretOffset)
        {
            CompletionContext includeContext;
            if (NativePreprocessorCompletion.TryClassifyInclude(source, caretOffset, out includeContext))
                return includeContext;
            CompletionContext ctx = new CompletionContext
            {
                Kind = CompletionContextKind.General,
                Prefix = string.Empty,
                MemberBase = string.Empty,
                PrefixStart = caretOffset,
            };
            if (source == null) return ctx;
            int i = caretOffset;
            if (i > source.Length) i = source.Length;
            if (i < 0) i = 0;

            int s = i;
            while (s > 0 && IsIdChar(source[s - 1])) s--;
            ctx.Prefix = source.Substring(s, i - s);
            ctx.PrefixStart = s;

            int dot = s - 1;
            if (dot >= 0 && (source[dot] == '.' || (dot >= 1 && source[dot] == '>' && source[dot - 1] == '-')))
            {
                int end = source[dot] == '.' ? dot : dot - 1;   // handle both '.' and '->'
                int b = end;
                while (b > 0 && IsIdChar(source[b - 1])) b--;
                ctx.MemberBase = source.Substring(b, end - b);
                if (ctx.MemberBase.Length > 0) ctx.Kind = CompletionContextKind.Member;
            }
            return ctx;
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution)
        {
            return GetCompletions(source, caretOffset, filePath, solution, Classify(source, caretOffset));
        }

        public List<CompletionItem> GetCompletions(string source, int caretOffset, string filePath, SolutionModel solution, CompletionContext ctx)
        {
            if (ctx.Kind == CompletionContextKind.PreprocessorInclude)
                return NativePreprocessorCompletion.CreateHeaderItems(filePath, solution, ctx.IncludeDelimiter);
            if (NativePreprocessorCompletion.IsDirectiveContext(source, caretOffset))
                return NativePreprocessorCompletion.CreateItems();

            List<CompletionItem> items = new List<CompletionItem>();

            if (ctx.Kind == CompletionContextKind.Member)
            {
                // §10 IntelliSense: resolve the base identifier through the real symbol
                // table (variable → declared type → typedef chain → record members).
                // Only the vector/dim3 coordinate fallback remains hardcoded.
                if (TryAddRecordMemberCompletions(items, source, filePath, ctx.MemberBase))
                    return items;

                // uint3/dim3 coordinate built-ins and vector types expose x/y/z (and w for *4 types).
                items.Add(new CompletionItem("x", CompletionKind.Field, null, "unsigned int x"));
                items.Add(new CompletionItem("y", CompletionKind.Field, null, "unsigned int y"));
                items.Add(new CompletionItem("z", CompletionKind.Field, null, "unsigned int z"));
                string b = ctx.MemberBase;
                if (b != "threadIdx" && b != "blockIdx" && b != "blockDim" && b != "gridDim")
                    items.Add(new CompletionItem("w", CompletionKind.Field, null, "w"));
                return items;
            }

            // Keywords are not symbols — always from the keyword list.
            foreach (string kw in Keywords) items.Add(new CompletionItem(kw, CompletionKind.Keyword, null, "keyword " + kw));

            // FR-IC-02: source types, built-ins, the CUDA runtime API, AND the current TU's user symbols
            // (functions/kernels, variables, parameters, fields, types) from the real CudaVirtualCompiler
            // SymbolTable — one table that already seeds the Builtins, so no second hardcoded copy. Fall
            // back to the static seed lists when the source can't be compiled (offline robustness, NFR-03).
            if (!TryAddSymbolTableCompletions(items, source, filePath))
                AddFallbackSeed(items, source);
            return items;
        }

        // Member completion off a user-defined type: find the base variable's symbol,
        // chase its declared type through typedef aliases, locate the record's class
        // scope, and emit its fields/methods. Returns false (caller falls back to the
        // vector x/y/z/w list) when anything along the chain is unresolvable — or
        // when background analysis is off (§8.2: no compile on keystrokes).
        private static bool TryAddRecordMemberCompletions(List<CompletionItem> items, string source,
            string filePath, string memberBase)
        {
            if (!AnalysisGate.BackgroundAnalysisEnabled) return false;
            if (string.IsNullOrEmpty(memberBase)) return false;
            try
            {
                var opts = new global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions
                {
                    EnableQualifierSync = false,
                    EnableLaunchConfig = false,
                    EnableMemorySpace = false,
                    EnableLint = false,
                };
                var result = global::HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(
                    source ?? string.Empty, filePath, opts);
                var root = result != null && result.SymbolTable != null ? result.SymbolTable.Root : null;
                if (root == null) return false;

                var baseSymbol = FindFirstSymbol(root, memberBase, sym =>
                    sym.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Variable ||
                    sym.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Parameter ||
                    sym.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Field);
                if (baseSymbol == null || baseSymbol.Type == null) return false;
                string typeName = baseSymbol.Type.Base;

                // Chase typedef/using aliases: a Type symbol whose own Type.Base names
                // another type. Bounded so an accidental cycle cannot spin.
                for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(typeName); hop++)
                {
                    var typeSymbol = FindFirstSymbol(root, typeName, sym =>
                        sym.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Type);
                    if (typeSymbol == null || typeSymbol.Type == null ||
                        string.IsNullOrEmpty(typeSymbol.Type.Base) ||
                        string.Equals(typeSymbol.Type.Base, typeName, StringComparison.Ordinal))
                        break;
                    typeName = typeSymbol.Type.Base;
                }
                if (string.IsNullOrEmpty(typeName)) return false;

                var recordScope = FindClassScope(root, typeName);
                if (recordScope == null) return false;

                int before = items.Count;
                foreach (var member in recordScope.Symbols)
                {
                    if (member.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Field ||
                        member.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Variable)
                    {
                        string fieldType = member.Type != null ? member.Type.ToString() : string.Empty;
                        items.Add(new CompletionItem(member.Name, CompletionKind.Field, null,
                            (fieldType + " " + member.Name).Trim(), "member of " + typeName));
                    }
                    else if (member.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Function)
                    {
                        string retType = member.Type != null ? member.Type.ToString() : "void";
                        items.Add(new CompletionItem(member.Name, CompletionKind.Method, null,
                            BuildFunctionSignature(member, string.Empty, retType), "member of " + typeName));
                    }
                }
                return items.Count > before;
            }
            catch { return false; }
        }

        private static global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Symbol FindFirstSymbol(
            global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Scope scope, string name,
            Func<global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Symbol, bool> accept)
        {
            foreach (var sym in scope.Symbols)
                if (string.Equals(sym.Name, name, StringComparison.Ordinal) && accept(sym)) return sym;
            foreach (var child in scope.Children)
            {
                var found = FindFirstSymbol(child, name, accept);
                if (found != null) return found;
            }
            return null;
        }

        private static global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Scope FindClassScope(
            global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Scope scope, string name)
        {
            foreach (var child in scope.Children)
            {
                if (child.Kind == global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.BlockKind.Class &&
                    string.Equals(child.Name, name, StringComparison.Ordinal))
                    return child;
                var found = FindClassScope(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // Walk the parsed TU's scope tree and emit every symbol (deduped by name, user symbols preferred
        // over builtins of the same name). Returns false when the source produced no symbol table.
        private static bool TryAddSymbolTableCompletions(List<CompletionItem> items, string source, string filePath)
        {
            // §8.2: this compiles the translation unit on completion keystrokes —
            // normal editing must not invoke the virtual compiler unless the user
            // explicitly enabled background analysis. Off the gate, the static seed
            // lists (keywords, builtins, runtime API) still serve completions.
            if (!AnalysisGate.BackgroundAnalysisEnabled) return false;
            try
            {
                var opts = new global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions
                {
                    // Only the passes needed to build the symbol table — skip the heavier analyses so
                    // completion stays responsive (NFR-01).
                    EnableQualifierSync = false,
                    EnableLaunchConfig = false,
                    EnableMemorySpace = false,
                    EnableLint = false,
                };
                var result = global::HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(source ?? string.Empty, filePath, opts);
                var table = result != null ? result.SymbolTable : null;
                if (table == null || table.Root == null) return false;

                var seen = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
                CollectScope(table.Root, seen);
                if (seen.Count == 0) return false;
                foreach (var kv in seen) items.Add(kv.Value);
                return true;
            }
            catch { return false; }
        }

        private static void CollectScope(global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Scope scope,
            Dictionary<string, CompletionItem> seen)
        {
            if (scope == null) return;
            foreach (var sym in scope.Symbols)
            {
                if (sym == null || string.IsNullOrEmpty(sym.Name)) continue;
                if (sym.Name.StartsWith("__cuda", StringComparison.Ordinal)) continue; // internal
                CompletionItem item = ToItem(sym);
                // User symbols win over builtins of the same name; otherwise first-wins.
                if (!seen.TryGetValue(sym.Name, out var existing) || (!sym.IsBuiltin && existing == null))
                    seen[sym.Name] = item;
                else if (!sym.IsBuiltin) seen[sym.Name] = item;
            }
            foreach (var child in scope.Children) CollectScope(child, seen);
        }

        // Rich runtime/intrinsic signatures for the seeded built-ins (the symbol table doesn't carry
        // built-in parameter lists, so these preserve full signatures for Quick Info / Parameter Info).
        private static readonly Dictionary<string, string> RuntimeSignatures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "__syncthreads", "void __syncthreads()" },
            { "__syncwarp", "void __syncwarp(unsigned mask = 0xffffffff)" },
            { "__threadfence", "void __threadfence()" },
            { "atomicAdd", "T atomicAdd(T* address, T val)" },
            { "atomicSub", "T atomicSub(T* address, T val)" },
            { "atomicExch", "T atomicExch(T* address, T val)" },
            { "atomicCAS", "T atomicCAS(T* address, T compare, T val)" },
            { "atomicMax", "T atomicMax(T* address, T val)" },
            { "atomicMin", "T atomicMin(T* address, T val)" },
            { "cudaMalloc", "cudaError_t cudaMalloc(void** devPtr, size_t size)" },
            { "cudaMallocManaged", "cudaError_t cudaMallocManaged(void** devPtr, size_t size)" },
            { "cudaFree", "cudaError_t cudaFree(void* devPtr)" },
            { "cudaMemcpy", "cudaError_t cudaMemcpy(void* dst, const void* src, size_t count, cudaMemcpyKind kind)" },
            { "cudaMemcpyAsync", "cudaError_t cudaMemcpyAsync(void* dst, const void* src, size_t count, cudaMemcpyKind kind, cudaStream_t stream = 0)" },
            { "cudaMemset", "cudaError_t cudaMemset(void* devPtr, int value, size_t count)" },
            { "cudaDeviceSynchronize", "cudaError_t cudaDeviceSynchronize()" },
            { "cudaGetLastError", "cudaError_t cudaGetLastError()" },
            { "cudaStreamCreate", "cudaError_t cudaStreamCreate(cudaStream_t* pStream)" },
            { "__expf", "float __expf(float x)" },
            { "__logf", "float __logf(float x)" },
            { "sqrtf", "float sqrtf(float x)" },
            { "min", "T min(T a, T b)" },
            { "max", "T max(T a, T b)" },
        };

        private static CompletionItem ToItem(global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Symbol sym)
        {
            string typeStr = sym.Type != null ? sym.Type.ToString() : "";
            switch (sym.Kind)
            {
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Function:
                    string space = sym.IsKernel ? "__global__ " : (sym.Space == global::HelixPythonNamespace.CudaVirtualCompiler.Ast.FuncSpace.Device ? "__device__ " : "");
                    string detail = BuildFunctionSignature(sym, space, typeStr);
                    // FR-KB-06: rich catalog documentation for the seeded built-ins (param descriptions etc.).
                    string doc = CudaCatalog.GetDoc(sym.Name);
                    return new CompletionItem(sym.Name, sym.IsBuiltin ? CompletionKind.Builtin : CompletionKind.Function, null, detail, doc);
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Type:
                    return new CompletionItem(sym.Name, CompletionKind.Class, null, "type " + sym.Name);
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Parameter:
                    return new CompletionItem(sym.Name, CompletionKind.Parameter, null, (typeStr + " " + sym.Name + " (parameter)").Trim());
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Field:
                    return new CompletionItem(sym.Name, CompletionKind.Field, null, (typeStr + " " + sym.Name).Trim());
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.EnumConstant:
                    return new CompletionItem(sym.Name, CompletionKind.Builtin, null, "enum constant " + sym.Name);
                case global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.SymbolKind.Namespace:
                    return new CompletionItem(sym.Name, CompletionKind.Module, null, "namespace " + sym.Name);
                default:
                    return new CompletionItem(sym.Name, sym.IsBuiltin ? CompletionKind.Builtin : CompletionKind.Variable,
                        null, (typeStr + " " + sym.Name).Trim(), CudaCatalog.GetDoc(sym.Name));
            }
        }

        // Full signature "ret name(type p, ...)" for a function symbol: real parameters from the user's
        // FuncDecl, the rich static map for built-ins, and a "(...)" fallback.
        private static string BuildFunctionSignature(global::HelixPythonNamespace.CudaVirtualCompiler.Symtable.Symbol sym, string space, string retType)
        {
            var decl = sym.FuncDecl;
            if (decl != null && decl.Params != null)
            {
                var ps = new List<string>();
                foreach (var p in decl.Params)
                {
                    string pt = p.Type != null ? p.Type.ToString() : "";
                    ps.Add(string.IsNullOrEmpty(p.Name) ? pt : (pt + " " + p.Name).Trim());
                }
                return (space + retType + " " + sym.Name + "(" + string.Join(", ", ps) + ")").Trim();
            }
            string rich;
            if (sym.IsBuiltin && RuntimeSignatures.TryGetValue(sym.Name, out rich)) return rich;
            return (space + retType + " " + sym.Name + "(...)").Trim();
        }

        // Offline fallback: the original static seed + a light file scan (used only when compile fails).
        private static void AddFallbackSeed(List<CompletionItem> items, string source)
        {
            foreach (string t in Types) items.Add(new CompletionItem(t, CompletionKind.Class, null, "type " + t));
            foreach (string bi in Builtins) items.Add(new CompletionItem(bi, CompletionKind.Variable, null, "uint3 " + bi + " (kernel built-in)"));
            foreach (string mk in MemcpyKinds) items.Add(new CompletionItem(mk, CompletionKind.Builtin, null, "cudaMemcpyKind"));
            AddRuntime(items);
            AddFileSymbols(items, source);
        }

        // CUDA runtime + device intrinsics with brief signatures for the calltip/doc panel.
        private static void AddRuntime(List<CompletionItem> items)
        {
            Fn(items, "__syncthreads", "void __syncthreads()");
            Fn(items, "__syncwarp", "void __syncwarp(unsigned mask = 0xffffffff)");
            Fn(items, "__threadfence", "void __threadfence()");
            Fn(items, "atomicAdd", "T atomicAdd(T* address, T val)");
            Fn(items, "atomicSub", "T atomicSub(T* address, T val)");
            Fn(items, "atomicExch", "T atomicExch(T* address, T val)");
            Fn(items, "atomicCAS", "T atomicCAS(T* address, T compare, T val)");
            Fn(items, "atomicMax", "T atomicMax(T* address, T val)");
            Fn(items, "atomicMin", "T atomicMin(T* address, T val)");
            Fn(items, "cudaMalloc", "cudaError_t cudaMalloc(void** devPtr, size_t size)");
            Fn(items, "cudaMallocManaged", "cudaError_t cudaMallocManaged(void** devPtr, size_t size)");
            Fn(items, "cudaFree", "cudaError_t cudaFree(void* devPtr)");
            Fn(items, "cudaMemcpy", "cudaError_t cudaMemcpy(void* dst, const void* src, size_t count, cudaMemcpyKind kind)");
            Fn(items, "cudaMemcpyAsync", "cudaError_t cudaMemcpyAsync(void* dst, const void* src, size_t count, cudaMemcpyKind kind, cudaStream_t stream = 0)");
            Fn(items, "cudaMemset", "cudaError_t cudaMemset(void* devPtr, int value, size_t count)");
            Fn(items, "cudaDeviceSynchronize", "cudaError_t cudaDeviceSynchronize()");
            Fn(items, "cudaGetLastError", "cudaError_t cudaGetLastError()");
            Fn(items, "cudaStreamCreate", "cudaError_t cudaStreamCreate(cudaStream_t* pStream)");
            Fn(items, "__expf", "float __expf(float x)");
            Fn(items, "__logf", "float __logf(float x)");
            Fn(items, "sqrtf", "float sqrtf(float x)");
            Fn(items, "min", "T min(T a, T b)");
            Fn(items, "max", "T max(T a, T b)");
        }

        private static void Fn(List<CompletionItem> items, string name, string sig)
        {
            // FR-IC-09: Detail carries the signature (calltips read it); Documentation
            // carries the catalog prose so Quick Info stays populated even on the
            // no-compile fallback path (the catalog is static data — §8.2-safe).
            items.Add(new CompletionItem(name, CompletionKind.Function, null, sig, CudaCatalog.GetDoc(name)));
        }

        // Best-effort scan of the current file for function / kernel definitions so the user's own
        // symbols appear in the list (icons distinguish them from built-ins).
        private static void AddFileSymbols(List<CompletionItem> items, string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string kw in Keywords) seen.Add(kw);
            try
            {
                foreach (Match m in Regex.Matches(source, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\("))
                {
                    string name = m.Groups[1].Value;
                    if (name.Length > 1 && seen.Add(name))
                        items.Add(new CompletionItem(name, CompletionKind.Function, null, "function " + name));
                }
            }
            catch { /* regex is best-effort; never block completion */ }
        }

        private static bool IsIdChar(char c) { return c == '_' || char.IsLetterOrDigit(c); }
    }
}
