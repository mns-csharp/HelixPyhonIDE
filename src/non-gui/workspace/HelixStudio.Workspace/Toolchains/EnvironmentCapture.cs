using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>One installed package, as the interpreter itself reported it.</summary>
    public sealed class PackageRecord
    {
        public PackageRecord(string name, string version)
        {
            Name = name ?? string.Empty;
            Version = version ?? string.Empty;
        }

        public string Name { get; private set; }

        /// <summary>Verbatim. A version is the package's own word for itself, not ours.</summary>
        public string Version { get; private set; }
    }

    /// <summary>
    /// What the machine looked like when a solution was run.
    ///
    /// This is the difference between "the project declares its dependencies" — which every Python
    /// tool can say — and "this result was produced by these exact versions", which is the claim a
    /// reproducibility argument actually needs. pyproject.toml says what was asked for; this says
    /// what answered.
    ///
    /// It inherits the Development Environment tab's three honesty rules, and they are the reason
    /// most of the fields below come in pairs:
    ///
    /// 1. A tool that could not be identified is recorded as UNIDENTIFIED with the reason, never as
    ///    missing. "We could not tell" and "it is not there" are different facts.
    /// 2. A capture that could not complete says so. A partial list presented as the inventory is
    ///    the failure this whole record exists to prevent.
    /// 3. Every version string is the tool's own words, unparsed and untrimmed of meaning.
    /// </summary>
    public sealed class EnvironmentRecord
    {
        /// <summary>When this was taken. UTC, because a lock file crosses machines.</summary>
        public DateTime CapturedUtc = DateTime.UtcNow;

        /// <summary>What wrote it.</summary>
        public string Product = "HelixPython";
        public string ProductVersion = string.Empty;

        public string OperatingSystem = string.Empty;
        public string Architecture = string.Empty;

        /// <summary>The interpreter the run used, and what it said about itself.</summary>
        public string InterpreterPath = string.Empty;
        public string InterpreterVersion = string.Empty;
        public KitOutcome InterpreterOutcome = KitOutcome.NotFound;
        public string InterpreterDetail = string.Empty;

        /// <summary>Resolved package versions, as the interpreter's own package index reported them.</summary>
        public IList<PackageRecord> Packages = new List<PackageRecord>();

        /// <summary>False when the package list could not be obtained, or is known to be partial.</summary>
        public bool PackagesResolved;
        public string PackagesDetail = string.Empty;

        /// <summary>The machine's language kits, from the Development Environment scanner.</summary>
        public IList<InstalledKit> Toolchains = new List<InstalledKit>();

        /// <summary>False when the toolchain scan was cancelled or never run.</summary>
        public bool ToolchainScanComplete;

        /// <summary>
        /// True only when every part answered: the interpreter identified itself, the packages
        /// resolved, and the toolchain scan finished. Anything less and the record says so on its
        /// face rather than reading as a full inventory.
        /// </summary>
        public bool IsComplete
        {
            get
            {
                return InterpreterOutcome == KitOutcome.Installed
                    && PackagesResolved
                    && ToolchainScanComplete;
            }
        }

        /// <summary>A sentence naming what is missing, or null when nothing is.</summary>
        public string Incompleteness
        {
            get
            {
                var reasons = new List<string>();
                if (InterpreterOutcome != KitOutcome.Installed)
                    reasons.Add("the interpreter was not identified (" + Describe(InterpreterOutcome) + ")");
                if (!PackagesResolved)
                    reasons.Add("the installed packages could not be listed");
                if (!ToolchainScanComplete)
                    reasons.Add("the toolchain scan did not finish");
                return reasons.Count == 0 ? null : string.Join("; ", reasons.ToArray());
            }
        }

        internal static string Describe(KitOutcome outcome)
        {
            switch (outcome)
            {
                case KitOutcome.Installed: return "identified";
                case KitOutcome.NotFound: return "not found";
                case KitOutcome.TimedOut: return "timed out";
                default: return "unidentified";
            }
        }

        /// <summary>
        /// The record as TOML, beside the manifest it describes.
        ///
        /// TOML because pyproject.toml is, so the two read as one project rather than as a project
        /// plus a foreign artefact — and because a person has to be able to read this in a diff.
        /// </summary>
        public string ToToml()
        {
            var sb = new StringBuilder();

            sb.Append("# Written by ").Append(Product)
              .Append(" on every run. This is the environment that ANSWERED;\n");
            sb.Append("# pyproject.toml is what was asked for. Do not edit by hand.\n\n");

            sb.Append("[capture]\n");
            Pair(sb, "captured_utc", CapturedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            Pair(sb, "product", Product);
            Pair(sb, "product_version", ProductVersion);
            sb.Append("complete = ").Append(IsComplete ? "true" : "false").Append('\n');
            string missing = Incompleteness;
            if (missing != null) Pair(sb, "incomplete_because", missing);
            sb.Append('\n');

            sb.Append("[machine]\n");
            Pair(sb, "operating_system", OperatingSystem);
            Pair(sb, "architecture", Architecture);
            sb.Append('\n');

            sb.Append("[interpreter]\n");
            Pair(sb, "path", InterpreterPath);
            Pair(sb, "outcome", Describe(InterpreterOutcome));
            if (InterpreterOutcome == KitOutcome.Installed) Pair(sb, "version", InterpreterVersion);
            else if (!string.IsNullOrEmpty(InterpreterDetail)) Pair(sb, "detail", InterpreterDetail);
            sb.Append('\n');

            sb.Append("[packages]\n");
            sb.Append("resolved = ").Append(PackagesResolved ? "true" : "false").Append('\n');
            sb.Append("count = ").Append(Packages.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (!PackagesResolved && !string.IsNullOrEmpty(PackagesDetail))
                Pair(sb, "detail", PackagesDetail);
            sb.Append('\n');

            foreach (PackageRecord package in Packages)
            {
                sb.Append("[[package]]\n");
                Pair(sb, "name", package.Name);
                Pair(sb, "version", package.Version);
                sb.Append('\n');
            }

            sb.Append("[toolchains]\n");
            sb.Append("scan_complete = ").Append(ToolchainScanComplete ? "true" : "false").Append('\n');
            sb.Append('\n');

            foreach (InstalledKit kit in Toolchains)
            {
                sb.Append("[[toolchain]]\n");
                Pair(sb, "language", kit.Language);
                Pair(sb, "component", kit.Component);
                Pair(sb, "outcome", Describe(kit.Outcome));
                if (kit.Outcome == KitOutcome.Installed)
                {
                    Pair(sb, "version", kit.Version);
                    Pair(sb, "path", kit.Path);
                }
                else if (!string.IsNullOrEmpty(kit.Detail))
                {
                    // The reason a probe could not identify a tool, kept because "we could not tell"
                    // and "it is not installed" are different facts about this machine.
                    Pair(sb, "detail", kit.Detail);
                }
                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static void Pair(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append(" = ").Append(Quote(value)).Append('\n');
        }

        /// <summary>A TOML basic string. Backslashes and quotes escaped; a Windows path is full of both.</summary>
        public static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>Runs the interpreter to ask it about itself. Abstracted so the capture is testable
    /// on a machine with no Python at all — which is the only way to test the failure branches.</summary>
    public interface IInterpreterProbe
    {
        /// <summary>Run <paramref name="executable"/> with <paramref name="arguments"/>, capturing
        /// stdout and stderr together.</summary>
        ProbeRun Run(string executable, string arguments, CancellationToken cancellationToken);
    }

    /// <summary>Takes the environment record, and writes it beside the manifest.</summary>
    public static class EnvironmentCapture
    {
        /// <summary>The file, beside pyproject.toml.</summary>
        public const string FileName = "environment.lock.toml";

        /// <summary>
        /// Ask the interpreter what it is and what it has.
        ///
        /// Never throws: a capture that failed is a record saying it failed, not an exception on the
        /// path to running the user's program. Running code is the point; describing the machine is
        /// a service to whoever reads the result later, and it must not be able to stop the run.
        /// </summary>
        public static EnvironmentRecord Capture(string interpreterPath,
                                                DevelopmentEnvironmentReport toolchains,
                                                IInterpreterProbe probe,
                                                CancellationToken cancellationToken)
        {
            var record = new EnvironmentRecord
            {
                InterpreterPath = interpreterPath ?? string.Empty,
                OperatingSystem = SafeDescribe(),
                Architecture = IntPtr.Size == 8 ? "x64" : "x86",
                ProductVersion = ProductVersionOf(typeof(EnvironmentCapture)),
            };

            if (toolchains != null)
            {
                record.Toolchains = Flatten(toolchains);
                record.ToolchainScanComplete = toolchains.IsCompleteScan;
            }

            if (probe == null || string.IsNullOrEmpty(interpreterPath))
            {
                record.InterpreterOutcome = KitOutcome.NotFound;
                record.InterpreterDetail = "no interpreter was resolved for this run";
                record.PackagesDetail = "no interpreter to ask";
                return record;
            }

            // -VV, not -V: the long form carries the build and compiler, which is what tells two
            // 3.12.1 installations apart.
            ProbeRun version = Safe(probe, interpreterPath, "-VV", cancellationToken);
            record.InterpreterOutcome = version.Outcome;
            if (version.Outcome == KitOutcome.Installed)
                record.InterpreterVersion = (version.Output ?? string.Empty).Trim();
            else
                record.InterpreterDetail = (version.Output ?? string.Empty).Trim();

            ProbeRun freeze = Safe(probe, interpreterPath, "-m pip freeze --all", cancellationToken);
            if (freeze.Outcome == KitOutcome.Installed)
            {
                record.Packages = ParseFreeze(freeze.Output);
                record.PackagesResolved = true;
            }
            else
            {
                record.PackagesDetail = string.IsNullOrEmpty(freeze.Output)
                    ? EnvironmentRecord.Describe(freeze.Outcome)
                    : freeze.Output.Trim();
            }

            return record;
        }

        /// <summary>
        /// Parse <c>pip freeze</c>. Only <c>name==version</c> lines are taken as versions.
        ///
        /// An editable install prints <c>-e git+https://…</c> and a local one prints a path; neither
        /// is a version, and recording the left-hand side as one would put a lie in a lock file. They
        /// are kept with their text as the version, which is what pip itself would replay.
        /// </summary>
        public static IList<PackageRecord> ParseFreeze(string output)
        {
            var packages = new List<PackageRecord>();
            if (string.IsNullOrEmpty(output)) return packages;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int split = line.IndexOf("==", StringComparison.Ordinal);
                if (split > 0)
                {
                    packages.Add(new PackageRecord(line.Substring(0, split).Trim(),
                                                   line.Substring(split + 2).Trim()));
                }
                else if (line.StartsWith("-e ", StringComparison.Ordinal) || line.StartsWith("@ ", StringComparison.Ordinal))
                {
                    packages.Add(new PackageRecord(line, string.Empty));
                }
                else if (line.IndexOf(' ') < 0 && line.IndexOf('@') > 0)
                {
                    // "name @ file:///..." arrives without spaces in some pip versions.
                    packages.Add(new PackageRecord(line.Substring(0, line.IndexOf('@')).Trim(), line));
                }
            }

            return packages;
        }

        /// <summary>
        /// Write the record beside <paramref name="manifestPath"/> and return where it went.
        ///
        /// Returns null rather than throwing when the directory is unwritable: a read-only checkout
        /// is a normal thing to run from, and failing the run over a note about the machine would be
        /// the wrong trade.
        /// </summary>
        public static string WriteBeside(string manifestPath, EnvironmentRecord record)
        {
            if (record == null || string.IsNullOrEmpty(manifestPath)) return null;

            try
            {
                string directory = Directory.Exists(manifestPath)
                    ? manifestPath
                    : Path.GetDirectoryName(manifestPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

                string path = Path.Combine(directory, FileName);
                File.WriteAllText(path, record.ToToml(), new UTF8Encoding(false));
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static ProbeRun Safe(IInterpreterProbe probe, string exe, string args, CancellationToken cancel)
        {
            try { return probe.Run(exe, args, cancel) ?? ProbeRun.Failed("the probe returned nothing"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return ProbeRun.Failed(ex.Message); }
        }

        private static IList<InstalledKit> Flatten(DevelopmentEnvironmentReport report)
        {
            var kits = new List<InstalledKit>();
            foreach (KitGroup group in report.Groups)
                foreach (InstalledKit kit in group.Kits) kits.Add(kit);
            return kits;
        }

        private static string SafeDescribe()
        {
            try { return Environment.OSVersion.VersionString; }
            catch (Exception) { return string.Empty; }
        }

        private static string ProductVersionOf(Type type)
        {
            try
            {
                var version = type.Assembly.GetName().Version;
                return version == null ? string.Empty : version.ToString();
            }
            catch (Exception) { return string.Empty; }
        }
    }
}
