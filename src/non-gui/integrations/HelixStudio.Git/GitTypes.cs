namespace HelixPythonNamespace.Git
{
    using System.Collections.Generic;

    public enum GitFileStatus
    {
        Unmodified,
        Modified,
        Added,
        Deleted,
        Renamed,
        Untracked,
        Ignored,
        Conflict,
    }

    public sealed class GitStatusEntry
    {
        public string FilePath = "";        // Repo-relative.
        public GitFileStatus IndexStatus = GitFileStatus.Unmodified;
        public GitFileStatus WorkingStatus = GitFileStatus.Unmodified;
        public string RenameSource = "";    // Set when renamed.
    }

    public sealed class GitCommitEntry
    {
        public string Hash = "";
        public string ShortHash = "";
        public string Author = "";
        public string Subject = "";
        public long Timestamp;     // Unix seconds.
    }

    public sealed class GitBranch
    {
        public string Name = "";
        public bool Current;
        public string Upstream = "";
    }

    public sealed class GitBlameEntry
    {
        public int StartLine;        // 1-based, inclusive
        public int LineCount;
        public string CommitHash = "";   // 40-char SHA
        public string Author = "";
        public long Timestamp;  // Unix seconds
    }

    public sealed class GitRemote
    {
        public string Name = "";
        public string Url = "";
    }

    public sealed class GitAheadBehind
    {
        public int Ahead;    // local commits not in remote
        public int Behind;   // remote commits not in local
    }

    /// <summary>HTTPS credentials returned by a <see cref="GitCredentialPrompt"/>.</summary>
    public sealed class GitCredentials
    {
        public string Username = "";
        public string Password = "";   // for HTTPS, this is typically a PAT
    }

    /// <summary>
    /// Credential prompt callback. The host implements this to surface a dialog.
    /// Returns null to signal "user cancelled" (the operation aborts).
    /// </summary>
    public delegate GitCredentials GitCredentialPrompt(string url, string usernameHint);

    /// <summary>Result of a one-shot lockstep sync. Port of the C++ <c>GitSyncOutcome</c>.</summary>
    public sealed class GitSyncOutcome
    {
        public enum SyncResult
        {
            UpToDate,
            FastForwarded,
            Rebased,
            Conflicts,
            Error,
        }

        public SyncResult Result = SyncResult.Error;
        public int AbsorbedRemote;
        public int RewoundLocal;
        public List<string> ConflictPaths = new List<string>();
        public string ErrorMessage = "";
        public bool StashCreated;
        public bool StashRestored;
    }
}
