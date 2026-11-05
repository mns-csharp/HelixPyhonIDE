using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// A REAL fixture solution, at its real path under <c>tests/fixtures/</c>, that a test may edit.
    ///
    /// The standing rule is "load a real solution, add/edit code in the solution, then perform a
    /// specific test". Tests that mutate code used to work on a GUID-named copy under %TEMP%
    /// (<c>helix_fx_python-test-sample-oop_baf89b31…</c>), which is not the solution the rule means
    /// and — seen on screen during a run — is indistinguishable from a fabricated project.
    ///
    /// So the edit happens in the committed fixture, and this type guarantees the corpus is put back
    /// exactly as it was: every file is snapshotted on open, and on dispose modified files are
    /// rewritten, deleted files restored, and files the test added removed. Restoration is verified,
    /// not assumed.
    ///
    /// Dispose the MainForm BEFORE the workspace, or the editor may still hold a file open.
    /// </summary>
    internal sealed class FixtureWorkspace : IDisposable
    {
        private readonly Dictionary<string, byte[]> _snapshot =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;
        private readonly string _backupDir;

        /// <summary>
        /// Where on-disk backups live. In-memory state dies with a crashed test host — and one did,
        /// leaving python-test-sample-oop with every Matrix2x3 renamed to Matrix2x3Renamed in the
        /// committed fixture. A copy on disk lets the NEXT run put it back
        /// (<see cref="RecoverAbandonedSnapshots"/>).
        /// </summary>
        private static string BackupRoot
        {
            get { return Path.Combine(Path.GetTempPath(), "helix_fixture_backups"); }
        }

        private const string RootMarker = "__fixture_root__.txt";

        /// <summary>The real fixture directory — this is what gets opened as a solution.</summary>
        public string Root { get; private set; }

        private FixtureWorkspace(string root)
        {
            Root = root;
            foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                if (!Skip(Rel(d))) _directories.Add(d);
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                if (!Skip(Rel(f))) _snapshot[f] = File.ReadAllBytes(f);
            Assert.IsTrue(_snapshot.Count > 0, "fixture appears empty: " + root);

            _backupDir = Path.Combine(BackupRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_backupDir);
            foreach (var entry in _snapshot)
            {
                string dest = Path.Combine(_backupDir, Rel(entry.Key));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, entry.Value);
            }
            // Written LAST: a marker only appears once the backup is complete, so a half-written
            // backup is never used to "restore" a fixture.
            File.WriteAllText(Path.Combine(_backupDir, RootMarker), root);
        }

        /// <summary>
        /// Restore any fixture left modified by a run that died before it could clean up. Returns the
        /// number of fixtures restored. Called from AssemblyInitialize.
        /// </summary>
        public static int RecoverAbandonedSnapshots()
        {
            if (!Directory.Exists(BackupRoot)) return 0;
            int restored = 0;

            foreach (string dir in Directory.GetDirectories(BackupRoot))
            {
                string marker = Path.Combine(dir, RootMarker);
                if (!File.Exists(marker)) continue;          // incomplete backup — never trust it

                string fixtureRoot;
                try { fixtureRoot = File.ReadAllText(marker).Trim(); }
                catch { continue; }
                if (string.IsNullOrEmpty(fixtureRoot) || !Directory.Exists(fixtureRoot)) continue;

                bool ok = true;
                foreach (string backupFile in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = backupFile.Substring(dir.Length).TrimStart('\\', '/');
                    if (string.Equals(rel, RootMarker, StringComparison.OrdinalIgnoreCase)) continue;

                    string target = Path.Combine(fixtureRoot, rel);
                    try
                    {
                        byte[] original = File.ReadAllBytes(backupFile);
                        if (File.Exists(target) && SameBytes(File.ReadAllBytes(target), original)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllBytes(target, original);
                    }
                    catch { ok = false; }
                }

                if (ok)
                {
                    restored++;
                    TryIo(() => Directory.Delete(dir, true));
                }
            }
            return restored;
        }

        public static FixtureWorkspace Open(params string[] fixtureSegments)
        {
            return new FixtureWorkspace(FixtureSolution.Path_(fixtureSegments));
        }

        /// <summary>A path inside the fixture, asserted to exist.</summary>
        public string File_(params string[] segments)
        {
            string p = Root;
            foreach (string s in segments) p = Path.Combine(p, s);
            Assert.IsTrue(File.Exists(p), "fixture file missing: " + p);
            return p;
        }

        private string Rel(string full)
        {
            return full.Substring(Root.Length).TrimStart('\\', '/');
        }

        /// <summary>Build trees and caches are not part of the source corpus.</summary>
        private static bool Skip(string relativePath)
        {
            string p = "\\" + relativePath.Replace('/', '\\') + "\\";
            foreach (string seg in new[] { "\\build\\", "\\cmake-build", "\\_deps\\", "\\CMakeFiles\\",
                                           "\\.git\\", "\\__pycache__\\", "\\.pytest_cache\\",
                                           "\\TestResults\\", "\\target\\", "\\dist\\" })
                if (p.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Put every fixture file back to its committed content, WITHOUT retiring the on-disk
        /// backup. Used between tests that share one workspace, so one test's edits cannot leak
        /// into the next while the crash-recovery copy stays in place for the rest of the class.
        /// </summary>
        public void RestoreFiles()
        {
            RestoreFilesCore();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RestoreFilesCore();

            // Restored cleanly — the on-disk safety copy is no longer needed.
            if (_backupDir != null && Directory.Exists(_backupDir))
                TryIo(() => Directory.Delete(_backupDir, true));
        }

        private void RestoreFilesCore()
        {
            var problems = new List<string>();

            // Anything the test added, remove.
            foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
            {
                if (Skip(Rel(f)) || _snapshot.ContainsKey(f)) continue;
                if (!TryIo(() => File.Delete(f))) problems.Add("could not delete added file " + f);
            }

            // Anything modified or deleted, put back byte for byte.
            foreach (var entry in _snapshot)
            {
                bool needsWrite = !File.Exists(entry.Key) ||
                                  !SameBytes(File.ReadAllBytes(entry.Key), entry.Value);
                if (!needsWrite) continue;
                string dir = Path.GetDirectoryName(entry.Key);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!TryIo(() => File.WriteAllBytes(entry.Key, entry.Value)))
                    problems.Add("could not restore " + entry.Key);
            }

            // Directories the test created (a rename can make one), innermost first.
            var added = new List<string>();
            foreach (string d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                if (!Skip(Rel(d)) && !_directories.Contains(d)) added.Add(d);
            added.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string d in added)
                if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                    TryIo(() => Directory.Delete(d));

            // Verify rather than assume — a fixture left dirty poisons every later run.
            foreach (var entry in _snapshot)
                if (!File.Exists(entry.Key)) problems.Add("still missing after restore: " + entry.Key);

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "the fixture solution was not restored to its committed state:" +
                    Environment.NewLine + string.Join(Environment.NewLine, problems.ToArray()));
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>File handles can linger briefly after the editor closes; retry before failing.</summary>
        private static bool TryIo(Action action)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { action(); return true; }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50); }
            }
            return false;
        }
    }
}
