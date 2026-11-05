using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Central registry of named keybindings. Default chords are seeded by
    /// <see cref="RegisterDefault"/>; user overrides live in
    /// <see cref="UserSettings.KeyBindings"/>. Each registered menu item gets
    /// its <see cref="ToolStripMenuItem.ShortcutKeys"/> set from the effective
    /// chord (override or default).
    /// </summary>
    public sealed class KeybindingManager
    {
        public sealed class CommandContribution
        {
            public string Id;
            public string DisplayName;
            public Keys DefaultChord;
            public Func<bool> CanExecute;
            public Action Execute;
        }
        // One command's binding: its id, label, factory-default chord, currently effective
        // chord, and every menu item wired to it (a command can appear in multiple menus).
        public sealed class Binding
        {
            public string Name;
            public string DisplayName;
            public Keys DefaultChord;
            public Keys Chord;
            public List<ToolStripMenuItem> MenuItems = new List<ToolStripMenuItem>();
        }

        private readonly Dictionary<string, Binding> _bindings =
            new Dictionary<string, Binding>(StringComparer.Ordinal);
        private readonly UserSettingsManager _settings;
        private readonly Dictionary<string, CommandContribution> _commands =
            new Dictionary<string, CommandContribution>(StringComparer.Ordinal);

        public KeybindingManager(UserSettingsManager settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public IEnumerable<Binding> AllBindings => _bindings.Values;
        public IEnumerable<CommandContribution> AllCommands => _commands.Values;

        public void RegisterCommand(CommandContribution command)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (string.IsNullOrWhiteSpace(command.Id)) throw new ArgumentException("A command id is required.", "command");
            if (command.Execute == null) throw new ArgumentException("A command execute callback is required.", "command");
            if (_commands.ContainsKey(command.Id)) throw new InvalidOperationException("Command '" + command.Id + "' is already registered.");
            _commands.Add(command.Id, command);
            RegisterDefault(command.Id, command.DisplayName ?? command.Id, command.DefaultChord);
        }

        public bool CanExecute(string id)
        {
            return _commands.TryGetValue(id, out CommandContribution command) &&
                (command.CanExecute == null || command.CanExecute());
        }

        public bool Execute(string id)
        {
            if (!CanExecute(id)) return false;
            _commands[id].Execute(); return true;
        }

        public ToolStripMenuItem CreateMenuItem(string id)
        {
            if (!_commands.TryGetValue(id, out CommandContribution command)) throw new KeyNotFoundException(id);
            var item = new ToolStripMenuItem(command.DisplayName ?? id);
            item.Click += delegate { Execute(id); };
            item.DropDownOpening += delegate { item.Enabled = CanExecute(id); };
            Register(id, item);
            return item;
        }

        public KeybindingPreset CurrentPreset => _settings.Settings.KeybindingPreset;

        /// <summary>Overwrite every registered binding with the chord from <paramref name="preset"/>.
        /// Unknown command ids in the preset are ignored; registered commands not in the preset keep
        /// their current chord.</summary>
        public void ApplyPreset(KeybindingPreset preset)
        {
            // Apply every chord in the preset that maps to a registered command; SetChord
            // also persists each override. Unknown ids in the preset are simply skipped.
            var map = KeybindingPresets.Get(preset);
            foreach (var kv in map)
            {
                if (_bindings.TryGetValue(kv.Key, out _))
                    SetChord(kv.Key, kv.Value);
            }
            // Remember the active preset for the next launch.
            _settings.Settings.KeybindingPreset = preset;
            _settings.Save();
        }

        /// <summary>
        /// Declare a binding with a default chord. Call once per command.
        /// Later <see cref="Register"/> calls wire a menu item to the binding.
        /// </summary>
        public void RegisterDefault(string name, string displayName, Keys defaultChord)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name");
            // Create the binding on first registration; a repeat call just refreshes its fields.
            if (!_bindings.TryGetValue(name, out var b))
            {
                b = new Binding { Name = name, DefaultChord = defaultChord };
                _bindings[name] = b;
            }
            b.DisplayName = displayName ?? name;
            b.DefaultChord = defaultChord;
            // Effective chord = the user's saved override if present, else the default.
            b.Chord = ResolveEffective(name, defaultChord);
        }

        /// <summary>Wire a menu item to a previously-registered binding.</summary>
        public void Register(string name, ToolStripMenuItem menuItem)
        {
            // Ignore items for unregistered commands; track each item once, then stamp the chord.
            if (menuItem == null || !_bindings.TryGetValue(name, out var b)) return;
            if (!b.MenuItems.Contains(menuItem)) b.MenuItems.Add(menuItem);
            try { menuItem.ShortcutKeys = b.Chord; } catch { /* some chords aren't valid shortcuts in WinForms */ }
        }

        /// <summary>
        /// The binding (other than <paramref name="excludeName"/>) currently using
        /// <paramref name="chord"/>, or <c>null</c> if the chord is free. <see cref="Keys.None"/>
        /// never conflicts (it means "no shortcut"). Used by the keybindings editor to warn before
        /// a rebind silently steals another command's shortcut.
        /// </summary>
        public Binding FindConflict(string excludeName, Keys chord)
        {
            if (chord == Keys.None) return null;
            foreach (var b in _bindings.Values)
            {
                if (!string.Equals(b.Name, excludeName, StringComparison.Ordinal) && b.Chord == chord)
                    return b;
            }
            return null;
        }

        /// <summary>Update a binding's chord and re-apply to every wired menu item.</summary>
        public void SetChord(string name, Keys chord)
        {
            if (!_bindings.TryGetValue(name, out var b)) return;
            // Update the in-memory chord and re-stamp every wired menu item.
            b.Chord = chord;
            foreach (var mi in b.MenuItems)
            {
                try { mi.ShortcutKeys = chord; } catch { }
            }
            // Persist as an override only when it differs from the default; when it matches
            // the default, drop any stored override so settings stay minimal.
            if (chord == b.DefaultChord)
            {
                _settings.Settings.KeyBindings?.Remove(name);
            }
            else
            {
                if (_settings.Settings.KeyBindings == null)
                    _settings.Settings.KeyBindings = new Dictionary<string, string>(StringComparer.Ordinal);
                _settings.Settings.KeyBindings[name] = ChordToString(chord);
            }
            _settings.Save();
        }

        // The effective chord for a command: a parseable saved override wins, else the fallback.
        private Keys ResolveEffective(string name, Keys fallback)
        {
            if (_settings.Settings.KeyBindings != null
                && _settings.Settings.KeyBindings.TryGetValue(name, out string chord)
                && TryParseChord(chord, out Keys parsed))
            {
                return parsed;
            }
            return fallback;
        }

        // Serialize a Keys chord to the persisted "Ctrl+Shift+X" form: modifiers first
        // (in a fixed order), then the base key code.
        /// <summary>The effective chord currently bound to <paramref name="name"/>, or Keys.None.</summary>
        public Keys ChordFor(string name)
        {
            return _bindings.TryGetValue(name, out var b) ? b.Chord : Keys.None;
        }

        // Friendly display for punctuation keys whose enum name is unreadable (OemQuestion -> "/").
        private static string KeyName(Keys key)
        {
            switch (key)
            {
                case Keys.OemQuestion: return "/";
                default: return key.ToString();
            }
        }

        // Inverse of KeyName for parsing; Keys.None means "not a friendly token".
        private static Keys KeyFromName(string token)
        {
            switch (token)
            {
                case "/": return Keys.OemQuestion;
                default: return Keys.None;
            }
        }

        public static string ChordToString(Keys chord)
        {
            if (chord == Keys.None) return string.Empty;
            var parts = new List<string>();
            if ((chord & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((chord & Keys.Shift) == Keys.Shift) parts.Add("Shift");
            if ((chord & Keys.Alt) == Keys.Alt) parts.Add("Alt");
            Keys key = chord & Keys.KeyCode;
            if (key != Keys.None) parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        // Parse a "Ctrl+Shift+X" string back into a Keys chord. Modifier tokens OR in their
        // flags; any other token is parsed as a key name. An unrecognized token fails the whole
        // parse (chord reset to None) so a corrupt setting can't produce a half-valid binding.
        public static bool TryParseChord(string s, out Keys chord)
        {
            chord = Keys.None;
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (string part in s.Split('+'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { chord |= Keys.Control; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { chord |= Keys.Shift; continue; }
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { chord |= Keys.Alt; continue; }
                Keys friendly = KeyFromName(p);
                if (friendly != Keys.None) { chord |= friendly; continue; }
                if (!Enum.TryParse(p, true, out Keys k)) { chord = Keys.None; return false; }
                chord |= k;
            }
            return true;
        }
    }
}
