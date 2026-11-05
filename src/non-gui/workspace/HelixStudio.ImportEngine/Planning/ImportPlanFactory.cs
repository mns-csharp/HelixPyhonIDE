using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Planning
{
    /// <summary>
    /// Builds the default plan from an analysis: every package a separate
    /// project — except one detected package directory nested inside ANOTHER
    /// detected package's directory, which defaults to PartOfParent (it's a
    /// sub-package, not an independent project; ImportPlanExecutor also relies
    /// on nested packages never being independently moved) — duplicate names
    /// auto-deduplicated with numeric suffixes, and the sole executable (when
    /// unambiguous) pre-selected as startup.
    /// </summary>
    public static class ImportPlanFactory
    {
        public static ImportPlan CreateDefault(ImportAnalysis analysis, string solutionName)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            var plan = new ImportPlan
            {
                SourceRoot = analysis.SourceRoot,
                SolutionName = solutionName,
            };

            var normalizedDirs = analysis.Packages
                .Select(p => Path.GetFullPath(p.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar))
                .ToList();

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < analysis.Packages.Count; i++)
            {
                PackageDescriptor pkg = analysis.Packages[i];
                var pp = new PackagePlan(pkg);

                if (IsContainedInAnother(i, normalizedDirs))
                {
                    // Leave FinalName as the detected name — a PartOfParent package never
                    // emits its own project, so it needs no dedup against siblings.
                    pp.Disposition = PackageDisposition.PartOfParent;
                }
                else
                {
                    // SRS 4.4 "resolve duplicate package names": suffix later
                    // duplicates so every project name is unique from the start.
                    string candidate = pkg.Name;
                    int n = 2;
                    while (used.Contains(candidate))
                    {
                        candidate = pkg.Name + "_" + n;
                        n++;
                    }
                    pp.FinalName = candidate;
                    used.Add(candidate);
                }
                plan.Packages.Add(pp);
            }

            // Startup default: the single package that owns an entry module.
            List<PackagePlan> executables = FindExecutables(analysis, plan);
            if (executables.Count == 1)
                plan.StartupProjectName = executables[0].FinalName;

            // Surplus-main resolution is offered whenever the audit found any.
            plan.ResolveEntryPoints = analysis.AuditReport != null && !analysis.AuditReport.IsCompliant;

            // CUDA debug scaffold generation is offered whenever inline kernels were found.
            plan.DetectedCudaScaffold = analysis.DetectedCudaScaffold;
            plan.GenerateCudaDebugScaffold = analysis.DetectedCudaScaffold != null && analysis.DetectedCudaScaffold.HasKernels;

            return plan;
        }

        /// <summary>True when <paramref name="index"/>'s directory is physically nested inside
        /// any OTHER directory in <paramref name="normalizedDirs"/> (both already full,
        /// separator-trimmed paths).</summary>
        private static bool IsContainedInAnother(int index, List<string> normalizedDirs)
        {
            string dir = normalizedDirs[index];
            for (int j = 0; j < normalizedDirs.Count; j++)
            {
                if (j == index) continue;
                if (dir.StartsWith(normalizedDirs[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Plans whose package directory contains an entry-equivalent module.</summary>
        public static List<PackagePlan> FindExecutables(ImportAnalysis analysis, ImportPlan plan)
        {
            var result = new List<PackagePlan>();
            if (analysis.AuditReport == null) return result;
            foreach (PackagePlan pp in plan.SeparateProjects)
            {
                string dir = Path.GetFullPath(pp.Source.DirectoryPath);
                bool owns = analysis.AuditReport.MainModules.Any(m =>
                {
                    string full = Path.GetFullPath(m);
                    return full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase);
                });
                if (owns) result.Add(pp);
            }
            return result;
        }
    }
}
