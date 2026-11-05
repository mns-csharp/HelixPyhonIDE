using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// One IDE instance plus one real fixture solution, shared by every test in a class.
    ///
    /// Creating a MainForm per test does not scale: each instance leaks (the file watcher was one
    /// culprit, fixed; there is at least one more), so successive opens of the same solution get
    /// slower and slower until the run exceeds its timeout and the host dies with no catchable
    /// managed exception. Measured on the same six tests: ~1-14 ms each behind a shared form,
    /// versus 6 s climbing to 1 m 16 s and then a crash with a form per test. That degradation is
    /// the likely explanation for full-suite runs appearing to "hang" partway through.
    ///
    /// So: open the IDE once per class, open the solution once, and give each test a clean view of
    /// the files via <see cref="RestoreFiles"/>.
    /// </summary>
    internal sealed class FixtureIde : IDisposable
    {
        private FixtureWorkspace _workspace;
        private bool _disposed;

        public MainForm Form { get; private set; }

        /// <summary>The real fixture directory that is open as a solution.</summary>
        public string Root { get { return _workspace.Root; } }

        private FixtureIde(FixtureWorkspace workspace, MainForm form)
        {
            _workspace = workspace;
            Form = form;
        }

        private static readonly System.Collections.Generic.Dictionary<string, FixtureIde> _shared =
            new System.Collections.Generic.Dictionary<string, FixtureIde>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The ONE IDE instance for this fixture, shared by every test class in the assembly.
        ///
        /// Per-class sharing was not enough: six classes still meant six MainForm instances in one
        /// test host, and the run still died. Whatever a MainForm fails to release accumulates per
        /// INSTANCE, so the only reliable answer is to create as few as possible — one per fixture,
        /// for the whole assembly, disposed in AssemblyCleanup.
        ///
        /// Tests must therefore leave the IDE usable for the next class: restore edited files
        /// (<see cref="RestoreFiles"/>) rather than assuming a fresh instance.
        /// </summary>
        public static FixtureIde Shared(params string[] fixtureSegments)
        {
            string key = string.Join("/", fixtureSegments);
            FixtureIde ide;
            if (_shared.TryGetValue(key, out ide) && ide != null && !ide._disposed) return ide;

            ide = Open(fixtureSegments);
            _shared[key] = ide;
            return ide;
        }

        /// <summary>Dispose every shared IDE. Called once from AssemblyCleanup.</summary>
        public static void DisposeShared()
        {
            foreach (var ide in new System.Collections.Generic.List<FixtureIde>(_shared.Values))
            {
                try { if (ide != null) ide.Dispose(); } catch { }
            }
            _shared.Clear();
        }

        public static FixtureIde Open(params string[] fixtureSegments)
        {
            FixtureWorkspace workspace = FixtureWorkspace.Open(fixtureSegments);
            MainForm form = TestMainForm.CreateComposed();
            try
            {
                FixtureSolution.RealizeHeadless(form);
                FixtureSolution.OpenSolution(form, workspace.Root);
            }
            catch
            {
                FixtureSolution.TearDown(form);
                workspace.Dispose();
                throw;
            }
            return new FixtureIde(workspace, form);
        }

        /// <summary>A path inside the fixture, asserted to exist.</summary>
        public string File_(params string[] segments)
        {
            return _workspace.File_(segments);
        }

        /// <summary>
        /// Open a fixture file and hand back its editor tab. If the tab is already open (a previous
        /// test in this class used it) its buffer is reloaded from disk, so each test starts from
        /// the committed content rather than inheriting the last test's edits.
        /// </summary>
        public EditorTabPage OpenFile(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (Form._openEditorTabs != null && Form._openEditorTabs.ContainsKey(full))
            {
                var existing = Form._openEditorTabs[full] as EditorTabPage;
                if (existing != null)
                {
                    existing.LoadFile(full);
                    TestMainForm.PumpUntilIdle(iterations: 3, delayMs: 10);
                    return existing;
                }
            }
            return FixtureSolution.OpenFile(Form, filePath);
        }

        /// <summary>
        /// Put every fixture file back to its committed content. Call from TestCleanup in classes
        /// whose tests edit code, so one test's edit cannot leak into the next.
        /// </summary>
        public void RestoreFiles()
        {
            _workspace.RestoreFiles();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Form first: the editor can still hold a file open, which would block the restore.
            FixtureSolution.TearDown(Form);
            Form = null;
            _workspace.Dispose();
            _workspace = null;
        }
    }
}
