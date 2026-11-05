using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Exercises <see cref="SolutionFileWatcher"/>: it must watch PYTHON sources (.py) — not the
    /// .cs/.csproj it originally watched — and route an external change to a file with unsaved
    /// in-app edits to <see cref="SolutionFileWatcher.ConflictDetected"/> rather than the plain
    /// <see cref="SolutionFileWatcher.ExternalChangeDetected"/>.
    /// </summary>
    [TestClass]
    public class FileWatcherTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_fw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void WatchesPythonFiles_AndRoutesDirtyFileChangeToConflict()
        {
            string dir = MakeTempDir();
            try
            {
                string clean = Path.Combine(dir, "clean.py");
                string dirty = Path.Combine(dir, "dirty.py");
                File.WriteAllText(clean, "x = 1\n");
                File.WriteAllText(dirty, "y = 1\n");

                using (var w = new SolutionFileWatcher(dir))
                {
                    // Pretend "dirty.py" is open in the editor with unsaved edits.
                    w.IsFileModifiedInEditor = p => p != null && p.EndsWith("dirty.py", StringComparison.OrdinalIgnoreCase);

                    var changeEv = new ManualResetEventSlim(false);
                    var conflictEv = new ManualResetEventSlim(false);
                    string changePath = null, conflictPath = null;
                    w.ExternalChangeDetected += (s, e) => { changePath = e.FilePath; changeEv.Set(); };
                    w.ConflictDetected += (s, e) => { conflictPath = e.FilePath; conflictEv.Set(); };
                    w.Start();
                    Thread.Sleep(150); // let the OS watcher arm

                    // Change the CLEAN (not-open) file → plain external change.
                    File.AppendAllText(clean, "z = 2\n");
                    Assert.IsTrue(changeEv.Wait(6000), "a .py change must be detected (watcher now watches Python, not .cs)");
                    StringAssert.EndsWith(changePath, "clean.py");
                    Assert.IsFalse(conflictEv.IsSet, "a change to a non-edited file is not a conflict");

                    // Change the DIRTY (open-with-edits) file → conflict.
                    File.AppendAllText(dirty, "w = 3\n");
                    Assert.IsTrue(conflictEv.Wait(6000), "a change to a file with unsaved edits must raise a conflict");
                    StringAssert.EndsWith(conflictPath, "dirty.py");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void SuspendWhileEditing_IsReentrant_InnerDisposeKeepsOuterSuppression()
        {
            string dir = MakeTempDir();
            try
            {
                string file = Path.Combine(dir, "mod.py");
                File.WriteAllText(file, "a = 1\n");

                using (var w = new SolutionFileWatcher(dir))
                {
                    var changeEv = new ManualResetEventSlim(false);
                    w.ExternalChangeDetected += (s, e) => changeEv.Set();
                    w.ConflictDetected += (s, e) => changeEv.Set();
                    w.Start();
                    Thread.Sleep(150);

                    // Nested scopes model an editor save (inner) inside a refactor (outer).
                    // Disposing the INNER scope must NOT lift the outer suppression: before the
                    // depth-counter fix, scope dispose cleared a boolean flag, so the write below
                    // (made while the outer scope is still open, after the 1s grace expired)
                    // surfaced as a false "External Change Detected".
                    using (w.SuspendWhileEditing())            // outer — a refactor
                    {
                        using (w.SuspendWhileEditing()) { }    // inner — an editor save
                        Thread.Sleep(1300);                    // outlive the inner grace period
                        File.AppendAllText(file, "b = 2\n");   // IDE's own write, outer still open
                        Assert.IsFalse(changeEv.Wait(1500),
                            "a write inside an open outer scope must stay suppressed even after " +
                            "an inner scope was disposed");
                    }

                    // After the OUTER scope closes and its grace period passes, a genuinely
                    // external write must be reported again — suppression must not stick.
                    Thread.Sleep(1300);
                    changeEv.Reset();
                    File.AppendAllText(file, "c = 3\n");
                    Assert.IsTrue(changeEv.Wait(6000),
                        "suppression must end once the outermost scope is disposed");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void IgnoresChangesInVenvAndCacheDirs()
        {
            string dir = MakeTempDir();
            try
            {
                string venv = Path.Combine(dir, ".venv");
                Directory.CreateDirectory(venv);
                string vendored = Path.Combine(venv, "mod.py");
                File.WriteAllText(vendored, "a = 1\n");

                using (var w = new SolutionFileWatcher(dir))
                {
                    var changeEv = new ManualResetEventSlim(false);
                    w.ExternalChangeDetected += (s, e) => changeEv.Set();
                    w.ConflictDetected += (s, e) => changeEv.Set();
                    w.Start();
                    Thread.Sleep(150);

                    File.AppendAllText(vendored, "b = 2\n");
                    // Debounce is 500ms; a 2s wait with no event proves the .venv change was ignored.
                    Assert.IsFalse(changeEv.Wait(2000), "changes under .venv must be ignored");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void IgnoresRunGeneratedEnvironmentLockButStillWatchesProjectManifest()
        {
            string dir = MakeTempDir();
            try
            {
                string manifest = Path.Combine(dir, "pyproject.toml");
                string environmentLock = Path.Combine(dir, "environment.lock.toml");
                File.WriteAllText(manifest, "[project]\nname='sample'\n");
                File.WriteAllText(environmentLock, "# captured environment\n");

                using (var w = new SolutionFileWatcher(dir))
                {
                    var changed = new ManualResetEventSlim(false);
                    string changedPath = null;
                    w.ExternalChangeDetected += (s, e) => { changedPath = e.FilePath; changed.Set(); };
                    w.Start();
                    Thread.Sleep(150);

                    File.AppendAllText(environmentLock, "\n[toolchain]\n");
                    Assert.IsFalse(changed.Wait(1500),
                        "the run-generated lock file must not trigger a solution reload prompt");

                    File.AppendAllText(manifest, "\n[tool.helixstudio]\nkind='app'\n");
                    Assert.IsTrue(changed.Wait(6000), "external manifest edits must still be reported");
                    StringAssert.EndsWith(changedPath, "pyproject.toml");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
