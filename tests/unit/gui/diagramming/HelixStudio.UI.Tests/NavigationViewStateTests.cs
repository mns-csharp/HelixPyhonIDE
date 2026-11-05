using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class NavigationViewStateTests
    {
        [TestMethod]
        public void NavigateBack_DrillsUpThroughEveryParentLevel()
        {
            NavigationViewState state = new NavigationViewState("Example")
                .DrillDownToProjects()
                .DrillDownToNamespaces("Application")
                .DrillDownToTypes("Application.Services");

            Assert.AreEqual(NavigationLevel.Type, state.Level);
            Assert.AreEqual(
                "Example > Projects > Application > Application.Services",
                state.GetBreadcrumbDisplay());

            state = state.NavigateBack();
            Assert.AreEqual(NavigationLevel.Namespace, state.Level);
            Assert.AreEqual("Application", state.ProjectName);
            Assert.AreEqual("Example > Projects > Application", state.GetBreadcrumbDisplay());

            state = state.NavigateBack();
            Assert.AreEqual(NavigationLevel.Project, state.Level);
            Assert.AreEqual("Example > Projects", state.GetBreadcrumbDisplay());

            state = state.NavigateBack();
            Assert.AreEqual(NavigationLevel.Solution, state.Level);
            Assert.AreEqual("Example", state.GetBreadcrumbDisplay());
            Assert.IsNull(state.NavigateBack(), "The solution is the root of the hierarchy.");
        }
    }
}
