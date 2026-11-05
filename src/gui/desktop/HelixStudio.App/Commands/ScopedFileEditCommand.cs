namespace HelixPythonNamespace.App.Commands
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using HelixPythonNamespace.UI.Interaction;

    /// <summary>
    /// Undoable multi-file text edit. Captures pre/post content for every file a
    /// refactor touched so Ctrl+Z restores every byte atomically (best-effort per
    /// file). Does NOT track directory moves — callers that rename directories
    /// must reverse those manually.
    /// </summary>
    public sealed class ScopedFileEditCommand : ICommand
    {
        private readonly string _description;
        // One entry per file touched, each carrying its old and new full text.
        private readonly IReadOnlyList<FileEdit> _edits;
        // Refresh/notify hook run once after all files are written (each direction).
        private readonly Action _onApplied;
        // Write as UTF-8 without a BOM so files stay byte-identical to the originals.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public ScopedFileEditCommand(string description, IReadOnlyList<FileEdit> edits, Action onApplied = null)
        {
            _description = description ?? "Refactor";
            _edits = edits ?? new List<FileEdit>();
            _onApplied = onApplied;
        }

        public string Description { get { return _description; } }

        // Redo writes every file's NewText; Undo writes every file's OldText.
        public void Execute() { WriteEach(forward: true); }
        public void Undo() { WriteEach(forward: false); }

        // Rewrite each tracked file in the chosen direction. A failure on one file is
        // swallowed so the remaining files still get written (best-effort atomicity).
        private void WriteEach(bool forward)
        {
            foreach (FileEdit e in _edits)
            {
                try { File.WriteAllText(e.Path, forward ? e.NewText : e.OldText, Utf8NoBom); }
                catch { /* best-effort per-file */ }
            }
            _onApplied?.Invoke();
        }

        // Value record of a single file's before/after content used to build the command.
        public struct FileEdit
        {
            public string Path;
            public string OldText;
            public string NewText;
            public FileEdit(string path, string oldText, string newText)
            { Path = path; OldText = oldText; NewText = newText; }
        }
    }
}
