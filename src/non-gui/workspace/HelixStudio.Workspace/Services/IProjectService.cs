using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Service for manipulating projects within a solution.
    /// </summary>
    public interface IProjectService
    {
        /// <summary>
        /// Add a new C# project to the solution.
        /// </summary>
        ProjectModel AddProject(SolutionModel solution, string name, string targetFramework);

        /// <summary>
        /// Add a new project to the solution with specified language (C# or C++).
        /// </summary>
        ProjectModel AddProject(SolutionModel solution, string name, string targetFramework, ProjectLanguage language);

        /// <summary>
        /// Remove a project from the solution.
        /// </summary>
        /// <param name="deleteFiles">If true, also delete the project files from disk.</param>
        void RemoveProject(SolutionModel solution, ProjectModel project, bool deleteFiles);

        /// <summary>
        /// Rename a project.
        /// </summary>
        void RenameProject(SolutionModel solution, ProjectModel project, string newName);

        /// <summary>
        /// Add a project reference.
        /// </summary>
        void AddProjectReference(ProjectModel project, ProjectModel referencedProject);

        /// <summary>
        /// Remove a project reference.
        /// </summary>
        void RemoveProjectReference(ProjectModel project, ProjectModel referencedProject);

        /// <summary>Move a project by one display position and persist that order.</summary>
        bool MoveProject(SolutionModel solution, ProjectModel project, int offset);
    }
}
