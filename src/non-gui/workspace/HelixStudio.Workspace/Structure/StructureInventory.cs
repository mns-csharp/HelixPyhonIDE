using System;
using System.Collections.Generic;
using System.Threading;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Fills the model's record of what is REALLY on disk —
    /// <see cref="ProjectModel.ContentFiles"/>/<see cref="ProjectModel.ContentDirectories"/> and
    /// <see cref="SolutionModel.SolutionItems"/>/<see cref="SolutionModel.ContentDirectories"/> —
    /// from one member-aware scan, run by the loader before the parity check.
    ///
    /// <b>Why this class exists.</b> The Solution Explorer used to derive its file spine entirely
    /// from SYMBOLS: a file appeared because it contributed a type, a module-level function, or a
    /// non-Python resource. A file that contributed none of those had no node — so every
    /// symbol-free <c>__init__.py</c>, every root <c>README.md</c>, and every output directory was
    /// invisible in the tree while sitting plainly in Explorer. The parity checker was right to
    /// call each of them an Orphan, and the repair engine could do nothing about any of them,
    /// because the repair for an Orphan is "render it" and nothing rendered. On the motivating
    /// solution that left FOURTEEN defects standing after a repair the engine reported as
    /// successful, with Compile/Run/Debug blocked and no prompt the user could accept.
    ///
    /// <b>One scan, two consumers.</b> The renderer draws these lists and
    /// <see cref="TreeLayoutProjector"/> projects them. That is deliberate and is the only way the
    /// projection can be honest: a projector that guessed at the disk independently could claim a
    /// node the panel does not draw, which would turn a loud PY8004 failure into a silent one —
    /// the tree would look right and disagree with disk anyway, which is exactly the state the
    /// motivating solution was in for its entire life.
    ///
    /// <b>Still checkable.</b> Projecting the model rather than the disk keeps the check headless
    /// and keeps its teeth: if the loader fails to inventory something, or a file lands after the
    /// scan, the disk side and the model side disagree and the report says so. The check is not
    /// made vacuous by sharing a source — it is made a check of the LOADER, which is what it
    /// should have been measuring all along.
    /// </summary>
    public sealed class StructureInventory
    {
        /// <summary>
        /// Scans <paramref name="solution"/>'s directory and distributes every non-excluded real
        /// path to its owning member, or to the solution when no member owns it. Clears the lists
        /// first, so a re-load never accumulates stale entries from the previous one.
        /// </summary>
        public static void Populate(SolutionModel solution)
        {
            Populate(solution, CancellationToken.None);
        }

        public static void Populate(SolutionModel solution, CancellationToken cancel)
        {
            if (solution == null) return;

            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null) continue;
                project.ContentFiles.Clear();
                project.ContentDirectories.Clear();
            }
            solution.SolutionItems.Clear();
            solution.ContentDirectories.Clear();

            string root = StructurePath.Canonical(solution.SolutionDirectory);
            if (string.IsNullOrEmpty(root)) return;

            List<Owner> owners = BuildOwners(solution, root);
            var scanner = new DiskLayoutScanner { ExcludedRelativePaths = ExcludedPaths(owners) };
            StructureLayout disk = scanner.Scan(root, cancel);

            // The solution node stands for the solution directory AND its root manifest, and each
            // project node stands for its member directory AND that member's own pyproject.toml
            // (SRS §5.5 — double-clicking the row opens it). Those two files are therefore already
            // represented by an ancestor node; listing them again would give one file two nodes,
            // which breaks the isomorphism in the other direction.
            string rootManifest = StructurePath.Relative(root, solution.FilePath);

            foreach (string file in disk.Files)
            {
                cancel.ThrowIfCancellationRequested();
                if (rootManifest != null && StructurePath.AreSame(file, rootManifest)) continue;

                Owner owner = OwnerOf(file, owners);
                if (owner == null) { solution.SolutionItems.Add(file); continue; }
                if (StructurePath.AreSame(file, owner.ManifestPath)) continue;
                owner.Project.ContentFiles.Add(Rebase(file, owner));
            }

            foreach (string directory in disk.Directories)
            {
                cancel.ThrowIfCancellationRequested();
                Owner owner = OwnerOf(directory, owners);
                if (owner == null) { solution.ContentDirectories.Add(directory); continue; }
                // The member's own directory is the project node; it is not content within itself.
                if (StructurePath.AreSame(directory, owner.Directory)) continue;
                owner.Project.ContentDirectories.Add(Rebase(directory, owner));
            }
        }

        // ---- ownership -------------------------------------------------------------------------

        private sealed class Owner
        {
            public ProjectModel Project;
            public string Directory;        // solution-relative; "" when the member IS the root
            public string ManifestPath;     // solution-relative
        }

        /// <summary>
        /// Members with a directory inside the solution, longest path first so a member nested
        /// inside another is matched by the deepest owner — the same longest-prefix rule
        /// <see cref="StructureParityChecker"/> and SolutionPolicy's rule 3 apply.
        /// </summary>
        private static List<Owner> BuildOwners(SolutionModel solution, string root)
        {
            var owners = new List<Owner>();
            foreach (ProjectModel project in solution.Projects)
            {
                if (project == null) continue;
                string directory = StructurePath.Relative(root, project.ProjectDirectory);
                if (directory == null) continue;   // outside the solution: the checker reports it
                owners.Add(new Owner
                {
                    Project = project,
                    Directory = directory,
                    ManifestPath = StructurePath.Relative(root, project.FilePath),
                });
            }
            owners.Sort((a, b) => b.Directory.Length.CompareTo(a.Directory.Length));
            return owners;
        }

        private static Owner OwnerOf(string relativePath, List<Owner> owners)
        {
            foreach (Owner owner in owners)   // longest directory first
            {
                if (owner.Directory.Length == 0) return owner;   // a member rooted at the solution
                if (StructurePath.AreSame(relativePath, owner.Directory)) return owner;
                if (StructurePath.IsUnder(relativePath, owner.Directory)) return owner;
            }
            return null;
        }

        private static string Rebase(string relativePath, Owner owner)
        {
            if (owner.Directory.Length == 0) return StructurePath.Normalize(relativePath);
            return StructurePath.Normalize(relativePath).Substring(owner.Directory.Length + 1);
        }

        private static List<string> ExcludedPaths(List<Owner> owners)
        {
            var excluded = new List<string>();
            foreach (Owner owner in owners)
            {
                if (owner.Project.ExcludedPaths == null) continue;
                foreach (string relative in owner.Project.ExcludedPaths)
                {
                    if (string.IsNullOrEmpty(relative)) continue;
                    excluded.Add(StructurePath.Combine(owner.Directory, relative));
                }
            }
            return excluded;
        }
    }
}
