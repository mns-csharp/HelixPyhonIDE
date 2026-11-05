namespace HelixPythonNamespace.Git
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using LibGit2Sharp;

    /// <summary>
    /// Embedded git operations backed by LibGit2Sharp (no external <c>git.exe</c>). All
    /// methods are stateless; the caller passes the repo working-tree directory. Network
    /// ops take an optional credential prompt; null fails closed on auth-demanding remotes.
    /// Port of the C++ <c>GitService</c> (libgit2 → LibGit2Sharp).
    /// </summary>
    public static class GitService
    {
        // ---- discovery ----

        public static bool IsRepo(string dir)
        {
            try { return Repository.Discover(dir) != null; }
            catch { return false; }
        }

        public static string RepoWorkdir(string dir)
        {
            try
            {
                string gitDir = Repository.Discover(dir);
                if (gitDir == null) return "";
                using (Repository repo = new Repository(gitDir))
                {
                    string wd = repo.Info.WorkingDirectory;
                    if (string.IsNullOrEmpty(wd)) return "";
                    return wd.TrimEnd('/', '\\');
                }
            }
            catch { return ""; }
        }

        // ---- branches / remotes ----

        public static string CurrentBranch(string repoDir)
        {
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return "";
                if (repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn) return "";
                return repo.Head != null ? repo.Head.FriendlyName : "";
            }
        }

        public static List<GitBranch> ListBranches(string repoDir)
        {
            List<GitBranch> outList = new List<GitBranch>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                foreach (Branch b in repo.Branches)
                {
                    if (b.IsRemote) continue;
                    outList.Add(new GitBranch
                    {
                        Name = b.FriendlyName,
                        Current = b.IsCurrentRepositoryHead,
                        Upstream = b.TrackedBranch != null ? b.TrackedBranch.FriendlyName : "",
                    });
                }
            }
            return outList;
        }

        public static List<GitRemote> ListRemotes(string repoDir)
        {
            List<GitRemote> outList = new List<GitRemote>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                foreach (Remote r in repo.Network.Remotes)
                    outList.Add(new GitRemote { Name = r.Name, Url = r.Url });
            }
            return outList;
        }

        // ---- status ----

        public static List<GitStatusEntry> Status(string repoDir)
        {
            List<GitStatusEntry> outList = new List<GitStatusEntry>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                StatusOptions opts = new StatusOptions
                {
                    Show = StatusShowOption.IndexAndWorkDir,
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                    DetectRenamesInIndex = true,
                    DetectRenamesInWorkDir = true,
                };
                foreach (StatusEntry e in repo.RetrieveStatus(opts))
                {
                    if (e.State == FileStatus.Unaltered) continue;
                    GitStatusEntry s = new GitStatusEntry
                    {
                        FilePath = e.FilePath,
                        IndexStatus = IndexFlagToStatus(e.State),
                        WorkingStatus = WorkdirFlagToStatus(e.State),
                    };
                    if (e.IndexToWorkDirRenameDetails != null) s.RenameSource = e.IndexToWorkDirRenameDetails.OldFilePath;
                    else if (e.HeadToIndexRenameDetails != null) s.RenameSource = e.HeadToIndexRenameDetails.OldFilePath;
                    outList.Add(s);
                }
            }
            return outList;
        }

        public static HashSet<string> ListTrackedFiles(string repoDir)
        {
            HashSet<string> outSet = new HashSet<string>(StringComparer.Ordinal);
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outSet;
                foreach (IndexEntry entry in repo.Index) outSet.Add(entry.Path);
            }
            return outSet;
        }

        // ---- diff / history / blame ----

        public static string DiffFile(string repoDir, string relPath, bool staged = false)
        {
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return "";
                string[] paths = { relPath };
                try
                {
                    Patch patch;
                    if (staged)
                    {
                        if (repo.Info.IsHeadUnborn || repo.Head.Tip == null) return "";
                        patch = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.Index, paths);
                    }
                    else
                    {
                        patch = repo.Diff.Compare<Patch>(paths);
                    }
                    return patch != null ? patch.Content : "";
                }
                catch { return ""; }
            }
        }

        public static List<GitCommitEntry> FileHistory(string repoDir, string relPath, int limit = 100)
        {
            List<GitCommitEntry> outList = new List<GitCommitEntry>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                try
                {
                    foreach (LogEntry le in repo.Commits.QueryBy(relPath))
                    {
                        if (outList.Count >= limit) break;
                        Commit c = le.Commit;
                        outList.Add(ToCommitEntry(c));
                    }
                }
                catch { }
            }
            return outList;
        }

        public static List<GitBlameEntry> Blame(string repoDir, string relPath)
        {
            List<GitBlameEntry> outList = new List<GitBlameEntry>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                try
                {
                    foreach (BlameHunk h in repo.Blame(relPath))
                    {
                        GitBlameEntry e = new GitBlameEntry
                        {
                            StartLine = h.FinalStartLineNumber + 1, // LibGit2Sharp is 0-based; C++ is 1-based
                            LineCount = h.LineCount,
                            CommitHash = h.FinalCommit != null ? h.FinalCommit.Sha : "",
                        };
                        if (h.FinalSignature != null)
                        {
                            e.Author = h.FinalSignature.Name ?? "";
                            e.Timestamp = h.FinalSignature.When.ToUnixTimeSeconds();
                        }
                        outList.Add(e);
                    }
                }
                catch { }
            }
            return outList;
        }

        // ---- staging / commit / checkout ----

        public static bool Add(string repoDir, IEnumerable<string> relPaths, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try { Commands.Stage(repo, relPaths); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Reset(string repoDir, IEnumerable<string> relPaths, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try { Commands.Unstage(repo, relPaths); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Commit(string repoDir, string message, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try
                {
                    Signature sig = DefaultSignature(repo);
                    repo.Commit(message, sig, sig);
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Checkout(string repoDir, string branchName, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                Branch b = repo.Branches[branchName];
                if (b == null) { errorOut = "no such branch: " + branchName; return false; }
                try { Commands.Checkout(repo, b); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool RenameBranch(string repoDir, string oldName, string newName, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                if (repo.Branches[oldName] == null) { errorOut = "no such branch: " + oldName; return false; }
                if (repo.Branches[newName] != null) { errorOut = "branch already exists: " + newName; return false; }
                try { repo.Branches.Rename(oldName, newName); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        // ---- init / clone ----

        public static bool Init(string dir, bool bare, out string errorOut)
        {
            errorOut = "";
            if (IsRepo(dir)) { errorOut = "already a repository"; return false; }
            try { Repository.Init(dir, bare); return true; }
            catch (Exception ex) { errorOut = ex.Message; return false; }
        }

        public static bool Clone(string url, string destDir, GitCredentialPrompt prompt, out string errorOut)
        {
            errorOut = "";
            try
            {
                FetchOptions fetchOptions = new FetchOptions
                {
                    CredentialsProvider = MakeCredsHandler(prompt)
                };
                CloneOptions opts = new CloneOptions(fetchOptions);
                Repository.Clone(url, destDir, opts);
                return true;
            }
            catch (Exception ex) { errorOut = ex.Message; return false; }
        }

        // ---- network ----

        public static bool Fetch(string repoDir, string remoteName, GitCredentialPrompt prompt, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                Remote remote = repo.Network.Remotes[remoteName];
                if (remote == null) { errorOut = "no such remote: " + remoteName; return false; }
                try
                {
                    FetchOptions opts = new FetchOptions { CredentialsProvider = MakeCredsHandler(prompt) };
                    IEnumerable<string> refspecs = remote.FetchRefSpecs.Select(r => r.Specification);
                    Commands.Fetch(repo, remoteName, refspecs, opts, null);
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Pull(string repoDir, string remoteName, GitCredentialPrompt prompt, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try
                {
                    Signature sig = DefaultSignature(repo);
                    PullOptions opts = new PullOptions
                    {
                        FetchOptions = new FetchOptions { CredentialsProvider = MakeCredsHandler(prompt) },
                        MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly },
                    };
                    MergeResult res = Commands.Pull(repo, sig, opts);
                    if (res.Status == MergeStatus.Conflicts || res.Status == MergeStatus.NonFastForward)
                    {
                        errorOut = "non-fast-forward; pull aborted";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Push(string repoDir, string remoteName, GitCredentialPrompt prompt, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                Remote remote = repo.Network.Remotes[remoteName];
                if (remote == null) { errorOut = "no such remote: " + remoteName; return false; }
                if (repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn) { errorOut = "no branch checked out"; return false; }
                try
                {
                    Branch branch = repo.Head;
                    string refspec = "refs/heads/" + branch.FriendlyName + ":refs/heads/" + branch.FriendlyName;
                    PushOptions opts = new PushOptions { CredentialsProvider = MakeCredsHandler(prompt) };
                    repo.Network.Push(remote, refspec, opts);
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool AddRemote(string repoDir, string name, string url, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                if (repo.Network.Remotes[name] != null) { errorOut = "remote already exists: " + name; return false; }
                try { repo.Network.Remotes.Add(name, url); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool SetRemoteUrl(string repoDir, string name, string url, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                if (repo.Network.Remotes[name] == null) { errorOut = "no such remote: " + name; return false; }
                try { repo.Network.Remotes.Update(name, u => u.Url = url); return true; }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool SetUpstream(string repoDir, string localBranch, string remoteName, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                Branch b = repo.Branches[localBranch];
                if (b == null) { errorOut = "no such branch: " + localBranch; return false; }
                try
                {
                    repo.Branches.Update(b, u => { u.Remote = remoteName; u.UpstreamBranch = "refs/heads/" + localBranch; });
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        // ---- .gitignore management ----

        private static readonly string[] PythonIgnorePatterns =
        {
            "__pycache__/", "*.py[cod]", "*$py.class", "*.so",
            "build/", "dist/", "*.egg-info/", ".venv/", "venv/",
        };

        public static List<string> EnsurePythonGitIgnore(string repoDir, out string errorOut)
        {
            errorOut = "";
            List<string> added = new List<string>();
            string gi = Path.Combine(repoDir, ".gitignore");

            List<string> existingLines = new List<string>();
            if (File.Exists(gi))
            {
                try { foreach (string line in File.ReadAllLines(gi)) existingLines.Add(TrimRight(line)); }
                catch { errorOut = "could not read existing .gitignore"; return added; }
            }
            HashSet<string> existing = new HashSet<string>(existingLines, StringComparer.Ordinal);

            foreach (string p in PythonIgnorePatterns)
                if (!existing.Contains(p)) added.Add(p);
            if (added.Count == 0) return added;

            try
            {
                using (StreamWriter w = new StreamWriter(gi, append: true))
                {
                    if (existingLines.Count > 0) w.Write("\n");
                    w.Write("# Added by HelixStudio - Python defaults\n");
                    foreach (string p in added) w.Write(p + "\n");
                }
            }
            catch { errorOut = "could not open .gitignore for append"; added.Clear(); return added; }
            return added;
        }

        public static List<string> ListTrackedNowIgnored(string repoDir)
        {
            List<string> outList = new List<string>();
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return outList;
                foreach (IndexEntry entry in repo.Index)
                {
                    try { if (repo.Ignore.IsPathIgnored(entry.Path)) outList.Add(entry.Path); }
                    catch { }
                }
            }
            return outList;
        }

        public static bool UntrackFromIndex(string repoDir, IEnumerable<string> relPaths, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try
                {
                    foreach (string p in relPaths) Commands.Remove(repo, p, removeFromWorkingDirectory: false);
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static List<string> ReadGitIgnore(string repoDir)
        {
            List<string> outList = new List<string>();
            string gi = Path.Combine(repoDir, ".gitignore");
            if (!File.Exists(gi)) return outList;
            try { foreach (string line in File.ReadAllLines(gi)) outList.Add(line.TrimEnd('\r')); }
            catch { }
            return outList;
        }

        public static bool WriteGitIgnore(string repoDir, IEnumerable<string> lines, out string errorOut)
        {
            errorOut = "";
            string gi = Path.Combine(repoDir, ".gitignore");
            try
            {
                using (StreamWriter w = new StreamWriter(gi, append: false))
                {
                    foreach (string l in lines) w.Write(l + "\n");
                }
                return true;
            }
            catch (Exception ex) { errorOut = ex.Message; return false; }
        }

        // ---- lockstep / smart-sync ----

        public static bool HasUpstreamTrackingRef(string repoDir, string localBranch, string remoteName)
        {
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return false;
                Branch rb = repo.Branches[remoteName + "/" + localBranch];
                return rb != null && rb.IsRemote;
            }
        }

        public static bool AheadBehind(string repoDir, string localBranch, string remoteName, GitAheadBehind outAb, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                Branch local = repo.Branches[localBranch];
                if (local == null || local.Tip == null) { errorOut = "no such local branch: " + localBranch; return false; }
                Branch remote = repo.Branches[remoteName + "/" + localBranch];
                if (remote == null || remote.Tip == null) { errorOut = "no remote-tracking ref for " + localBranch; return false; }
                try
                {
                    HistoryDivergence div = repo.ObjectDatabase.CalculateHistoryDivergence(local.Tip, remote.Tip);
                    outAb.Ahead = div.AheadBy ?? 0;
                    outAb.Behind = div.BehindBy ?? 0;
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Stash(string repoDir, string message, out bool createdOut, out string errorOut)
        {
            createdOut = false;
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try
                {
                    Signature sig = DefaultSignature(repo);
                    Stash s = repo.Stashes.Add(sig, message, StashModifiers.Default | StashModifiers.IncludeUntracked);
                    createdOut = s != null;
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool StashPop(string repoDir, out string errorOut)
        {
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                try
                {
                    StashApplyStatus status = repo.Stashes.Pop(0);
                    if (status == StashApplyStatus.Conflicts) { errorOut = "stash pop produced conflicts"; return false; }
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool Rebase(string repoDir, string remoteName, List<string> conflictPaths, out int rewoundLocalOut, out string errorOut)
        {
            rewoundLocalOut = 0;
            errorOut = "";
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) { errorOut = "not a repository"; return false; }
                if (repo.Info.IsHeadDetached || repo.Info.IsHeadUnborn) { errorOut = "HEAD is not on a branch"; return false; }
                Branch branch = repo.Head;
                Branch upstream = repo.Branches[remoteName + "/" + branch.FriendlyName];
                if (upstream == null || upstream.Tip == null) { errorOut = "no remote-tracking ref to rebase onto"; return false; }
                try
                {
                    HistoryDivergence div = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, upstream.Tip);
                    rewoundLocalOut = div.AheadBy ?? 0;

                    Signature sig = DefaultSignature(repo);
                    Identity ident = new Identity(sig.Name, sig.Email);
                    RebaseResult res = repo.Rebase.Start(branch, upstream, null, ident, new RebaseOptions());
                    if (res.Status == RebaseStatus.Conflicts)
                    {
                        foreach (Conflict c in repo.Index.Conflicts)
                        {
                            string p = c.Ours != null ? c.Ours.Path : (c.Theirs != null ? c.Theirs.Path : null);
                            if (!string.IsNullOrEmpty(p) && !conflictPaths.Contains(p)) conflictPaths.Add(p);
                        }
                        repo.Rebase.Abort();
                    }
                    return true;
                }
                catch (Exception ex) { errorOut = ex.Message; return false; }
            }
        }

        public static bool SmartSync(string repoDir, string remoteName, GitCredentialPrompt prompt, GitSyncOutcome outcome)
        {
            // 1. Fetch.
            string err;
            if (!Fetch(repoDir, remoteName, prompt, out err))
            {
                outcome.Result = GitSyncOutcome.SyncResult.Error;
                outcome.ErrorMessage = "fetch failed: " + err;
                return false;
            }

            string branch = CurrentBranch(repoDir);
            if (string.IsNullOrEmpty(branch))
            {
                outcome.Result = GitSyncOutcome.SyncResult.Error;
                outcome.ErrorMessage = "HEAD is detached — cannot sync";
                return false;
            }

            GitAheadBehind ab = new GitAheadBehind();
            if (!AheadBehind(repoDir, branch, remoteName, ab, out err))
            {
                outcome.Result = GitSyncOutcome.SyncResult.Error;
                outcome.ErrorMessage = "ahead/behind probe failed: " + err;
                return false;
            }
            outcome.AbsorbedRemote = ab.Behind;

            if (ab.Behind == 0)
            {
                outcome.Result = GitSyncOutcome.SyncResult.UpToDate;
                return true;
            }

            bool needStash = RepoIsDirty(repoDir);
            if (needStash)
            {
                bool created;
                if (!Stash(repoDir, "helix SmartSync auto-stash", out created, out err))
                {
                    outcome.Result = GitSyncOutcome.SyncResult.Error;
                    outcome.ErrorMessage = "auto-stash failed: " + err;
                    return false;
                }
                outcome.StashCreated = created;
            }

            if (ab.Ahead == 0)
            {
                if (!Pull(repoDir, remoteName, prompt, out err))
                {
                    outcome.Result = GitSyncOutcome.SyncResult.Error;
                    outcome.ErrorMessage = "fast-forward failed: " + err;
                    RestoreStash(repoDir, outcome);
                    return false;
                }
                outcome.Result = GitSyncOutcome.SyncResult.FastForwarded;
                RestoreStash(repoDir, outcome);
                return true;
            }

            List<string> conflicts = new List<string>();
            int rewound;
            bool ok = Rebase(repoDir, remoteName, conflicts, out rewound, out err);
            if (!ok)
            {
                outcome.Result = GitSyncOutcome.SyncResult.Error;
                outcome.ErrorMessage = "rebase failed: " + err;
                RestoreStash(repoDir, outcome);
                return false;
            }
            if (conflicts.Count > 0)
            {
                outcome.Result = GitSyncOutcome.SyncResult.Conflicts;
                outcome.ConflictPaths = conflicts;
                RestoreStash(repoDir, outcome);
                return true;
            }
            outcome.Result = GitSyncOutcome.SyncResult.Rebased;
            outcome.RewoundLocal = rewound;
            RestoreStash(repoDir, outcome);
            return true;
        }

        // ---- helpers ----

        private static void RestoreStash(string repoDir, GitSyncOutcome outcome)
        {
            if (!outcome.StashCreated) return;
            string err;
            if (StashPop(repoDir, out err)) outcome.StashRestored = true;
            else if (string.IsNullOrEmpty(outcome.ErrorMessage)) outcome.ErrorMessage = "stash pop failed: " + err;
        }

        private static bool RepoIsDirty(string repoDir)
        {
            using (Repository repo = Open(repoDir))
            {
                if (repo == null) return false;
                RepositoryStatus st = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });
                return st.IsDirty;
            }
        }

        private static Repository Open(string dir)
        {
            try
            {
                string gitDir = Repository.Discover(dir);
                if (gitDir == null) return null;
                return new Repository(gitDir);
            }
            catch { return null; }
        }

        private static GitCommitEntry ToCommitEntry(Commit c)
        {
            return new GitCommitEntry
            {
                Hash = c.Sha,
                ShortHash = c.Sha.Length >= 7 ? c.Sha.Substring(0, 7) : c.Sha,
                Author = c.Author != null ? c.Author.Name : "",
                Subject = c.MessageShort ?? "",
                Timestamp = c.Author != null ? c.Author.When.ToUnixTimeSeconds() : 0,
            };
        }

        private static Signature DefaultSignature(Repository repo)
        {
            Signature sig = repo.Config.BuildSignature(DateTimeOffset.Now);
            if (sig != null) return sig;
            return new Signature("HelixStudio", "helix@localhost", DateTimeOffset.Now);
        }

        private static LibGit2Sharp.Handlers.CredentialsHandler MakeCredsHandler(GitCredentialPrompt prompt)
        {
            if (prompt == null) return null;
            return (url, usernameFromUrl, types) =>
            {
                GitCredentials creds = prompt(url, usernameFromUrl ?? "");
                if (creds == null) throw new LibGit2SharpException("credentials prompt cancelled");
                return new UsernamePasswordCredentials { Username = creds.Username, Password = creds.Password };
            };
        }

        private static GitFileStatus IndexFlagToStatus(FileStatus f)
        {
            if ((f & FileStatus.NewInIndex) != 0) return GitFileStatus.Added;
            if ((f & FileStatus.ModifiedInIndex) != 0) return GitFileStatus.Modified;
            if ((f & FileStatus.DeletedFromIndex) != 0) return GitFileStatus.Deleted;
            if ((f & FileStatus.RenamedInIndex) != 0) return GitFileStatus.Renamed;
            if ((f & FileStatus.TypeChangeInIndex) != 0) return GitFileStatus.Modified;
            if ((f & FileStatus.Conflicted) != 0) return GitFileStatus.Conflict;
            return GitFileStatus.Unmodified;
        }

        private static GitFileStatus WorkdirFlagToStatus(FileStatus f)
        {
            if ((f & FileStatus.NewInWorkdir) != 0) return GitFileStatus.Untracked;
            if ((f & FileStatus.ModifiedInWorkdir) != 0) return GitFileStatus.Modified;
            if ((f & FileStatus.DeletedFromWorkdir) != 0) return GitFileStatus.Deleted;
            if ((f & FileStatus.RenamedInWorkdir) != 0) return GitFileStatus.Renamed;
            if ((f & FileStatus.TypeChangeInWorkdir) != 0) return GitFileStatus.Modified;
            if ((f & FileStatus.Ignored) != 0) return GitFileStatus.Ignored;
            if ((f & FileStatus.Conflicted) != 0) return GitFileStatus.Conflict;
            return GitFileStatus.Unmodified;
        }

        private static string TrimRight(string s)
        {
            int end = s.Length;
            while (end > 0 && (s[end - 1] == '\r' || s[end - 1] == '\n' || s[end - 1] == ' ' || s[end - 1] == '\t')) end--;
            return end == s.Length ? s : s.Substring(0, end);
        }
    }
}
