namespace HelixPythonNamespace.Cli
{
    /// <summary>Parsed command-line flags. Port of the C++ <c>Options</c> struct.</summary>
    public sealed class CliOptions
    {
        public bool Json;
        public bool Quiet;
        public bool IncludeWarnings = true;
        /// <summary>cuda-compile: also run the REAL nvcc for authoritative diagnostics (parity check).</summary>
        public bool Nvcc;
        public string CodeFilter = "";

        /// <summary>
        /// For the <c>metrics</c> command: if set (&gt; 0), the command exits with the
        /// count of functions whose cyclomatic complexity exceeds this value — a CI gate.
        /// 0 (the default) means "report only, never fail".
        /// </summary>
        public int MaxComplexity = 0;

        /// <summary><c>refactor --kind=&lt;K&gt;</c>: the structural transform to apply.</summary>
        public string RefactorKind = "";

        /// <summary><c>refactor --apply</c>: print the transformed source.</summary>
        public bool Apply;

        /// <summary><c>refactor --roundtrip=&lt;K&gt;</c>: apply forward K then its reverse; exact check.</summary>
        public string RoundtripKind = "";

        /// <summary><c>refactor --auto</c>: list only candidates hinted by analysis findings.</summary>
        public bool AutoHint;

        /// <summary><c>gui-emit --framework=&lt;tkinter|pyqt6|pyside6|wxpython&gt;</c>.</summary>
        public string Framework = "";

        /// <summary><c>cargo-build/cargo-run --release</c> (and future dotnet/pkg runners): build in release/optimized mode.</summary>
        public bool Release;

        /// <summary><c>julia-run --entry=&lt;script.jl&gt;</c>: explicit entry script (else the package is loaded by name).</summary>
        public string Entry = "";

        /// <summary>A secondary path argument: <c>venv-create --python=&lt;base&gt;</c> / <c>req-sync --pyproject=&lt;path&gt;</c> / <c>venv-bind --venv=&lt;dir&gt;</c>.</summary>
        public string AuxPath = "";

        /// <summary><c>java-debug --line=&lt;n&gt;</c>: source line to break at.</summary>
        public int DebugLine = 0;

        /// <summary><c>java-debug --steps=&lt;n&gt;</c>: single-steps to take after the first breakpoint (default 1).</summary>
        public int DebugSteps = 1;

        /// <summary><c>query --sql=&lt;statement&gt;</c>: the atoms query to run against a structure.</summary>
        public string Sql = "";

        /// <summary><c>query --frames</c>: answer the query once per frame instead of once.</summary>
        public bool PerFrame;

        /// <summary>
        /// <c>dssp --reference</c>: DsspOptions.ReferenceCompatible(), the seven switches set the
        /// way mkdssp sets them, so a run can be diffed against mkdssp line for line.
        /// </summary>
        public bool DsspReference;

        /// <summary><c>dssp --reference=&lt;2|4&gt;</c>: which mkdssp major version to match. 4 by
        /// default; 2 drops polyproline-II and the pi-helix priority, which 2.x does not have.</summary>
        public int DsspReferenceMajor = 4;

        /// <summary><c>dssp --recursive</c>: descend into subdirectories.</summary>
        public bool Recursive;

        /// <summary><c>cuda-profile --block=&lt;n&gt;</c>: threads-per-block assumed for the theoretical-occupancy calc (default 256).</summary>
        public int BlockSize = 256;
    }
}
