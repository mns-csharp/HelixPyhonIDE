using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// An empty class diagram says why it is empty.
    ///
    /// Reported from the running IDE as "Class Designer Canvas is empty, something is broken" — and
    /// nothing was broken: the Show filter was on "Interfaces", and a category filter has nothing
    /// to say at SOLUTION level, where the diagram lists projects rather than types. The canvas
    /// cleared itself and offered no explanation, so the only reading left was failure.
    ///
    /// Three reasons, three sentences, each naming the control that fixes it.
    /// </summary>
    [TestClass]
    public class CanvasEmptyStateTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static string Why(MainForm form)
        {
            return (string)typeof(MainForm).GetMethod("WhyTheCanvasIsEmpty", Private).Invoke(form, null);
        }

        private static void SetField(MainForm form, string name, object value)
        {
            typeof(MainForm).GetField(name, Private).SetValue(form, value);
        }

        private static MainForm Shell()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.ClientSize = new Size(1280, 900);
            FixtureSolution.RealizeHeadless(form);
            TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
            return form;
        }

        [TestMethod]
        public void WithNoSolutionItSaysSo()
        {
            using (MainForm form = Shell())
            {
                StringAssert.Contains(Why(form) ?? "", "No solution open",
                    "an empty canvas with no solution should say that much");
                form.Close();
            }
        }

        /// <summary>
        /// The reported case: a category filter at solution level. The canvas is cleared BY DESIGN
        /// there — categories filter types, and the solution view has none — so the explanation is
        /// the whole of the fix.
        /// </summary>
        [TestMethod]
        public void ACategoryFilterAtSolutionLevelExplainsItselfAndNamesTheControl()
        {
            using (MainForm form = Shell())
            {
                var solution = new HelixPythonNamespace.Core.Model.SolutionModel("s", "s.toml");
                SetField(form, "_currentSolution", solution);
                // The state a solution opens in — the diagram showing the solution and its projects.
                // Without it the canvas has no level, and "which reason" cannot be answered.
                SetField(form, "_navigationState",
                    new HelixPythonNamespace.UI.Rendering.NavigationViewState(solution.Name));

                var combo = (ComboBox)typeof(MainForm).GetField("_designerCategoryCombo", Private).GetValue(form);
                combo.SelectedIndex = 1;      // Interfaces
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);

                string why = Why(form);
                Assert.IsNotNull(why, "a canvas cleared by the filter must say so");
                StringAssert.Contains(why, "Interfaces", "the message should name the filter that is hiding things");
                StringAssert.Contains(why, "Show", "the message should name the control that fixes it");
                StringAssert.Contains(why, "All", "the message should say what to set it to");

                form.Close();
            }
        }

        /// <summary>And with nothing ticked, it points at the tree instead.</summary>
        [TestMethod]
        public void WithNothingCheckedItPointsAtTheSolutionExplorer()
        {
            using (MainForm form = Shell())
            {
                var solution = new HelixPythonNamespace.Core.Model.SolutionModel("s", "s.toml");
                SetField(form, "_currentSolution", solution);

                var combo = (ComboBox)typeof(MainForm).GetField("_designerCategoryCombo", Private).GetValue(form);
                combo.SelectedIndex = 0;      // All — so the filter is not the reason
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);

                string why = Why(form);
                Assert.IsNotNull(why, "an empty canvas with nothing checked must say so");
                StringAssert.Contains(why, "Solution Explorer",
                    "the message should send the user to the tree, which is where the checkboxes are");

                form.Close();
            }
        }

        /// <summary>The hint is a real control over the canvas, shown and hidden with the reason.</summary>
        [TestMethod]
        public void TheHintIsPutOnScreenAndTakenAwayAgain()
        {
            using (MainForm form = Shell())
            {
                var solution = new HelixPythonNamespace.Core.Model.SolutionModel("s", "s.toml");
                SetField(form, "_currentSolution", solution);
                typeof(MainForm).GetMethod("UpdateDesignerCanvasEmptyHint", Private).Invoke(form, null);

                var hint = (Label)typeof(MainForm).GetField("_designerEmptyHint", Private).GetValue(form);
                Assert.IsNotNull(hint, "no hint control was created");
                var host = (Control)typeof(MainForm).GetField("_designerCanvasHost", Private).GetValue(form);
                Assert.IsTrue(host.Controls.Contains(hint), "the hint is not over the canvas");
                Assert.IsTrue(hint.Text.Length > 0, "the hint is on screen with nothing written on it");

                form.Close();
            }
        }
    }
}
