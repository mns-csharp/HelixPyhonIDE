using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixTextEditorNET.Tests
{
    [TestClass]
    public class AutoCListWindowFilteringTests
    {
        [TestMethod]
        public void Filter_KeepsRankedFuzzyMatches()
        {
            using (var popup = new AutoCListWindow(new Dictionary<int, System.Drawing.Bitmap>()))
            {
                const string candidate = "regression_no_py6003_for_list";
                popup.SetItems(candidate + "?0 unrelated?0", ' ', '?', false);

                Assert.IsTrue(popup.Filter("0", ignoreCase: false),
                    "a fuzzy candidate accepted by the completion ranker must remain visible");
                Assert.AreEqual(candidate, popup.SelectedItemText);
                Assert.AreEqual(1, popup.ItemCount);
            }
        }

        [TestMethod]
        public void Filter_PreservesConfiguredPrefixMatchingAndRejectsUnrelatedNames()
        {
            using (var popup = new AutoCListWindow(new Dictionary<int, System.Drawing.Bitmap>()))
            {
                popup.SetItems("import?0 Important?0", ' ', '?', false);

                Assert.IsTrue(popup.Filter("imp", ignoreCase: false));
                Assert.AreEqual(2, popup.ItemCount);
                Assert.IsFalse(popup.Filter("xyz", ignoreCase: false));
                Assert.AreEqual(0, popup.ItemCount);
            }
        }
    }
}
