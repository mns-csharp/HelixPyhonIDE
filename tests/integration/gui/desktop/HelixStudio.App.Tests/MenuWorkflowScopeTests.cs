using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 320. Menus are workflow-scoped: what cannot apply right now is hidden, not shown dead.
    ///
    ///  * Top-level menus — with no solution loaded there is nothing to build, analyse, test,
    ///    measure, profile, diagram, debug or commit, so those eight menus disappear.
    ///  * Debug menu — the session commands (Pause, Restart, Stop, the Step family, Run to Cursor)
    ///    only appear once a session is live. This also closed a real inconsistency: enable-state was
    ///    applied to the debug TOOLBAR only, so the menu's Step commands sat fully enabled with no
    ///    session while the identical toolbar buttons were correctly greyed.
    /// </summary>
    [TestClass]
    public class MenuWorkflowScopeTests
    {
        private static readonly string[] SolutionScopedMenus =
        {
            "buildTopMenuItem", "analysisMenuItem", "testsMenuItem", "metricsMenuItem",
            "profilerMenuItem", "diagramMenuItem", "debugMenuItem", "gitMenuItem",
        };

        private static readonly string[] AlwaysAvailableMenus =
        {
            "fileMenuItem", "editMenuItem", "viewMenuItem", "toolsMenuItem", "helpMenuItem",
        };

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static void SetField(object obj, string name, object value)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            f.SetValue(obj, value);
        }

        private static void OpenDebugMenu(MainForm form)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnDebugMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnDebugMenuOpening expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        /// <summary>Finds a Debug drop-down entry by label fragment, as the production code does.</summary>
        private static ToolStripItem DebugItem(MainForm form, string token)
        {
            var debug = Field<ToolStripMenuItem>(form, "debugMenuItem");
            foreach (ToolStripItem it in debug.DropDownItems)
            {
                string text = (it.Text ?? string.Empty).Replace("&", string.Empty);
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return it;
            }
            return null;
        }

        [TestMethod]
        public void WithNoSolution_TheSolutionScopedMenusAreHidden()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form.UpdateMenuWorkflowVisibility();

                foreach (string n in SolutionScopedMenus)
                    Assert.IsFalse(Field<ToolStripMenuItem>(form, n).Available,
                        n + " has nothing to act on with no solution loaded");

                foreach (string n in AlwaysAvailableMenus)
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                        n + " must always remain — it is how the user gets in and configures the IDE");
            }
        }

        [TestMethod]
        public void OnceASolutionIsLoaded_TheyComeBack()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetField(form, "_currentSolution", new SolutionModel(@"C:\proj\pyproject.toml", "Proj"));
                form.UpdateMenuWorkflowVisibility();

                foreach (string n in SolutionScopedMenus)
                    Assert.IsTrue(Field<ToolStripMenuItem>(form, n).Available,
                        n + " should be available once a solution is open");
            }
        }

        [TestMethod]
        public void WithNoDebugSession_OnlyThePreSessionCommandsAreShown()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                OpenDebugMenu(form);

                foreach (string token in new[] { "Pause", "Restart", "Stop", "Step Over",
                                                 "Step Into", "Step Out", "Run to Cursor" })
                {
                    ToolStripItem it = DebugItem(form, token);
                    Assert.IsNotNull(it, token + " expected in the Debug menu");
                    Assert.IsFalse(it.Available, token + " cannot apply with no session running");
                }

                // Meaningful before a session exists, so these stay.
                foreach (string token in new[] { "Continue", "Toggle Breakpoint",
                                                 "Break on Entry", "Break on Raised", "Break on Uncaught" })
                {
                    ToolStripItem it = DebugItem(form, token);
                    Assert.IsNotNull(it, token + " expected in the Debug menu");
                    Assert.IsTrue(it.Available, token + " should stay available before a session starts");
                }
            }
        }

        [TestMethod]
        public void TheStepCommands_AreNoLongerEnabledWithoutASession()
        {
            // The regression this guards: enable-state was pushed to the debug toolbar only, so the
            // menu's Step commands were left at their designer default (enabled) forever.
            using (var form = TestMainForm.CreateComposed())
            {
                foreach (string token in new[] { "Step Over", "Step Into", "Step Out" })
                    DebugItem(form, token).Enabled = true;    // the pre-fix state

                OpenDebugMenu(form);

                foreach (string token in new[] { "Step Over", "Step Into", "Step Out" })
                    Assert.IsFalse(DebugItem(form, token).Enabled,
                        token + " must not be enabled while nothing is paused");
            }
        }

        [TestMethod]
        public void TheDebugMenuMatchesTheToolbarInvariant()
        {
            // Both surfaces derive from the same pure function, so they can no longer drift.
            var idle = HelixPythonNamespace.App.Coordinators.DebugCoordinator.ComputeToolbarState(false, false);
            var running = HelixPythonNamespace.App.Coordinators.DebugCoordinator.ComputeToolbarState(true, false);
            var paused = HelixPythonNamespace.App.Coordinators.DebugCoordinator.ComputeToolbarState(true, true);

            Assert.IsFalse(idle.Step, "nothing to step when no session exists");
            Assert.IsFalse(running.Step, "stepping requires a paused session");
            Assert.IsTrue(paused.Step);
            Assert.IsTrue(idle.Continue, "Continue doubles as Start when idle");
            Assert.IsFalse(running.Continue, "nothing to resume while already running");
        }
    }
}
