using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 321. The toolbar follows the same workflow rule as the menus: a button the current
    /// workflow cannot use is hidden, not left greyed.
    ///
    /// Note for future edits: the toolbar buttons declared in MainForm.Designer.cs are a DESIGN-TIME
    /// mirror only — MenuCoordinator.Build replaces host._toolStrip wholesale at runtime. Setting
    /// Available on a designer field therefore does nothing at runtime; the live buttons are reached
    /// through the fields MenuCoordinator assigns back, or the Name it stamps on the rest.
    /// </summary>
    [TestClass]
    public class ToolbarWorkflowScopeTests
    {
        private static ToolStrip Bar(MainForm form)
        {
            FieldInfo f = typeof(MainForm).GetField("_toolStrip", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_toolStrip field expected");
            return (ToolStrip)f.GetValue(form);
        }

        private static ToolStripItem ByName(MainForm form, string name)
        {
            foreach (ToolStripItem it in Bar(form).Items)
                if (it.Name == name) return it;
            return null;
        }

        private static void SetSolution(MainForm form, SolutionModel solution)
        {
            FieldInfo f = typeof(MainForm).GetField("_currentSolution", BindingFlags.Instance | BindingFlags.NonPublic);
            f.SetValue(form, solution);
        }

        private static readonly string[] SolutionScopedButtons =
        {
            "tbSaveLayout", "tbRefreshCanvas", "tbAddInterface", "tbAddAbstract", "tbAddConcrete",
            "tbAnalyze", "tbCompile", "tbZoomIn", "tbZoomOut", "tbZoomFit",
        };

        [TestMethod]
        public void EveryRuntimeToolbarButton_IsNamed_SoItCanBeScoped()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                foreach (string n in SolutionScopedButtons)
                    Assert.IsNotNull(ByName(form, n),
                        n + " missing — MenuCoordinator must stamp a stable Name on every scoped button");
            }
        }

        [TestMethod]
        public void WithNoSolution_TheSolutionScopedButtonsAreHidden()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form.UpdateToolbarWorkflowVisibility();

                foreach (string n in SolutionScopedButtons)
                    Assert.IsFalse(ByName(form, n).Available,
                        n + " has nothing to act on with no solution loaded");

                // The way in stays.
                foreach (string n in new[] { "tbNew", "tbOpen", "tbImport" })
                    Assert.IsTrue(ByName(form, n).Available, n + " is an entry point");
            }
        }

        [TestMethod]
        public void OnceASolutionIsLoaded_TheyComeBack()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, new SolutionModel(@"C:\proj\pyproject.toml", "Proj"));
                form.UpdateToolbarWorkflowVisibility();

                foreach (string n in SolutionScopedButtons)
                    Assert.IsTrue(ByName(form, n).Available, n + " should return once a solution is open");
            }
        }

        [TestMethod]
        public void TheSessionOnlyDebugButtons_MatchTheDebugMenu()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form, new SolutionModel(@"C:\proj\pyproject.toml", "Proj"));
                form.UpdateToolbarWorkflowVisibility();

                // Start/Continue and Toggle Breakpoint begin a session, so they show with a solution.
                Assert.IsTrue(Field<ToolStripButton>(form, "_dbgContinueButton").Available);
                Assert.IsTrue(Field<ToolStripButton>(form, "_dbgToggleBpButton").Available);

                // The rest only exist while a session is live — same rule the Debug menu applies.
                foreach (string n in new[] { "_dbgPauseButton", "_dbgRestartButton", "_dbgStopButton",
                                             "_dbgStepOverButton", "_dbgStepIntoButton", "_dbgStepOutButton" })
                    Assert.IsFalse(Field<ToolStripButton>(form, n).Available,
                        n + " should be hidden with no debug session running");
            }
        }

        [TestMethod]
        public void ScopingNeverLeavesAnOrphanedSeparator()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form.UpdateToolbarWorkflowVisibility();

                bool groupHasContent = false;
                foreach (ToolStripItem it in Bar(form).Items)
                {
                    if (it is ToolStripSeparator sep)
                    {
                        if (sep.Available)
                            Assert.IsTrue(groupHasContent,
                                "a separator is visible but the group before it is empty");
                        groupHasContent = false;
                        continue;
                    }
                    if (it.Available) groupHasContent = true;
                }
            }
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }
    }
}
