using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Ai.Tools
{
    /// <summary>One file's contents captured immediately before the agent changed it.</summary>
    public sealed class EditJournalEntry
    {
        public EditJournalEntry(string path, string originalContent, bool existedBefore)
        {
            Path = path;
            OriginalContent = originalContent;
            ExistedBefore = existedBefore;
            RecordedAtUtc = DateTime.UtcNow;
        }

        public string Path { get; }

        /// <summary>Null when the file did not exist — undo deletes it rather than writing null.</summary>
        public string OriginalContent { get; }

        public bool ExistedBefore { get; }
        public DateTime RecordedAtUtc { get; }
    }

    /// <summary>
    /// Undo for agent file writes.
    ///
    /// The agent edits real files on disk, outside the editor's own undo stack, so without this a
    /// bad run would be unrecoverable for anyone not using source control. Every write and edit
    /// records the prior contents first; <see cref="UndoAll"/> restores them newest-first, which
    /// matters when the agent touched the same file more than once in a run.
    ///
    /// Only the FIRST state of each file per run needs to survive, but keeping every snapshot in
    /// order lets the panel offer step-by-step undo later without changing the format.
    /// </summary>
    public sealed class EditJournal
    {
        private readonly List<EditJournalEntry> _entries = new List<EditJournalEntry>();
        private readonly object _gate = new object();

        /// <summary>Snapshots made this run, oldest first.</summary>
        public IReadOnlyList<EditJournalEntry> Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }

        public int Count { get { lock (_gate) { return _entries.Count; } } }

        /// <summary>
        /// Capture a file's current state. Called before the write, never after — and it must not
        /// throw, because a journaling failure must not abort the edit the user approved.
        /// </summary>
        public void Record(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            try
            {
                bool exists = File.Exists(fullPath);
                string content = exists ? File.ReadAllText(fullPath) : null;
                lock (_gate) { _entries.Add(new EditJournalEntry(fullPath, content, exists)); }
            }
            catch (Exception)
            {
                // Unreadable file (locked, permissions). The edit still proceeds; undo simply
                // cannot restore this one, which is better than blocking an approved change.
            }
        }

        /// <summary>
        /// Roll every recorded file back to its pre-run state. Applied newest-first so a file
        /// touched several times lands on its ORIGINAL contents, not an intermediate revision.
        /// Returns a human-readable report; never throws.
        /// </summary>
        public string UndoAll()
        {
            EditJournalEntry[] snapshot;
            lock (_gate)
            {
                snapshot = _entries.ToArray();
                _entries.Clear();
            }
            if (snapshot.Length == 0) return "Nothing to undo.";

            int restored = 0, deleted = 0;
            var failures = new List<string>();
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                EditJournalEntry entry = snapshot[i];
                try
                {
                    if (entry.ExistedBefore)
                    {
                        File.WriteAllText(entry.Path, entry.OriginalContent);
                        restored++;
                    }
                    else if (File.Exists(entry.Path))
                    {
                        File.Delete(entry.Path);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(entry.Path) + ": " + ex.Message);
                }
            }

            var report = "Undo: restored " + restored + " file(s)"
                + (deleted > 0 ? ", deleted " + deleted + " created file(s)" : string.Empty) + ".";
            if (failures.Count > 0)
                report += " Failed on " + failures.Count + ": " + string.Join("; ", failures.ToArray());
            return report;
        }

        public void Clear() { lock (_gate) { _entries.Clear(); } }
    }
}
