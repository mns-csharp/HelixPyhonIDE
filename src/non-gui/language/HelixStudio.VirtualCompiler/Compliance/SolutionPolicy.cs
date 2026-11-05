using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.VirtualCompiler.Compliance
{
    /// <summary>
    /// The solution-wide rules the virtual compiler enforces as hard blockers, and the context it
    /// needs to apply them.
    ///
    /// The rules exist for the same reason: a user must be in full control of the code their
    /// solution executes, and of how the IDE represents it. Source that is not a workspace member
    /// cannot be browsed, compile-checked, or breakpointed, yet can still be executed through a
    /// runtime <c>sys.path</c> insertion — so the IDE would be running code it cannot show you.
    /// Procedural source defeats the structural views (type/member trees, hierarchy, designers)
    /// that the IDE is built around. And a package that ships its own tests cannot be built,
    /// packaged or referenced apart from its test harness, so tests belong in a project of their
    /// own — the sibling test project the solution model already prescribes.
    ///
    /// Third-party packages are exempt from all of them. They are not the user's code, the user
    /// cannot restructure them, and forcing them to comply would make ordinary dependencies
    /// unusable.
    /// </summary>
    public sealed class SolutionPolicy
    {
        /// <summary>Directories of the solution's workspace member projects.</summary>
        public IReadOnlyList<string> MemberDirectories { get; }

        /// <summary>The solution root; files outside it are not the user's source.</summary>
        public string SolutionRoot { get; }

        /// <summary>Rule 1: every user-written file must belong to a member project.</summary>
        public bool EnforceWorkspaceMembership { get; set; } = true;

        /// <summary>Rule 2: user-written code must be object-oriented.</summary>
        public bool EnforceObjectOrientation { get; set; } = true;

        /// <summary>Rule 3: only a test project may contain test files.</summary>
        public bool EnforceTestFilePlacement { get; set; } = true;

        /// <summary>
        /// Rule 5: this file is open on its own, outside any solution, and may import installed
        /// packages only. Set by <see cref="ForSingleFile"/>, which also clears rule 1 — the two
        /// are the same requirement asked of two different contexts, and a lone file has no
        /// workspace to be a member of, so reporting both would be reporting one fault twice and
        /// naming two different fixes for it.
        /// </summary>
        public bool EnforceSingleFileImports { get; set; }

        /// <summary>
        /// The policy for a file opened OUTSIDE any solution.
        ///
        /// <para>
        /// Rules 2 and 3 stay on: they are claims about the code itself, and the code does not stop
        /// being the user's because no solution is open. Rule 1 goes off and rule 5 takes its place.
        /// Rule 4 (tree/disk parity) has no meaning here — there is no tree.
        /// </para>
        /// <para>
        /// <b>The solution root is the file's own directory, and that is not incidental.</b>
        /// <see cref="IsUserWritten"/> treats everything under the root as the user's code, so
        /// scoping it to the one directory is what keeps a script opened from inside a
        /// <c>site-packages</c> or <c>vendor</c> tree exempt via <see cref="ThirdPartyDetector"/>,
        /// while a script anywhere else is policed.
        /// </para>
        /// </summary>
        public static SolutionPolicy ForSingleFile(string filePath)
        {
            string dir = null;
            if (!string.IsNullOrEmpty(filePath))
            {
                try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
                catch { dir = null; }
            }

            return new SolutionPolicy(dir, null)
            {
                EnforceWorkspaceMembership = false,
                EnforceSingleFileImports = true,
            };
        }

        // Kind per member directory. Absent means "unknown", which resolves to ClassLibrary —
        // the same tolerant default ProjectKinds.FromToken applies to a missing TOML token, and
        // the safe one here: an unknown project is policed, not exempted.
        // Keyed by path IDENTITY, so the comparer follows the filesystem — see
        // HelixPythonNamespace.Core.IO.PathComparison. On a case-sensitive volume `projects/Calc` and
        // `projects/calc` are different projects and must not collide in this map.
        private readonly Dictionary<string, ProjectKind> _kindsByDirectory =
            new Dictionary<string, ProjectKind>(HelixPythonNamespace.Core.IO.PathComparison.Comparer);

        public SolutionPolicy(string solutionRoot, IEnumerable<string> memberDirectories)
            : this(solutionRoot, memberDirectories, null)
        {
        }

        /// <param name="projectKinds">
        /// The kind of each member directory, so rule 3 can tell a test project (the one kind
        /// allowed to hold test files) from every other kind, and can name the offending kind in
        /// the message. Directories not listed default to <see cref="ProjectKind.ClassLibrary"/>.
        /// </param>
        public SolutionPolicy(string solutionRoot, IEnumerable<string> memberDirectories,
            IEnumerable<KeyValuePair<string, ProjectKind>> projectKinds)
        {
            SolutionRoot = Normalize(solutionRoot);
            MemberDirectories = NormalizeAll(memberDirectories);
            if (projectKinds != null)
            {
                foreach (KeyValuePair<string, ProjectKind> pair in projectKinds)
                {
                    string dir = Normalize(pair.Key);
                    if (string.IsNullOrEmpty(dir)) continue;
                    _kindsByDirectory[HelixPythonNamespace.Core.IO.PathComparison.TrimTrailing(dir)] = pair.Value;
                }
            }
        }

        /// <summary>The declared kind of the project rooted at <paramref name="directory"/>.</summary>
        public ProjectKind GetProjectKind(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return ProjectKind.ClassLibrary;
            string full = Normalize(directory);
            if (string.IsNullOrEmpty(full)) return ProjectKind.ClassLibrary;
            ProjectKind kind;
            return _kindsByDirectory.TryGetValue(
                    HelixPythonNamespace.Core.IO.PathComparison.TrimTrailing(full), out kind)
                ? kind : ProjectKind.ClassLibrary;
        }

        /// <summary>
        /// True when <paramref name="filePath"/> is source the user is responsible for: inside the
        /// solution, and not a third-party dependency or generated artifact.
        /// </summary>
        public bool IsUserWritten(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string full = Normalize(filePath);
            if (string.IsNullOrEmpty(full)) return false;
            if (ThirdPartyDetector.IsThirdParty(full)) return false;
            if (string.IsNullOrEmpty(SolutionRoot)) return true;
            return HelixPythonNamespace.Core.IO.PathComparison.IsUnderDirectory(full, SolutionRoot);
        }

        /// <summary>True when the file lives inside one of the workspace member projects.</summary>
        public bool IsWorkspaceMember(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string full = Normalize(filePath);
            foreach (string dir in MemberDirectories)
                if (HelixPythonNamespace.Core.IO.PathComparison.IsUnderDirectory(full, dir)) return true;
            return false;
        }

        /// <summary>
        /// The member project that owns <paramref name="filePath"/>, or null when no member does.
        ///
        /// Deliberately the LONGEST matching member directory: projects nest (a workspace root
        /// that is itself a project, with members beneath it), and a file inside a nested member
        /// belongs to that member, not to the ancestor that also contains it. Getting this wrong
        /// would blame the wrong project — and, worse, would let a test file inside a nested
        /// non-test member pass because some ancestor happened to be a test project.
        /// </summary>
        public string FindOwningProjectDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string full = Normalize(filePath);
            if (string.IsNullOrEmpty(full)) return null;

            string best = null;
            foreach (string dir in MemberDirectories)
            {
                if (!HelixPythonNamespace.Core.IO.PathComparison.IsUnderDirectory(full, dir)) continue;
                string trimmed = HelixPythonNamespace.Core.IO.PathComparison.TrimTrailing(dir);
                if (best == null || trimmed.Length > best.Length) best = trimmed;
            }
            return best;
        }

        /// <summary>True when <paramref name="directory"/> is one of the solution's test projects.</summary>
        public bool IsTestProjectDirectory(string directory)
        {
            return ProjectKinds.IsTest(GetProjectKind(directory));
        }

        private static IReadOnlyList<string> NormalizeAll(IEnumerable<string> paths)
        {
            List<string> dirs = new List<string>();
            if (paths != null)
            {
                foreach (string d in paths)
                {
                    string full = Normalize(d);
                    if (!string.IsNullOrEmpty(full)) dirs.Add(full);
                }
            }
            return dirs;
        }

        private static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try { return Path.GetFullPath(p); }
            catch { return null; }
        }
    }
}
