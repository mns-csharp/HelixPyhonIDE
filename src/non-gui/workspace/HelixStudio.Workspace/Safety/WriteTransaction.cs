using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Safety
{
    /// <summary>
    /// Transaction-safe file writing with backup and rollback support.
    /// </summary>
    public sealed class WriteTransaction : IDisposable
    {
        private readonly string _backupDirectory;
        private readonly Dictionary<string, string> _backupPaths;
        private readonly Dictionary<string, string> _pendingWrites;
        private readonly List<string> _pendingDeletes;
        private bool _committed;
        private bool _disposed;

        public WriteTransaction(string solutionDirectory)
        {
            _backupDirectory = Path.Combine(solutionDirectory, ".csooptool-backup");
            _backupPaths = new Dictionary<string, string>();
            _pendingWrites = new Dictionary<string, string>();
            _pendingDeletes = new List<string>();
        }

        /// <summary>
        /// Register a file for backup before modification.
        /// </summary>
        public void RegisterFile(string filePath)
        {
            if (_disposed || _committed)
                throw new InvalidOperationException("Transaction is no longer active.");

            if (_backupPaths.ContainsKey(filePath))
                return; // Already registered

            if (File.Exists(filePath))
            {
                // Ensure backup directory exists
                if (!Directory.Exists(_backupDirectory))
                {
                    Directory.CreateDirectory(_backupDirectory);
                }

                // Create backup
                var backupPath = Path.Combine(_backupDirectory,
                    Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(filePath));

                File.Copy(filePath, backupPath, true);
                _backupPaths[filePath] = backupPath;
            }
        }

        /// <summary>
        /// Write content to a file (staged until commit).
        /// </summary>
        public void WriteFile(string filePath, string content)
        {
            if (_disposed || _committed)
                throw new InvalidOperationException("Transaction is no longer active.");

            RegisterFile(filePath);
            _pendingWrites[filePath] = content;
        }

        /// <summary>
        /// Mark a file for deletion (staged until commit).
        /// </summary>
        public void DeleteFile(string filePath)
        {
            if (_disposed || _committed)
                throw new InvalidOperationException("Transaction is no longer active.");

            RegisterFile(filePath);
            _pendingDeletes.Add(filePath);

            // Remove from pending writes if it was added
            _pendingWrites.Remove(filePath);
        }

        /// <summary>
        /// Commit all pending changes atomically.
        /// </summary>
        public void Commit()
        {
            if (_disposed || _committed)
                throw new InvalidOperationException("Transaction is no longer active.");

            try
            {
                // Write all pending files
                foreach (var kvp in _pendingWrites)
                {
                    var directory = Path.GetDirectoryName(kvp.Key);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write to temp file first, then rename
                    var tempPath = kvp.Key + ".tmp";
                    File.WriteAllText(tempPath, kvp.Value);

                    // Atomic rename (as atomic as the OS allows)
                    if (File.Exists(kvp.Key))
                    {
                        File.Delete(kvp.Key);
                    }
                    File.Move(tempPath, kvp.Key);
                }

                // Delete marked files
                foreach (var filePath in _pendingDeletes)
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }

                // Clean up backup directory on success
                CleanupBackups();

                _committed = true;
            }
            catch
            {
                // On failure, attempt rollback
                Rollback();
                throw;
            }
        }

        /// <summary>
        /// Rollback all changes and restore backups.
        /// </summary>
        public void Rollback()
        {
            if (_disposed || _committed)
                return;

            try
            {
                // Remove any temp files
                foreach (var filePath in _pendingWrites.Keys)
                {
                    var tempPath = filePath + ".tmp";
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }

                // Restore backups
                foreach (var kvp in _backupPaths)
                {
                    if (File.Exists(kvp.Value))
                    {
                        // Restore backup
                        File.Copy(kvp.Value, kvp.Key, true);
                    }
                }
            }
            finally
            {
                CleanupBackups();
            }
        }

        private void CleanupBackups()
        {
            foreach (var backupPath in _backupPaths.Values)
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            _backupPaths.Clear();

            // Remove backup directory if empty
            try
            {
                if (Directory.Exists(_backupDirectory) &&
                    Directory.GetFiles(_backupDirectory).Length == 0)
                {
                    Directory.Delete(_backupDirectory);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (!_committed)
            {
                Rollback();
            }

            _disposed = true;
        }
    }
}
