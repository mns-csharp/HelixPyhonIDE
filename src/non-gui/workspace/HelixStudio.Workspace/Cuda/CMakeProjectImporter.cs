using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>A build target with its source arguments resolved to actual files on disk.</summary>
    public sealed class CMakeResolvedTarget
    {
        public CMakeResolvedTarget(string name, CMakeTargetKind kind)
        {
            Name = name;
            Kind = kind;
            SourceFiles = new List<string>();
            MissingSources = new List<string>();
        }

        public string Name { get; }
        public CMakeTargetKind Kind { get; }

        /// <summary>True when the CMakeLists that DECLARES this target enables the CUDA language
        /// (<c>project(... LANGUAGES CUDA ...)</c> / <c>enable_language(CUDA)</c>) — the persisted,
        /// per-project record that distinguishes a CUDA target from a plain C++ one. Sources with
        /// .cu/.cuh extensions are the fallback signal for CMakeLists with no language clause.</summary>
        public bool DeclaresCuda { get; set; }

        /// <summary>Absolute paths of source files that exist on disk (searched under the project folder + include dirs).</summary>
        public List<string> SourceFiles { get; }

        /// <summary>Source arguments that could not be located on disk (unexpanded vars, generated files, typos).</summary>
        public List<string> MissingSources { get; }
    }

    /// <summary>A CMake folder modelled as a native project: its metadata plus disk-resolved targets.</summary>
    public sealed class CMakeProjectDescriptor
    {
        public CMakeProjectDescriptor(string folderPath, string name, ProjectKind kind, CMakeListsMetadata metadata)
        {
            FolderPath = folderPath;
            Name = name;
            Kind = kind;
            Metadata = metadata;
            Targets = new List<CMakeResolvedTarget>();
        }

        public string FolderPath { get; }
        public string Name { get; }

        /// <summary>Derived native kind: <see cref="ProjectKind.CudaExecutable"/> vs <see cref="ProjectKind.CudaLibrary"/>.</summary>
        public ProjectKind Kind { get; internal set; }

        public CMakeListsMetadata Metadata { get; }
        public List<CMakeResolvedTarget> Targets { get; }
    }

    public sealed class CMakeImportResult
    {
        public bool Success;
        public string Error;
        public CMakeProjectDescriptor Project;
        public List<string> Diagnostics = new List<string>();
    }

    public static class CMakeConventions
    {
        /// <summary>
        /// Synthetic target name for native source files the importer scanned off disk but no
        /// real CMake target's source list claims (see <see cref="CMakeProjectImporter"/>
        /// .AddUnassignedNativeSources) — shared with <c>CMakeSolutionLoader</c> so both sides
        /// name this bucket identically without a magic-string duplicate.
        /// </summary>
        public const string UnassignedSourcesTargetName = "Unassigned Sources";

        /// <summary>
        /// A <c>set(HELIXSTUDIO_STARTUP_TARGET &lt;name&gt;)</c> at the CMake root marks which
        /// package auto-opens as the startup project — the native mirror of a Python package's
        /// own <c>[tool.helixstudio] startup = true</c>. Read via the reader's existing generic
        /// set()-variable table (<see cref="CMakeListsMetadata.Variables"/>), no reader changes
        /// needed.
        /// </summary>
        public const string StartupTargetVariable = "HELIXSTUDIO_STARTUP_TARGET";
    }

    /// <summary>
    /// Turns a folder containing a <c>CMakeLists.txt</c> into a native <see cref="CMakeProjectDescriptor"/>
    /// (FR-PM-03) — the CUDA/native counterpart to <see cref="HelixPythonNamespace.Workspace.Python.PythonDirectoryImporter"/>.
    /// It parses the CMakeLists with <see cref="CMakeListsReader"/>, resolves each target's source arguments to
    /// real files on disk (relative to the project folder), and derives the native <see cref="ProjectKind"/>.
    /// The result is what the project tree renders: targets as first-class nodes with their source files.
    /// </summary>
    public sealed class CMakeProjectImporter
    {
        public CMakeImportResult Import(string folderPath)
        {
            var result = new CMakeImportResult();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                result.Error = "Folder does not exist: " + folderPath;
                return result;
            }
            string folder = Path.GetFullPath(folderPath);
            string cmakePath = Path.Combine(folder, "CMakeLists.txt");
            if (!File.Exists(cmakePath))
            {
                result.Error = "No CMakeLists.txt in " + folder;
                return result;
            }

            CMakeListsMetadata meta;
            try { meta = CMakeListsReader.Read(cmakePath); }
            catch (Exception ex) { result.Error = "Failed to parse CMakeLists.txt: " + ex.Message; return result; }

            string name = string.IsNullOrEmpty(meta.ProjectName)
                ? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))
                : meta.ProjectName;

            ProjectKind kind = DeriveKind(meta);
            var descriptor = new CMakeProjectDescriptor(folder, name, kind, meta);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ImportDirectory(folder, folder, meta, descriptor, result, visited);
            AddUnassignedNativeSources(folder, descriptor, result);
            if (descriptor.Targets.Exists(t => t.Kind == CMakeTargetKind.Executable))
                descriptor.Kind = ProjectKind.CudaExecutable;

            result.Project = descriptor;
            result.Success = true;
            return result;
        }

        private static void ImportDirectory(string rootFolder, string currentFolder, CMakeListsMetadata meta,
            CMakeProjectDescriptor descriptor, CMakeImportResult result, HashSet<string> visited)
        {
            string cmakeFile = Path.GetFullPath(Path.Combine(currentFolder, "CMakeLists.txt"));
            if (!visited.Add(cmakeFile)) return;

            bool declaresCuda = meta.Languages != null &&
                meta.Languages.Exists(l => string.Equals(l, "CUDA", StringComparison.OrdinalIgnoreCase));
            foreach (CMakeTarget t in meta.Targets)
            {
                var rt = new CMakeResolvedTarget(t.Name, t.Kind) { DeclaresCuda = declaresCuda };
                foreach (string src in t.ResolvedSources)
                {
                    if (t.Kind == CMakeTargetKind.Custom) continue;
                    string abs = ResolveSourceFile(currentFolder, meta, src);
                    if (abs != null) { if (!rt.SourceFiles.Contains(abs)) rt.SourceFiles.Add(abs); }
                    else rt.MissingSources.Add(src);
                }
                descriptor.Targets.Add(rt);
                if (rt.MissingSources.Count > 0)
                    result.Diagnostics.Add("Target '" + t.Name + "': " + rt.MissingSources.Count
                        + " source(s) not found on disk (" + string.Join(", ", rt.MissingSources) + ").");
            }

            foreach (string sub in meta.Subdirectories)
            {
                if (string.IsNullOrWhiteSpace(sub) || sub.IndexOf("${", StringComparison.Ordinal) >= 0)
                {
                    result.Diagnostics.Add("Skipped dynamic add_subdirectory(" + sub + ").");
                    continue;
                }
                string subFolder = SafeCombine(currentFolder, sub);
                if (subFolder == null || !IsInsideRoot(rootFolder, subFolder))
                {
                    result.Diagnostics.Add("Skipped add_subdirectory outside the project root: " + sub);
                    continue;
                }
                string childCmake = Path.Combine(subFolder, "CMakeLists.txt");
                if (!File.Exists(childCmake))
                {
                    result.Diagnostics.Add("Subdirectory has no CMakeLists.txt: " + sub);
                    continue;
                }
                try
                {
                    ImportDirectory(rootFolder, subFolder, CMakeListsReader.Read(childCmake),
                        descriptor, result, visited);
                }
                catch (Exception ex)
                {
                    result.Diagnostics.Add("Could not parse " + childCmake + ": " + ex.Message);
                }
            }
        }

        private static bool IsInsideRoot(string root, string candidate)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        // CMake is a programming language: functions, loops, generator expressions and file(GLOB)
        // cannot be reconstructed correctly by a non-executing reader. Keep statically resolved
        // targets honest, but never make real source files disappear from Solution Explorer.
        private static void AddUnassignedNativeSources(string root, CMakeProjectDescriptor descriptor,
            CMakeImportResult result)
        {
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirOwner = new Dictionary<string, CMakeResolvedTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (CMakeResolvedTarget target in descriptor.Targets)
            {
                foreach (string file in target.SourceFiles)
                {
                    string full = Path.GetFullPath(file);
                    assigned.Add(full);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir) && !dirOwner.ContainsKey(dir)) dirOwner[dir] = target;
                }
            }

            var unassigned = new CMakeResolvedTarget(CMakeConventions.UnassignedSourcesTargetName, CMakeTargetKind.Custom);
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsNativeSource(file) || IsIgnoredTree(root, file)) continue;
                    string full = Path.GetFullPath(file);
                    if (assigned.Contains(full)) continue;

                    // CMake targets conventionally list only compilation units; headers next to a
                    // target's own sources belong to that target, not to a synthetic bucket —
                    // without this, every `add_library(x STATIC lib.cpp)` with a sibling lib.h
                    // sprouted a redundant "Unassigned Sources" project. Unclaimed COMPILATION
                    // units stay in the bucket: those are honest may-not-build signals.
                    if (IsHeader(full))
                    {
                        string dir = Path.GetDirectoryName(full);
                        CMakeResolvedTarget owner;
                        if (!string.IsNullOrEmpty(dir) && dirOwner.TryGetValue(dir, out owner))
                        {
                            owner.SourceFiles.Add(full);
                            continue;
                        }
                    }

                    unassigned.SourceFiles.Add(full);
                }
            }
            catch (Exception ex)
            {
                result.Diagnostics.Add("Native source discovery was incomplete: " + ex.Message);
            }

            unassigned.SourceFiles.Sort(StringComparer.OrdinalIgnoreCase);
            if (unassigned.SourceFiles.Count > 0)
            {
                descriptor.Targets.Add(unassigned);
                result.Diagnostics.Add(unassigned.SourceFiles.Count
                    + " native file(s) could not be assigned without executing CMake; shown under 'Unassigned Sources'.");
            }
        }

        private static bool IsHeader(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".h": case ".hh": case ".hpp": case ".hxx": case ".cuh": return true;
                default: return false;
            }
        }

        private static bool IsNativeSource(string file)
        {
            string ext = Path.GetExtension(file);
            switch (ext.ToLowerInvariant())
            {
                case ".c": case ".cc": case ".cpp": case ".cxx":
                case ".h": case ".hh": case ".hpp": case ".hxx":
                case ".cu": case ".cuh": return true;
                default: return false;
            }
        }

        private static bool IsIgnoredTree(string root, string file)
        {
            string rel;
            try { rel = file.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length).Replace('/', '\\'); }
            catch { return true; }
            return rel.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0
                || rel.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) >= 0
                || rel.IndexOf("\\cmake-build-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Executable if any executable target exists; otherwise a library.
        private static ProjectKind DeriveKind(CMakeListsMetadata meta)
        {
            for (int i = 0; i < meta.Targets.Count; i++)
                if (meta.Targets[i].Kind == CMakeTargetKind.Executable)
                    return ProjectKind.CudaExecutable;
            return ProjectKind.CudaLibrary;
        }

        // Resolve a source argument to an existing file: try the folder directly, then each include dir.
        private static string ResolveSourceFile(string folder, CMakeListsMetadata meta, string src)
        {
            if (string.IsNullOrEmpty(src) || src.IndexOf("${", StringComparison.Ordinal) >= 0) return null;
            string direct = SafeCombine(folder, src);
            if (direct != null && File.Exists(direct)) return direct;
            for (int i = 0; i < meta.IncludeDirectories.Count; i++)
            {
                string cand = SafeCombine(folder, Path.Combine(meta.IncludeDirectories[i], src));
                if (cand != null && File.Exists(cand)) return cand;
            }
            return null;
        }

        private static string SafeCombine(string baseDir, string rel)
        {
            try { return Path.GetFullPath(Path.Combine(baseDir, rel)); }
            catch { return null; }
        }
    }
}
