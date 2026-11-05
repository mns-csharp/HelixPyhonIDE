using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 302: the editor commands (Toggle Comment / Move Line / Go to Line) are now registered
    /// keybindings so they appear in the Keybindings dialog and are remappable, with ProcessCmdKey
    /// dispatching via ChordFor. Guards the "/" display for Ctrl+OemQuestion (was "Ctrl+OemQuestion")
    /// with a clean round-trip, and the ChordFor lookup.
    /// </summary>
    [TestClass]
    public class KeybindingChordDisplayTests
    {
        [TestMethod]
        public void OemQuestion_DisplaysAsSlash_AndRoundTrips()
        {
            Keys chord = Keys.Control | Keys.OemQuestion;
            string s = KeybindingManager.ChordToString(chord);
            Assert.AreEqual("Ctrl+/", s, "the / key should display as '/', not 'OemQuestion'");

            Assert.IsTrue(KeybindingManager.TryParseChord(s, out Keys back), "'Ctrl+/' should parse");
            Assert.AreEqual(chord, back, "display -> parse must round-trip");
        }

        [TestMethod]
        public void ChordFor_ReturnsTheRegisteredChord()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            mgr.RegisterDefault("editor.toggleComment", "Toggle Line Comment", Keys.Control | Keys.OemQuestion);
            mgr.RegisterDefault("editor.moveLineUp", "Move Line Up", Keys.Alt | Keys.Up);

            Assert.AreEqual(Keys.Control | Keys.OemQuestion, mgr.ChordFor("editor.toggleComment"));
            Assert.AreEqual(Keys.Alt | Keys.Up, mgr.ChordFor("editor.moveLineUp"));
            Assert.AreEqual(Keys.None, mgr.ChordFor("does.not.exist"));
        }
    }
}
