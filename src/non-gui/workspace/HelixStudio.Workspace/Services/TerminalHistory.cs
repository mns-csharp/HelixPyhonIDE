using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Shell-style command history for the integrated terminal's input line (FR-X-01).
    /// Pure logic (no UI) so it is CI-testable: the WinForms <c>TerminalPanel</c> wires
    /// Up/Down arrows to <see cref="Prev"/>/<see cref="Next"/> and records each submitted
    /// command via <see cref="Add"/>. Semantics mirror cmd/bash: Up walks toward the oldest
    /// entry, Down walks back toward the empty "new line", consecutive duplicates collapse,
    /// and blank submissions are ignored but reset the cursor to the bottom.
    /// </summary>
    public sealed class TerminalHistory
    {
        private readonly List<string> items_ = new List<string>();
        // Cursor into items_. Equal to Count means "editing a fresh line below the newest entry".
        private int index_;

        /// <summary>Number of stored commands.</summary>
        public int Count { get { return items_.Count; } }

        /// <summary>Record a submitted command and reset the cursor to the bottom.</summary>
        public void Add(string command)
        {
            if (!string.IsNullOrWhiteSpace(command) &&
                (items_.Count == 0 || items_[items_.Count - 1] != command))
            {
                items_.Add(command);
            }
            index_ = items_.Count;
        }

        /// <summary>
        /// Up-arrow: step to the previous (older) command. Returns the command to place in the
        /// input, or <c>null</c> when there is no history at all (leave the input untouched).
        /// Stops at the oldest entry rather than wrapping.
        /// </summary>
        public string Prev()
        {
            if (items_.Count == 0) return null;
            if (index_ > 0) index_--;
            return items_[index_];
        }

        /// <summary>
        /// Down-arrow: step to the next (newer) command. Returns that command, an empty string
        /// once the cursor moves past the newest entry (a fresh input line), or <c>null</c> when
        /// there is no history at all.
        /// </summary>
        public string Next()
        {
            if (items_.Count == 0) return null;
            if (index_ < items_.Count) index_++;
            return index_ >= items_.Count ? string.Empty : items_[index_];
        }
    }
}
