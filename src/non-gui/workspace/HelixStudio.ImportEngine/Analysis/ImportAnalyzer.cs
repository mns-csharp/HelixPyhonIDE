using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Workspace.Python;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.ImportEngine.Analysis
{
    /// <summary>
    /// Read-only analysis of an arbitrary Python directory (SRS
    /// solution_explorer_rules 4.2/4.3). Delegates package discovery,
    /// classification and the main-module audit to the existing
    /// PythonDirectoryImporter analysis pass, then layers on the detections the
    /// importer does not surface: namespace dirs, orphan files, virtual
    /// environments, build artifacts, test dirs, documentation, duplicate names.
    /// Never writes to the filesystem.
    /// </summary>
    public sealed class ImportAnalyzer
    {
        private static readonly HashSet<string> VenvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".venv", "venv", "env", ".env", "virtualenv"
        };

        private static readonly HashSet<string> ArtifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "__pycache__", "build", "dist", ".tox", ".pytest_cache", ".mypy_cache",
            ".ruff_cache", "site-packages", ".eggs", "htmlcov", ".coverage_html"
        };

        private static readonly HashSet<string> DocNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "docs", "doc", "documentation"
        };

        public ImportAnalysis Analyze(string sourceRoot)
        {
            var analysis = new ImportAnalysis();
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                analysis.Success = false;
                analysis.ErrorMessage = "Source directory does not exist: " + sourceRoot;
                return analysis;
            }
            sourceRoot = Path.GetFullPath(sourceRoot);
            analysis.SourceRoot = sourceRoot;

            // 1) The importer's own read-only pass: packages, synthesized root,
            //    classification, main-module audit.
            var options = new ImportOptions
            {
                SourceRoot = sourceRoot,
                SolutionName = "analysis",
                AnalysisOnly = true
            };
            ImportResult result = new PythonDirectoryImporter().Import(options);
            if (!result.Success)
            {
                analysis.Success = false;
                analysis.ErrorMessage = result.ErrorMessage;
                return analysis;
            }
            analysis.Packages.AddRange(result.Packages);
            analysis.RepositoryClassification = result.RepositoryClassification;
            analysis.AuditReport = result.AuditReport;
            foreach (string d in result.Diagnostics) analysis.Diagnostics.Add(d);

            // 2) Directory sweep for the additional 4.3 detections. Venvs and
            //    artifacts are recognized *at* their root and not descended into;
            //    everything else recurses.
            var packageDirs = new HashSet<string>(
                analysis.Packages.Where(p => !p.IsSynthesized).Select(p => Path.GetFullPath(p.DirectoryPath)),
                StringComparer.OrdinalIgnoreCase);
            Sweep(sourceRoot, sourceRoot, packageDirs, analysis);

            // 3) Duplicate package names (SRS 4.3 "detect duplicate names"): two
            //    packages in different directories importing under the same name.
            foreach (var group in analysis.Packages
                         .Where(p => !p.IsSynthesized)
                         .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                var dup = new DuplicateNameGroup(group.Key);
                foreach (PackageDescriptor p in group) dup.DirectoryPaths.Add(p.DirectoryPath);
                analysis.DuplicateNames.Add(dup);
                analysis.Diagnostics.Add(
                    "Duplicate package name '" + group.Key + "' in: " +
                    string.Join("; ", dup.DirectoryPaths));
            }

            // 4) Inline-CUDA-kernel detection (SS §linked-cmake-project): scans every real
            //    package's .py files for the "kernel source as a raw-string constant" shape.
            analysis.DetectedCudaScaffold = CudaKernelScaffoldAnalyzer.Analyze(
                analysis.Packages.FindAll(p => !p.IsSynthesized));
            if (analysis.DetectedCudaScaffold.HasKernels)
                analysis.Diagnostics.Add(
                    "Detected " + analysis.DetectedCudaScaffold.Kernels.Count +
                    " inline CUDA kernel(s) — a debug scaffold (native CMake project with " +
                    "one host executable per kernel) can be generated for real-toolchain debugging.");

            // 5) Strict-OOP forecast (read-only): what the guard will auto-convert on
            //    import, and what would survive conversion and fail a guarded import.
            analysis.OopViolations.AddRange(PythonOopAuditor.AuditTree(sourceRoot));
            if (analysis.OopViolations.Count > 0)
            {
                int convertible = analysis.OopViolations.Count(
                    v => v.Kind == OopViolationKind.ModuleLevelFunction);
                int residual = analysis.OopViolations.Count - convertible;
                analysis.Diagnostics.Add(
                    "Strict-OOP: " + convertible + " module-level function(s) will be encapsulated " +
                    "into per-module classes on import" +
                    (residual > 0
                        ? "; " + residual + " statement(s) cannot be auto-converted and will fail a " +
                          "guarded import unless fixed or excluded."
                        : "."));
            }

            analysis.Success = true;
            return analysis;
        }

        private static void Sweep(string dir, string root, HashSet<string> packageDirs, ImportAnalysis analysis)
        {
            string name = Path.GetFileName(dir);
            bool isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);

            if (!isRoot)
            {
                if (IsVirtualEnv(dir, name))
                {
                    analysis.VirtualEnvironments.Add(new DetectedDirectory(dir, VenvReason(dir, name)));
                    return;  // never descend into an environment
                }
                if (ArtifactNames.Contains(name) || name.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.BuildArtifacts.Add(new DetectedDirectory(dir, "build artifact directory '" + name + "'"));
                    return;
                }
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    return;  // VCS/IDE metadata — not reported, not descended
                }

                bool isPackage = packageDirs.Contains(Path.GetFullPath(dir));
                bool hasInit = File.Exists(Path.Combine(dir, "__init__.py"));
                string[] pyFiles = SafeGetFiles(dir, "*.py");

                if (IsTestDirectory(name, pyFiles))
                {
                    analysis.TestDirectories.Add(new DetectedDirectory(
                        dir, hasInit
                            ? "test package (" + pyFiles.Length + " test file(s))"
                            : "test directory (" + pyFiles.Length + " test file(s))"));
                }
                else if (DocNames.Contains(name) || IsDocDirectory(dir))
                {
                    analysis.DocumentationDirectories.Add(new DetectedDirectory(dir, DocReason(dir, name)));
                    // Docs may still contain example code; keep sweeping below.
                }

                // Namespace dir (PEP 420 shape): holds modules but no __init__.py
                // and was not claimed as a package.
                if (!isPackage && !hasInit && pyFiles.Length > 0)
                {
                    bool underPackage = IsUnderAnyPackage(dir, packageDirs);
                    bool isTestDir = IsTestDirectory(name, pyFiles);
                    if (!underPackage)
                    {
                        analysis.NamespaceDirectories.Add(new DetectedDirectory(
                            dir, pyFiles.Length + " module(s), no __init__.py"));
                        // Test modules belong to the test detection above; everything
                        // else here has no owning package -> orphan.
                        if (!isTestDir)
                        {
                            foreach (string f in pyFiles) analysis.OrphanFiles.Add(f);
                        }
                    }
                }
            }

            foreach (string sub in SafeGetDirectories(dir))
                Sweep(sub, root, packageDirs, analysis);
        }

        private static bool IsUnderAnyPackage(string dir, HashSet<string> packageDirs)
        {
            string full = Path.GetFullPath(dir);
            foreach (string pkg in packageDirs)
            {
                if (full.StartsWith(pkg + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(full, pkg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsVirtualEnv(string dir, string name)
        {
            if (File.Exists(Path.Combine(dir, "pyvenv.cfg"))) return true;
            if (!VenvNames.Contains(name)) return false;
            // A dir merely NAMED like a venv still counts (SolutionService skips it),
            // but report the strongest evidence available.
            return true;
        }

        private static string VenvReason(string dir, string name)
        {
            return File.Exists(Path.Combine(dir, "pyvenv.cfg"))
                ? "contains pyvenv.cfg"
                : "directory name '" + name + "'";
        }

        private static bool IsTestDirectory(string name, string[] pyFiles)
        {
            bool namedLikeTests = string.Equals(name, "tests", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(name, "test", StringComparison.OrdinalIgnoreCase);
            int testFiles = pyFiles.Count(f =>
            {
                string fn = Path.GetFileName(f);
                return fn.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
                       fn.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase);
            });
            if (namedLikeTests && (pyFiles.Length == 0 || testFiles > 0)) return true;
            return testFiles > 0 && testFiles == pyFiles.Length && pyFiles.Length > 0;
        }

        private static bool IsDocDirectory(string dir)
        {
            string[] docFiles = SafeGetFiles(dir, "*.rst");
            if (docFiles.Length >= 2) return true;
            if (File.Exists(Path.Combine(dir, "conf.py")) && SafeGetFiles(dir, "*.rst").Length > 0) return true;
            if (File.Exists(Path.Combine(dir, "mkdocs.yml"))) return true;
            return false;
        }

        private static string DocReason(string dir, string name)
        {
            if (File.Exists(Path.Combine(dir, "conf.py"))) return "Sphinx documentation (conf.py)";
            if (File.Exists(Path.Combine(dir, "mkdocs.yml"))) return "MkDocs documentation";
            int rst = SafeGetFiles(dir, "*.rst").Length;
            int md = SafeGetFiles(dir, "*.md").Length;
            if (rst + md > 0) return rst + md + " documentation file(s)";
            return "directory name '" + name + "'";
        }

        private static string[] SafeGetFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
            catch (UnauthorizedAccessException) { return new string[0]; }
            catch (IOException) { return new string[0]; }
        }

        private static string[] SafeGetDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { return new string[0]; }
            catch (IOException) { return new string[0]; }
        }
    }
}
