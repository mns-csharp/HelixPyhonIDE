namespace HelixPythonNamespace.Editor.Tests
{
    using System;
    using System.Reflection;
    using System.Windows.Forms;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor;

    /// <summary>
    /// Iter 348 (screenshot audit): the navigation bar listed a file's symbols and jumped to them,
    /// but never showed which one the caret was in — a screenshot of SymPy's constructor.py had the
    /// caret on line 16, squarely inside <c>def _construct_simple(...)</c>, with all three dropdowns
    /// still reading their placeholder. "Where am I?" is the question a navigation bar exists to
    /// answer, so the bar now tracks the caret.
    ///
    /// NavigationBar is a plain Panel of ComboBoxes — no Scintilla, so this runs as a unit test.
    /// </summary>
    [TestClass]
    public class NavigationBarCaretTrackingTests
    {
        private const string Source =
            "import os\n" +                       // 1
            "\n" +                                // 2
            "TOP_LEVEL = 3\n" +                   // 3
            "\n" +                                // 4
            "def helper(x):\n" +                  // 5
            "    def inner(y):\n" +               // 6
            "        return y\n" +                // 7
            "    return inner(x)\n" +             // 8
            "\n" +                                // 9
            "class Shape:\n" +                    // 10
            "    def area(self):\n" +             // 11
            "        return 0\n";                 // 12

        [TestMethod]
        public void TheFunctionsDropdown_ShowsTheFunctionTheCaretIsIn()
        {
            using (NavigationBar bar = Populated())
            {
                bar.SyncToLine(8);   // inside helper, past the nested def
                Assert.AreEqual("helper", Selected(bar, "_functions"));
            }
        }

        [TestMethod]
        public void NestedSymbols_ResolveToTheInnermostOne()
        {
            using (NavigationBar bar = Populated())
            {
                bar.SyncToLine(7);   // inside helper.inner
                Assert.AreEqual("helper.inner", Selected(bar, "_functions"),
                    "the caret is in the nested helper, so that is where the reader is");
            }
        }

        [TestMethod]
        public void AMethodBody_ShowsBothItsClassAndItsMethod()
        {
            using (NavigationBar bar = Populated())
            {
                bar.SyncToLine(12);
                Assert.AreEqual("Shape", Selected(bar, "_classes"));
                Assert.AreEqual("Shape.area", Selected(bar, "_functions"));
            }
        }

        /// <summary>
        /// The case a "nearest symbol above" implementation would get wrong: the import block at the
        /// top is not inside anything, so the bar must say so rather than pointing at a symbol the
        /// caret is nowhere near.
        /// </summary>
        [TestMethod]
        public void OutsideEverySymbol_TheDropdownsFallBackToTheirPlaceholder()
        {
            using (NavigationBar bar = Populated())
            {
                bar.SyncToLine(1);
                Assert.AreEqual("Functions", Selected(bar, "_functions"));
                Assert.AreEqual("Classes", Selected(bar, "_classes"));
            }
        }

        [TestMethod]
        public void ModuleLevelVariables_AreTrackedToo()
        {
            using (NavigationBar bar = Populated())
            {
                bar.SyncToLine(3);
                Assert.AreEqual("TOP_LEVEL", Selected(bar, "_variables"));
            }
        }

        /// <summary>Syncing must never fire the navigate event — that would make caret movement jump.</summary>
        [TestMethod]
        public void SyncingDoesNotRaiseNavigateToLine()
        {
            using (NavigationBar bar = Populated())
            {
                int raised = 0;
                bar.NavigateToLine += delegate { raised++; };
                bar.SyncToLine(7);
                bar.SyncToLine(12);
                bar.SyncToLine(1);
                Assert.AreEqual(0, raised, "tracking the caret must not navigate the editor");
            }
        }

        private static NavigationBar Populated()
        {
            var bar = new NavigationBar();
            bar.Populate(Source, "sample.py");
            return bar;
        }

        private static string Selected(NavigationBar bar, string fieldName)
        {
            FieldInfo f = typeof(NavigationBar).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, fieldName + " field expected on NavigationBar");
            var cb = (ComboBox)f.GetValue(bar);
            return cb.SelectedItem != null ? cb.SelectedItem.ToString() : null;
        }
    }
}
