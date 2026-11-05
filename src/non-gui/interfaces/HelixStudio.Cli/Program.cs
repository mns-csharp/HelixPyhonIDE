namespace HelixPythonNamespace.Cli
{
    using System;

    /// <summary>Console entry point. Wires <see cref="CliRunner"/> to the real stdout/stderr.</summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            return CliRunner.Run(args, Console.Out, Console.Error);
        }
    }
}
