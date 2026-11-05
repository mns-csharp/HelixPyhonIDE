using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    public sealed class ProjectService : IProjectService
    {
        public ProjectModel AddProject(SolutionModel solution, string name, string targetFramework)
        {
            return AddProject(solution, name, targetFramework, ProjectLanguage.Python);
        }

        public ProjectModel AddProject(SolutionModel solution, string name, string targetFramework, ProjectLanguage language)
        {
            if (solution == null) throw new ArgumentNullException(nameof(solution));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");

            string baseDir = Path.GetDirectoryName(solution.FilePath);
            string projectDir = Path.Combine(baseDir, name);
            Directory.CreateDirectory(projectDir);
            string pyproject = Path.Combine(projectDir, "pyproject.toml");
            string pyVersion = DerivePythonVersion(targetFramework);
            PyProjectWriter.WriteMinimal(pyproject, name, pyVersion);
            string packageDir = Path.Combine(projectDir, SafePackage(name));
            Directory.CreateDirectory(packageDir);
            string init = Path.Combine(packageDir, "__init__.py");
            if (!File.Exists(init)) File.WriteAllText(init, "");

            ProjectModel project = new ProjectModel(pyproject, name, solution);
            project.DefaultNamespace = SafePackage(name);
            project.PythonVersion = pyVersion;
            project.TargetFramework = "python" + pyVersion;
            solution.Projects.Add(project);
            return project;
        }

        public void RemoveProject(SolutionModel solution, ProjectModel project, bool deleteFiles)
        {
            if (solution == null || project == null) return;
            solution.Projects.Remove(project);
            if (deleteFiles)
            {
                string dir = Path.GetDirectoryName(project.FilePath);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        public void RenameProject(SolutionModel solution, ProjectModel project, string newName)
        {
            if (solution == null) throw new ArgumentNullException(nameof(solution));
            if (project == null) throw new ArgumentNullException(nameof(project));
            newName = (newName ?? string.Empty).Trim();
            if (newName.Length == 0) throw new ArgumentException("A project name is required.", nameof(newName));
            if (string.Equals(project.Name, newName, StringComparison.Ordinal)) return;

            string root = Path.GetDirectoryName(solution.FilePath);
            string oldProjectDir = project.ProjectDirectory;
            bool isRootProject = PathsEqual(root, oldProjectDir);
            if (isRootProject)
                throw new InvalidOperationException(
                    "The root project owns the solution directory. Rename it through the solution-folder transaction.");
            string newProjectDir = isRootProject ? oldProjectDir : Path.Combine(root, newName);
            if (!isRootProject && Directory.Exists(newProjectDir))
                throw new IOException("A project directory named '" + newName + "' already exists.");

            string oldPackageName = string.IsNullOrWhiteSpace(project.DefaultNamespace)
                ? SafePackage(project.Name) : project.DefaultNamespace;
            string newPackageName = SafePackage(newName);
            string oldPackageDir = Path.Combine(oldProjectDir, oldPackageName);
            string newPackageDirBeforeProjectMove = Path.Combine(oldProjectDir, newPackageName);
            bool renamePackage = Directory.Exists(oldPackageDir) &&
                File.Exists(Path.Combine(oldPackageDir, "__init__.py")) &&
                !string.Equals(oldPackageName, newPackageName, StringComparison.Ordinal);
            if (renamePackage && Directory.Exists(newPackageDirBeforeProjectMove))
                throw new IOException("A Python package named '" + newPackageName + "' already exists.");

            var packageRename = renamePackage
                ? new PackageRenameService().Compute(solution, oldPackageDir, newPackageName, root)
                : default(PackageRenameService.ComputedPackageRename);
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectModel p in solution.Projects)
                if (File.Exists(p.FilePath)) originals[p.FilePath] = File.ReadAllText(p.FilePath);
            if (File.Exists(solution.FilePath) && !originals.ContainsKey(solution.FilePath))
                originals[solution.FilePath] = File.ReadAllText(solution.FilePath);
            if (renamePackage)
                foreach (FileTextEdit edit in packageRename.Edits)
                    if (File.Exists(edit.Path) && !originals.ContainsKey(edit.Path))
                        originals[edit.Path] = edit.OldText;

            bool packageMoved = false;
            bool projectMoved = false;
            try
            {
                if (renamePackage)
                {
                    Directory.Move(oldPackageDir, newPackageDirBeforeProjectMove);
                    packageMoved = true;
                    foreach (FileTextEdit edit in packageRename.Edits)
                    {
                        string target = RebasePath(edit.Path, oldPackageDir, newPackageDirBeforeProjectMove);
                        File.WriteAllText(target, edit.NewText);
                    }
                }

                if (!isRootProject)
                {
                    Directory.Move(oldProjectDir, newProjectDir);
                    projectMoved = true;
                }

                string renamedManifest = isRootProject
                    ? project.FilePath : Path.Combine(newProjectDir, Path.GetFileName(project.FilePath));
                TomlTableMerger.EnsureTable(renamedManifest, "project",
                    new Dictionary<string, string> { { "name", QuoteToml(newName) } });

                foreach (ProjectModel p in solution.Projects)
                {
                    if (!p.ProjectReferences.Any(r => ReferenceEquals(r, project))) continue;
                    string manifest = ReferenceEquals(p, project) ? renamedManifest : p.FilePath;
                    IEnumerable<string> references = p.ProjectReferences.Select(r =>
                        ReferenceEquals(r, project) ? newName : r.Name);
                    if (File.Exists(manifest))
                        PyProjectReferenceWriter.SetReferences(manifest, references);
                }

                if (solution.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
                {
                    var members = solution.Projects
                        .Where(p => !PathsEqual(p.ProjectDirectory, root))
                        .Select(p => ReferenceEquals(p, project) ? newName : Path.GetFileName(p.ProjectDirectory))
                        .ToList();
                    TomlWorkspaceRootWriter.SetMembers(solution.FilePath, members);
                    TomlWorkspaceRootWriter.SetProjectOrder(solution.FilePath,
                        solution.Projects.Select(p => ReferenceEquals(p, project) ? newName : p.Name).ToList());
                }
            }
            catch
            {
                try
                {
                    if (projectMoved && Directory.Exists(newProjectDir) && !Directory.Exists(oldProjectDir))
                        Directory.Move(newProjectDir, oldProjectDir);
                    if (packageMoved && Directory.Exists(newPackageDirBeforeProjectMove) && !Directory.Exists(oldPackageDir))
                        Directory.Move(newPackageDirBeforeProjectMove, oldPackageDir);
                    foreach (KeyValuePair<string, string> original in originals)
                        if (File.Exists(original.Key)) File.WriteAllText(original.Key, original.Value);
                }
                catch { }
                throw;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string RebasePath(string path, string oldDirectory, string newDirectory)
        {
            string full = Path.GetFullPath(path);
            string oldFull = Path.GetFullPath(oldDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!full.StartsWith(oldFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return path;
            return Path.GetFullPath(newDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + full.Substring(oldFull.Length);
        }

        private static string QuoteToml(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public void AddProjectReference(ProjectModel project, ProjectModel referencedProject)
        {
            if (project == null || referencedProject == null) return;
            if (ReferenceEquals(project, referencedProject))
                throw new InvalidOperationException(
                    "PSM-45: A project cannot reference itself.");
            // PSM-43/44 are PYTHON solution-model rules. In .NET (and the other manifest
            // languages) referencing an executable project is legal and standard practice —
            // a unit-test project referencing the console app under test, for example.
            if (referencedProject.IsExecutable &&
                referencedProject.Language == HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python)
            {
                string rule = project.IsExecutable ? "PSM-44" : "PSM-43";
                throw new InvalidOperationException(
                    rule + ": Project '" + project.Name +
                    "' cannot reference executable project '" + referencedProject.Name + "'.");
            }
            if (CanReach(referencedProject, project, new HashSet<ProjectModel>()))
                throw new InvalidOperationException(
                    "PSM-45: Circular project reference detected: " +
                    project.Name + " -> " + referencedProject.Name + " -> " + project.Name + ".");
            if (!project.ProjectReferences.Contains(referencedProject))
                project.ProjectReferences.Add(referencedProject);
        }

        public void RemoveProjectReference(ProjectModel project, ProjectModel referencedProject)
        {
            if (project == null || referencedProject == null) return;
            project.ProjectReferences.Remove(referencedProject);
        }

        public bool MoveProject(SolutionModel solution, ProjectModel project, int offset)
        {
            if (solution == null || project == null || offset == 0) return false;
            int current = solution.Projects.IndexOf(project);
            int target = current + offset;
            if (current < 0 || target < 0 || target >= solution.Projects.Count) return false;
            if (string.IsNullOrEmpty(solution.FilePath) ||
                !solution.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return false;

            solution.Projects.RemoveAt(current);
            solution.Projects.Insert(target, project);
            try
            {
                TomlWorkspaceRootWriter.SetProjectOrder(solution.FilePath,
                    solution.Projects.Select(p => p.Name).ToList());
                return true;
            }
            catch
            {
                solution.Projects.RemoveAt(target);
                solution.Projects.Insert(current, project);
                throw;
            }
        }

        /// <summary>
        /// Returns dependency-first build order (PSM-46), or throws with PSM-45 when the
        /// in-memory graph contains a cycle introduced outside <see cref="AddProjectReference"/>.
        /// </summary>
        public static IReadOnlyList<ProjectModel> GetBuildOrder(SolutionModel solution)
        {
            var result = new List<ProjectModel>();
            if (solution == null || solution.Projects == null) return result;

            var visiting = new HashSet<ProjectModel>();
            var visited = new HashSet<ProjectModel>();
            foreach (ProjectModel project in solution.Projects)
                VisitForBuildOrder(project, visiting, visited, result);
            return result;
        }

        private static void VisitForBuildOrder(
            ProjectModel project,
            HashSet<ProjectModel> visiting,
            HashSet<ProjectModel> visited,
            List<ProjectModel> result)
        {
            if (project == null || visited.Contains(project)) return;
            if (!visiting.Add(project))
                throw new InvalidOperationException(
                    "PSM-45: Circular project reference detected at '" + project.Name + "'.");

            foreach (ProjectModel dependency in project.ProjectReferences)
                VisitForBuildOrder(dependency, visiting, visited, result);

            visiting.Remove(project);
            visited.Add(project);
            result.Add(project);
        }

        private static bool CanReach(
            ProjectModel start,
            ProjectModel target,
            HashSet<ProjectModel> visited)
        {
            if (ReferenceEquals(start, target)) return true;
            if (start == null || !visited.Add(start)) return false;
            foreach (ProjectModel next in start.ProjectReferences)
                if (CanReach(next, target, visited)) return true;
            return false;
        }

        private static string DerivePythonVersion(string targetFramework)
        {
            if (string.IsNullOrEmpty(targetFramework)) return "3.12";
            string candidate = targetFramework.ToLowerInvariant()
                .Replace("python", string.Empty).Replace("py", string.Empty);
            if (candidate.Length >= 3 && candidate[0] == '3' && candidate[1] == '.') return candidate;
            return "3.12";
        }

        private static string SafePackage(string name)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '_' || c == '-' || c == '.') sb.Append('_');
            }
            return sb.Length == 0 ? "pkg" : sb.ToString();
        }
    }
}
