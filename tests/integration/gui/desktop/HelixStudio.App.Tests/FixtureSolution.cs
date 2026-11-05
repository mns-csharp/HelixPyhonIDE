using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Loads REAL solutions from <c>tests/fixtures/</c> for GUI tests.
    ///
    /// Standing rule: HelixStudio is tested by opening a real fixture solution, never by fabricating
    /// a project on the fly. Synthetic sources carry none of what real code carries — include
    /// graphs, package layout, manifests, build systems, deep files — and features have repeatedly
    /// passed against generated snippets while failing on the fixtures. A test that writes its own
    /// <c>pyproject.toml</c> and a <c>shapes.py</c> full of "# filler line N" is testing a project
    /// that no user will ever have.
    ///
    /// Tests that MUTATE source (rename, delete, move, add) must work on <see cref="CopyToTemp"/> —
    /// a copy of the fixture — so the committed fixture is never edited by a test run.
    /// </summary>
    internal static class FixtureSolution
    {
        /// <summary>The repo's tests/fixtures directory.</summary>
        public static string Root()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && Directory.GetFiles(dir.FullName, "*.sln").Length == 0)
                dir = dir.Parent;
            Assert.IsNotNull(dir, "could not locate the repository root (the product .sln)");
            string fixtures = Path.Combine(dir.FullName, "tests", "fixtures");
            Assert.IsTrue(Directory.Exists(fixtures), "fixtures directory missing: " + fixtures);
            return fixtures;
        }

        /// <summary>A fixture solution directory, e.g. Path("python", "python-test-sample-oop").</summary>
        public static string Path_(params string[] segments)
        {
            string p = Root();
            foreach (string s in segments) p = Path.Combine(p, s);
            Assert.IsTrue(Directory.Exists(p), "fixture missing: " + p);
            return p;
        }

        /// <summary>
        /// Copy a fixture solution into a fresh temp directory and return that directory. Build
        /// trees are skipped so the copy configures clean.
        ///
        /// The copy is NOT deleted on cleanup by default: an `async void` reload continuation from
        /// an earlier test can still be reading the directory when a later test runs, and deleting
        /// it underneath pops a real "Error Reloading" MessageBox on the developer's desktop with no
        /// test left to dismiss it. Leaving it under %TEMP% is the documented safe behaviour.
        /// </summary>
        public static string CopyToTemp(params string[] fixtureSegments)
        {
            string src = Path_(fixtureSegments);
            string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix_fx_" + System.IO.Path.GetFileName(src) + "_" + Guid.NewGuid().ToString("N"));
            CopyTree(src, dst);
            return dst;
        }

        private static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(src.Length).TrimStart('\\', '/');
                if (IsSkipped(rel)) continue;
                Directory.CreateDirectory(System.IO.Path.Combine(dst, rel));
            }
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\', '/');
                if (IsSkipped(rel)) continue;
                string target = System.IO.Path.Combine(dst, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                try { File.Copy(f, target, true); } catch { }
            }
        }

        private static bool IsSkipped(string relativePath)
        {
            string p = "\\" + relativePath.Replace('/', '\\') + "\\";
            foreach (string seg in new[] { "\\build\\", "\\cmake-build", "\\_deps\\", "\\CMakeFiles\\",
                                           "\\.git\\", "\\__pycache__\\", "\\TestResults\\", "\\target\\" })
                if (p.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// The 1-based line a declaration sits on in a fixture file, so a test can assert against
        /// the fixture's REAL layout instead of hard-coding a number that silently rots when the
        /// fixture is edited.
        /// </summary>
        public static int LineOf(string filePath, string declaration)
        {
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimEnd().StartsWith(declaration, StringComparison.Ordinal)) return i + 1;
            Assert.Fail("'" + declaration + "' not found in " + filePath);
            return -1;
        }

        /// <summary>
        /// Which fixture solution stands in for each language. Choosing WHICH fixture to open is
        /// test data; how it opens is the IDE's business and is never restated here.
        /// </summary>
        public static string DirectoryFor(ProjectLanguage language, bool cuda)
        {
            switch (language)
            {
                case ProjectLanguage.Python: return Path_("python", "simple-project");
                // The plain-C++ arm lives under `cuda/` too: this fork keeps only the Python and
                // CUDA fixture trees, and `cuda/cmake-solution` is an ordinary C++ CMake solution
                // that happens to sit there. The directory it is filed under is not its language.
                default: return cuda ? Path_("cuda", "CudaCppMultiPackageWorkspace")
                                     : Path_("cuda", "cmake-solution");
            }
        }

        /// <summary>
        /// OPEN a fixture solution through the IDE's own File ▸ Open Solution path, and wait for it.
        ///
        /// <see cref="MainForm.FindOpenableProjectAnchor"/> picks the anchor file, and
        /// <see cref="MainForm.OpenSolutionAsync(string)"/> does every bit of the routing —
        /// registry detection, the native CMake branch for C++/CUDA, the per-language provider
        /// layer, the Python fallback. A test must not restate any of that: an earlier
        /// version of this helper carried its own per-language manifest table and a special case
        /// that bypassed the provider entirely, which meant the tests were exercising a loader the
        /// test author wrote rather than the one the user's click runs.
        ///
        /// OpenSolutionAsync is `async void`, so this pumps until the solution is adopted.
        /// </summary>
        public static SolutionModel OpenSolution(MainForm form, string solutionDirectory)
        {
            string anchor = MainForm.FindOpenableProjectAnchor(solutionDirectory);
            Assert.IsNotNull(anchor,
                "the IDE found nothing openable in this fixture directory: " + solutionDirectory);

            // MSTest clears the context per test; this is the await that must capture it.
            TestMainForm.EnsureUiSynchronizationContext();
            form.OpenSolutionAsync(anchor);

            // Getting this wait right took three attempts, so the reasoning is recorded:
            //   * _currentSolution alone is assigned partway through the open — returning there
            //     hands the test a loaded model behind an empty tree (166 types, 0 nodes).
            //   * "tree has nodes" is ALSO too early: BeginLoading puts a placeholder node in the
            //     Solution Explorer ("Loading <solution>…") before the load has done its work, so
            //     the condition is satisfied while _layoutStore is still null. A rename right after
            //     that then failed the reload with "Value cannot be null. Parameter name:
            //     layoutStore" — which looked like a product bug and was purely a premature wait.
            // _layoutStore is the reliable "the open got past its setup stage" signal — it is
            // assigned immediately before the tree is filled, and it is what the rename path needs.
            // The budget is generous because this is a genuine load of a real multi-package
            // solution through the real loader, and it competes with everything else in a shared
            // test host; a 15s budget expired mid-load and produced a misleading failure.
            // Phase 1 — the solution model must be adopted. This is the only hard requirement, and
            // every caller depends on it.
            for (int i = 0; i < 2400 && form._currentSolution == null; i++)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(25);
            }

            // Phase 2 — give the Solution Explorer and layout store a chance to finish, but do NOT
            // require them. Requiring them here was a mistake that cost a lot of time: on a %TEMP%
            // copy of a fixture the tree can stay empty and the layout store null with no error
            // reported, so a hard wait simply burned the whole budget and failed tests that never
            // touched either. Tests that genuinely need them assert for themselves — the tree tests
            // in their ClassInitialize, reload tests via OpenSolutionForReload.
            for (int i = 0; i < 200; i++)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(25);
                if (form._layoutStore != null && TreeNodeCount(form) > 0) break;
            }
            // Deliberately NOT asserted: _layoutStore is reached on the committed fixtures but not
            // always on a %TEMP% copy, and the open reports no error when it stops short — the
            // solution and the tree are both there. Only the rename/reload path actually needs the
            // layout store (a null one fails the reload with "Value cannot be null. Parameter name:
            // layoutStore"), so tests that trigger a reload assert it for themselves rather than
            // every caller paying for it. Asserting it here broke every editor test.

            Assert.IsNotNull(form._currentSolution,
                "File ▸ Open Solution never adopted a solution for " + anchor);
            return form._currentSolution;
        }

        /// <summary>Nodes currently in the IDE's Solution Explorer (0 before it is populated).</summary>
        internal static int TreeNodeCount(MainForm form)
        {
            if (form._projectTreePanel == null) return 0;
            var field = typeof(HelixPythonNamespace.UI.Controls.ProjectTreePanel).GetField(
                "_treeView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) return 0;
            var tree = field.GetValue(form._projectTreePanel) as System.Windows.Forms.TreeView;
            if (tree == null) return 0;

            int count = 0;
            var stack = new System.Collections.Generic.Stack<System.Windows.Forms.TreeNode>();
            foreach (System.Windows.Forms.TreeNode r in tree.Nodes) stack.Push(r);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                count++;
                foreach (System.Windows.Forms.TreeNode c in n.Nodes) stack.Push(c);
            }
            return count;
        }

        /// <summary>
        /// Open a fixture solution AND wait for the Solution Explorer to be populated, for tests
        /// that assert on tree contents. OpenSolution treats the tree as best-effort (a %TEMP% copy
        /// can leave it empty with no error reported), so a tree test must state the requirement
        /// itself instead of relying on timing.
        /// </summary>
        public static SolutionModel OpenSolutionForTree(MainForm form, string solutionDirectory)
        {
            SolutionModel solution = OpenSolution(form, solutionDirectory);

            // 120 seconds, not 60. The loop exits the moment the tree populates, so a longer cap
            // costs nothing on a healthy open — it only decides how marginal a slow one has to be
            // before the assertion below fires. The old 60s budget was already marginal for
            // ProblemsPanelQuickFixTests.TheHost_OffersEveryMemberPackageOfTheOpenSolution (which
            // opens several solutions in one test), and every panel added to MainForm's startup
            // pushes it closer: it went over when the AI Terminal landed, and again when the Live
            // Simulation panel did. A test that fails on how much the shell has grown is measuring
            // the wrong thing.
            for (int i = 0; i < 4800 && TreeNodeCount(form) == 0; i++)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(25);
            }
            Assert.IsTrue(TreeNodeCount(form) > 0,
                "the Solution Explorer never populated for " + solutionDirectory);
            return solution;
        }

        /// <summary>
        /// Open a fixture solution AND require the layout store, for tests that go on to trigger a
        /// reload (rename, refactor). Without it the reload fails with "Value cannot be null.
        /// Parameter name: layoutStore".
        /// </summary>
        public static SolutionModel OpenSolutionForReload(MainForm form, string solutionDirectory)
        {
            SolutionModel solution = OpenSolution(form, solutionDirectory);
            Assert.IsNotNull(form._layoutStore,
                "this test triggers a reload, which needs the layout store, but the open never " +
                "reached that stage for " + solutionDirectory);
            return solution;
        }

        /// <summary>Open the fixture solution that represents <paramref name="language"/>.</summary>
        public static SolutionModel OpenSolution(MainForm form, ProjectLanguage language, bool cuda = false)
        {
            return OpenSolution(form, DirectoryFor(language, cuda));
        }

        /// <summary>
        /// A real type of the given kind from a loaded solution. Tree/filter tests used to invent a
        /// "Vehicle" class with a "start_engine" method under <c>C:\ws\</c>; asking the loaded model
        /// for one of its own symbols means the assertion tracks whatever the parser really produced.
        /// </summary>
        public static TypeModel FirstType(SolutionModel solution, TypeKind kind)
        {
            foreach (ProjectModel p in solution.Projects)
                foreach (TypeModel t in p.Types)
                    if (t.TypeKind == kind) return t;
            Assert.Fail("no " + kind + " in the loaded fixture solution " + solution.FilePath);
            return null;
        }

        /// <summary>A real member of the given kind, together with the type that declares it.</summary>
        public static MemberModel FirstMember(SolutionModel solution, MemberKind kind, out TypeModel owner)
        {
            foreach (ProjectModel p in solution.Projects)
                foreach (TypeModel t in p.Types)
                    foreach (MemberModel m in t.Members)
                        if (m.MemberKind == kind) { owner = t; return m; }
            Assert.Fail("no " + kind + " member in the loaded fixture solution " + solution.FilePath);
            owner = null;
            return null;
        }

        /// <summary>A real module-level function from the loaded solution.</summary>
        public static FunctionModel FirstFunction(SolutionModel solution)
        {
            foreach (ProjectModel p in solution.Projects)
                foreach (FunctionModel f in p.Functions)
                    return f;
            Assert.Fail("no module-level function in the loaded fixture solution " + solution.FilePath);
            return null;
        }

        /// <summary>
        /// A real source file of the given extension from a fixture solution — for tests that need
        /// "a file of language X" and used to invent <c>a.py</c> / <c>a.cs</c> names that never
        /// existed on disk. Build trees are skipped so generated compiler probes never win.
        /// </summary>
        public static string AnySourceFile(string solutionDirectory, string extension)
        {
            foreach (string f in Directory.GetFiles(solutionDirectory, "*" + extension,
                                                    SearchOption.AllDirectories))
            {
                string rel = f.Substring(solutionDirectory.Length);
                if (IsSkipped(rel.TrimStart('\\', '/'))) continue;
                return f;
            }
            Assert.Fail("no *" + extension + " file in fixture " + solutionDirectory);
            return null;
        }

        /// <summary>
        /// Open a fixture solution's real file in the IDE editor and hand back its tab.
        /// </summary>
        public static HelixPythonNamespace.Editor.EditorTabPage OpenFile(MainForm form, string filePath, int line = -1)
        {
            form.OpenInEditor(filePath, line);
            TestMainForm.PumpUntilIdle(iterations: 5, delayMs: 10);

            string full = System.IO.Path.GetFullPath(filePath);
            Assert.IsTrue(form._openEditorTabs.ContainsKey(full),
                "the IDE did not open an editor tab for " + full);
            var tab = form._openEditorTabs[full] as HelixPythonNamespace.Editor.EditorTabPage;
            Assert.IsNotNull(tab, "the open tab must be an EditorTabPage: " + full);
            tab.Editor.CreateControl();
            TestMainForm.PumpUntilIdle(iterations: 3, delayMs: 10);
            return tab;
        }

        /// <summary>
        /// Give a MainForm a real window handle WITHOUT ever making it visible.
        ///
        /// GUI tests need a realized HWND (Scintilla will not initialise otherwise), but they must
        /// never put a window on the developer's screen. An earlier version of this tried to park
        /// the form at (-32000,-32000) and call Show(). That cannot work, and the reason matters:
        /// MainForm restores its saved size, maximizes when <c>WindowMaximized</c> is set, and its
        /// OnShown handler deliberately CLAMPS an off-screen window back onto the work area — right
        /// product behaviour (a window saved on a disconnected monitor must come back), which simply
        /// defeats any attempt to hide by position. A user watched a test run take over their
        /// desktop because of it.
        ///
        /// CreateControl realizes the handle and its children without a WS_VISIBLE window, so the
        /// OnShown path never runs and nothing appears.
        /// </summary>
        /// <summary>
        /// Tear down a headless MainForm safely.
        ///
        /// Do NOT call Close(): it posts WM_CLOSE, which disposes the form from inside the window
        /// procedure. If any handle creation is still in flight — likely, since the IDE creates
        /// panels lazily and the open runs async — that throws "Value Dispose() cannot be called
        /// while doing CreateHandle()" from deep inside WinForms, which takes the whole test host
        /// down mid-run. That crash is what left a fixture solution half-renamed on disk.
        /// Draining the queue first and disposing directly avoids the re-entrancy entirely.
        /// </summary>
        public static void TearDown(MainForm form)
        {
            if (form == null) return;
            try { TestMainForm.PumpUntilIdle(iterations: 10, delayMs: 20); } catch { }
            try { form.Dispose(); } catch { }
        }

        public static void RealizeHeadless(MainForm form)
        {
            // Enforced here so a GUI test cannot run unprotected: without this the IDE may raise a
            // MessageBox that no one can dismiss, blocking the STA thread and stranding a dialog on
            // the developer's desktop. Set once for every host in HelixPythonNamespace.runsettings.
            Assert.AreEqual("1", System.Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"),
                "GUI tests must run non-interactive — run with /Settings:HelixStudio.runsettings");

            TestMainForm.EnsureUiSynchronizationContext();

            form.ShowInTaskbar = false;
            form.CreateControl();
            System.IntPtr forceHandle = form.Handle;   // realize even if CreateControl deferred it
            Assert.AreNotEqual(System.IntPtr.Zero, forceHandle, "the form needs a real window handle");
            Assert.IsFalse(form.Visible, "a test must never make the IDE window visible");
            System.Windows.Forms.Application.DoEvents();
        }
    }
}
