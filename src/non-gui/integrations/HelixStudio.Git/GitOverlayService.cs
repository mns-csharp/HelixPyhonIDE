namespace HelixPythonNamespace.Git
{
    using System.Collections.Generic;

    /// <summary>Per-repo tracked/dirty sets for the project-tree overlay. Port of the C++ <c>GitOverlayPerRepo</c>.</summary>
    public sealed class GitOverlayPerRepo
    {
        public bool InRepo;
        public string RepoWorkdir = "";
        public HashSet<string> Tracked = new HashSet<string>(System.StringComparer.Ordinal);
        public HashSet<string> Dirty = new HashSet<string>(System.StringComparer.Ordinal);
    }

    public sealed class GitOverlaySnapshot
    {
        public int Generation;
        public List<string> ProjectDirectories = new List<string>();
    }

    public sealed class GitOverlayResult
    {
        public int Generation;
        public Dictionary<string, GitOverlayPerRepo> ByProjectDir = new Dictionary<string, GitOverlayPerRepo>(System.StringComparer.Ordinal);
        public bool Ok = true;
        public string ErrorMessage = "";
    }

    /// <summary>
    /// Computes git tracked/dirty overlay data for a set of project directories. The GUI
    /// thread consumes the result to paint tree-row glyphs. Port of the engine portion of
    /// the C++ <c>GitOverlayService</c> (the Qt-item icon walk stays on the GUI side).
    /// </summary>
    public static class GitOverlayService
    {
        public static GitOverlayResult Compute(GitOverlaySnapshot snapshot)
        {
            GitOverlayResult result = new GitOverlayResult { Generation = snapshot.Generation };

            // Resolve each project dir to its repo workdir once, dedup the heavy work by workdir.
            Dictionary<string, GitOverlayPerRepo> byWorkdir = new Dictionary<string, GitOverlayPerRepo>(System.StringComparer.Ordinal);
            foreach (string projDir in snapshot.ProjectDirectories)
            {
                string workdir = GitService.RepoWorkdir(projDir);
                GitOverlayPerRepo per;
                if (string.IsNullOrEmpty(workdir))
                {
                    per = new GitOverlayPerRepo { InRepo = false };
                }
                else if (!byWorkdir.TryGetValue(workdir, out per))
                {
                    per = new GitOverlayPerRepo { InRepo = true, RepoWorkdir = workdir };
                    per.Tracked = GitService.ListTrackedFiles(workdir);
                    foreach (GitStatusEntry s in GitService.Status(workdir))
                    {
                        if (s.WorkingStatus == GitFileStatus.Ignored) continue;
                        if (s.IndexStatus != GitFileStatus.Unmodified || s.WorkingStatus != GitFileStatus.Unmodified)
                            per.Dirty.Add(s.FilePath);
                    }
                    byWorkdir[workdir] = per;
                }
                result.ByProjectDir[projDir] = per;
            }
            return result;
        }
    }
}
