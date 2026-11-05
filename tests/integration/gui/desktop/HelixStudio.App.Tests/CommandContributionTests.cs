using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public sealed class CommandContributionTests
    {
        [TestMethod]
        public void ContributedCommand_HasBindingStateAndExecutableMenuSurface()
        {
            var settings = new UserSettingsManager();
            var registry = new KeybindingManager(settings);
            bool enabled = false, invoked = false;
            registry.RegisterCommand(new KeybindingManager.CommandContribution
            {
                Id = "extension.sample", DisplayName = "Sample", DefaultChord = Keys.Control | Keys.F12,
                CanExecute = () => enabled, Execute = () => invoked = true
            });
            using (ToolStripMenuItem item = registry.CreateMenuItem("extension.sample"))
            {
                Assert.AreEqual(Keys.Control | Keys.F12, item.ShortcutKeys);
                Assert.IsFalse(registry.Execute("extension.sample"));
                enabled = true;
                Assert.IsTrue(registry.Execute("extension.sample"));
                Assert.IsTrue(invoked);
            }
        }
    }
}
