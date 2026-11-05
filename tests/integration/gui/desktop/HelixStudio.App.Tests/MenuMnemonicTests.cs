using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 242: every drop-down menu must give its items distinct Alt-key mnemonics — a duplicate
    /// (e.g. File had "Recent &Solutions" and "&Save" both on Alt+S) means the key cycles instead of
    /// uniquely activating an item. Walks the live menu tree and asserts no menu repeats a mnemonic.
    /// </summary>
    [TestClass]
    public class MenuMnemonicTests
    {
        private static char? Mnemonic(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (text[i] == '&')
                {
                    if (text[i + 1] == '&') { i++; continue; } // escaped literal ampersand
                    return char.ToUpperInvariant(text[i + 1]);
                }
            }
            return null;
        }

        private static void CheckMenu(string path, ToolStripItemCollection items, List<string> failures)
        {
            var seen = new Dictionary<char, string>();
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                char? m = Mnemonic(mi.Text);
                if (m.HasValue)
                {
                    if (seen.TryGetValue(m.Value, out string other))
                        failures.Add(path + ": '&" + m.Value + "' shared by \"" + other + "\" and \"" + mi.Text + "\"");
                    else
                        seen[m.Value] = mi.Text;
                }
                if (mi.HasDropDownItems)
                    CheckMenu(path + " > " + (mi.Text ?? "").Replace("&", ""), mi.DropDownItems, failures);
            }
        }

        [TestMethod]
        public void NoMenu_RepeatsAnAltKeyMnemonic()
        {
            using (var form = new MainForm())
            {
                Assert.IsNotNull(form.MainMenuStrip, "MainForm should have a menu strip.");
                var failures = new List<string>();
                CheckMenu("(menu bar)", form.MainMenuStrip.Items, failures);
                Assert.AreEqual(string.Empty, string.Join("; ", failures));
            }
        }
    }
}
