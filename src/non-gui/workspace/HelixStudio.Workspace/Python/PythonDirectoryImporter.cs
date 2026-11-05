using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Converts a raw Python source tree into a managed project layout.
    /// Two target formats are supported and can be emitted together:
    /// <list type="bullet">
    /// <item><b>TomlWorkspace</b> — root <c>pyproject.toml</c> with
    ///   <c>[tool.uv.workspace]</c> and one per-member pyproject.toml.</item>
    /// <item><b>VisualStudio</b> — <c>.sln</c> + one <c>.pyproj</c> per package
    ///   (PTVS MSBuild schema) plus a minimal root <c>pyproject.toml</c> so the
    ///   tool's native loader still works.</item>
    /// </list>
    /// </summary>
    public sealed class PythonDirectoryImporter
    {
        public ImportResult Import(ImportOptions options)
        {
            ImportResult result = new ImportResult();
            if (options == null)
            {
                result.ErrorMessage = "ImportOptions is null.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(options.SourceRoot) || !Directory.Exists(options.SourceRoot))
            {
                result.ErrorMessage = "Source directory does not exist: " + options.SourceRoot;
                return result;
            }

            string sourceRoot = Path.GetFullPath(options.SourceRoot);

            // Requirement #3/#4a: audit the source against the solution-management rules
            // (single main-equivalent module, single module-level def main) before we lay
            // down any project scaffolding, and surface the findings to the caller so it can
            // offer resolutions (#4b). TOML auto-generation stays enabled; the GUI open/import
            // flow consults AuditReport.IsCompliant to gate a "no major issues" auto-accept.
            SolutionAuditReport audit = PythonSolutionAuditor.Audit(sourceRoot);
            result.AuditReport = audit;
            foreach (SolutionAuditFinding finding in audit.Findings)
            {
                if (finding.Severity == SolutionAuditSeverity.Error)
                {
                    result.Diagnostics.Add("Audit: " + finding.Message +
                        (string.IsNullOrEmpty(finding.SuggestedResolution) ? "" : " Resolution: " + finding.SuggestedResolution));
                }
            }

            string solutionName = string.IsNullOrWhiteSpace(options.SolutionName)
                ? SanitizePackageName(Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar)))
                : options.SolutionName;

            // 1. Scan packages (shared by all target formats).
            List<PackageDescriptor> packages = ScanPackages(sourceRoot, result.Diagnostics);

            // 2. Synthesize a root package for loose .py files if needed.
            List<string> loose = GetLooseRootFiles(sourceRoot);
            if (loose.Count > 0)
            {
                PackageDescriptor synthetic = new PackageDescriptor(solutionName, sourceRoot) { IsSynthesized = true };
                synthetic.PyFiles.AddRange(loose);
                foreach (string sub in Directory.GetDirectories(sourceRoot))
                {
                    string subName = Path.GetFileName(sub);
                    if (SolutionService.IgnoredDirectories.Contains(subName)) continue;
                    synthetic.SubDirectories.Add(subName);
                }
                packages.Insert(0, synthetic);
                result.Diagnostics.Add(
                    "Synthesized root project '" + synthetic.Name + "' for " + loose.Count + " loose Python file(s) at " + sourceRoot + ".");
            }

            if (packages.Count == 0)
            {
                result.ErrorMessage = "No Python packages or .py files found under " + sourceRoot + ".";
                return result;
            }

            foreach (PackageDescriptor package in packages)
                result.Packages.Add(package);
            result.RepositoryClassification = ClassifyRepository(packages, audit);

            // IMP-02: analysis is a hard read-only boundary. Do not even create OutputRoot.
            if (options.AnalysisOnly)
            {
                result.IsAnalysisOnly = true;
                result.Success = true;
                result.Diagnostics.Add(
                    "Read-only analysis completed; no files were modified.");
                return result;
            }

            string outputRoot = string.IsNullOrWhiteSpace(options.OutputRoot)
                ? sourceRoot
                : Path.GetFullPath(options.OutputRoot);
            Directory.CreateDirectory(outputRoot);

            bool emitVs = options.TargetFormat == ImportTargetFormat.VisualStudio
                       || options.TargetFormat == ImportTargetFormat.Both;
            bool emitToml = options.TargetFormat == ImportTargetFormat.TomlWorkspace
                         || options.TargetFormat == ImportTargetFormat.Both;

            if (emitVs)
            {
                string slnPath = Path.Combine(outputRoot, solutionName + ".sln");
                if (File.Exists(slnPath) && !options.OverwriteExisting)
                {
                    result.ErrorMessage = "Solution file already exists: " + slnPath + " (enable Overwrite to replace it).";
                    return result;
                }
                foreach (PackageDescriptor pkg in packages)
                {
                    pkg.ProjectGuid = Guid.NewGuid();
                    pkg.PyProjFilePath = Path.Combine(pkg.DirectoryPath, pkg.Name + ".pyproj");
                    if (File.Exists(pkg.PyProjFilePath) && !options.OverwriteExisting)
                    {
                        result.ErrorMessage = "Project file already exists: " + pkg.PyProjFilePath + " (enable Overwrite to replace it).";
                        return result;
                    }
                }
                foreach (PackageDescriptor pkg in packages) PyProjectFileWriter.Write(pkg);
                VsSolutionWriter.Write(slnPath, packages);
                result.SlnPath = slnPath;
            }

            // Every target emits the root pyproject.toml so the tool's loader sees the project.
            string rootPyproject = Path.Combine(sourceRoot, "pyproject.toml");
            if (emitToml)
            {
                IPyProjectConflictResolver resolver = options.ConflictResolver
                    ?? new SilentPyProjectConflictResolver(options.OverwriteExisting
                        ? PyProjectConflictResolution.Overwrite
                        : PyProjectConflictResolution.Keep);

                // Per-member TOMLs — excluding the synthesized root (we don't write a second
                // pyproject.toml at the root here; the root file is emitted below).
                foreach (PackageDescriptor pkg in packages)
                {
                    if (pkg.IsSynthesized) continue;
                    string memberToml = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
                    PyProjectConflictResolution chosen = File.Exists(memberToml)
                        ? resolver.Resolve(memberToml)
                        : PyProjectConflictResolution.Overwrite; // fresh write
                    string note = TomlWorkspaceMemberWriter.Write(pkg, options.PythonVersion ?? "3.12", chosen);
                    result.ConflictsResolved.Add(note);
                }

                TomlWorkspaceRootWriter.Write(rootPyproject, solutionName, options.PythonVersion ?? "3.12", packages, sourceRoot);
                result.Diagnostics.Add("Wrote workspace root " + rootPyproject + ".");
            }
            else
            {
                // VisualStudio-only: emit the minimal root pyproject.toml when absent so the
                // tool's native loader still has something to open.
                if (!File.Exists(rootPyproject))
                {
                    PyProjectWriter.WriteMinimal(rootPyproject, solutionName, options.PythonVersion ?? "3.12");
                    result.Diagnostics.Add("Wrote minimal pyproject.toml at " + rootPyproject + ".");
                }
                else
                {
                    result.Diagnostics.Add("pyproject.toml already exists at " + rootPyproject + "; preserved.");
                }
            }
            result.PyProjectTomlPath = rootPyproject;

            result.Success = true;
            return result;
        }

        // ---------- internals ----------

        private static List<PackageDescriptor> ScanPackages(string root, List<string> diagnostics)
        {
            List<PackageDescriptor> packages = new List<PackageDescriptor>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch (UnauthorizedAccessException ex) { diagnostics.Add("Skipped (access denied): " + dir + " — " + ex.Message); continue; }
                foreach (string sub in subs)
                {
                    if (SolutionService.IgnoredDirectories.Contains(Path.GetFileName(sub))) continue;
                    stack.Push(sub);
                }
                string init = Path.Combine(dir, "__init__.py");
                if (File.Exists(init)) packages.Add(BuildPackageDescriptor(dir));
            }
            return packages;
        }

        private static string ClassifyRepository(
            List<PackageDescriptor> packages,
            SolutionAuditReport audit)
        {
            int entryCount = audit == null ? 0 : audit.MainModules.Count;
            if (packages == null || packages.Count == 0) return "Unknown";
            if (entryCount == 0) return packages.Count == 1 ? "Library project" : "Mixed repository";
            if (entryCount == 1) return packages.Count == 1
                ? "Single executable project"
                : "Mixed repository";
            return "Multiple independent applications";
        }

        private static PackageDescriptor BuildPackageDescriptor(string dir)
        {
            string name = SanitizePackageName(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)));
            PackageDescriptor pkg = new PackageDescriptor(name, dir);
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".py" || ext == ".pyi") pkg.PyFiles.Add(Path.GetFileName(f));
            }
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string subName = Path.GetFileName(sub);
                if (SolutionService.IgnoredDirectories.Contains(subName)) continue;
                pkg.SubDirectories.Add(subName);
            }
            return pkg;
        }

        private static List<string> GetLooseRootFiles(string sourceRoot)
        {
            List<string> loose = new List<string>();
            foreach (string f in Directory.GetFiles(sourceRoot))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".py" || ext == ".pyi") loose.Add(Path.GetFileName(f));
            }
            if (File.Exists(Path.Combine(sourceRoot, "__init__.py"))) return new List<string>();
            return loose;
        }

        private static string SanitizePackageName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "project";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '_' || c == '-' || c == '.') sb.Append('_');
            }
            if (sb.Length == 0) return "project";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
