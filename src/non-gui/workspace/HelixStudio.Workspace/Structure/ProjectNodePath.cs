using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Where the Solution Explorer draws one project's node, as a canonical solution-relative
    /// path — the single definition of that rule for the whole parity engine.
    ///
    /// <b>Why it is a class and not two inlined expressions.</b> Both halves of the check compute
    /// this: <see cref="TreeLayoutProjector"/> to decide where to hang the member's files, and
    /// <see cref="StructureParityChecker"/> to build the anchor it rebases disk paths through. The
    /// two answers MUST be the same string, because the check is exactly a comparison of paths
    /// derived from them — if they disagree the engine reports a defect that describes nothing
    /// but its own inconsistency. They did disagree, and this class is what stopped that: the
    /// projector said <c>project.Name</c> unconditionally while the checker's anchor said
    /// <c>Relative(root, ProjectDirectory)</c>, which for a project rooted at the solution
    /// directory are <c>"cuda_python_practice"</c> and <c>""</c>.
    ///
    /// <b>The root-anchored member.</b> A single-project Python solution has no member
    /// subdirectory: the root <c>pyproject.toml</c> carries both <c>[project]</c> and
    /// <c>[tool.helixstudio] kind</c>, so <see cref="ProjectModel.ProjectDirectory"/> IS
    /// <see cref="SolutionModel.SolutionDirectory"/>. That is the shape the New Project wizard
    /// creates for <c>py.console</c>, and <c>SolutionService</c> loads it deliberately (the same
    /// shape appears in a workspace whose root is also a member). Its node therefore stands for
    /// the solution directory, in exactly the way the solution node and the manifest rows already
    /// do — a nested second rendering of one directory, not a folder of its own. Projecting it as
    /// a <c>project.Name</c> subdirectory claims a folder the user does not have, which is the
    /// disagreement PY8004 exists to forbid.
    ///
    /// <see cref="StructureInventory"/> has had this case since it was written ("a member rooted
    /// at the solution"); this is the same rule stated once for the other two stages.
    /// </summary>
    internal static class ProjectNodePath
    {
        /// <summary>
        /// The node path for <paramref name="project"/> under a solution rooted at
        /// <paramref name="solutionRoot"/>: <c>""</c> when the member IS the root, otherwise its
        /// declared name as a single segment directly under the solution node.
        ///
        /// A project directory OUTSIDE the solution keeps its declared name — that is a Depth
        /// defect for <see cref="StructureParityChecker"/> to report, and inventing a different
        /// node path for it here would only hide it.
        /// </summary>
        public static string Declared(ProjectModel project, string solutionRoot)
        {
            if (project == null) return string.Empty;
            string actual = StructurePath.Relative(solutionRoot, project.ProjectDirectory);
            if (actual != null && actual.Length == 0) return string.Empty;
            return StructurePath.Normalize(project.Name);
        }
    }
}
