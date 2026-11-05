using System.Collections.Generic;
using System.Threading;
using HelixPythonNamespace.Debugger;
using HelixPythonNamespace.Workspace.Toolchains;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Feeds the Development Environment scan every CPython the IDE can already find, rather than
    /// only the one on PATH.
    ///
    /// <see cref="PythonInterpreterManager.DiscoverAll"/> is the IDE's real answer to "which
    /// Pythons are on this machine" — registry (HKLM/HKCU PythonCore, Anaconda), the well-known
    /// install roots, <c>py -0p</c> and PATH, deduplicated and version-probed. A Store-installed or
    /// registry-only interpreter is genuinely installed and genuinely absent from PATH, so a scan
    /// without this would under-report Python on exactly the machines it matters on.
    ///
    /// It lives here and not in HelixPythonNamespace.Workspace because that assembly does not reference
    /// HelixPythonNamespace.Debugger, and adding the reference to reach one class is a worse trade than the
    /// provider seam.
    /// </summary>
    public sealed class PythonInterpreterKitProvider : IKitProvider
    {
        private readonly PythonInterpreterManager _manager;

        public PythonInterpreterKitProvider(PythonInterpreterManager manager = null)
        {
            _manager = manager ?? new PythonInterpreterManager();
        }

        public string Language { get { return "Python"; } }

        public IList<InstalledKit> Discover(CancellationToken cancellationToken)
        {
            var kits = new List<InstalledKit>();
            if (cancellationToken.IsCancellationRequested) return kits;

            foreach (PythonInterpreter interpreter in _manager.DiscoverAll())
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (interpreter == null || string.IsNullOrEmpty(interpreter.ExecutablePath)) continue;

                string flavor = string.IsNullOrEmpty(interpreter.Flavor) ? "Python" : interpreter.Flavor;
                string architecture = string.IsNullOrEmpty(interpreter.Architecture)
                    ? string.Empty : " " + interpreter.Architecture;
                string component = flavor + (string.IsNullOrEmpty(interpreter.Version)
                    ? string.Empty : " " + interpreter.Version) + architecture;

                // Provenance matters here: "found in the registry" and "found on PATH" mean
                // different things when the user is deciding which interpreter a project uses.
                string detail = "found via " + (interpreter.Source ?? "discovery")
                    + (interpreter.HasPythonNet ? "; pythonnet available" : string.Empty);

                kits.Add(new InstalledKit("Python", component, interpreter.Version,
                    interpreter.ExecutablePath, KitOutcome.Installed, detail));
            }
            return kits;
        }
    }
}
