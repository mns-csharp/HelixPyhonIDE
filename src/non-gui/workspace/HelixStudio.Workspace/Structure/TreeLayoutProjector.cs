using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Stage 2a — projects the structural spine the Solution Explorer will draw, from the MODEL.
    ///
    /// <b>From the model, never from the <c>TreeView</c>.</b> Reading <c>_treeView.Nodes</c> would
    /// couple the checker to a WinForms control, to whatever the filter box currently holds, and to
    /// the clone-based filtering that rebuilds nodes — so parity would appear to break every time a
    /// user typed in the search box. Projecting from <see cref="SolutionModel"/> keeps the check
    /// headless, testable in the fast non-GUI suite, and a property of the solution rather than of
    /// the current view state.
    ///
    /// What it projects is what <c>ProjectTreePanel.BuildTree</c> actually draws today:
    /// <list type="bullet">
    /// <item>the solution node ↔ the solution directory;</item>
    /// <item>one project node per <see cref="SolutionModel.Projects"/> entry, as a DIRECT child of
    /// the solution node, named <see cref="ProjectModel.Name"/> — which is the whole bug: neither
    /// the depth nor the label is derived from <see cref="ProjectModel.ProjectDirectory"/>. The one
    /// exception is a member rooted AT the solution directory, whose node stands for that directory
    /// and so projects at <c>""</c>; <see cref="ProjectNodePath"/> holds that rule for both this
    /// class and <see cref="StructureParityChecker"/>, which have to agree on it;</item>
    /// <item>below each project node, everything the loader inventoried under the member —
    /// <see cref="ProjectModel.ContentFiles"/> and <see cref="ProjectModel.ContentDirectories"/> —
    /// at its real project-relative depth, unioned with the symbol- and resource-derived paths;</item>
    /// <item><see cref="SolutionModel.SolutionItems"/> and
    /// <see cref="SolutionModel.ContentDirectories"/> at the solution root.</item>
    /// </list>
    ///
    /// The inventory is the same list <c>ProjectTreePanel</c> renders, and that is the point:
    /// projecting an independently-guessed disk could claim a node the panel does not draw, which
    /// would make a clean report mean nothing. See <see cref="StructureInventory"/> for why the
    /// check is not thereby made vacuous.
    ///
    /// Symbol layers and the adornment nodes (Dependencies, Environment, Toolchain, Configuration,
    /// Excluded) are deliberately NOT projected — they are virtual by construction and outside the
    /// parity contract. The test for whether a future node type belongs here: if a user can create
    /// a file "in" it, it must have a real directory, so it must be projected.
    /// </summary>
    public sealed class TreeLayoutProjector
    {
        /// <summary>
        /// The spine as the tree WILL draw it — project nodes hanging directly off the solution
        /// node under their declared names. Compare against <see cref="DiskLayoutScanner"/>'s
        /// result to get the report.
        /// </summary>
        public StructureLayout Project(SolutionModel solution)
        {
            var layout = new StructureLayout();
            if (solution == null) return layout;
            string root = StructurePath.Canonical(solution.SolutionDirectory);
            if (string.IsNullOrEmpty(root)) return layout;

            // The solution node stands for the solution directory AND its root manifest —
            // double-clicking the row opens the pyproject.toml (SRS §5.5), so the manifest is
            // represented, not missing.
            AddIfInside(layout, root, solution.FilePath);

            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null || string.IsNullOrEmpty(project.Name)) continue;
                // "" for a member rooted at the solution directory, whose node stands for that
                // directory rather than for a folder of its own — see ProjectNodePath. AddDirectory
                // ignores "", so no phantom root entry is projected.
                string nodeDirectory = ProjectNodePath.Declared(project, root);
                layout.AddDirectory(nodeDirectory);

                // Same convention one level down: the project node stands for the member
                // directory and its own manifest. Taken from FilePath rather than hardcoded as
                // "pyproject.toml" so it agrees with the file StructureInventory skips for the
                // same reason — a hardcoded name that did not match the real one would claim a
                // Phantom manifest and orphan the real one, in one stroke.
                string manifest = System.IO.Path.GetFileName(project.FilePath ?? string.Empty);
                if (manifest.Length > 0) layout.AddFile(StructurePath.Combine(nodeDirectory, manifest));

                // The real contents, rebased from the member's ACTUAL directory onto its DECLARED
                // node — the same rebasing StructureParityChecker.DiskToNode applies from the disk
                // side, so the two meet whatever the anchor defects are.
                foreach (string relative in project.ContentFiles)
                    layout.AddFile(StructurePath.Combine(nodeDirectory, relative));

                // Explicitly, because an EMPTY directory has no file to imply it.
                foreach (string relative in project.ContentDirectories)
                    layout.AddDirectory(StructurePath.Combine(nodeDirectory, relative));

                // Symbol- and resource-derived paths on top of the inventory, deliberately: this is
                // where the check keeps its teeth. If the model claims a source file the inventory
                // did not find, the union projects it and the disk side reports it as a Phantom
                // instead of the two silently cancelling out. It is also the fallback for a model
                // built by a loader that never ran the inventory.
                foreach (string relative in ProjectRelativeFiles(project))
                    layout.AddFile(StructurePath.Combine(nodeDirectory, relative));
            }

            // Real files under the solution root that no member owns (README.md, pytest.ini, …),
            // drawn under `Solution Items`, and real directories that are neither a member nor
            // inside one (results/, artifacts/). Both are INSIDE the contract: they are real paths
            // at real depths that a user can create a file next to, which is the single test for
            // contract membership.
            foreach (string item in solution.SolutionItems)
                layout.AddFile(item);
            foreach (string directory in solution.ContentDirectories)
                layout.AddDirectory(directory);

            return layout;
        }

        /// <summary>
        /// Every file the tree will show under one project node, as paths relative to the project
        /// node itself. Deduplicated by <see cref="StructureLayout"/>, so a module contributing
        /// both a type and a module-level function is one file either way.
        /// </summary>
        private static IEnumerable<string> ProjectRelativeFiles(ProjectModel project)
        {
            string projectDirectory = StructurePath.Canonical(project.ProjectDirectory);
            var results = new List<string>();

            foreach (TypeModel type in project.Types)
                AddRelative(results, projectDirectory, type == null ? null : type.SourceFilePath);

            foreach (FunctionModel function in project.Functions)
                AddRelative(results, projectDirectory, function == null ? null : function.SourceFilePath);

            // ResourceFiles are already project-relative (the model's own contract) — they are the
            // non-source files that ship inside the package, rendered under the "Resources" group.
            foreach (string resource in project.ResourceFiles)
            {
                if (string.IsNullOrEmpty(resource)) continue;
                results.Add(StructurePath.Normalize(resource));
            }
            return results;
        }

        private static void AddRelative(List<string> results, string projectDirectory, string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return;
            string relative = StructurePath.Relative(projectDirectory, absolutePath);
            // Null means the model claims a source file that is not inside its own project
            // directory. That is a real defect, but it is the CHECKER's to report against the disk
            // side — inventing a projected path for it here would only hide it.
            if (!string.IsNullOrEmpty(relative)) results.Add(relative);
        }

        private static void AddIfInside(StructureLayout layout, string root, string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return;
            string relative = StructurePath.Relative(root, absolutePath);
            if (!string.IsNullOrEmpty(relative)) layout.AddFile(relative);
        }
    }
}
