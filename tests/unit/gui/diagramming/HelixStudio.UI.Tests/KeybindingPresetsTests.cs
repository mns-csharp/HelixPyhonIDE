using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class KeybindingPresetsTests
    {
        [TestMethod]
        public void WindowsPreset_HasSaneDefaults()
        {
            var map = KeybindingPresets.Get(KeybindingPreset.Windows);
            Assert.AreEqual(Keys.Control | Keys.N, map["file.new"]);
            Assert.AreEqual(Keys.F5, map["debug.start"]);
            Assert.AreEqual(Keys.Control | Keys.Space, map["view.completion"]);
        }

        [TestMethod]
        public void MacStylePreset_DiffersOnKeyCommands()
        {
            var mac = KeybindingPresets.Get(KeybindingPreset.MacStyle);
            var win = KeybindingPresets.Get(KeybindingPreset.Windows);
            Assert.AreNotEqual(win["view.completion"], mac["view.completion"]);
            Assert.AreEqual(Keys.Control | Keys.J, mac["view.quickInfo"]);
        }

        [TestMethod]
        public void LinuxStylePreset_UsesAltForPanelSwitching()
        {
            var linux = KeybindingPresets.Get(KeybindingPreset.LinuxStyle);
            Assert.AreEqual(Keys.Alt | Keys.D1, linux["view.panel.designer"]);
            Assert.AreEqual(Keys.Control | Keys.B, linux["build.compile"]);
        }

        [TestMethod]
        public void CtrlS_SavesTheFile_NotTheLayout_InEveryPreset()
        {
            // Iter 212: Ctrl+S must save the active file (universal convention). Save Layout —
            // a far rarer action — moves to Ctrl+Shift+S so it no longer hijacks the save chord.
            foreach (var preset in new[] { KeybindingPreset.Windows, KeybindingPreset.MacStyle, KeybindingPreset.LinuxStyle })
            {
                var map = KeybindingPresets.Get(preset);
                Assert.AreEqual(Keys.Control | Keys.S, map["file.save"],
                    preset + ": Ctrl+S must be bound to file.save.");
                Assert.AreEqual(Keys.Control | Keys.Shift | Keys.S, map["file.saveLayout"],
                    preset + ": Save Layout must move to Ctrl+Shift+S.");
                Assert.AreNotEqual(map["file.save"], map["file.saveLayout"],
                    preset + ": Save and Save Layout must not share a chord.");
            }
        }

        [TestMethod]
        public void AnalysisTestMetricsProfiler_HaveWorkingChords_InEveryPreset()
        {
            // Iter 223: these were previously only wired on the dead MenuCoordinator menu, so they
            // neither displayed nor dispatched. Now registered on the live menu items with stable
            // Ctrl+Shift+<key> chords across every platform preset.
            var expected = new System.Collections.Generic.Dictionary<string, Keys>
            {
                { "analysis.lint",     Keys.Control | Keys.Shift | Keys.L },
                { "analysis.deadCode", Keys.Control | Keys.Shift | Keys.D },
                { "tests.runAll",      Keys.Control | Keys.Shift | Keys.T },
                { "metrics.compute",   Keys.Control | Keys.Shift | Keys.M },
                { "profiler.run",      Keys.Control | Keys.Shift | Keys.P },
            };
            foreach (var preset in new[] { KeybindingPreset.Windows, KeybindingPreset.MacStyle, KeybindingPreset.LinuxStyle })
            {
                var map = KeybindingPresets.Get(preset);
                foreach (var kv in expected)
                    Assert.AreEqual(kv.Value, map[kv.Key], preset + ":" + kv.Key);
            }
        }

        [TestMethod]
        public void NoPreset_AssignsTheSameChordToTwoCommands()
        {
            // Iter 229: a duplicate default chord silently breaks one command (WinForms dispatches to
            // whichever menu item it finds first). Guard every shipped preset against that. Keys.None
            // ("no shortcut") may legitimately repeat.
            foreach (var preset in new[] { KeybindingPreset.Windows, KeybindingPreset.MacStyle, KeybindingPreset.LinuxStyle })
            {
                var map = KeybindingPresets.Get(preset);
                var owner = new System.Collections.Generic.Dictionary<Keys, string>();
                foreach (var kv in map)
                {
                    if (kv.Value == Keys.None) continue;
                    Assert.IsFalse(owner.ContainsKey(kv.Value),
                        preset + ": chord " + kv.Value + " is assigned to both '"
                        + (owner.ContainsKey(kv.Value) ? owner[kv.Value] : "?") + "' and '" + kv.Key + "'.");
                    owner[kv.Value] = kv.Key;
                }
            }
        }

        [TestMethod]
        public void AllPresets_DefineTheSameCommandSet()
        {
            var win = KeybindingPresets.Get(KeybindingPreset.Windows);
            var mac = KeybindingPresets.Get(KeybindingPreset.MacStyle);
            var lin = KeybindingPresets.Get(KeybindingPreset.LinuxStyle);

            var expected = new HashSet<string>(KeybindingPresets.AllCommandIds);
            foreach (var (name, map) in new[] { ("Windows", win), ("Mac", mac), ("Linux", lin) })
            {
                foreach (string cmd in expected)
                    Assert.IsTrue(map.ContainsKey(cmd),
                        "Preset '" + name + "' is missing binding for command '" + cmd + "'.");
            }
        }
    }
}
