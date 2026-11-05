using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>One package produced by splitting an over-broad "multiple def main()" package.</summary>
    public sealed class SplitPackage
    {
        public SplitPackage(string name, string directoryPath, bool isExecutable)
        {
            Name = name;
            DirectoryPath = directoryPath;
            IsExecutable = isExecutable;
        }

        public string Name { get; }
        public string DirectoryPath { get; }
        public bool IsExecutable { get; }
    }

    /// <summary>Outcome of splitting one over-broad package.</summary>
    public sealed class PackageSplitResult
    {
        public PackageSplitResult()
        {
            Packages = new List<SplitPackage>();
            Notes = new List<string>();
        }

        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>The shared library package first, then one executable package per original entry file.</summary>
        public List<SplitPackage> Packages { get; }
        public List<string> Notes { get; }
    }

    /// <summary>
    /// Splits a single Python package directory that declares MULTIPLE module-level
    /// <c>def main()</c> functions (SRS #3d — a solution/package may declare only one) into:
    /// <list type="bullet">
    ///   <item>one shared LIBRARY package holding every support module (everything that
    ///     ISN'T one of the entry files) plus the original <c>__init__.py</c> and any
    ///     subdirectories; and</item>
    ///   <item>one EXECUTABLE package per original entry file, each holding just that one
    ///     file — renamed to <c>main.py</c> so it also satisfies the entry-module-naming
    ///     rule the stricter <see cref="PackageComplianceValidator"/> gates Run/Debug on —
    ///     plus a fresh empty <c>__init__.py</c>.</item>
    /// </list>
    /// Every entry file's sibling-relative imports (<c>from . import X</c> / <c>from .X import
    /// Y</c>) are rewritten to absolute imports rooted at the new library package via
    /// <see cref="RelativeImportRewriter"/>, since those sibling modules just moved out from
    /// under it. Only ever call this on a directory the caller already knows is safe to
    /// mutate (a disposable working copy, never a user's original source) — it moves and
    /// rewrites files with no built-in "never touch the original" guard of its own.
    /// </summary>
    public static class PackageSplitter
    {
        /// <param name="packageDir">The package directory to split.</param>
        /// <param name="packageBaseName">Used to derive the shared library package's name.</param>
        /// <param name="newPackagesParent">
        /// Where the new lib/app package directories are created. Defaults to
        /// <paramref name="packageDir"/>'s own filesystem parent (true siblings) — but the
        /// caller MUST override this when <paramref name="packageDir"/> IS the boundary the
        /// whole import operates within (e.g. a flat source root with no wrapper folder):
        /// otherwise the new packages would be created as siblings of that root in ITS OWN
        /// parent directory, i.e. escaping the working copy entirely.
        /// </param>
        public static PackageSplitResult Split(string packageDir, string packageBaseName, string newPackagesParent = null)
        {
            var result = new PackageSplitResult();
            if (string.IsNullOrWhiteSpace(packageDir) || !Directory.Exists(packageDir))
            {
                result.ErrorMessage = "Package directory does not exist: " + packageDir;
                return result;
            }

            List<string> topLevelPy;
            try { topLevelPy = Directory.GetFiles(packageDir, "*.py").ToList(); }
            catch (Exception ex) { result.ErrorMessage = ex.Message; return result; }

            var entryFiles = new List<string>();
            foreach (string f in topLevelPy)
            {
                if (string.Equals(Path.GetFileName(f), "__init__.py", StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = File.ReadAllText(f); } catch { continue; }
                if (PythonSolutionAuditor.ContainsModuleLevelMainFunction(text)) entryFiles.Add(f);
            }
            entryFiles.Sort(StringComparer.OrdinalIgnoreCase);

            if (entryFiles.Count < 2)
            {
                result.ErrorMessage = "Fewer than two entry files found under " + packageDir + "; nothing to split.";
                return result;
            }

            string parent = newPackagesParent;
            if (string.IsNullOrEmpty(parent)) parent = Path.GetDirectoryName(packageDir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent)) parent = packageDir;
            string baseName = string.IsNullOrWhiteSpace(packageBaseName)
                ? Sanitize(Path.GetFileName(packageDir.TrimEnd('\\', '/')))
                : Sanitize(packageBaseName);
            if (baseName.Length == 0) baseName = "pkg";

            HashSet<string> reservedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in Directory.GetDirectories(parent)) reservedDirs.Add(d);

            // Snapshot packageDir's contents BEFORE creating anything — when newPackagesParent
            // == packageDir (the source-root case), the new lib/app directories are created AS
            // CHILDREN of packageDir, so a live re-query after that point would see them too and
            // try to move e.g. libDir into a subfolder of itself.
            string[] originalTopLevelFiles = Directory.GetFiles(packageDir);
            string[] originalSubdirectories = Directory.GetDirectories(packageDir);

            try
            {
                // ---- 1. Shared library package: move EVERYTHING except the entry files. ----
                string libName = UniqueDirName(parent, baseName + "_lib", reservedDirs);
                string libDir = Path.Combine(parent, libName);
                Directory.CreateDirectory(libDir);
                reservedDirs.Add(libDir);

                HashSet<string> entrySet = new HashSet<string>(entryFiles, StringComparer.OrdinalIgnoreCase);
                foreach (string f in originalTopLevelFiles)
                {
                    if (entrySet.Contains(f)) continue;
                    File.Move(f, Path.Combine(libDir, Path.GetFileName(f)));
                }
                foreach (string d in originalSubdirectories)
                    Directory.Move(d, Path.Combine(libDir, Path.GetFileName(d)));
                if (!File.Exists(Path.Combine(libDir, "__init__.py")))
                    File.WriteAllText(Path.Combine(libDir, "__init__.py"), string.Empty);

                result.Packages.Add(new SplitPackage(libName, libDir, isExecutable: false));
                result.Notes.Add("Extracted shared library package '" + libName + "' from " + packageDir);

                // ---- 2. One executable package per entry file. ----
                // Renaming an entry file to main.py breaks anything ELSE that plainly
                // `import`s it by its ORIGINAL module name (a real shape: one standalone
                // script reusing another one's helpers, e.g. "import _t1_t7_validate_x as
                // t17" — both files legitimately have their own def main() too, so both
                // still get split, but the reference between them must survive). Track
                // old-name -> new-absolute-module-path for a final cross-package pass.
                var crossFileRenames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string entryFile in entryFiles)
                {
                    string entryBase = Sanitize(Path.GetFileNameWithoutExtension(entryFile));
                    if (entryBase.Length == 0) entryBase = "app";
                    string appName = UniqueDirName(parent, entryBase, reservedDirs);
                    string appDir = Path.Combine(parent, appName);
                    Directory.CreateDirectory(appDir);
                    reservedDirs.Add(appDir);

                    string targetFile = Path.Combine(appDir, "main.py");
                    File.Move(entryFile, targetFile);

                    string source = File.ReadAllText(targetFile);
                    RelativeImportRewriter.RewriteResult rewritten =
                        RelativeImportRewriter.RewriteSameLevelRelativeImports(source, libName);
                    if (rewritten.Count > 0)
                    {
                        File.WriteAllText(targetFile, rewritten.Text);
                        result.Notes.Add("Rewrote " + rewritten.Count + " relative import(s) in " + targetFile +
                                         " -> absolute imports at '" + libName + "'.");
                    }

                    File.WriteAllText(Path.Combine(appDir, "__init__.py"), string.Empty);

                    result.Packages.Add(new SplitPackage(appName, appDir, isExecutable: true));
                    result.Notes.Add("Extracted executable package '" + appName + "' (was " +
                                     Path.GetFileName(entryFile) + ") from " + packageDir);

                    string originalModuleName = Path.GetFileNameWithoutExtension(entryFile);
                    crossFileRenames[originalModuleName] = appName + ".main";
                }

                // ---- 3. Fix up any OTHER file (in the lib package or a sibling app
                //      package) that plainly imports one of the entry files by its
                //      original name. ----
                if (crossFileRenames.Count > 0)
                {
                    foreach (SplitPackage pkg in result.Packages)
                    {
                        foreach (string py in Directory.GetFiles(pkg.DirectoryPath, "*.py", SearchOption.AllDirectories))
                        {
                            string text;
                            try { text = File.ReadAllText(py); } catch { continue; }
                            int totalHits = 0;
                            foreach (KeyValuePair<string, string> rename in crossFileRenames)
                            {
                                PythonIdentifierRewriter.RewriteResult r =
                                    PythonIdentifierRewriter.RewriteModuleImports(text, rename.Key, rename.Value);
                                if (r.Count == 0) continue;
                                text = r.Text;
                                totalHits += r.Count;
                            }
                            if (totalHits == 0) continue;
                            File.WriteAllText(py, text);
                            result.Notes.Add("Rewrote " + totalHits + " cross-package import reference(s) in " + py);
                        }
                    }
                }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>Picks a sibling directory name under <paramref name="parent"/> that doesn't yet exist.</summary>
        private static string UniqueDirName(string parent, string baseName, HashSet<string> reservedFullPaths)
        {
            string candidateName = baseName;
            string candidatePath = Path.Combine(parent, candidateName);
            int n = 2;
            while (reservedFullPaths.Contains(candidatePath) || Directory.Exists(candidatePath) || File.Exists(candidatePath))
            {
                candidateName = baseName + "_" + n;
                candidatePath = Path.Combine(parent, candidateName);
                n++;
            }
            return candidateName;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string result = sb.ToString().Trim('_');
            if (result.Length > 0 && char.IsDigit(result[0])) result = "m_" + result;
            return result;
        }
    }
}
