using System.Collections.Generic;
using System.Threading;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>
    /// A richer, language-specific source of kits than "run the exe on PATH and read the banner".
    ///
    /// Some kits are installed without ever touching PATH — a CPython registered under
    /// <c>HKLM\SOFTWARE\Python\PythonCore</c> is genuinely installed and genuinely invisible to a
    /// PATH probe. The IDE already owns detectors for those cases; a provider is how they feed this
    /// scan instead of being re-implemented here. Kits a provider returns are merged into its
    /// language's group, and one already found at the same path is not listed twice.
    /// </summary>
    public interface IKitProvider
    {
        /// <summary>The catalog language whose group these kits belong to, e.g. "Python".</summary>
        string Language { get; }

        IList<InstalledKit> Discover(CancellationToken cancellationToken);
    }
}
