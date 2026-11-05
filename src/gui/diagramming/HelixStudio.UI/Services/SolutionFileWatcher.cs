using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;

namespace HelixPythonNamespace.UI.Services
{
    /// <summary>
    /// Event args for file change events.
    /// </summary>
    public sealed class FileChangedEventArgs : EventArgs
    {
        public FileChangedEventArgs(string filePath, WatcherChangeTypes changeType)
        {
            FilePath = filePath;
            ChangeType = changeType;
        }

        public string FilePath { get; }
        public WatcherChangeTypes ChangeType { get; }
    }

    /// <summary>
    /// Event args for conflict detection.
    /// </summary>
    public sealed class FileConflictEventArgs : EventArgs
    {
        public FileConflictEventArgs(string filePath, string message)
        {
            FilePath = filePath;
            Message = message;
        }

        public string FilePath { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Watches solution files for external changes.
    /// </summary>
    public sealed class SolutionFileWatcher : IDisposable
    {
        private readonly string _solutionDirectory;
        private FileSystemWatcher _pyWatcher;
        private FileSystemWatcher _tomlWatcher;
        private Timer _debounceTimer;
        private readonly HashSet<string> _pendingChanges;
        private readonly object _lock = new object();
        // Depth counter, not a bool: suspension scopes nest (an editor save inside a
        // refactor's outer scope), and suppression must hold until the OUTERMOST scope
        // closes. Incremented/decremented via Interlocked; > 0 means "suspended".
        private int _suspendDepth;
        private bool _disposed;
        private DateTime _editingEndedAt;
        // Everything already written to disk when the last edit scope closed is OURS. A
        // FileSystemWatcher notification is delivered asynchronously on a thread-pool thread and
        // can arrive long after the write that caused it — on a busy UI thread, well past the
        // grace period — so a purely time-based window let the IDE prompt "External file change
        // detected: Cargo.toml. Reload solution?" for a file the IDE had just written itself.
        // Comparing the file's own last-write time against this horizon is delivery-latency proof.
        private DateTime _selfWriteHorizonUtc = DateTime.MinValue;

        private const int DebounceDelayMs = 500;
        private const int GracePeriodMs = 1000; // Ignore changes for 1 second after editing ends
        private const string EnvironmentLockFileName = "environment.lock.toml";

        public SolutionFileWatcher(string solutionDirectory)
        {
            _solutionDirectory = solutionDirectory ?? throw new ArgumentNullException("solutionDirectory");
            _pendingChanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Fired when a file with unsaved in-app edits is changed externally — a true conflict.
        /// Routed here (instead of <see cref="ExternalChangeDetected"/>) when
        /// <see cref="IsFileModifiedInEditor"/> reports the file dirty, so the host can prompt the
        /// user to keep/reload rather than silently offering a reload.
        /// </summary>
        public event EventHandler<FileConflictEventArgs> ConflictDetected;

        /// <summary>
        /// Fired when an external change has been detected and debounced (and the file is NOT being
        /// edited in-app — see <see cref="ConflictDetected"/> for the dirty-file case).
        /// </summary>
        public event EventHandler<FileChangedEventArgs> ExternalChangeDetected;

        /// <summary>
        /// Host-supplied predicate: returns true when the given file is open in the editor with
        /// unsaved changes. An external change to such a file is raised as a <see
        /// cref="ConflictDetected"/> rather than a plain <see cref="ExternalChangeDetected"/>. The
        /// watcher itself has no knowledge of editor state, so the host wires this.
        /// </summary>
        public Func<string, bool> IsFileModifiedInEditor { get; set; }

        /// <summary>
        /// Start watching for file changes.
        /// </summary>
        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("SolutionFileWatcher");
            }

            Stop();

            if (!Directory.Exists(_solutionDirectory))
            {
                return;
            }

            // Watch Python sources (this is a Python tool — the project files are .py, not .cs).
            _pyWatcher = new FileSystemWatcher(_solutionDirectory, "*.py")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            _pyWatcher.Changed += OnFileChanged;
            _pyWatcher.Created += OnFileChanged;
            _pyWatcher.Deleted += OnFileChanged;
            _pyWatcher.Renamed += OnFileRenamed;

            // Watch project manifests (pyproject.toml and any other *.toml under the solution).
            _tomlWatcher = new FileSystemWatcher(_solutionDirectory, "*.toml")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _tomlWatcher.Changed += OnFileChanged;
            _tomlWatcher.Created += OnFileChanged;
            _tomlWatcher.Deleted += OnFileChanged;

            // Debounce timer
            _debounceTimer = new Timer(DebounceDelayMs);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += OnDebounceElapsed;

            _pyWatcher.EnableRaisingEvents = true;
            _tomlWatcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Stop watching for file changes.
        /// </summary>
        public void Stop()
        {
            if (_pyWatcher != null)
            {
                _pyWatcher.EnableRaisingEvents = false;
                _pyWatcher.Dispose();
                _pyWatcher = null;
            }

            if (_tomlWatcher != null)
            {
                _tomlWatcher.EnableRaisingEvents = false;
                _tomlWatcher.Dispose();
                _tomlWatcher = null;
            }

            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }

            lock (_lock)
            {
                _pendingChanges.Clear();
            }
        }

        /// <summary>
        /// Suspend file watching while editing. Reentrant: scopes may nest, and events stay
        /// suppressed until the last open scope is disposed (plus a short grace period).
        /// </summary>
        public IDisposable SuspendWhileEditing()
        {
            System.Threading.Interlocked.Increment(ref _suspendDepth);
            // Stop the debounce timer and clear any pending changes
            _debounceTimer?.Stop();
            lock (_lock)
            {
                _pendingChanges.Clear();
            }
            return new EditingScope(this);
        }

        // True while any suspension scope is open or the post-edit grace period is running.
        private bool IsSuppressed()
        {
            return _suspendDepth > 0
                || (DateTime.UtcNow - _editingEndedAt).TotalMilliseconds < GracePeriodMs;
        }

        /// <summary>
        /// True when the file's content is no newer than the moment our last edit scope closed —
        /// i.e. this notification describes a write WE made, however late it was delivered. A
        /// genuinely external edit happens after that horizon and still prompts.
        /// </summary>
        private bool IsOurOwnWrite(string fullPath)
        {
            DateTime horizon = _selfWriteHorizonUtc;
            if (horizon == DateTime.MinValue) return false;
            try
            {
                // A deleted file has no timestamp to compare; treat it as external so genuine
                // deletions still surface.
                if (!File.Exists(fullPath)) return false;
                return File.GetLastWriteTimeUtc(fullPath) <= horizon;
            }
            catch { return false; }
        }

        // Virtual-env / build / cache directories that produce churn but aren't project sources.
        private static readonly string[] IgnoredDirs =
        {
            "bin", "obj", "__pycache__", ".venv", "venv", "env", ".env",
            "build", "dist", ".tox", ".pytest_cache", ".mypy_cache", ".git",
        };

        private static bool IsIgnoredPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (string.Equals(Path.GetFileName(fullPath), EnvironmentLockFileName,
                StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string d in IgnoredDirs)
            {
                string seg = Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar;
                if (fullPath.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (IsIgnoredPath(e.FullPath))
            {
                return;
            }

            // Ignore changes when we are making edits ourselves or within grace period
            if (IsSuppressed() || IsOurOwnWrite(e.FullPath))
            {
                return;
            }

            lock (_lock)
            {
                _pendingChanges.Add(e.FullPath);
            }

            // Reset debounce timer
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            bool ignoreOld = IsIgnoredPath(e.OldFullPath);
            bool ignoreNew = IsIgnoredPath(e.FullPath);
            if (ignoreOld && ignoreNew)
            {
                return;
            }

            // Ignore changes when we are making edits ourselves or within grace period
            if (IsSuppressed())
            {
                return;
            }

            lock (_lock)
            {
                if (!ignoreOld) _pendingChanges.Add(e.OldFullPath);
                if (!ignoreNew) _pendingChanges.Add(e.FullPath);
            }

            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            // Don't process changes if we're still in the grace period
            if (IsSuppressed())
            {
                lock (_lock)
                {
                    _pendingChanges.Clear();
                }
                return;
            }

            List<string> changes;
            lock (_lock)
            {
                changes = new List<string>(_pendingChanges);
                _pendingChanges.Clear();
            }

            Func<string, bool> isDirty = IsFileModifiedInEditor;
            foreach (var filePath in changes)
            {
                // A change to a file the user is editing (unsaved) is a CONFLICT — surface it
                // distinctly so they can choose to keep their edits or take the on-disk version.
                bool modifiedInEditor = false;
                try { modifiedInEditor = isDirty != null && isDirty(filePath); }
                catch { modifiedInEditor = false; }

                if (modifiedInEditor)
                {
                    ConflictDetected?.Invoke(this, new FileConflictEventArgs(filePath,
                        "This file was changed on disk while you have unsaved edits in the editor."));
                    continue;
                }

                var changeType = File.Exists(filePath)
                    ? WatcherChangeTypes.Changed
                    : WatcherChangeTypes.Deleted;

                ExternalChangeDetected?.Invoke(this, new FileChangedEventArgs(filePath, changeType));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        private sealed class EditingScope : IDisposable
        {
            private readonly SolutionFileWatcher _watcher;
            private bool _closed;

            public EditingScope(SolutionFileWatcher watcher)
            {
                _watcher = watcher;
            }

            public void Dispose()
            {
                // Idempotent: a double-disposed scope must not decrement the depth twice,
                // or an unrelated open scope would lose its suppression.
                if (_closed) return;
                _closed = true;

                // Clear any pending changes that accumulated during editing
                _watcher._debounceTimer?.Stop();
                lock (_watcher._lock)
                {
                    _watcher._pendingChanges.Clear();
                }
                // Set grace period timestamp before releasing this level of suspension
                _watcher._editingEndedAt = DateTime.UtcNow;
                // Everything on disk as of NOW was written by us. Notifications for those writes
                // may still be in flight; the horizon lets them be recognised whenever they land.
                _watcher._selfWriteHorizonUtc = DateTime.UtcNow;
                System.Threading.Interlocked.Decrement(ref _watcher._suspendDepth);
            }
        }
    }
}
