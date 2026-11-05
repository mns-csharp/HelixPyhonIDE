using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 228: the keybindings editor now warns before a rebind steals another command's chord.
    /// Covers the detection logic: a chord already used by a different command conflicts; the same
    /// command doesn't conflict with itself; a free chord and Keys.None never conflict.
    /// </summary>
    [TestClass]
    public class KeybindingConflictTests
    {
        private static KeybindingManager NewManager()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            // Use names unlikely to have real user overrides so effective chords equal the defaults.
            mgr.RegisterDefault("test.cut", "Cut", Keys.Control | Keys.X);
            mgr.RegisterDefault("test.copy", "Copy", Keys.Control | Keys.C);
            return mgr;
        }

        [TestMethod]
        public void FindConflict_DetectsAnotherCommandUsingTheChord()
        {
            var mgr = NewManager();
            var conflict = mgr.FindConflict("test.copy", Keys.Control | Keys.X);
            Assert.IsNotNull(conflict, "Ctrl+X is already used by Cut.");
            Assert.AreEqual("test.cut", conflict.Name);
        }

        [TestMethod]
        public void FindConflict_IgnoresSelf_FreeChord_AndNone()
        {
            var mgr = NewManager();
            Assert.IsNull(mgr.FindConflict("test.cut", Keys.Control | Keys.X),
                "A command must not conflict with its own existing chord.");
            Assert.IsNull(mgr.FindConflict("test.copy", Keys.Control | Keys.Q),
                "An unused chord must not report a conflict.");
            Assert.IsNull(mgr.FindConflict("test.copy", Keys.None),
                "Keys.None (no shortcut) must never conflict.");
        }
    }
}
