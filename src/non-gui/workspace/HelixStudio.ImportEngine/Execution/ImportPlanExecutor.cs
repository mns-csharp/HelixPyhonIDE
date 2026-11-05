using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Execution
{
    /// <summary>
    /// Applies an ImportPlan: physical renames / src-layout moves when approved
    /// (SRS 4.6, never overwriting anything), then generates the solution and
    /// project TOMLs / VS files (SRS 4.5), persists the startup choice, and
    /// optionally applies the auditor's surplus-main.py rename plan.
    /// </summary>
    public sealed class ImportPlanExecutor
    {
        public ImportExecutionResult Execute(ImportPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var result = new ImportExecutionResult();

            if (string.IsNullOrWhiteSpace(plan.SourceRoot) || !Directory.Exists(plan.SourceRoot))
            {
                result.ErrorMessage = "Source directory does not exist: " + plan.SourceRoot;
                return result;
            }
            if (string.IsNullOrWhiteSpace(plan.SolutionName))
            {
                result.ErrorMessage = "Solution name must not be empty.";
                return result;
            }
            var nameClash = plan.SeparateProjects
                .GroupBy(p => p.FinalName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (nameClash != null)
            {
                result.ErrorMessage = "Two projects would share the name '" + nameClash.Key +
                                      "'. Rename one of them before importing.";
                return result;
            }

            string root = Path.GetFullPath(plan.SourceRoot);

            if (!string.IsNullOrEmpty(plan.OriginalSourceRoot))
                result.Diagnostics.Add("Original source left untouched: " + plan.OriginalSourceRoot +
                                       "  (importing into a working copy: " + root + ")");

            try
            {
                // ---- Phase 1: physical rearrangement (SRS 4.6) ----
                var finalPackages = new List<PackageDescriptor>();
                var renamedModules = new List<KeyValuePair<string, string>>();
                // Deepest directory first: ImportPlanFactory defaults a package nested inside
                // another to PartOfParent (skipped below, never moved on its own), but a user
                // can override that back to SeparateProject on the Projects page. When they do,
                // the nested child's directory is always longer than any ancestor's — moving it
                // out to its own src\<name> BEFORE the ancestor's own move runs means the
                // ancestor's later Directory.Move no longer drags an already-relocated
                // subdirectory along, so it never tries to move a path that's already gone.
                foreach (PackagePlan pp in plan.Packages
                    .OrderByDescending(p => Path.GetFullPath(p.Source.DirectoryPath).Length))
                {
                    if (pp.Disposition == PackageDisposition.Excluded)
                    {
                        result.Diagnostics.Add("Excluded: " + pp.Source.DirectoryPath);
                        continue;
                    }
                    if (pp.Disposition == PackageDisposition.PartOfParent)
                    {
                        result.Diagnostics.Add("Folded into parent project: " + pp.Source.DirectoryPath);
                        continue;
                    }

                    string dir = Path.GetFullPath(pp.Source.DirectoryPath);

                    // A package can't be physically relocated when its directory
                    // *is* the source root itself — that includes the synthesized
                    // loose-files project and any real package whose __init__.py
                    // sits directly at the root (e.g. a single flat app dir with
                    // no wrapper folder). Moving/renaming it would mean moving the
                    // root into its own descendant (root\src\<name>), which Windows
                    // refuses with "Access to the path ... is denied."; renaming it
                    // out from under the very path the user pointed the wizard at
                    // is equally undesirable. Leave it where it is.
                    bool isSourceRoot = pp.Source.IsSynthesized ||
                                        string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);

                    // Rename the package directory when the final name differs and the
                    // directory is a real package dir (never rename the source root).
                    if (pp.IsRenamed && !isSourceRoot)
                    {
                        string renamed = Path.Combine(Path.GetDirectoryName(dir), pp.FinalName);
                        if (!MoveDirectory(dir, renamed, "rename", result)) return result;
                        dir = renamed;
                        // A renamed package directory changes the importable module
                        // name; remember it for the import-rewrite phase (SRS 4.7,
                        // IMP-07/08). src-layout moves do NOT change import names.
                        renamedModules.Add(new KeyValuePair<string, string>(pp.Source.Name, pp.FinalName));
                    }

                    // Move under src\ when the layout asks for it.
                    if (plan.Layout == ImportLayout.RearrangeToSrc && !isSourceRoot)
                    {
                        string srcDir = Path.Combine(root, "src");
                        bool alreadyInSrc = dir.StartsWith(srcDir + Path.DirectorySeparatorChar,
                                                           StringComparison.OrdinalIgnoreCase);
                        if (!alreadyInSrc)
                        {
                            Directory.CreateDirectory(srcDir);
                            string target = Path.Combine(srcDir, pp.FinalName);
                            if (!MoveDirectory(dir, target, "layout", result)) return result;
                            dir = target;
                        }
                    }
                    else if (plan.Layout == ImportLayout.RearrangeToSrc && isSourceRoot)
                    {
                        result.Diagnostics.Add(
                            "Kept in place (is the source root itself, cannot move under src\\): " + dir);
                    }

                    var moved = new PackageDescriptor(pp.FinalName, dir)
                    {
                        IsSynthesized = pp.Source.IsSynthesized
                    };
                    moved.PyFiles.AddRange(pp.Source.PyFiles);
                    moved.SubDirectories.AddRange(pp.Source.SubDirectories);
                    finalPackages.Add(moved);
                }

                if (finalPackages.Count == 0)
                {
                    result.ErrorMessage = "Every detected package was excluded; nothing to import.";
                    return result;
                }

                // ---- Phase 1.5: split packages with multiple def main() into N executable
                //      packages + one shared library (SRS #3d — a package may declare only
                //      one). Only when the user opted into automatic resolution; only ever
                //      touches this working copy (see OriginalSourceRoot), never what the
                //      user actually pointed the wizard at.
                if (plan.ResolveEntryPoints)
                {
                    var splitTargets = new List<KeyValuePair<PackageDescriptor, PackageSplitResult>>();
                    foreach (PackageDescriptor pkg in finalPackages)
                    {
                        if (pkg.IsSynthesized) continue;
                        int mainFuncCount = 0;
                        foreach (string relFile in pkg.PyFiles)
                        {
                            string full = Path.Combine(pkg.DirectoryPath, relFile);
                            if (!File.Exists(full)) continue;
                            string text;
                            try { text = File.ReadAllText(full); } catch { continue; }
                            if (PythonSolutionAuditor.ContainsModuleLevelMainFunction(text)) mainFuncCount++;
                        }
                        if (mainFuncCount < 2) continue;

                        // When the package being split IS the working root (a flat source dir
                        // with no wrapper folder — see isSourceRoot in Phase 1 above), the new
                        // lib/app packages must be created AS CHILDREN of it, not as true
                        // filesystem siblings — its own parent lies outside the working copy
                        // entirely (in production, that's the user's real repo tree).
                        string splitParent = IsSameDirectory(pkg.DirectoryPath, root) ? pkg.DirectoryPath : null;
                        PackageSplitResult split = PackageSplitter.Split(pkg.DirectoryPath, pkg.Name, splitParent);
                        if (!split.Success)
                        {
                            result.Diagnostics.Add("Could not split '" + pkg.Name +
                                "' into multiple executable packages: " + split.ErrorMessage);
                            continue;
                        }
                        splitTargets.Add(new KeyValuePair<PackageDescriptor, PackageSplitResult>(pkg, split));
                    }

                    foreach (KeyValuePair<PackageDescriptor, PackageSplitResult> entry in splitTargets)
                    {
                        PackageDescriptor original = entry.Key;
                        PackageSplitResult split = entry.Value;
                        finalPackages.Remove(original);

                        bool redirectedStartup = false;
                        foreach (SplitPackage sp in split.Packages)
                        {
                            var descriptor = new PackageDescriptor(sp.Name, sp.DirectoryPath);
                            foreach (string f in Directory.GetFiles(sp.DirectoryPath, "*.py"))
                                descriptor.PyFiles.Add(Path.GetFileName(f));
                            foreach (string d in Directory.GetDirectories(sp.DirectoryPath))
                                descriptor.SubDirectories.Add(Path.GetFileName(d));
                            finalPackages.Add(descriptor);

                            // The startup project pointed at the original (now-removed) package
                            // name — redirect it to the first extracted executable package so
                            // the solution still has a startup.
                            if (sp.IsExecutable && !redirectedStartup &&
                                string.Equals(plan.StartupProjectName, original.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                plan.StartupProjectName = sp.Name;
                                redirectedStartup = true;
                            }
                        }
                        foreach (string note in split.Notes) result.Notes.Add(note);

                        // The original directory is now just an empty shell (every file moved
                        // out into the lib/app packages) — remove it so it doesn't linger as a
                        // confusing, package-less leftover. Best-effort: a non-empty or locked
                        // directory is harmless to leave behind.
                        //
                        // NEVER delete when the split package WAS the working root itself (the
                        // "isSourceRoot" case above, e.g. a flat directory with no wrapper
                        // folder) — every later phase (Phase 2 writes root\pyproject.toml,
                        // Phase 3 writes member tomls, Phase 4 re-audits) still needs `root` to
                        // exist. Deleting it out from under them turns "every file moved to a
                        // sibling package" into "the whole import cannot finish."
                        if (!IsSameDirectory(original.DirectoryPath, root))
                        {
                            try
                            {
                                if (Directory.Exists(original.DirectoryPath) &&
                                    Directory.GetFileSystemEntries(original.DirectoryPath).Length == 0)
                                    Directory.Delete(original.DirectoryPath);
                            }
                            catch { }
                        }
                    }
                }

                // ---- Phase 1.6: import-statement rewriting (SRS 4.7, IMP-07/08) ----
                // Every `import old_pkg` / `from old_pkg.x import y` across the
                // imported tree follows its package's new name. AST-driven via
                // PythonIdentifierRewriter: unparsable files are left untouched.
                foreach (KeyValuePair<string, string> rename in renamedModules)
                {
                    int filesChanged = 0, statements = 0;
                    foreach (string py in PythonSolutionAuditor.EnumeratePythonFiles(root))
                    {
                        string source;
                        try { source = File.ReadAllText(py); } catch { continue; }
                        var rewritten = PythonIdentifierRewriter.RewriteModuleImports(
                            source, rename.Key, rename.Value);
                        if (rewritten.Count == 0) continue;
                        try
                        {
                            File.WriteAllText(py, rewritten.Text);
                            filesChanged++;
                            statements += rewritten.Count;
                        }
                        catch (Exception ex)
                        {
                            result.Diagnostics.Add("Import rewrite failed for " + py + ": " + ex.Message);
                        }
                    }
                    if (statements > 0)
                        result.Notes.Add("rewrote " + statements + " import statement(s) in " +
                                         filesChanged + " file(s): " + rename.Key + " -> " + rename.Value);
                }

                // ---- Phase 2: generation (SRS 4.5) ----
                PyProjectConflictResolution resolution = plan.OverwriteExisting
                    ? PyProjectConflictResolution.Overwrite
                    : PyProjectConflictResolution.Merge;

                string rootToml = Path.Combine(root, "pyproject.toml");
                if (plan.TargetFormat == ImportTargetFormat.TomlWorkspace ||
                    plan.TargetFormat == ImportTargetFormat.Both)
                {
                    foreach (PackageDescriptor pkg in finalPackages.Where(p => !p.IsSynthesized))
                        result.Notes.Add(TomlWorkspaceMemberWriter.Write(pkg, plan.PythonVersion, resolution));
                    TomlWorkspaceRootWriter.Write(rootToml, plan.SolutionName, plan.PythonVersion,
                                                  finalPackages, root);
                    result.PyProjectTomlPath = rootToml;

                    // ---- Phase 2.5: CUDA kernel debug scaffold (SS §linked-cmake-project) ----
                    // Needs the final (post-move/rename) package paths and a root TOML that
                    // already exists, so it runs right after both are settled.
                    CudaKernelScaffoldWriter.Write(plan, finalPackages, root, rootToml, result);
                }

                if (plan.TargetFormat == ImportTargetFormat.VisualStudio ||
                    plan.TargetFormat == ImportTargetFormat.Both)
                {
                    string slnPath = Path.Combine(root, plan.SolutionName + ".sln");
                    if (File.Exists(slnPath) && !plan.OverwriteExisting)
                    {
                        result.ErrorMessage = "Solution file already exists: " + slnPath +
                                              " (enable Overwrite to replace it).";
                        return result;
                    }
                    foreach (PackageDescriptor pkg in finalPackages)
                    {
                        pkg.ProjectGuid = Guid.NewGuid();
                        pkg.PyProjFilePath = Path.Combine(pkg.DirectoryPath, pkg.Name + ".pyproj");
                        PyProjectFileWriter.Write(pkg);
                        result.Notes.Add("created " + pkg.PyProjFilePath);
                    }
                    VsSolutionWriter.Write(slnPath, finalPackages);
                    result.SlnPath = slnPath;
                    if (plan.TargetFormat == ImportTargetFormat.VisualStudio && !File.Exists(rootToml))
                    {
                        PyProjectWriter.WriteMinimal(rootToml, plan.SolutionName, plan.PythonVersion);
                        result.PyProjectTomlPath = rootToml;
                    }
                }

                // ---- Phase 3: language metadata + startup choice (SRS 4.4/4.5) ----
                if (plan.TargetFormat != ImportTargetFormat.VisualStudio)
                {
                    foreach (PackageDescriptor pkg in finalPackages.Where(p => !p.IsSynthesized))
                    {
                        string memberToml = Path.Combine(pkg.DirectoryPath, "pyproject.toml");
                        if (!File.Exists(memberToml)) continue;
                        // kind = app/library so the loader knows which members are
                        // runnable — without it NormalizeStartup (PSM-08) strips the
                        // startup flag from every project.
                        bool executable = PackageOwnsEntryModule(pkg.DirectoryPath);
                        var meta = new Dictionary<string, string>
                        {
                            { "kind", executable ? "\"app\"" : "\"library\"" }
                        };
                        TomlTableMerger.EnsureTable(memberToml, "tool.helixstudio", meta);
                        bool isStartup = plan.StartupProjectName != null &&
                                         string.Equals(pkg.Name, plan.StartupProjectName, StringComparison.OrdinalIgnoreCase);
                        PyProjectStartupWriter.SetStartup(memberToml, isStartup);
                    }
                    if (plan.StartupProjectName != null)
                        result.Diagnostics.Add("Startup project: " + plan.StartupProjectName);
                }

                // ---- Phase 4: entry-point resolution (surplus main.py / def main() renames),
                //      scoped to EACH package independently. A multi-project solution's
                //      packages are compliance units on their own — this matches how the
                //      stricter PackageComplianceValidator (the one that actually gates
                //      Run/Debug) already scopes its checks per project. Auditing/resolving
                //      across the whole tree in one pass (the old behavior) would treat two
                //      unrelated packages' main.py files as if they were the SAME violation,
                //      and would wrongly still flag a solution Phase 1.5 already split into N
                //      independently-compliant packages as non-compliant.
                var perPackageAudits = new List<SolutionAuditReport>();
                foreach (PackageDescriptor pkg in finalPackages.Where(p => !p.IsSynthesized))
                {
                    SolutionAuditReport pkgAudit = PythonSolutionAuditor.Audit(pkg.DirectoryPath);
                    if (plan.ResolveEntryPoints && !pkgAudit.IsCompliant)
                    {
                        SolutionResolutionPlan resPlan = SolutionResolutionPlanner.Plan(pkgAudit);
                        if (resPlan.HasAutomatableFixes)
                        {
                            ResolutionApplyResult applied = SolutionResolutionPlanner.Apply(
                                resPlan, new PhysicalWorkspaceFileSystem());
                            foreach (FileRenameAction a in applied.Applied)
                                result.Moves.Add(new PerformedMove(a.OldPath, a.NewPath, "entry-point"));
                            // Function renames rewrite a file IN PLACE (declaration + same-file
                            // call sites) rather than moving it, so they don't fit
                            // PerformedMove's from-path/to-path shape — recorded as notes
                            // instead. This only ever touches this working copy (see
                            // OriginalSourceRoot above), never what the user actually pointed
                            // the wizard at.
                            foreach (FunctionRenameAction a in applied.FunctionsApplied)
                                result.Notes.Add("renamed def " + a.OldName + "() -> def " + a.NewName +
                                                 "() in " + a.FilePath);
                            if (!applied.AllSucceeded)
                                result.Diagnostics.Add("Some entry-point renames failed in '" + pkg.Name + "'; see audit.");
                            pkgAudit = PythonSolutionAuditor.Audit(pkg.DirectoryPath);
                        }
                        // Any residual finding this planner still couldn't auto-fix.
                        if (resPlan.HasManualFindings)
                        {
                            foreach (SolutionAuditFinding f in resPlan.ManualFindings)
                                result.Diagnostics.Add("Requires manual fix in '" + pkg.Name + "': " + f.Message +
                                    (string.IsNullOrEmpty(f.SuggestedResolution) ? "" : " Resolution: " + f.SuggestedResolution));
                        }
                    }
                    perPackageAudits.Add(pkgAudit);
                }
                result.PostAudit = CombineAudits(root, perPackageAudits);

                // ---- Phase 5: strict-OOP guard (encapsulate + requalify + audit) ----
                // Runs after entry-point resolution so the surviving def main() is
                // known, and after generation because it only rewrites .py contents.
                if (plan.ConvertToObjectOriented)
                {
                    if (!OopConversionPhase.Run(finalPackages, root, result))
                        return result;
                }

                result.ProjectCount = finalPackages.Count;
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        // Path-equality that tolerates a trailing separator either side.
        private static bool IsSameDirectory(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        // Merges N per-package audits into one report: IsCompliant is true only when every
        // package's OWN audit was clean, but no package's main.py is compared against another
        // package's — each package's #3c/#3d rules are self-contained.
        private static SolutionAuditReport CombineAudits(string root, List<SolutionAuditReport> perPackage)
        {
            var combined = new SolutionAuditReport(root);
            foreach (SolutionAuditReport r in perPackage)
            {
                combined.MainModules.AddRange(r.MainModules);
                combined.MainFunctionFiles.AddRange(r.MainFunctionFiles);
                foreach (SolutionAuditFinding f in r.Findings)
                    if (f.Severity == SolutionAuditSeverity.Error) combined.Findings.Add(f);
            }
            return combined;
        }

        // True when the package directory holds an entry-equivalent module
        // (main.py and variants) anywhere under it.
        private static bool PackageOwnsEntryModule(string packageDir)
        {
            foreach (string f in PythonSolutionAuditor.EnumeratePythonFiles(packageDir))
                if (PythonSolutionAuditor.IsMainModule(f)) return true;
            return false;
        }

        // Move with never-overwrite semantics; records the move or fails the result.
        private static bool MoveDirectory(string from, string to, string reason, ImportExecutionResult result)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
            if (Directory.Exists(to) || File.Exists(to))
            {
                result.ErrorMessage = "Cannot move '" + from + "' to '" + to + "': target already exists.";
                return false;
            }
            if (!Directory.Exists(from))
            {
                // Should not happen given the deepest-first processing order above, but a raw
                // DirectoryNotFoundException here is a useless message to show a user — this
                // shape (source vanished mid-import) only arises when it was physically nested
                // inside another package's directory that already moved elsewhere.
                result.ErrorMessage = "Cannot move '" + from + "': it no longer exists — it was likely nested " +
                                      "inside another package that was already moved. Mark the nested package " +
                                      "'Part of parent' or 'Excluded' on the Projects page instead of Separate project.";
                return false;
            }
            Directory.Move(from, to);
            result.Moves.Add(new PerformedMove(from, to, reason));
            return true;
        }
    }
}
