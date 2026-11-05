using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 281 (screenshot audit): the wizard's nav buttons rendered as "Cancel | Next > | &lt; Back",
    /// putting Back to the RIGHT of Next — backwards from every wizard convention. They must read
    /// "&lt; Back | Next > | Cancel" left-to-right (Windows Wizard-97 / VS2022). Guards the on-screen order.
    /// </summary>
    [TestClass]
    public class NewProjectWizardButtonOrderTests
    {
        [TestMethod]
        public void NavButtons_ReadBackNextCancel_LeftToRight()
        {
            using (var form = new NewProjectWizardForm())
            {
                // Docked-in-panel positions only resolve after a layout pass — show off-screen.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                int back = Field<Button>(form, "_back").Left;
                int next = Field<Button>(form, "_next").Left;
                int cancel = Field<Button>(form, "_cancel").Left;

                Assert.IsTrue(back < next, "Back must be to the LEFT of Next (was rendered to its right)");
                Assert.IsTrue(next < cancel, "Cancel must be the right-most nav button");

                form.Close();
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
