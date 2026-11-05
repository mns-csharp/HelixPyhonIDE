using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 236: guards against "lying" toolbar tooltips — a tooltip that advertises a shortcut the
    /// app never binds (the iter-235 Packager "(Ctrl+Shift+B)" bug). Every parenthesised chord in a
    /// toolbar button tooltip must be a chord some command actually owns in the default preset.
    /// </summary>
    [TestClass]
    public class ToolbarTooltipShortcutTests
    {
        [TestMethod]
        public void EveryAdvertisedToolbarShortcut_IsActuallyBound()
        {
            var boundChords = new HashSet<Keys>(KeybindingPresets.Get(KeybindingPreset.Windows).Values);
            var trailingParen = new Regex(@"\(([^)]+)\)\s*$");
            var failures = new List<string>();

            using (var form = new MainForm())
            {
                FieldInfo f = typeof(MainForm).GetField("_toolStrip", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(f, "_toolStrip field expected.");
                var toolStrip = (ToolStrip)f.GetValue(form);
                Assert.IsNotNull(toolStrip, "_toolStrip should be built by the designer.");

                foreach (ToolStripItem item in toolStrip.Items)
                {
                    var btn = item as ToolStripButton;
                    if (btn == null || string.IsNullOrEmpty(btn.ToolTipText)) continue;
                    Match m = trailingParen.Match(btn.ToolTipText.Trim());
                    if (!m.Success) continue;

                    string chordText = m.Groups[1].Value.Trim();
                    // Only validate parentheticals that actually look like a keyboard chord.
                    if (!KeybindingManager.TryParseChord(chordText, out Keys chord) || chord == Keys.None) continue;

                    if (!boundChords.Contains(chord))
                        failures.Add(btn.Name + " advertises '" + chordText + "' which no command binds");
                }
            }

            Assert.AreEqual(string.Empty, string.Join("; ", failures),
                "Toolbar tooltips must not advertise shortcuts that aren't bound.");
        }
    }
}
