namespace HelixPythonNamespace.Git.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Git;

    [TestClass]
    public class GitServiceTests
    {
        private readonly List<string> _temps = new List<string>();

        private string NewDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "helix_git_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            _temps.Add(d);
            return d;
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string d in _temps)
            {
                try { ForceDeleteDir(d); } catch { }
            }
        }

        private static void ForceDeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(dir, true);
        }

        private static void CommitFile(string repo, string name, string content, string msg)
        {
            File.WriteAllText(Path.Combine(repo, name), content);
            string err;
            Assert.IsTrue(GitService.Add(repo, new[] { name }, out err), "Add failed: " + err);
            Assert.IsTrue(GitService.Commit(repo, msg, out err), "Commit failed: " + err);
        }

        private string InitRepoWithCommit()
        {
            string dir = NewDir();
            string err;
            Assert.IsTrue(GitService.Init(dir, false, out err), "Init failed: " + err);
            CommitFile(dir, "a.txt", "hello\n", "initial");
            return dir;
        }

        // ---- local ----

        [TestMethod]
        public void InitAndIsRepo()
        {
            string dir = NewDir();
            Assert.IsFalse(GitService.IsRepo(dir));
            string err;
            Assert.IsTrue(GitService.Init(dir, false, out err));
            Assert.IsTrue(GitService.IsRepo(dir));
            Assert.IsFalse(GitService.Init(dir, false, out err)); // already a repo
            Assert.IsTrue(GitService.RepoWorkdir(dir).Length > 0);
        }

        [TestMethod]
        public void StatusReflectsLifecycle()
        {
            string dir = NewDir();
            string err;
            GitService.Init(dir, false, out err);

            File.WriteAllText(Path.Combine(dir, "f.py"), "x = 1\n");
            List<GitStatusEntry> st = GitService.Status(dir);
            Assert.AreEqual(1, st.Count);
            Assert.AreEqual(GitFileStatus.Untracked, st[0].WorkingStatus);

            Assert.IsTrue(GitService.Add(dir, new[] { "f.py" }, out err));
            st = GitService.Status(dir);
            Assert.AreEqual(GitFileStatus.Added, st[0].IndexStatus);

            Assert.IsTrue(GitService.Commit(dir, "add f", out err));
            Assert.AreEqual(0, GitService.Status(dir).Count);

            File.WriteAllText(Path.Combine(dir, "f.py"), "x = 2\n");
            st = GitService.Status(dir);
            Assert.AreEqual(GitFileStatus.Modified, st[0].WorkingStatus);
        }

        [TestMethod]
        public void AddThenResetUnstages()
        {
            string dir = NewDir();
            string err;
            GitService.Init(dir, false, out err);
            CommitFile(dir, "a.txt", "1\n", "init");
            File.WriteAllText(Path.Combine(dir, "a.txt"), "2\n");
            Assert.IsTrue(GitService.Add(dir, new[] { "a.txt" }, out err));
            Assert.AreEqual(GitFileStatus.Modified, GitService.Status(dir)[0].IndexStatus);
            Assert.IsTrue(GitService.Reset(dir, new[] { "a.txt" }, out err));
            Assert.AreEqual(GitFileStatus.Modified, GitService.Status(dir)[0].WorkingStatus);
        }

        [TestMethod]
        public void TrackedFilesHistoryDiffBlame()
        {
            string dir = InitRepoWithCommit();
            Assert.IsTrue(GitService.ListTrackedFiles(dir).Contains("a.txt"));

            List<GitCommitEntry> hist = GitService.FileHistory(dir, "a.txt");
            Assert.IsTrue(hist.Count >= 1);
            Assert.AreEqual("initial", hist[0].Subject);
            Assert.AreEqual(7, hist[0].ShortHash.Length);

            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello\nworld\n");
            string diff = GitService.DiffFile(dir, "a.txt", false);
            Assert.IsTrue(diff.Contains("world"));

            List<GitBlameEntry> blame = GitService.Blame(dir, "a.txt");
            Assert.IsTrue(blame.Count >= 1);
            Assert.AreEqual(1, blame[0].StartLine);
            Assert.AreEqual(40, blame[0].CommitHash.Length);
        }

        [TestMethod]
        public void CurrentBranchAndCheckout()
        {
            string dir = InitRepoWithCommit();
            string start = GitService.CurrentBranch(dir);
            Assert.IsFalse(string.IsNullOrEmpty(start));

            // Create a new branch with LibGit2Sharp directly, then check it out via the service.
            using (LibGit2Sharp.Repository repo = new LibGit2Sharp.Repository(dir))
            {
                repo.Branches.Add("feature", repo.Head.Tip);
            }
            string err;
            Assert.IsTrue(GitService.Checkout(dir, "feature", out err), err);
            Assert.AreEqual("feature", GitService.CurrentBranch(dir));

            List<GitBranch> branches = GitService.ListBranches(dir);
            Assert.IsTrue(branches.Exists(b => b.Name == "feature" && b.Current));
        }

        [TestMethod]
        public void RenameBranch()
        {
            string dir = InitRepoWithCommit();
            using (LibGit2Sharp.Repository repo = new LibGit2Sharp.Repository(dir))
            {
                repo.Branches.Add("old-name", repo.Head.Tip);
            }
            string err;
            Assert.IsTrue(GitService.RenameBranch(dir, "old-name", "new-name", out err), err);
            Assert.IsFalse(GitService.RenameBranch(dir, "missing", "x", out err));
        }

        [TestMethod]
        public void GitIgnoreManagement()
        {
            string dir = NewDir();
            string err;
            GitService.Init(dir, false, out err);

            List<string> added = GitService.EnsurePythonGitIgnore(dir, out err);
            Assert.AreEqual(9, added.Count);
            Assert.IsTrue(GitService.ReadGitIgnore(dir).Contains("__pycache__/"));

            // Idempotent: second call adds nothing.
            Assert.AreEqual(0, GitService.EnsurePythonGitIgnore(dir, out err).Count);

            Assert.IsTrue(GitService.WriteGitIgnore(dir, new[] { "*.log", "secret.txt" }, out err));
            List<string> lines = GitService.ReadGitIgnore(dir);
            CollectionAssert.AreEqual(new List<string> { "*.log", "secret.txt" }, lines);
        }

        [TestMethod]
        public void TrackedNowIgnoredAndUntrack()
        {
            string dir = NewDir();
            string err;
            GitService.Init(dir, false, out err);
            CommitFile(dir, "data.txt", "x\n", "add data");

            GitService.WriteGitIgnore(dir, new[] { "data.txt" }, out err);
            List<string> nowIgnored = GitService.ListTrackedNowIgnored(dir);
            Assert.IsTrue(nowIgnored.Contains("data.txt"));

            Assert.IsTrue(GitService.UntrackFromIndex(dir, new[] { "data.txt" }, out err), err);
            Assert.IsFalse(GitService.ListTrackedFiles(dir).Contains("data.txt"));
        }

        [TestMethod]
        public void StashAndPop()
        {
            string dir = InitRepoWithCommit();
            File.WriteAllText(Path.Combine(dir, "a.txt"), "dirty\n");

            string err;
            bool created;
            Assert.IsTrue(GitService.Stash(dir, "wip", out created, out err), err);
            Assert.IsTrue(created);
            Assert.AreEqual(0, GitService.Status(dir).Count); // clean after stash

            Assert.IsTrue(GitService.StashPop(dir, out err), err);
            // Normalise line endings — git may apply autocrlf on the round-trip.
            string restored = File.ReadAllText(Path.Combine(dir, "a.txt")).Replace("\r\n", "\n");
            Assert.AreEqual("dirty\n", restored);
        }

        // ---- remote (filesystem bare repo — no network) ----

        private string InitBareRemote()
        {
            string dir = NewDir();
            string err;
            Assert.IsTrue(GitService.Init(dir, true, out err), "bare init failed: " + err);
            return dir;
        }

        [TestMethod]
        public void AddRemotePushAndClone()
        {
            string bare = InitBareRemote();
            string w = InitRepoWithCommit();
            string err;
            Assert.IsTrue(GitService.AddRemote(w, "origin", bare, out err), err);
            Assert.IsTrue(GitService.ListRemotes(w).Exists(r => r.Name == "origin"));
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), "push failed: " + err);

            string clone = Path.Combine(Path.GetTempPath(), "helix_git_clone_" + Guid.NewGuid().ToString("N"));
            _temps.Add(clone);
            Assert.IsTrue(GitService.Clone(bare, clone, null, out err), "clone failed: " + err);
            Assert.IsTrue(GitService.ListTrackedFiles(clone).Contains("a.txt"));
        }

        [TestMethod]
        public void SmartSyncFastForward()
        {
            string bare = InitBareRemote();
            string w = InitRepoWithCommit();
            string err;
            GitService.AddRemote(w, "origin", bare, out err);
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            string clone = Path.Combine(Path.GetTempPath(), "helix_git_ff_" + Guid.NewGuid().ToString("N"));
            _temps.Add(clone);
            Assert.IsTrue(GitService.Clone(bare, clone, null, out err), err);

            // Advance the remote via the original working tree.
            CommitFile(w, "b.txt", "second\n", "second");
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            GitSyncOutcome outcome = new GitSyncOutcome();
            Assert.IsTrue(GitService.SmartSync(clone, "origin", null, outcome),
                "SmartSync failed: " + outcome.ErrorMessage);
            Assert.AreEqual(GitSyncOutcome.SyncResult.FastForwarded, outcome.Result);
            Assert.IsTrue(GitService.ListTrackedFiles(clone).Contains("b.txt"));
        }

        [TestMethod]
        public void SmartSyncUpToDate()
        {
            string bare = InitBareRemote();
            string w = InitRepoWithCommit();
            string err;
            GitService.AddRemote(w, "origin", bare, out err);
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            string clone = Path.Combine(Path.GetTempPath(), "helix_git_utd_" + Guid.NewGuid().ToString("N"));
            _temps.Add(clone);
            Assert.IsTrue(GitService.Clone(bare, clone, null, out err), err);

            GitSyncOutcome outcome = new GitSyncOutcome();
            Assert.IsTrue(GitService.SmartSync(clone, "origin", null, outcome), outcome.ErrorMessage);
            Assert.AreEqual(GitSyncOutcome.SyncResult.UpToDate, outcome.Result);
        }

        [TestMethod]
        public void SmartSyncRebasesDivergedHistory()
        {
            string bare = InitBareRemote();
            string w = InitRepoWithCommit();
            string err;
            GitService.AddRemote(w, "origin", bare, out err);
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            string clone = Path.Combine(Path.GetTempPath(), "helix_git_rb_" + Guid.NewGuid().ToString("N"));
            _temps.Add(clone);
            Assert.IsTrue(GitService.Clone(bare, clone, null, out err), err);

            // Remote advances (touches b.txt); clone advances locally (touches c.txt) — no overlap.
            CommitFile(w, "b.txt", "remote\n", "remote commit");
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);
            CommitFile(clone, "c.txt", "local\n", "local commit");

            GitSyncOutcome outcome = new GitSyncOutcome();
            Assert.IsTrue(GitService.SmartSync(clone, "origin", null, outcome),
                "SmartSync failed: " + outcome.ErrorMessage);
            Assert.AreEqual(GitSyncOutcome.SyncResult.Rebased, outcome.Result);
            Assert.AreEqual(1, outcome.RewoundLocal);
            // After rebase, both the remote and the local commit's files are present.
            Assert.IsTrue(File.Exists(Path.Combine(clone, "b.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(clone, "c.txt")));
        }

        [TestMethod]
        public void AheadBehindReportsCounts()
        {
            string bare = InitBareRemote();
            string w = InitRepoWithCommit();
            string err;
            GitService.AddRemote(w, "origin", bare, out err);
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            string clone = Path.Combine(Path.GetTempPath(), "helix_git_ab_" + Guid.NewGuid().ToString("N"));
            _temps.Add(clone);
            Assert.IsTrue(GitService.Clone(bare, clone, null, out err), err);

            CommitFile(w, "b.txt", "x\n", "remote 2");
            Assert.IsTrue(GitService.Push(w, "origin", null, out err), err);

            string branch = GitService.CurrentBranch(clone);
            Assert.IsTrue(GitService.Fetch(clone, "origin", null, out err), err);
            Assert.IsTrue(GitService.HasUpstreamTrackingRef(clone, branch, "origin"));

            GitAheadBehind ab = new GitAheadBehind();
            Assert.IsTrue(GitService.AheadBehind(clone, branch, "origin", ab, out err), err);
            Assert.AreEqual(0, ab.Ahead);
            Assert.AreEqual(1, ab.Behind);
        }

        [TestMethod]
        public void OverlayServiceComputesTrackedAndDirty()
        {
            string dir = InitRepoWithCommit();
            File.WriteAllText(Path.Combine(dir, "a.txt"), "changed\n");

            GitOverlaySnapshot snap = new GitOverlaySnapshot { Generation = 1 };
            snap.ProjectDirectories.Add(dir);
            GitOverlayResult res = GitOverlayService.Compute(snap);

            Assert.AreEqual(1, res.Generation);
            GitOverlayPerRepo per = res.ByProjectDir[dir];
            Assert.IsTrue(per.InRepo);
            Assert.IsTrue(per.Tracked.Contains("a.txt"));
            Assert.IsTrue(per.Dirty.Contains("a.txt"));
        }
    }
}
