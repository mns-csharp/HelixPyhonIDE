using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.PythonAsmCompiler
{
    /// <summary>
    /// The signature of one emitted function, in Python's own type names (<c>int</c>/<c>float</c>).
    ///
    /// A caller cannot invoke the compiled code without this: under Win64 the third argument
    /// arrives in R8 if it is an integer and in XMM2 if it is a double, and the result comes back
    /// in RAX or XMM0 accordingly. The register is chosen by the declared type, not by the value.
    /// </summary>
    public sealed class TranslatedFunction
    {
        public string Name { get; }
        public IReadOnlyList<string> ParameterTypes { get; }
        public string ReturnType { get; }

        public TranslatedFunction(string name, IReadOnlyList<string> parameterTypes, string returnType)
        {
            Name = name ?? string.Empty;
            ParameterTypes = parameterTypes ?? Array.Empty<string>();
            ReturnType = returnType ?? "int";
        }

        public override string ToString()
        {
            return Name + "(" + string.Join(", ", ParameterTypes) + ") -> " + ReturnType;
        }
    }
}
