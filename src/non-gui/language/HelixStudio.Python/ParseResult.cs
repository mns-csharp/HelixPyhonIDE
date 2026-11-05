using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python
{
    public sealed class ParseResult
    {
        public readonly ModuleSyntax Module;
        public readonly IReadOnlyList<ParseDiagnostic> Diagnostics;

        public ParseResult(ModuleSyntax module, IReadOnlyList<ParseDiagnostic> diagnostics)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            Diagnostics = diagnostics ?? Array.Empty<ParseDiagnostic>();
        }

        public bool HasErrors
        {
            get
            {
                foreach (ParseDiagnostic d in Diagnostics)
                    if (d.Severity >= ParseSeverity.Error) return true;
                return false;
            }
        }
    }
}
