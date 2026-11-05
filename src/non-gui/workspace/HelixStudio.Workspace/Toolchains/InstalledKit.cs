using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// What a probe learned about one tool. The distinction between "not installed" and
    /// "could not tell" is load-bearing: a probe that timed out or failed to launch is ABSENCE OF
    /// EVIDENCE, and reporting it as "not installed" would be a claim the scan never established.
    /// </summary>
    public enum KitOutcome
    {
        /// <summary>The executable ran and reported a version.</summary>
        Installed = 0,

        /// <summary>The executable is nowhere on PATH or in the well-known locations.</summary>
        NotFound = 1,

        /// <summary>The executable exists and ran, but did not answer within the budget.</summary>
        TimedOut = 2,

        /// <summary>The executable exists but the probe could not be completed (launch error,
        /// unreadable output, unrecognised version banner).</summary>
        Inconclusive = 3,
    }

    /// <summary>One tool of one language kit, as found on THIS machine.</summary>
    public sealed class InstalledKit
    {
        public InstalledKit(string language, string component, string version, string path,
            KitOutcome outcome, string detail)
        {
            Language = language;
            Component = component;
            Version = version;
            Path = path;
            Outcome = outcome;
            Detail = detail;
        }

        /// <summary>The kit this tool belongs to — "Python", "C / C++", "Java", …</summary>
        public string Language { get; private set; }

        /// <summary>The tool itself — "CPython interpreter", "nvcc", "Cargo", …</summary>
        public string Component { get; private set; }

        /// <summary>The version as reported by the tool, or null when it never answered.</summary>
        public string Version { get; private set; }

        /// <summary>Full path to the executable, or null when it was never found.</summary>
        public string Path { get; private set; }

        public KitOutcome Outcome { get; private set; }

        /// <summary>The tool's own first line of output, kept verbatim for the details pane.</summary>
        public string Detail { get; private set; }

        public bool IsInstalled { get { return Outcome == KitOutcome.Installed; } }

        public override string ToString()
        {
            return Component + (string.IsNullOrEmpty(Version) ? "" : " " + Version);
        }
    }

    /// <summary>Every tool found for one language, in catalog order.</summary>
    public sealed class KitGroup
    {
        public KitGroup(string language, IList<InstalledKit> kits)
        {
            Language = language;
            Kits = kits ?? new List<InstalledKit>();
        }

        public string Language { get; private set; }

        public IList<InstalledKit> Kits { get; private set; }

        /// <summary>True when at least one tool of this kit reported a version.</summary>
        public bool IsInstalled
        {
            get
            {
                foreach (InstalledKit k in Kits) if (k.IsInstalled) return true;
                return false;
            }
        }

        /// <summary>
        /// Nothing identified, but something of this kit IS on disk — a tool that timed out or
        /// printed a banner we could not read. Such a kit is neither installed nor missing, and
        /// filing it under either would be a claim the scan did not establish.
        /// </summary>
        public bool IsUnverified
        {
            get
            {
                if (IsInstalled) return false;
                foreach (InstalledKit k in Kits)
                    if (k.Outcome == KitOutcome.TimedOut || k.Outcome == KitOutcome.Inconclusive) return true;
                return false;
            }
        }

        /// <summary>Every probe for this kit came back "not on this machine".</summary>
        public bool IsMissing { get { return !IsInstalled && !IsUnverified; } }
    }

    /// <summary>
    /// The result of one machine scan.
    /// <para>
    /// <see cref="IsCompleteScan"/> is part of any claim made from this report. A cancelled or
    /// partially-probed scan cannot support "language X is not installed" — it only supports
    /// "this scan did not see it".
    /// </para>
    /// </summary>
    public sealed class DevelopmentEnvironmentReport
    {
        public DevelopmentEnvironmentReport(IList<KitGroup> groups, bool isCompleteScan)
        {
            Groups = groups ?? new List<KitGroup>();
            IsCompleteScan = isCompleteScan;
        }

        /// <summary>Every language in the catalog, installed or not, in catalog order.</summary>
        public IList<KitGroup> Groups { get; private set; }

        /// <summary>False when the scan was cancelled before every probe had run.</summary>
        public bool IsCompleteScan { get; private set; }

        public IList<KitGroup> InstalledGroups
        {
            get
            {
                var found = new List<KitGroup>();
                foreach (KitGroup g in Groups) if (g.IsInstalled) found.Add(g);
                return found;
            }
        }

        /// <summary>
        /// Only the kits nothing was found for. A language whose tools were found but could not be
        /// identified is deliberately NOT here — see <see cref="KitGroup.IsUnverified"/>.
        /// </summary>
        public IList<KitGroup> MissingGroups
        {
            get
            {
                var missing = new List<KitGroup>();
                foreach (KitGroup g in Groups) if (g.IsMissing) missing.Add(g);
                return missing;
            }
        }

        /// <summary>Tools that exist but could not be identified — never counted as missing.</summary>
        public IList<InstalledKit> Unresolved
        {
            get
            {
                var unresolved = new List<InstalledKit>();
                foreach (KitGroup g in Groups)
                    foreach (InstalledKit k in g.Kits)
                        if (k.Outcome == KitOutcome.TimedOut || k.Outcome == KitOutcome.Inconclusive)
                            unresolved.Add(k);
                return unresolved;
            }
        }

        public int InstalledCount
        {
            get
            {
                int n = 0;
                foreach (KitGroup g in Groups)
                    foreach (InstalledKit k in g.Kits) if (k.IsInstalled) n++;
                return n;
            }
        }
    }
}
