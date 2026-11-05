using System.Collections.Generic;
using System.Windows.Forms;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Platform-flavoured keybinding presets. The app runs on Windows (WinForms
    /// /.NET Framework 4.7), so "Mac-style" and "Linux-style" don't actually use
    /// a Cmd / Super key — they're alternative chord sets familiar to users who
    /// migrate from macOS or Linux IDEs and want the same muscle memory.
    /// </summary>
    public enum KeybindingPreset
    {
        Windows,
        MacStyle,
        LinuxStyle,
    }

    public static class KeybindingPresets
    {
        /// <summary>Return the preset's chord map: command id → effective chord.</summary>
        public static IReadOnlyDictionary<string, Keys> Get(KeybindingPreset preset)
        {
            switch (preset)
            {
                case KeybindingPreset.MacStyle:   return BuildMacStyle();
                case KeybindingPreset.LinuxStyle: return BuildLinuxStyle();
                default:                          return BuildWindows();
            }
        }

        /// <summary>Every command id defined in <b>any</b> preset. Tests use this to
        /// assert that presets don't accidentally drop commands on one platform.</summary>
        public static IReadOnlyList<string> AllCommandIds => _commandIds;

        private static readonly string[] _commandIds =
        {
            "file.new", "file.open", "file.import", "file.openEditor", "file.save", "file.saveLayout",
            "edit.find", "edit.replace",
            "debug.start", "debug.stop", "debug.stepOver", "debug.stepInto", "debug.stepOut",
            "debug.toggleBreakpoint",
            "view.panel.designer", "view.panel.editor", "view.panel.debug",
            "view.panel.analysis", "view.panel.buildResults", "view.panel.problems",
            "view.completion", "view.quickInfo",
            "build.compile",
            "analysis.lint", "analysis.deadCode", "tests.runAll", "metrics.compute", "profiler.run",
            "run.startup", "view.showAllHidden", "build.packager",
        };

        private static Dictionary<string, Keys> BuildWindows()
        {
            return new Dictionary<string, Keys>
            {
                { "file.new",              Keys.Control | Keys.N },
                { "file.open",             Keys.Control | Keys.O },
                { "file.import",           Keys.Control | Keys.I },
                { "file.openEditor",       Keys.Control | Keys.E },
                { "file.save",             Keys.Control | Keys.S },
                { "file.saveLayout",       Keys.Control | Keys.Shift | Keys.S },
                { "edit.find",             Keys.Control | Keys.F },
                { "edit.replace",          Keys.Control | Keys.H },
                { "debug.start",           Keys.F5 },
                { "debug.stop",            Keys.Shift | Keys.F5 },
                { "debug.stepOver",        Keys.F10 },
                { "debug.stepInto",        Keys.F11 },
                { "debug.stepOut",         Keys.Shift | Keys.F11 },
                { "debug.toggleBreakpoint", Keys.F9 },
                { "view.panel.designer",   Keys.Control | Keys.D1 },
                { "view.panel.editor",     Keys.Control | Keys.D2 },
                { "view.panel.debug",      Keys.Control | Keys.D4 },
                { "view.panel.analysis",   Keys.Control | Keys.D5 },
                { "view.panel.buildResults", Keys.Control | Keys.D6 },
                { "view.panel.problems",   Keys.Control | Keys.D7 },
                { "view.completion",       Keys.Control | Keys.Space },
                { "view.quickInfo",        Keys.None }, // Ctrl+K,Ctrl+I is a chord — handled in editor, not ShortcutKeys.
                { "build.compile",         Keys.F6 },
                { "analysis.lint",         Keys.Control | Keys.Shift | Keys.L },
                { "analysis.deadCode",     Keys.Control | Keys.Shift | Keys.D },
                { "tests.runAll",          Keys.Control | Keys.Shift | Keys.T },
                { "metrics.compute",       Keys.Control | Keys.Shift | Keys.M },
                { "profiler.run",          Keys.Control | Keys.Shift | Keys.P },
                { "run.startup",           Keys.Control | Keys.F5 },
                { "view.showAllHidden",    Keys.Control | Keys.Shift | Keys.H },
                { "build.packager",        Keys.Control | Keys.Shift | Keys.B },
            };
        }

        private static Dictionary<string, Keys> BuildMacStyle()
        {
            // Alternate chords where macOS IDEs diverge from Windows. No Cmd key
            // available — we substitute Ctrl or pick non-conflicting chords.
            return new Dictionary<string, Keys>
            {
                { "file.new",              Keys.Control | Keys.N },
                { "file.open",             Keys.Control | Keys.O },
                { "file.import",           Keys.Control | Keys.Shift | Keys.O },
                { "file.openEditor",       Keys.Control | Keys.E },
                { "file.save",             Keys.Control | Keys.S },
                { "file.saveLayout",       Keys.Control | Keys.Shift | Keys.S },
                { "edit.find",             Keys.Control | Keys.F },
                { "edit.replace",          Keys.Alt | Keys.Control | Keys.F }, // JetBrains-on-Mac pattern
                { "debug.start",           Keys.Control | Keys.R },             // ⌘R → Ctrl+R
                { "debug.stop",            Keys.Control | Keys.OemPeriod },     // ⌘. → Ctrl+.
                { "debug.stepOver",        Keys.F8 },
                { "debug.stepInto",        Keys.F7 },
                { "debug.stepOut",         Keys.Shift | Keys.F8 },
                { "debug.toggleBreakpoint", Keys.Control | Keys.F8 },
                { "view.panel.designer",   Keys.Control | Keys.D1 },
                { "view.panel.editor",     Keys.Control | Keys.D2 },
                { "view.panel.debug",      Keys.Control | Keys.D4 },
                { "view.panel.analysis",   Keys.Control | Keys.D5 },
                { "view.panel.buildResults", Keys.Control | Keys.D6 },
                { "view.panel.problems",   Keys.Control | Keys.D7 },
                { "view.completion",       Keys.Alt | Keys.Escape }, // JetBrains basic completion
                { "view.quickInfo",        Keys.Control | Keys.J },  // JetBrains ⌃J
                { "build.compile",         Keys.Control | Keys.F9 },
                { "analysis.lint",         Keys.Control | Keys.Shift | Keys.L },
                { "analysis.deadCode",     Keys.Control | Keys.Shift | Keys.D },
                { "tests.runAll",          Keys.Control | Keys.Shift | Keys.T },
                { "metrics.compute",       Keys.Control | Keys.Shift | Keys.M },
                { "profiler.run",          Keys.Control | Keys.Shift | Keys.P },
                { "run.startup",           Keys.Control | Keys.F5 },
                { "view.showAllHidden",    Keys.Control | Keys.Shift | Keys.H },
                { "build.packager",        Keys.Control | Keys.Shift | Keys.B },
            };
        }

        private static Dictionary<string, Keys> BuildLinuxStyle()
        {
            // Close to the Windows defaults but with GNOME/KDE-ish search/build chords.
            return new Dictionary<string, Keys>
            {
                { "file.new",              Keys.Control | Keys.N },
                { "file.open",             Keys.Control | Keys.O },
                { "file.import",           Keys.Control | Keys.I },
                { "file.openEditor",       Keys.Control | Keys.E },
                { "file.save",             Keys.Control | Keys.S },
                { "file.saveLayout",       Keys.Control | Keys.Shift | Keys.S },
                { "edit.find",             Keys.Control | Keys.F },
                { "edit.replace",          Keys.Control | Keys.H },
                { "debug.start",           Keys.F5 },
                { "debug.stop",            Keys.Shift | Keys.F5 },
                { "debug.stepOver",        Keys.F10 },
                { "debug.stepInto",        Keys.F11 },
                { "debug.stepOut",         Keys.Shift | Keys.F11 },
                { "debug.toggleBreakpoint", Keys.F9 },
                { "view.panel.designer",   Keys.Alt | Keys.D1 },
                { "view.panel.editor",     Keys.Alt | Keys.D2 },
                { "view.panel.debug",      Keys.Alt | Keys.D4 },
                { "view.panel.analysis",   Keys.Alt | Keys.D5 },
                { "view.panel.buildResults", Keys.Alt | Keys.D6 },
                { "view.panel.problems",   Keys.Alt | Keys.D7 },
                { "view.completion",       Keys.Control | Keys.Space },
                { "view.quickInfo",        Keys.None },
                { "build.compile",         Keys.Control | Keys.B },
                { "analysis.lint",         Keys.Control | Keys.Shift | Keys.L },
                { "analysis.deadCode",     Keys.Control | Keys.Shift | Keys.D },
                { "tests.runAll",          Keys.Control | Keys.Shift | Keys.T },
                { "metrics.compute",       Keys.Control | Keys.Shift | Keys.M },
                { "profiler.run",          Keys.Control | Keys.Shift | Keys.P },
                { "run.startup",           Keys.Control | Keys.F5 },
                { "view.showAllHidden",    Keys.Control | Keys.Shift | Keys.H },
                { "build.packager",        Keys.Control | Keys.Shift | Keys.B },
            };
        }
    }
}
