using System;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The AI tab's API-key round trip.
    ///
    /// The failure modes here are quiet ones — a key silently overwritten by a placeholder, a
    /// pasted key that fails auth because of trailing whitespace, or a key rendered in clear text
    /// on a shared screen — so each is pinned rather than left to inspection.
    /// </summary>
    [TestClass]
    public class PreferencesAiKeyTests
    {
        private static UserSettingsManager FreshSettings()
        {
            // HELIXSTUDIO_STATE_DIR is set by the test run, so this writes to the scratch state
            // dir and never touches %APPDATA%\HelixStudio\user-settings.json.
            var manager = new UserSettingsManager();
            manager.Load();
            return manager;
        }

        /// <summary>
        /// Press OK for real.
        ///
        /// <c>Button.PerformClick()</c> is a no-op while the button cannot be selected, and a
        /// control on a form that was never shown reports <c>Visible == false</c> — so a test that
        /// just calls PerformClick on an unrealized dialog silently exercises nothing and then
        /// asserts against unchanged settings. The form is realized off every monitor first
        /// (never flash a window onto the user's desktop), which is what makes the click land.
        /// </summary>
        private static void ClickOk(PreferencesDialog dialog)
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new System.Drawing.Point(-4000, -4000);
            TestWindow.ShowOffScreen(dialog);
            Application.DoEvents();
            dialog.AcceptButton.PerformClick();
            Application.DoEvents();
        }

        private static TextBox FindKeyBox(PreferencesDialog dialog, string labelText)
        {
            TabPage tab = dialog.Controls.OfType<TabControl>().First()
                .TabPages.Cast<TabPage>().First(t => t.Text == "AI");
            Label label = tab.Controls.OfType<Label>().First(l => l.Text == labelText);
            // The box sits on the same row as its label.
            return tab.Controls.OfType<TextBox>().First(b => Math.Abs(b.Top - label.Top) <= 6);
        }

        [TestMethod]
        public void TheAiTabExists_WithABoxForEachProvider()
        {
            var settings = FreshSettings();
            using (var dialog = new PreferencesDialog(settings, null))
            {
                TabPage tab = dialog.Controls.OfType<TabControl>().First()
                    .TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text == "AI");
                Assert.IsNotNull(tab, "Preferences must expose an AI tab");
                Assert.AreEqual(2, tab.Controls.OfType<TextBox>().Count(),
                    "one key box per registered provider");
            }
        }

        [TestMethod]
        public void KeysAreMasked_NotRenderedInClearText()
        {
            var settings = FreshSettings();
            using (var dialog = new PreferencesDialog(settings, null))
            {
                TabPage tab = dialog.Controls.OfType<TabControl>().First()
                    .TabPages.Cast<TabPage>().First(t => t.Text == "AI");
                foreach (TextBox box in tab.Controls.OfType<TextBox>())
                    Assert.IsTrue(box.UseSystemPasswordChar, "an API key must not be shoulder-surfable");
            }
        }

        [TestMethod]
        public void AKeyRoundTripsThroughSettings()
        {
            var settings = FreshSettings();
            {
                using (var dialog = new PreferencesDialog(settings, null))
                {
                    FindKeyBox(dialog, "Claude:").Text = "sk-ant-test-key";
                    FindKeyBox(dialog, "OpenAI:").Text = "sk-openai-test-key";
                    ClickOk(dialog);
                }

                Assert.AreEqual("sk-ant-test-key", settings.Settings.AnthropicApiKey);
                Assert.AreEqual("sk-openai-test-key", settings.Settings.OpenAiApiKey);

                // Reopening shows the stored key rather than a placeholder — a placeholder would
                // be written back over the real key on the next OK.
                using (var reopened = new PreferencesDialog(settings, null))
                {
                    Assert.AreEqual("sk-ant-test-key", FindKeyBox(reopened, "Claude:").Text);
                }
            }
        }

        [TestMethod]
        public void APastedKeyIsTrimmed()
        {
            // A key with a stray space fails authentication with an opaque 401, which is a
            // miserable thing to debug.
            var settings = FreshSettings();
            using (var dialog = new PreferencesDialog(settings, null))
            {
                FindKeyBox(dialog, "Claude:").Text = "  sk-ant-padded  \r\n";
                ClickOk(dialog);
                Assert.AreEqual("sk-ant-padded", settings.Settings.AnthropicApiKey);
            }
        }

        [TestMethod]
        public void ClearingTheBox_UnsetsTheKeySoTheEnvironmentVariableTakesOver()
        {
            var settings = FreshSettings();
            {
                settings.Settings.AnthropicApiKey = "sk-ant-old";
                using (var dialog = new PreferencesDialog(settings, null))
                {
                    FindKeyBox(dialog, "Claude:").Text = "   ";
                    ClickOk(dialog);
                }
                Assert.IsNull(settings.Settings.AnthropicApiKey,
                    "empty must mean unset, not an empty-string key that shadows the env var");
            }
        }

        [TestMethod]
        public void TheTabWarnsThatAKeyIsStoredUnencrypted()
        {
            var settings = FreshSettings();
            using (var dialog = new PreferencesDialog(settings, null))
            {
                TabPage tab = dialog.Controls.OfType<TabControl>().First()
                    .TabPages.Cast<TabPage>().First(t => t.Text == "AI");
                string text = string.Join(" ", tab.Controls.OfType<Label>().Select(l => l.Text));
                StringAssert.Contains(text, "unencrypted");
                StringAssert.Contains(text, "ANTHROPIC_API_KEY");
                StringAssert.Contains(text, "OPENAI_API_KEY");
            }
        }
    }
}
