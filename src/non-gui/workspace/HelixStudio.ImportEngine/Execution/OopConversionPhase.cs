using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Execution
{
    /// <summary>
    /// Phase 5 of the import pipeline — the strict-OOP guard (SRS §4 +
    /// "imported solutions are always strictly object-oriented"):
    /// <list type="number">
    /// <item>Encapsulate every module-level function of every imported project
    /// into a per-module class (<see cref="PythonOopConverter"/>), preserving the
    /// PSM entry shape (main.py's <c>def main()</c> + guard).</item>
    /// <item>Requalify every cross-module reference to a moved function
    /// (<see cref="PythonOopReferenceRewriter"/>).</item>
    /// <item>Audit the ENTIRE imported solution — every project/package —
    /// with <see cref="PythonOopAuditor"/>; any residual violation fails the
    /// import so a non-OOP solution is never produced.</item>
    /// </list>
    /// Only ever touches the working copy the executor already operates on.
    /// </summary>
    internal static class OopConversionPhase
    {
        /// <summary>Runs the guard; false means the import must fail (ErrorMessage is set).</summary>
        public static bool Run(List<PackageDescriptor> finalPackages, string root,
                               ImportExecutionResult result)
        {
            // ---- file inventory per package (the "loaded solution" = these files) ----
            var allFiles = new List<string>();
            var fileToPackage = new Dictionary<string, PackageDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageDescriptor pkg in finalPackages)
            {
                foreach (string f in PackageFiles(pkg))
                {
                    string full = Path.GetFullPath(f);
                    if (fileToPackage.ContainsKey(full)) continue;
                    fileToPackage[full] = pkg;
                    allFiles.Add(full);
                }
            }

            // ---- 5a: per-module encapsulation ----
            var conversions = new List<OopModuleConversion>();
            foreach (string file in allFiles)
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch (Exception ex)
                {
                    result.Diagnostics.Add("OOP conversion could not read " + file + ": " + ex.Message);
                    continue;
                }

                PackageDescriptor pkg = fileToPackage[file];
                string dotted = DottedPathOf(pkg, file);
                string moduleNameForClass = dotted.Length > 0
                    ? dotted.Substring(dotted.LastIndexOf('.') + 1)
                    : Path.GetFileNameWithoutExtension(file);
                bool isEntry = PythonSolutionAuditor.IsMainModule(file);

                OopConversionResult conv = PythonOopConverter.ConvertModule(source, moduleNameForClass, isEntry);
                foreach (string note in conv.Notes)
                    result.Diagnostics.Add("OOP [" + pkg.Name + "] " + Path.GetFileName(file) + ": " + note);
                if (!conv.Changed) continue;

                try { File.WriteAllText(file, conv.Text); }
                catch (Exception ex)
                {
                    result.Diagnostics.Add("OOP conversion could not write " + file + ": " + ex.Message);
                    continue;
                }
                result.OopConvertedModuleCount++;
                result.Notes.Add("OOP: encapsulated " + conv.MovedFunctions.Count + " function(s) of " +
                                 Relative(root, file) + " into class " + conv.ClassName);
                conversions.Add(new OopModuleConversion(file, dotted, conv.ClassName, conv.MovedFunctions));
            }

            // ---- 5b: solution-wide reference requalification ----
            foreach (OopModuleConversion conv in conversions)
            {
                int consumersChanged = 0, referencesChanged = 0;
                foreach (string file in allFiles)
                {
                    if (string.Equals(file, conv.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                    string source;
                    try { source = File.ReadAllText(file); } catch { continue; }
                    var rewritten = PythonOopReferenceRewriter.RewriteConsumer(source, file, conv);
                    if (rewritten.Count == 0) continue;
                    try
                    {
                        File.WriteAllText(file, rewritten.Text);
                        consumersChanged++;
                        referencesChanged += rewritten.Count;
                    }
                    catch (Exception ex)
                    {
                        result.Diagnostics.Add("OOP reference rewrite failed for " + file + ": " + ex.Message);
                    }
                }
                if (referencesChanged > 0)
                    result.Notes.Add("OOP: requalified " + referencesChanged + " reference(s) to " +
                                     conv.DottedPath + " in " + consumersChanged + " file(s)");
            }

            // ---- 5c: strict audit of the entire imported solution ----
            foreach (string file in allFiles)
                result.OopViolations.AddRange(PythonOopAuditor.AuditFile(file));

            if (result.OopViolations.Count == 0) return true;

            const int maxListed = 30;
            foreach (OopViolation v in result.OopViolations.Take(maxListed))
                result.Diagnostics.Add("OOP violation: " + Relative(root, v.FilePath) + "(" + v.Line + "): " + v.Message);
            if (result.OopViolations.Count > maxListed)
                result.Diagnostics.Add("OOP violation: ... and " + (result.OopViolations.Count - maxListed) + " more.");

            result.ErrorMessage =
                "Strict OOP guard: the imported solution is not fully object-oriented — " +
                result.OopViolations.Count + " violation(s) remain that automatic conversion cannot fix " +
                "(module-level statements, non-constant module state, or unparsable files). " +
                "Fix or exclude the offending files, or disable 'Convert to object-oriented form' to " +
                "import anyway. Details are in the diagnostics list.";
            return false;
        }

        // Files a package contributes to the solution: the synthesized root project owns
        // only the loose top-level files (its directory IS the source root, and sweeping
        // it recursively would claim every other project's tree too).
        private static IEnumerable<string> PackageFiles(PackageDescriptor pkg)
        {
            if (pkg.IsSynthesized)
            {
                string[] files;
                try { files = Directory.GetFiles(pkg.DirectoryPath, "*.py", SearchOption.TopDirectoryOnly); }
                catch { files = new string[0]; }
                return files;
            }
            return PythonSolutionAuditor.EnumeratePythonFiles(pkg.DirectoryPath);
        }

        /// <summary>Importable dotted path of a module file within its package
        /// (src-layout import root = the package directory's parent).</summary>
        internal static string DottedPathOf(PackageDescriptor pkg, string file)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            if (pkg.IsSynthesized) return baseName;

            string pkgDir = Path.GetFullPath(pkg.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar);
            string fileDir = Path.GetDirectoryName(Path.GetFullPath(file));
            var parts = new List<string> { pkg.Name };
            if (fileDir.Length > pkgDir.Length &&
                fileDir.StartsWith(pkgDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                parts.AddRange(fileDir.Substring(pkgDir.Length + 1)
                    .Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));
            }
            if (!string.Equals(baseName, "__init__", StringComparison.OrdinalIgnoreCase))
                parts.Add(baseName);
            return string.Join(".", parts);
        }

        private static string Relative(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string full = Path.GetFullPath(path);
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(prefix.Length)
                : full;
        }
    }
}
