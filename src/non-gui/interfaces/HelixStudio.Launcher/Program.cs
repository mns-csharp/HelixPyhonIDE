namespace HelixPythonNamespace.Launcher
{
    using System;

    /// <summary>
    /// Console entry point for <c>helixstudio.exe</c>. Wires <see cref="SolutionLauncher"/> to the
    /// real stdout/stderr and returns the launched program's exit code, so the command composes in
    /// a script the way any other console tool does.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            return SolutionLauncher.Run(args, Console.Out, Console.Error, new ProcessProgramLauncher());
        }
    }
}
