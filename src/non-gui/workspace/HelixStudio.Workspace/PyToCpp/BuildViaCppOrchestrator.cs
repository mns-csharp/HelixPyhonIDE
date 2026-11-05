using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    public struct BuildViaCppResult
    {
        public bool Success;
        public string OutputExePath;
        public string TypedPythonPath; // Pass 1's materialized intermediate — always written, per design
        public string CppSourcePath;
        public string Log;
        public List<TypeCheckDiagnostic> Diagnostics;
    }

    /// <summary>
    /// Drives the full "Build via C++" pipeline: find the entry-point .py file (the one with
    /// a module-level def main()) -> Pass 1 (strict type check + typed-Python rewrite,
    /// ALWAYS materialized to disk as a real, independently re-runnable .py — the point of
    /// the two-pass design is that this intermediate can be diffed/audited/re-run through
    /// python.exe on its own) -> re-parse the materialized file -> Pass 2 (C++ emission) ->
    /// MSVC compile.
    ///
    /// v1 only supports a single translatable file: Pass 1 doesn't resolve cross-file
    /// imports (see PythonToCppTypeChecker's class comment), so a program spanning multiple
    /// .py files with real cross-file calls isn't in scope yet. The entry file is whichever
    /// one has `def main()`; the rest of the solution's .py files are not considered.
    /// </summary>
    public sealed class BuildViaCppOrchestrator
    {
        public BuildViaCppResult Build(SolutionModel solution, string outputRootDir)
        {
            var r = new BuildViaCppResult { Diagnostics = new List<TypeCheckDiagnostic>() };
            var log = new StringBuilder();

            if (solution == null) { r.Log = "no solution"; return r; }
            if (string.IsNullOrEmpty(outputRootDir)) { r.Log = "no output directory"; return r; }

            log.AppendLine("[build-via-cpp] scanning the solution for an entry-point file (def main())...");
            (string entryFile, ModuleSyntax entryModule) = FindEntryPointFile(solution);
            if (entryFile == null)
            {
                log.AppendLine("[build-via-cpp] aborting: no .py file with a module-level def main() was found");
                r.Diagnostics.Add(new TypeCheckDiagnostic(PyCppDiagnosticCodes.NoEntryPoint, "", 0, 0, "no file with def main() found in the solution"));
                r.Log = log.ToString();
                return r;
            }
            log.AppendLine("[build-via-cpp] entry point: " + entryFile);

            // ---- Pass 1: strict type check ----
            var checker1 = new PythonToCppTypeChecker();
            TypedPythonResult checkResult = checker1.Check(entryModule, entryFile);
            if (!checkResult.Success)
            {
                foreach (TypeCheckDiagnostic d in checkResult.Diagnostics) log.AppendLine("[typecheck] " + d);
                log.AppendLine("[build-via-cpp] aborting: Pass 1 rejected the program (see diagnostics above)");
                r.Diagnostics.AddRange(checkResult.Diagnostics);
                r.Log = log.ToString();
                return r;
            }
            log.AppendLine("[build-via-cpp] Pass 1 (type check) succeeded");

            try { Directory.CreateDirectory(outputRootDir); }
            catch (Exception ex) { log.AppendLine("[build-via-cpp] mkdir failed: " + ex.Message); r.Log = log.ToString(); return r; }

            // Always materialize — the whole point of the two-pass design is a real,
            // independently inspectable/runnable typed-Python artifact, not just an internal
            // annotated tree. See PythonToCppTypeChecker's class comment.
            string typedPyPath = Path.Combine(outputRootDir, "typed_" + Path.GetFileNameWithoutExtension(entryFile) + ".py");
            try { File.WriteAllText(typedPyPath, checkResult.TypedSourceText); }
            catch (Exception ex) { log.AppendLine("[build-via-cpp] write " + typedPyPath + ": " + ex.Message); r.Log = log.ToString(); return r; }
            log.AppendLine("[build-via-cpp] wrote typed intermediate: " + typedPyPath);
            r.TypedPythonPath = typedPyPath;

            // Pass 2 walks the materialized file's OWN parse tree (not the original's) — its
            // NodeTypes map is keyed to that specific set of AST node instances, so it must
            // be re-checked against what was actually written to disk, not reused from Pass 1.
            HelixPythonNamespace.Python.ParseResult typedParsed;
            try { typedParsed = HelixPythonNamespace.Python.Python.ParseModule(File.ReadAllText(typedPyPath), typedPyPath); }
            catch (Exception ex) { log.AppendLine("[build-via-cpp] internal error re-parsing typed intermediate: " + ex.Message); r.Log = log.ToString(); return r; }
            if (typedParsed.HasErrors)
            {
                log.AppendLine("[build-via-cpp] internal error: the materialized typed Python failed to re-parse — this indicates a PythonPrettyPrinter bug, not a problem with the original program");
                foreach (var d in typedParsed.Diagnostics) log.AppendLine("[reparse] " + d);
                r.Log = log.ToString();
                return r;
            }

            var checker2 = new PythonToCppTypeChecker();
            TypedPythonResult recheck = checker2.Check(typedParsed.Module, typedPyPath);
            if (!recheck.Success)
            {
                log.AppendLine("[build-via-cpp] internal error: the materialized typed Python failed re-check — this indicates a PythonPrettyPrinter or Pass 1 bug, not a problem with the original program");
                foreach (TypeCheckDiagnostic d in recheck.Diagnostics) log.AppendLine("[recheck] " + d);
                r.Log = log.ToString();
                return r;
            }

            // ---- Pass 2: emit C++ ----
            string cppSource;
            try { cppSource = new PythonToCppEmitter(checker2).Emit(typedParsed.Module); }
            catch (Exception ex) { log.AppendLine("[build-via-cpp] internal error during C++ emission: " + ex.Message); r.Log = log.ToString(); return r; }

            string cppPath = Path.Combine(outputRootDir, "main.cpp");
            try
            {
                File.WriteAllText(cppPath, cppSource);
                File.WriteAllText(Path.Combine(outputRootDir, PyCppRuntimeHeader.FileName), PyCppRuntimeHeader.Content);
            }
            catch (Exception ex) { log.AppendLine("[build-via-cpp] write " + cppPath + ": " + ex.Message); r.Log = log.ToString(); return r; }
            log.AppendLine("[build-via-cpp] wrote " + cppPath);
            r.CppSourcePath = cppPath;

            // ---- Compile ----
            string objDir = Path.Combine(outputRootDir, "obj");
            string outExe = Path.Combine(outputRootDir, "app.exe");
            (int exitCode, string clLog) = MsvcToolchain.Compile(new[] { cppPath }, outExe, objDir);
            log.Append(clLog);

            r.Success = exitCode == 0 && File.Exists(outExe);
            r.OutputExePath = r.Success ? outExe : null;
            r.Log = log.ToString();
            return r;
        }

        private static (string path, ModuleSyntax module) FindEntryPointFile(SolutionModel solution)
        {
            if (solution?.Projects == null) return (null, null);
            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null || string.IsNullOrEmpty(project.ProjectDirectory) || !Directory.Exists(project.ProjectDirectory)) continue;
                string[] files;
                try { files = Directory.GetFiles(project.ProjectDirectory, "*.py", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (string path in files)
                {
                    string src;
                    try { src = File.ReadAllText(path); } catch { continue; }
                    if (string.IsNullOrWhiteSpace(src)) continue;

                    HelixPythonNamespace.Python.ParseResult parsed;
                    try { parsed = HelixPythonNamespace.Python.Python.ParseModule(src, path); } catch { continue; }
                    if (parsed.HasErrors || parsed.Module == null) continue;

                    bool hasMain = parsed.Module.Statements.OfType<FunctionDefSyntax>().Any(f => f.Name == "main");
                    if (hasMain) return (path, parsed.Module);
                }
            }
            return (null, null);
        }
    }
}
