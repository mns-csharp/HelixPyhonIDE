using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// The result of comparing the Solution Explorer's structural spine against the disk.
    ///
    /// <b>This object is the acceptance oracle for every claim about structure parity.</b> "The
    /// tree matches the disk" means <see cref="IsEmpty"/>, never a screenshot that looks right —
    /// the failure this engine was written from (<c>BugMap_oop</c>) drew a clean-looking
    /// five-project tree for its entire life while disagreeing with disk in five places.
    /// </summary>
    public sealed class StructureParityReport
    {
        private readonly List<StructureDefect> _defects = new List<StructureDefect>();

        public StructureParityReport(string solutionDirectory)
        {
            SolutionDirectory = solutionDirectory;
        }

        /// <summary>Absolute path of the solution directory the comparison ran against.</summary>
        public string SolutionDirectory { get; }

        public IReadOnlyList<StructureDefect> Defects
        {
            get { return _defects; }
        }

        /// <summary>
        /// False when the disk walk stopped early — a permission wall, an I/O error, or the depth
        /// backstop. An incomplete scan CANNOT produce a clean verdict: every path it never reached
        /// looks exactly like a path the tree correctly omits, so a truncated walk would
        /// manufacture <see cref="IsEmpty"/> out of its own failure. Callers must read this as
        /// "not verified", the same way they read a null report.
        /// </summary>
        public bool ScanCompleted { get; internal set; } = true;

        /// <summary>The only clean result. Anything else blocks Compile/Run/Debug (PY8004).</summary>
        public bool IsEmpty
        {
            get { return _defects.Count == 0; }
        }

        /// <summary>
        /// The load path's real question: is this solution verified to match its disk? Distinct
        /// from <see cref="IsEmpty"/>, which only says no defect was FOUND — a scan that could not
        /// finish finds nothing and means nothing.
        /// </summary>
        public bool IsVerifiedClean
        {
            get { return ScanCompleted && IsEmpty; }
        }

        /// <summary>True when at least one defect cannot be repaired automatically (a Type clash).</summary>
        public bool HasUnrepairable
        {
            get { return _defects.Any(d => !d.IsRepairable); }
        }

        public void Add(StructureDefect defect)
        {
            if (defect != null) _defects.Add(defect);
        }

        public IEnumerable<StructureDefect> OfClass(StructureDefectClass defectClass)
        {
            return _defects.Where(d => d.Class == defectClass);
        }

        public int CountOf(StructureDefectClass defectClass)
        {
            return _defects.Count(d => d.Class == defectClass);
        }

        /// <summary>
        /// One line per defect, grouped by class. This is what the repair dialog's detail view
        /// shows and what a failing test prints — a user (or a maintainer reading a test failure)
        /// must be able to see WHICH paths disagree, not just how many.
        /// </summary>
        public string Describe()
        {
            if (IsEmpty)
            {
                return ScanCompleted
                    ? "Structure parity: OK (" + SolutionDirectory + ")"
                    : "Structure parity: NOT VERIFIED — the disk scan could not complete ("
                      + SolutionDirectory + ")";
            }
            StringBuilder sb = new StringBuilder();
            if (!ScanCompleted)
                sb.AppendLine("  (the disk scan could not complete; there may be more)");
            sb.Append("Structure parity: ").Append(_defects.Count)
              .Append(" defect(s) in ").AppendLine(SolutionDirectory);
            foreach (StructureDefectClass c in Enum.GetValues(typeof(StructureDefectClass)).Cast<StructureDefectClass>())
            {
                List<StructureDefect> group = OfClass(c).ToList();
                if (group.Count == 0) continue;
                sb.Append("  ").Append(c).Append(" (").Append(group.Count).AppendLine("):");
                foreach (StructureDefect d in group)
                    sb.Append("    ").AppendLine(d.ToString());
            }
            return sb.ToString();
        }
    }
}
