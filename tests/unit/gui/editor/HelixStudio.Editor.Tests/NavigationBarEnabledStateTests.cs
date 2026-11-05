namespace HelixPythonNamespace.Editor.Tests
{
    using System.Reflection;
    using System.Windows.Forms;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor;

    /// <summary>
    /// Iter 433 (editor audit): a navigation dropdown with no navigable symbol is disabled, so the bar
    /// reads at a glance as "nothing here" rather than a dead control. A file with classes and
    /// functions but no module-level variables greys only the Variables dropdown; adding a module
    /// variable re-enables it.
    /// </summary>
    [TestClass]
    public class NavigationBarEnabledStateTests
    {
        private static ComboBox Combo(NavigationBar bar, string fieldName)
        {
            FieldInfo f = typeof(NavigationBar).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (ComboBox)f.GetValue(bar);
        }

        [TestMethod]
        public void EmptyVariables_IsDisabled_WhileClassesAndFunctionsEnabled()
        {
            using (var bar = new NavigationBar())
            {
                bar.Populate(
                    "def helper():\n" +
                    "    return 1\n" +
                    "class Shape:\n" +
                    "    def area(self):\n" +
                    "        return 0\n",
                    "sample.py");
                Assert.IsTrue(Combo(bar, "_classes").Enabled, "there is a class to navigate to");
                Assert.IsTrue(Combo(bar, "_functions").Enabled, "there is a function to navigate to");
                Assert.IsFalse(Combo(bar, "_variables").Enabled, "no module variables -> disabled");
            }
        }

        [TestMethod]
        public void ModuleVariable_ReEnablesVariablesDropdown()
        {
            using (var bar = new NavigationBar())
            {
                bar.Populate("TOP_LEVEL = 3\n", "sample.py");
                Assert.IsTrue(Combo(bar, "_variables").Enabled, "a module variable is navigable");
            }
        }

        [TestMethod]
        public void EmptyFile_DisablesAllThreeDropdowns()
        {
            using (var bar = new NavigationBar())
            {
                bar.Populate("# just a comment\n", "sample.py");
                Assert.IsFalse(Combo(bar, "_classes").Enabled);
                Assert.IsFalse(Combo(bar, "_functions").Enabled);
                Assert.IsFalse(Combo(bar, "_variables").Enabled);
            }
        }
    }
}
