using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>What one launch of one tool produced.</summary>
    public sealed class ProbeRun
    {
        private ProbeRun(string output, KitOutcome outcome)
        {
            Output = output ?? string.Empty;
            Outcome = outcome;
        }

        /// <summary>stdout and stderr together — `java -version` prints its banner to stderr.</summary>
        public string Output { get; private set; }

        /// <summary><see cref="KitOutcome.Installed"/> only means "it ran"; the version still has to parse.</summary>
        public KitOutcome Outcome { get; private set; }

        public static ProbeRun Ran(string output) { return new ProbeRun(output, KitOutcome.Installed); }
        public static ProbeRun TimedOut() { return new ProbeRun(null, KitOutcome.TimedOut); }
        public static ProbeRun Failed(string message) { return new ProbeRun(message, KitOutcome.Inconclusive); }
    }

    /// <summary>
    /// Finds and runs an external tool. The scanner talks to this and nothing else, so its rules
    /// can be tested against a machine that has none of these toolchains installed — which is the
    /// only way a test of "what is installed" can assert anything at all.
    /// </summary>
    public interface IToolProbeRunner
    {
        /// <summary>
        /// Full path of the probe's executable — on PATH, under the install roots its
        /// <see cref="ToolProbe.ExtraDirectoryEnvironmentVariables"/> name, or in one of its
        /// <see cref="ToolProbe.ExtraDirectories"/> — or null when it is on none of them.
        /// <para>
        /// The whole probe is passed rather than just a name because where a toolchain lives is
        /// part of the probe's data: PATH alone answered "not installed" for two toolchains that
        /// were sitting on disk.
        /// </para>
        /// </summary>
        string Resolve(ToolProbe probe);

        /// <summary>Run a resolved executable and capture what it prints.</summary>
        ProbeRun Run(ToolProbe probe, string executablePath, CancellationToken cancellationToken);
    }
}
