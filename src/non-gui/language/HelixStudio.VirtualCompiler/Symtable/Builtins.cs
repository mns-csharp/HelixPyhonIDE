using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Names implicitly bound in every Python module — the <c>builtins</c> module
    /// surface plus module-level dunder identifiers. Sourced from CPython 3.12's
    /// <c>Python/bltinmodule.c</c> and <c>Objects/exceptions.c</c>. Used by
    /// <see cref="NameResolver"/> to suppress false-positive undefined-name errors.
    /// </summary>
    public static class Builtins
    {
        public static readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal)
        {
            // Module-level dunders present in every module's namespace.
            "__name__", "__doc__", "__package__", "__loader__", "__spec__",
            "__builtins__", "__file__", "__path__", "__debug__",
            "__annotations__", "__dict__", "__class__", "__all__",
            "__version__", "__author__",

            // Constants
            "True", "False", "None", "Ellipsis", "NotImplemented",
            "copyright", "credits", "license", "exit", "quit",

            // Functions
            "abs", "aiter", "all", "anext", "any", "ascii", "bin", "bool",
            "breakpoint", "bytearray", "bytes", "callable", "chr", "classmethod",
            "compile", "complex", "delattr", "dict", "dir", "divmod", "enumerate",
            "eval", "exec", "filter", "float", "format", "frozenset", "getattr",
            "globals", "hasattr", "hash", "help", "hex", "id", "input", "int",
            "isinstance", "issubclass", "iter", "len", "list", "locals", "map",
            "max", "memoryview", "min", "next", "object", "oct", "open", "ord",
            "pow", "print", "property", "range", "repr", "reversed", "round",
            "set", "setattr", "slice", "sorted", "staticmethod", "str", "sum",
            "super", "tuple", "type", "vars", "zip", "__import__",

            // Exceptions / warnings
            "BaseException", "BaseExceptionGroup", "Exception", "ExceptionGroup",
            "ArithmeticError", "AssertionError", "AttributeError",
            "BlockingIOError", "BrokenPipeError", "BufferError", "BytesWarning",
            "ChildProcessError", "ConnectionAbortedError", "ConnectionError",
            "ConnectionRefusedError", "ConnectionResetError", "DeprecationWarning",
            "EOFError", "EnvironmentError", "EncodingWarning", "FileExistsError",
            "FileNotFoundError", "FloatingPointError", "FutureWarning",
            "GeneratorExit", "IOError", "ImportError", "ImportWarning",
            "IndentationError", "IndexError", "InterruptedError",
            "IsADirectoryError", "KeyError", "KeyboardInterrupt", "LookupError",
            "MemoryError", "ModuleNotFoundError", "NameError",
            "NotADirectoryError", "NotImplementedError", "OSError",
            "OverflowError", "PendingDeprecationWarning", "PermissionError",
            "ProcessLookupError", "RecursionError", "ReferenceError",
            "ResourceWarning", "RuntimeError", "RuntimeWarning",
            "StopAsyncIteration", "StopIteration", "SyntaxError", "SyntaxWarning",
            "SystemError", "SystemExit", "TabError", "TimeoutError", "TypeError",
            "UnboundLocalError", "UnicodeDecodeError", "UnicodeEncodeError",
            "UnicodeError", "UnicodeTranslateError", "UnicodeWarning",
            "UserWarning", "ValueError", "Warning", "WindowsError",
            "ZeroDivisionError", "PythonFinalizationError",

            // Typing-only names that appear in many codebases via `from __future__`
            // or forward references. Lenient default — treat as known.
            "TYPE_CHECKING"
        };

        public static bool IsBuiltin(string name)
        {
            return !string.IsNullOrEmpty(name) && Names.Contains(name);
        }

        /// <summary>
        /// Names implicitly available inside every function scope in addition to
        /// <see cref="Names"/>: CPython binds <c>__class__</c> and
        /// <c>__classcell__</c> inside methods when the class uses <c>super()</c>.
        /// </summary>
        public static bool IsImplicitInsideMethod(string name)
        {
            return name == "__class__" || name == "__classcell__";
        }
    }
}
