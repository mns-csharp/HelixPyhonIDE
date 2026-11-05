using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 280: the New Project wizard is a primary workflow, so it must obey the basic dialog
    /// keyboard contract — Enter triggers the current primary action (advance through pages, then
    /// Create on the summary, VS2022-style) and Esc cancels. Since the primary button changes per
    /// page, AcceptButton is set dynamically in GoTo; these guard that mapping.
    /// </summary>
    [TestClass]
    public class NewProjectWizardKeyboardTests
    {
        [TestMethod]
        public void EnterAdvances_And_EscCancels_OnTheLandingPage()
        {
            using (var form = new NewProjectWizardForm())
            {
                Button next = Field<Button>(form, "_next");
                Button cancel = Field<Button>(form, "_cancel");

                Assert.AreSame(next, form.AcceptButton,
                    "Enter should advance (Next) on the template-select page");
                Assert.AreSame(cancel, form.CancelButton,
                    "Esc should cancel the wizard from any page");
            }
        }

        [TestMethod]
        public void EnterCreates_OnTheSummaryPage()
        {
            using (var form = new NewProjectWizardForm())
            {
                // The default catalog auto-selects the first template, so navigation is deterministic.
                int summary = (int)typeof(NewProjectWizardForm)
                    .GetProperty("SummaryStep", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(form, null);
                MethodInfo advance = typeof(NewProjectWizardForm)
                    .GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo pageField = typeof(NewProjectWizardForm)
                    .GetField("_page", BindingFlags.Instance | BindingFlags.NonPublic);

                for (int i = 0; i < summary + 2 && (int)pageField.GetValue(form) < summary; i++)
                    advance.Invoke(form, null);

                Assert.AreEqual(summary, (int)pageField.GetValue(form), "should reach the summary page");

                Button create = Field<Button>(form, "_create");
                Assert.AreSame(create, form.AcceptButton,
                    "Enter should trigger Create on the summary page");
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
