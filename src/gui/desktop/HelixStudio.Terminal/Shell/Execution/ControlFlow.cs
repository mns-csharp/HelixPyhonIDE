namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>Thrown by the <c>break</c> builtin and caught by the enclosing loop.</summary>
public sealed class LoopBreakException : Exception
{
    public LoopBreakException(int levels) => Levels = levels;

    public int Levels { get; set; }
}

/// <summary>Thrown by the <c>continue</c> builtin.</summary>
public sealed class LoopContinueException : Exception
{
    public LoopContinueException(int levels) => Levels = levels;

    public int Levels { get; set; }
}

/// <summary>Thrown by the <c>return</c> builtin; caught at the function-call boundary.</summary>
public sealed class FunctionReturnException : Exception
{
    public FunctionReturnException(int exitCode) => ExitCode = exitCode;

    public int ExitCode { get; }
}

/// <summary>Thrown by the <c>exit</c> builtin; unwinds to the top of the shell.</summary>
public sealed class ShellExitException : Exception
{
    public ShellExitException(int exitCode) => ExitCode = exitCode;

    public int ExitCode { get; }
}
