namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;

    /// <summary>
    /// Where a name is defined, for the IDE's Go to Definition (F12) in CUDA-C
    /// files. 1-based line/column, matching the editor's addressing.
    /// </summary>
    public sealed class CudaDefinitionLocation
    {
        public readonly string FilePath;
        public readonly int Line;
        public readonly int Column;
        public readonly string SymbolKind;

        public CudaDefinitionLocation(string filePath, int line, int column, string symbolKind)
        {
            FilePath = filePath ?? string.Empty;
            Line = line;
            Column = column;
            SymbolKind = symbolKind ?? string.Empty;
        }
    }

    /// <summary>
    /// Symbol-table-backed Go to Definition for CUDA-C sources (SRS §10
    /// IntelliSense integration — replaces having no CUDA navigation at all).
    /// Compiles with the analysis passes off (lex/parse/symtable only), then
    /// resolves the token against the scope tree: functions first (definition
    /// preferred over a prototype), then types/globals/enum constants by scope
    /// walk. Seeded builtins are never navigation targets — they have no source
    /// location (their DefSpan is empty), so `printf` or `threadIdx` return null
    /// rather than jumping to a fake line 1.
    /// </summary>
    public static class CudaDefinitionLocator
    {
        public static CudaDefinitionLocation Locate(string source, string filePath, string token)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token)) return null;

            CudaCompileOptions options = new CudaCompileOptions();
            options.EnableAstValidator = false;
            options.EnableLaunchConfig = false;
            options.EnableQualifierSync = false;
            options.EnableMemorySpace = false;
            options.EnableLint = false;
            options.EnableLanguageStandard = false;
            options.EnableNameResolution = false;

            CudaCompileResult result = CudaVirtualCompiler.CompileSource(source, filePath, options);
            if (result.SymbolTable == null) return null;

            // Functions via the flat index: prefer the definition (has a body) over
            // a forward declaration, and either over nothing.
            List<Symbol> fns = result.SymbolTable.FindFunctions(token);
            Symbol best = null;
            for (int i = 0; i < fns.Count; ++i)
            {
                Symbol s = fns[i];
                if (!HasLocation(s)) continue;   // seeded builtin
                if (best == null) best = s;
                if (s.FuncDecl != null && s.FuncDecl.HasBody) { best = s; break; }
            }
            if (best != null) return Location(best, filePath, "function");

            // Everything else (types, globals, enum constants, fields): first
            // located symbol of that name anywhere in the scope tree.
            Symbol found = FindInScope(result.SymbolTable.Root, token);
            if (found != null) return Location(found, filePath, KindName(found.Kind));
            return null;
        }

        private static Symbol FindInScope(Scope scope, string token)
        {
            List<Symbol> local = scope.LookupAllLocal(token);
            for (int i = 0; i < local.Count; ++i)
            {
                if (HasLocation(local[i])) return local[i];
            }
            IReadOnlyList<Scope> children = scope.Children;
            for (int i = 0; i < children.Count; ++i)
            {
                Symbol s = FindInScope(children[i], token);
                if (s != null) return s;
            }
            return null;
        }

        // Seeded builtins have a default (empty) DefSpan; anything parsed from the
        // source ends past offset 0.
        private static bool HasLocation(Symbol s)
        {
            return s != null && s.DefSpan.End.Offset > 0;
        }

        private static CudaDefinitionLocation Location(Symbol s, string filePath, string kind)
        {
            return new CudaDefinitionLocation(filePath, s.DefSpan.Start.Line, s.DefSpan.Start.Column, kind);
        }

        private static string KindName(SymbolKind k)
        {
            switch (k)
            {
                case SymbolKind.Type: return "type";
                case SymbolKind.Variable: return "variable";
                case SymbolKind.Parameter: return "parameter";
                case SymbolKind.Field: return "field";
                case SymbolKind.EnumConstant: return "enum constant";
                case SymbolKind.Namespace: return "namespace";
                case SymbolKind.Function: return "function";
                default: return "symbol";
            }
        }
    }
}
