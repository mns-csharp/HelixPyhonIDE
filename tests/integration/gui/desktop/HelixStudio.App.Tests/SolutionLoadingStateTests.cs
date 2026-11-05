using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 350 (screenshot audit): a capture taken while SymPy was still loading showed the status
    /// bar saying "Loading solution..." and the Solution Explorer, in the same window, saying "No
    /// solution open. Open one from the Welcome page…" — inviting the user to start the very load
    /// they were waiting on. The panel now says what is actually happening.
    ///
    /// The hazard introduced by that fix is a placeholder stuck on "Loading…" forever when a load
    /// fails, so every exit path is covered here.
    /// </summary>
    [TestClass]
    public class SolutionLoadingStateTests
    {
        [TestMethod]
        public void WhileLoading_ThePanelNamesTheSolution_InsteadOfSayingNothingIsOpen()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.BeginLoading("sympy");

                string text = EmptyText(panel);
                StringAssert.Contains(text, "sympy", "the user should see which solution is loading");
                StringAssert.Contains(text, "Loading");
                Assert.IsFalse(text.Contains("No solution open"),
                    "telling the user nothing is open while a load runs is the bug being fixed");
                Assert.IsTrue(panel.IsShowingLoading);
            }
        }

        [TestMethod]
        public void WhenTheSolutionArrives_ThePlaceholderGoesBackToIdle()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.BeginLoading("sympy");
                panel.LoadSolution(new SolutionModel(@"C:\p\pyproject.toml", "P"));

                Assert.IsFalse(panel.IsShowingLoading);
                Assert.AreEqual(ProjectTreePanel.IdleEmptyText, EmptyText(panel));
            }
        }

        /// <summary>A load that fails must not leave the panel claiming forever that one is running.</summary>
        [TestMethod]
        public void AFailedLoad_DoesNotLeaveThePlaceholderStuck()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.BeginLoading("sympy");
                panel.EndLoading();

                Assert.IsFalse(panel.IsShowingLoading);
                Assert.AreEqual(ProjectTreePanel.IdleEmptyText, EmptyText(panel));
            }
        }

        [TestMethod]
        public void ClosingTheSolution_AlsoResetsThePlaceholder()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.BeginLoading("sympy");
                panel.Clear();

                Assert.IsFalse(panel.IsShowingLoading);
                Assert.AreEqual(ProjectTreePanel.IdleEmptyText, EmptyText(panel));
            }
        }

        /// <summary>
        /// Every pyproject.toml is called "pyproject.toml", so the folder is the recognisable part —
        /// the same choice the Welcome page's recent list makes.
        /// </summary>
        [TestMethod]
        public void TheLoadingNameComesFromTheFolder_NotTheFileName()
        {
            Assert.AreEqual("sympy", DisplayName(@"D:\repos\sympy\pyproject.toml"));
            Assert.AreEqual("solution", DisplayName(null));
            Assert.AreEqual("solution", DisplayName(string.Empty));
        }

        private static string DisplayName(string path)
        {
            MethodInfo m = typeof(HelixPythonNamespace.App.Coordinators.SolutionCoordinator).GetMethod(
                "SolutionDisplayNameFor", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "SolutionCoordinator.SolutionDisplayNameFor(string) expected");
            return (string)m.Invoke(null, new object[] { path });
        }

        private static string EmptyText(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_emptyLabel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_emptyLabel field expected on ProjectTreePanel");
            var label = (System.Windows.Forms.Label)f.GetValue(panel);
            return label != null ? (label.Text ?? string.Empty) : string.Empty;
        }
    }
}
