using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Renders <see cref="PackageComplianceFinding"/>s (docs/python-solution-model.md, PSM per-package
    /// rules) into Problems-panel rows. Each finding is one row, coded "PSM-NN", blaming the finding's
    /// first path so the row double-clicks to the offending file.
    /// </summary>
    internal static class PackageCompliancePresenter
    {
        public static IReadOnlyList<CompileDiagnostic> ToDiagnostics(IReadOnlyList<PackageComplianceFinding> findings)
        {
            var rows = new List<CompileDiagnostic>();
            if (findings == null) return rows;
            foreach (PackageComplianceFinding f in findings)
            {
                if (f == null) continue;
                string path = f.Paths.Count > 0 ? f.Paths[0] : string.Empty;
                var position = new SourcePosition(0, f.Line, 1);
                var span = new SourceSpan(position, position);
                rows.Add(new CompileDiagnostic(
                    path,
                    "PSM-" + ((int)f.Rule).ToString("00"),
                    ParseSeverity.Error,
                    span,
                    f.Message));
            }
            return rows;
        }

        /// <summary>Distinct packages that have at least one finding — the honest "issue" count.</summary>
        public static int CountAffectedPackages(IReadOnlyList<PackageComplianceFinding> findings)
        {
            if (findings == null) return 0;
            var seen = new HashSet<string>();
            foreach (PackageComplianceFinding f in findings)
                if (f != null) seen.Add(f.Package ?? string.Empty);
            return seen.Count;
        }
    }
}
