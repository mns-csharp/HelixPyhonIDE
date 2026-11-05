using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Reads back what <see cref="StructureRepairExecutor"/> wrote — specifically, whether a repair
    /// was interrupted and left the solution half-restructured.
    ///
    /// <b>Why a reader had to exist.</b> The journal was written and never read by anything. Its
    /// stated purpose was "any failure mid-plan triggers rollback from the journal", but the
    /// rollback ran from an in-memory list, so the file on disk was documentation, not a recovery
    /// mechanism. That distinction only shows up in the one case the journal is FOR: the process
    /// does not reach its own catch block. A crash, a kill, a power loss, or the user closing the
    /// IDE mid-repair leaves directories moved, the model unaware, and nothing anywhere that knows
    /// a repair was in flight — the next load simply sees a differently-shaped solution and
    /// re-derives a new set of defects from the wreckage.
    ///
    /// With write-ahead entries an interrupted run is legible: an operation whose
    /// <c>BEGIN</c> has no matching <c>DONE</c> is the one that was in flight.
    /// </summary>
    public static class StructureRepairJournal
    {
        private const string StateDirectoryName = ".helixstudio";
        private const string JournalName = "structure-repair.journal";

        public static string PathFor(string solutionDirectory)
        {
            if (string.IsNullOrEmpty(solutionDirectory)) return null;
            return Path.Combine(Path.Combine(solutionDirectory, StateDirectoryName), JournalName);
        }

        /// <summary>
        /// Operations whose <c>BEGIN</c> has no matching <c>DONE</c>, oldest first. Empty when the
        /// last repair finished (cleanly or with a completed rollback), when no repair ever ran, or
        /// when the journal cannot be read — never throws, because this is called on the load path
        /// and an unreadable bookkeeping file must not stop a solution opening.
        /// </summary>
        public static IList<string> ReadInterrupted(string solutionDirectory)
        {
            var pending = new List<string>();
            string path = PathFor(solutionDirectory);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return pending;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException) { return pending; }
            catch (UnauthorizedAccessException) { return pending; }

            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (StartsWithMarker(line, StructureRepairExecutor.BeginMarker, out string begun))
                {
                    pending.Add(begun);
                    continue;
                }
                // A DONE clears the matching BEGIN. Matched by content rather than by position
                // because a rolled-back run appends nothing — the pairing is all there is.
                if (StartsWithMarker(line, StructureRepairExecutor.DoneMarker, out string done))
                    pending.Remove(done);
            }
            return pending;
        }

        /// <summary>True when the last repair on this solution did not run to a conclusion.</summary>
        public static bool WasInterrupted(string solutionDirectory)
        {
            return ReadInterrupted(solutionDirectory).Count > 0;
        }

        private static bool StartsWithMarker(string line, string marker, out string remainder)
        {
            remainder = null;
            if (!line.StartsWith(marker + " ", StringComparison.Ordinal)) return false;
            remainder = line.Substring(marker.Length + 1).Trim();
            return true;
        }
    }
}
