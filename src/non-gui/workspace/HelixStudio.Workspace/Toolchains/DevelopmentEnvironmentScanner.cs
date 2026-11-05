using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// Asks the machine which programming-language kits are installed, by resolving each tool in
    /// <see cref="ToolProbeCatalog"/> and reading the version it reports.
    ///
    /// Cancellable (a scan touches every entry on PATH) and total: every language in the catalog
    /// appears in the report whether or not anything was found, because "Go is not installed here"
    /// is information the user came for.
    /// </summary>
    public sealed class DevelopmentEnvironmentScanner
    {
        private readonly IToolProbeRunner _runner;
        private readonly IList<ToolProbe> _probes;
        private readonly IList<IKitProvider> _providers;

        public DevelopmentEnvironmentScanner(IToolProbeRunner runner, IList<ToolProbe> probes = null,
            IList<IKitProvider> providers = null)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            _runner = runner;
            _probes = probes ?? ToolProbeCatalog.All();
            _providers = providers ?? new List<IKitProvider>();
        }

        /// <summary>Raised after each probe so a UI can show progress. May fire on any thread.</summary>
        public event EventHandler<KitProbedEventArgs> Probed;

        public DevelopmentEnvironmentReport Scan(CancellationToken cancellationToken)
        {
            var byLanguage = new Dictionary<string, List<InstalledKit>>();
            var order = new List<string>();
            bool complete = true;

            foreach (ToolProbe probe in _probes)
            {
                if (!byLanguage.ContainsKey(probe.Language))
                {
                    byLanguage[probe.Language] = new List<InstalledKit>();
                    order.Add(probe.Language);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // Stop, and SAY so. A partial scan that presented itself as finished would turn
                    // "we did not look" into "it is not installed".
                    complete = false;
                    break;
                }

                foreach (InstalledKit kit in RunProbe(probe, cancellationToken))
                {
                    byLanguage[probe.Language].Add(kit);
                    EventHandler<KitProbedEventArgs> handler = Probed;
                    if (handler != null) handler(this, new KitProbedEventArgs(kit));
                }
            }

            if (complete) complete = MergeProviders(byLanguage, order, cancellationToken);

            var groups = new List<KitGroup>();
            foreach (string language in order) groups.Add(new KitGroup(language, byLanguage[language]));
            return new DevelopmentEnvironmentReport(groups, complete);
        }

        /// <summary>
        /// Fold in what the language-specific providers know. A kit already found at the same
        /// executable path is not repeated — the registry and PATH routinely lead to the same file.
        /// </summary>
        private bool MergeProviders(Dictionary<string, List<InstalledKit>> byLanguage,
            List<string> order, CancellationToken cancellationToken)
        {
            foreach (IKitProvider provider in _providers)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                if (provider == null || string.IsNullOrEmpty(provider.Language)) continue;

                IList<InstalledKit> discovered;
                try { discovered = provider.Discover(cancellationToken); }
                catch (Exception) { continue; }   // one provider must not sink the whole scan
                if (discovered == null) continue;

                if (!byLanguage.ContainsKey(provider.Language))
                {
                    byLanguage[provider.Language] = new List<InstalledKit>();
                    order.Add(provider.Language);
                }
                List<InstalledKit> target = byLanguage[provider.Language];

                foreach (InstalledKit kit in discovered)
                {
                    if (kit == null || AlreadyListed(target, kit.Path)) continue;
                    target.Add(kit);
                    EventHandler<KitProbedEventArgs> handler = Probed;
                    if (handler != null) handler(this, new KitProbedEventArgs(kit));
                }
            }
            return true;
        }

        private static bool AlreadyListed(IList<InstalledKit> kits, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = Normalize(path);
            foreach (InstalledKit existing in kits)
                if (existing.Path != null && Normalize(existing.Path) == normalized) return true;
            return false;
        }

        private static string Normalize(string path)
        {
            try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/').ToUpperInvariant(); }
            catch (Exception) { return path.TrimEnd('\\', '/').ToUpperInvariant(); }
        }

        private IEnumerable<InstalledKit> RunProbe(ToolProbe probe, CancellationToken cancellationToken)
        {
            string path = _runner.Resolve(probe);
            if (string.IsNullOrEmpty(path))
            {
                yield return new InstalledKit(probe.Language, probe.Component, null, null,
                    KitOutcome.NotFound,
                    probe.Executable + " was not found on PATH, or in this kit's install locations");
                yield break;
            }

            ProbeRun run = _runner.Run(probe, path, cancellationToken);
            if (run.Outcome == KitOutcome.TimedOut)
            {
                yield return new InstalledKit(probe.Language, probe.Component, null, path,
                    KitOutcome.TimedOut, "did not answer in time");
                yield break;
            }
            if (run.Outcome == KitOutcome.Inconclusive)
            {
                yield return new InstalledKit(probe.Language, probe.Component, null, path,
                    KitOutcome.Inconclusive, run.Output);
                yield break;
            }

            switch (probe.Yield)
            {
                case ProbeYield.DotNetSdkList:
                    foreach (InstalledKit sdk in ParseDotNetSdks(probe, path, run.Output)) yield return sdk;
                    break;
                case ProbeYield.PythonLauncherList:
                    foreach (InstalledKit py in ParsePythonLauncher(probe, path, run.Output)) yield return py;
                    break;
                default:
                    yield return Single(probe, path, run.Output);
                    break;
            }
        }

        private static InstalledKit Single(ToolProbe probe, string path, string output)
        {
            string version = ParseVersion(probe.VersionPattern, output);
            string firstLine = FirstNonEmptyLine(output);
            if (version == null)
            {
                // It ran, so it IS installed — we simply could not read the version out of a banner
                // we did not anticipate. Saying "installed, version unknown" beats both a lie and a
                // silent omission, and the raw banner goes in the details.
                return new InstalledKit(probe.Language, probe.Component, null, path,
                    KitOutcome.Inconclusive, firstLine);
            }
            return new InstalledKit(probe.Language, probe.Component, version, path,
                KitOutcome.Installed, firstLine);
        }

        /// <summary>First capture group of <paramref name="pattern"/> over the tool's output.</summary>
        public static string ParseVersion(string pattern, string output)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(output)) return null;
            try
            {
                Match m = Regex.Match(output, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                if (!m.Success) return null;
                return m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
            }
            catch (RegexMatchTimeoutException) { return null; }
            catch (ArgumentException) { return null; }   // a bad pattern must not take the scan down
        }

        /// <summary>`8.0.100 [C:\Program Files\dotnet\sdk]` → one kit per line.</summary>
        public static IList<InstalledKit> ParseDotNetSdks(ToolProbe probe, string dotnetPath, string output)
        {
            var kits = new List<InstalledKit>();
            foreach (string line in Lines(output))
            {
                int bracket = line.IndexOf('[');
                if (bracket <= 0) continue;
                string version = line.Substring(0, bracket).Trim();
                string root = line.Substring(bracket + 1).TrimEnd(']').Trim();
                if (version.Length == 0) continue;
                string full = root;
                try { full = System.IO.Path.Combine(root, version); }
                catch (ArgumentException) { }
                kits.Add(new InstalledKit(probe.Language, "SDK " + version, version, full,
                    KitOutcome.Installed, line.Trim()));
            }
            if (kits.Count == 0)
                kits.Add(new InstalledKit(probe.Language, probe.Component, null, dotnetPath,
                    KitOutcome.Inconclusive, FirstNonEmptyLine(output)));
            return kits;
        }

        /// <summary>
        /// `py -0p` output: ` -V:3.12 *        C:\Python312\python.exe`. The launcher's own
        /// formatting has changed across releases, so the parse is deliberately positional: a
        /// version-looking token somewhere on the line, and a path that ends in an executable.
        /// </summary>
        public static IList<InstalledKit> ParsePythonLauncher(ToolProbe probe, string pyPath, string output)
        {
            var kits = new List<InstalledKit>();
            foreach (string line in Lines(output))
            {
                Match path = Regex.Match(line, @"([A-Za-z]:\\[^\r\n]*?\.exe|/[^\r\n]*?/python[0-9.]*)");
                if (!path.Success) continue;
                Match version = Regex.Match(line, @"-V:\s*([0-9]+\.[0-9]+(?:-\S+)?)");
                string label = version.Success ? "Python " + version.Groups[1].Value : "Interpreter";
                bool isDefault = line.Contains("*");
                kits.Add(new InstalledKit(probe.Language, label + (isDefault ? " (default)" : ""),
                    version.Success ? version.Groups[1].Value : null,
                    path.Groups[1].Value.Trim(), KitOutcome.Installed, line.Trim()));
            }
            if (kits.Count == 0)
                kits.Add(new InstalledKit(probe.Language, probe.Component, null, pyPath,
                    KitOutcome.Inconclusive, FirstNonEmptyLine(output)));
            return kits;
        }

        private static IEnumerable<string> Lines(string output)
        {
            if (string.IsNullOrEmpty(output)) yield break;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.Trim().Length > 0) yield return line;
        }

        private static string FirstNonEmptyLine(string output)
        {
            foreach (string line in Lines(output)) return line.Trim();
            return string.Empty;
        }
    }

    public sealed class KitProbedEventArgs : EventArgs
    {
        public KitProbedEventArgs(InstalledKit kit) { Kit = kit; }
        public InstalledKit Kit { get; private set; }
    }
}
