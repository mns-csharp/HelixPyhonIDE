using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>How a CMake command argument was written.</summary>
    public enum CMakeArgKind
    {
        /// <summary>A bare token, e.g. <c>STATIC</c>, <c>${SRC}</c>, <c>main.cpp</c>.</summary>
        Unquoted,
        /// <summary>A double-quoted argument, e.g. <c>"hello world"</c>.</summary>
        Quoted,
        /// <summary>A bracket argument, e.g. <c>[[ ... ]]</c> / <c>[=[ ... ]=]</c>.</summary>
        Bracket,
    }

    /// <summary>A <c>${VAR}</c>, <c>$ENV{VAR}</c>, <c>$CACHE{VAR}</c>, or generator-expression <c>$&lt;...&gt;</c> reference inside an argument.</summary>
    public sealed class CMakeReference
    {
        public string Name;                 // variable/genex name (best-effort for genex)
        public bool IsGeneratorExpression;  // $<...>  vs  ${...}
        public string Scope;                // "", "ENV", or "CACHE"
        public int Offset;                  // char offset within the argument's Text
    }

    /// <summary>One parsed command argument, with its source position and any variable/genex references.</summary>
    public sealed class CMakeArgument
    {
        public string Text = "";            // logical value (quotes/brackets removed)
        public string Raw = "";             // exactly as written
        public CMakeArgKind Kind = CMakeArgKind.Unquoted;
        public int Line;
        public int Column;
        public readonly List<CMakeReference> References = new List<CMakeReference>();

        public override string ToString() { return Raw; }
    }

    /// <summary>Base type for CMake AST nodes.</summary>
    public abstract class CMakeNode
    {
        public int Line;
        public int Column;
    }

    /// <summary>A comment (line <c>#...</c> or bracket <c>#[[...]]</c>).</summary>
    public sealed class CMakeComment : CMakeNode
    {
        public string Text = "";
    }

    /// <summary>
    /// A command invocation, e.g. <c>add_executable(app main.cpp)</c>. Block-opening commands
    /// (<c>if</c>/<c>foreach</c>/<c>while</c>/<c>function</c>/<c>macro</c>/<c>block</c>) additionally
    /// carry a <see cref="Body"/> of nested nodes and the matching <see cref="EndCommand"/>.
    /// </summary>
    public sealed class CMakeCommand : CMakeNode
    {
        public string Name = "";
        public readonly List<CMakeArgument> Arguments = new List<CMakeArgument>();

        public bool IsBlockOpen;
        public bool IsBlockClose;
        public bool IsBlockMiddle;          // else / elseif
        public List<CMakeNode> Body;        // non-null only for block-opening commands
        public CMakeCommand EndCommand;     // the matching end*; null if unbalanced

        public override string ToString() { return Name + "(...)"; }
    }

    /// <summary>Severity + code + position of a parse problem.</summary>
    public sealed class CMakeParseDiagnostic
    {
        public int Line;
        public int Column;
        public string Code = "";            // e.g. CMP001
        public string Message = "";
        public bool IsError = true;

        public override string ToString()
        {
            return "(" + Line + "," + Column + ") " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// The parsed AST of a <c>CMakeLists.txt</c> / <c>*.cmake</c> file (FR-CORE-08): a block-nested node
    /// tree, a flat command list in source order, and parse diagnostics. The real language front-end that
    /// underpins the CMake linter (FR-LINT-04) and analyzer (FR-ANA-05), replacing the pragmatic
    /// command-tokenizer used by <see cref="CMakeListsReader"/>.
    /// </summary>
    public sealed class CMakeFile
    {
        public readonly List<CMakeNode> Nodes = new List<CMakeNode>();          // top level; blocks nest their bodies
        public readonly List<CMakeCommand> Commands = new List<CMakeCommand>(); // flat, source order
        public readonly List<CMakeParseDiagnostic> Diagnostics = new List<CMakeParseDiagnostic>();

        public bool HasErrors
        {
            get { for (int i = 0; i < Diagnostics.Count; i++) if (Diagnostics[i].IsError) return true; return false; }
        }
    }
}
