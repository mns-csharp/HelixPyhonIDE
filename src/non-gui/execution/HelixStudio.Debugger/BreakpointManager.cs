using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Debugger
{
    /// <summary>
    /// Breakpoint change event arguments.
    /// </summary>
    public class BreakpointChangedEventArgs : EventArgs
    {
        public string FilePath { get; set; }
        public int Line { get; set; }
        public bool IsAdded { get; set; }
    }

    /// <summary>
    /// Optional condition attached to a breakpoint. <see cref="Condition"/> is a Python expression
    /// debugpy evaluates at the line — the breakpoint only fires when it's truthy. <see
    /// cref="HitCondition"/> is a hit-count rule debugpy understands (e.g. "5", "&gt;=3", "%2").
    /// Either/both may be null for an unconditional / no-hit-rule breakpoint.
    /// </summary>
    public sealed class BreakpointCondition
    {
        public string Condition { get; set; }
        public string HitCondition { get; set; }
        /// <summary>
        /// Tracepoint / logpoint message. When set, debugpy treats the breakpoint as a LOGPOINT: at each
        /// hit it logs the interpolated message (<c>{expr}</c> segments are evaluated) to the debug output
        /// and CONTINUES without stopping. Null/blank = an ordinary (stopping) breakpoint.
        /// </summary>
        public string LogMessage { get; set; }
        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrWhiteSpace(Condition)
                    && string.IsNullOrWhiteSpace(HitCondition)
                    && string.IsNullOrWhiteSpace(LogMessage);
            }
        }
        /// <summary>True when this breakpoint is a tracepoint (logs + continues instead of stopping).</summary>
        public bool IsTracepoint { get { return !string.IsNullOrWhiteSpace(LogMessage); } }
    }

    /// <summary>
    /// Validates a breakpoint hit-count rule against the grammar debugpy/pydevd accepts: an optional
    /// comparison operator (<c>==</c>, <c>!=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>%</c>) followed by a non-negative integer (a bare number means "stop on the Nth hit").
    /// Blank is valid (= no hit rule). Catches typos that debugpy would otherwise silently ignore.
    /// </summary>
    public static class BreakpointHitCondition
    {
        private static readonly System.Text.RegularExpressions.Regex Pattern =
            new System.Text.RegularExpressions.Regex(
                @"^\s*(==|!=|>=|<=|>|<|%)?\s*\d+\s*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static bool IsValid(string hitCondition)
        {
            return string.IsNullOrWhiteSpace(hitCondition) || Pattern.IsMatch(hitCondition);
        }
    }

    /// <summary>
    /// Manages breakpoints across all open files.
    /// Inspired by SharpDevelop's Breakpoint model (Debugger.Core/Breakpoint.cs).
    /// </summary>
    public class BreakpointManager
    {
        private readonly Dictionary<string, HashSet<int>> _breakpoints;
        // Conditions are keyed by file then line, parallel to _breakpoints. Persisted alongside
        // the lines by Save()/Load() (JSON format), so both a breakpoint AND its condition/
        // hit-count survive a restart.
        private readonly Dictionary<string, Dictionary<int, BreakpointCondition>> _conditions
            = new Dictionary<string, Dictionary<int, BreakpointCondition>>(StringComparer.OrdinalIgnoreCase);
        // Disabled breakpoints: present here means the breakpoint still exists (shown in the panel and
        // editor margin) but is NOT sent to the debug adapter, so it doesn't stop execution. Parallel to
        // _breakpoints; persisted so the enabled/disabled state survives a restart.
        private readonly Dictionary<string, HashSet<int>> _disabled
            = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly string _settingsPath;

        /// <summary>Fired when a breakpoint is added or removed.</summary>
        public event EventHandler<BreakpointChangedEventArgs> BreakpointChanged;

        public BreakpointManager() : this(null)
        {
        }

        /// <summary>
        /// Creates a breakpoint store rooted in <paramref name="stateDirectory"/>. When omitted,
        /// <c>HELIXSTUDIO_STATE_DIR</c> is honored before the normal per-user application-data path.
        /// This keeps automated/portable IDE sessions isolated from a developer's real profile.
        /// </summary>
        public BreakpointManager(string stateDirectory)
        {
            _breakpoints = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            string appDataDir = stateDirectory;
            if (string.IsNullOrWhiteSpace(appDataDir))
                appDataDir = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            if (!string.IsNullOrWhiteSpace(appDataDir))
                appDataDir = Environment.ExpandEnvironmentVariables(appDataDir);
            if (string.IsNullOrWhiteSpace(appDataDir))
                appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PythonCodeEditor");
            appDataDir = Path.GetFullPath(appDataDir);
            try
            {
                if (!Directory.Exists(appDataDir))
                    Directory.CreateDirectory(appDataDir);
            }
            catch { }
            _settingsPath = Path.Combine(appDataDir, "breakpoints.txt");
        }

        /// <summary>Resolved persistence file, exposed for diagnostics and isolation tests.</summary>
        public string SettingsPath { get { return _settingsPath; } }

        /// <summary>
        /// Toggles a breakpoint at the given file and line. Returns true if breakpoint was added.
        /// </summary>
        public bool ToggleBreakpoint(string filePath, int line)
        {
            lock (_lock)
            {
                if (HasBreakpoint(filePath, line))
                {
                    RemoveBreakpoint(filePath, line);
                    return false;
                }
                else
                {
                    AddBreakpoint(filePath, line);
                    return true;
                }
            }
        }

        public void AddBreakpoint(string filePath, int line)
        {
            lock (_lock)
            {
                if (!_breakpoints.ContainsKey(filePath))
                    _breakpoints[filePath] = new HashSet<int>();

                if (_breakpoints[filePath].Add(line))
                {
                    OnBreakpointChanged(filePath, line, true);
                }
            }
        }

        public void RemoveBreakpoint(string filePath, int line)
        {
            lock (_lock)
            {
                HashSet<int> lines;
                if (_breakpoints.TryGetValue(filePath, out lines))
                {
                    if (lines.Remove(line))
                    {
                        if (lines.Count == 0)
                            _breakpoints.Remove(filePath);
                        // Drop any condition attached to this exact breakpoint.
                        Dictionary<int, BreakpointCondition> cmap;
                        if (_conditions.TryGetValue(filePath, out cmap))
                        {
                            cmap.Remove(line);
                            if (cmap.Count == 0) _conditions.Remove(filePath);
                        }
                        // Drop any disabled-state for this breakpoint too.
                        HashSet<int> off;
                        if (_disabled.TryGetValue(filePath, out off))
                        {
                            off.Remove(line);
                            if (off.Count == 0) _disabled.Remove(filePath);
                        }
                        OnBreakpointChanged(filePath, line, false);
                    }
                }
            }
        }

        /// <summary>
        /// Sets (or clears, when both args are blank) the condition / hit-count on a breakpoint.
        /// Adds the breakpoint first if it doesn't exist yet, so a user can author a conditional
        /// breakpoint in one gesture. Raises <see cref="BreakpointChanged"/> so the panel refreshes
        /// and the engine re-sends on the next configuration. Takes effect on the next run.
        /// </summary>
        public void SetCondition(string filePath, int line, string condition, string hitCondition)
        {
            lock (_lock)
            {
                if (!_breakpoints.ContainsKey(filePath)) _breakpoints[filePath] = new HashSet<int>();
                _breakpoints[filePath].Add(line);

                Dictionary<int, BreakpointCondition> map;
                if (!_conditions.TryGetValue(filePath, out map)) { map = new Dictionary<int, BreakpointCondition>(); _conditions[filePath] = map; }
                // Preserve any existing LogMessage (tracepoint) — editing the condition mustn't drop it.
                BreakpointCondition existing;
                string keepLog = (map.TryGetValue(line, out existing) && existing != null) ? existing.LogMessage : null;
                BreakpointCondition cond = new BreakpointCondition
                {
                    Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(),
                    HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition.Trim(),
                    LogMessage = keepLog,
                };
                if (cond.IsEmpty)
                {
                    map.Remove(line);
                    if (map.Count == 0) _conditions.Remove(filePath);
                }
                else map[line] = cond;
            }
            OnBreakpointChanged(filePath, line, true);
        }

        /// <summary>
        /// Turns a breakpoint into a TRACEPOINT (logpoint): at each hit debugpy logs
        /// <paramref name="logMessage"/> to the output and continues instead of stopping. Blank clears
        /// the tracepoint (back to an ordinary breakpoint), preserving any condition/hit-count. Adds the
        /// breakpoint if it doesn't exist yet.
        /// </summary>
        public void SetLogMessage(string filePath, int line, string logMessage)
        {
            lock (_lock)
            {
                if (!_breakpoints.ContainsKey(filePath)) _breakpoints[filePath] = new HashSet<int>();
                _breakpoints[filePath].Add(line);

                Dictionary<int, BreakpointCondition> map;
                if (!_conditions.TryGetValue(filePath, out map)) { map = new Dictionary<int, BreakpointCondition>(); _conditions[filePath] = map; }
                BreakpointCondition cond;
                if (!map.TryGetValue(line, out cond) || cond == null) cond = new BreakpointCondition();
                cond.LogMessage = string.IsNullOrWhiteSpace(logMessage) ? null : logMessage.Trim();
                if (cond.IsEmpty)
                {
                    map.Remove(line);
                    if (map.Count == 0) _conditions.Remove(filePath);
                }
                else map[line] = cond;
            }
            OnBreakpointChanged(filePath, line, true);
        }

        /// <summary>The condition on a breakpoint, or null if it's unconditional.</summary>
        public BreakpointCondition GetCondition(string filePath, int line)
        {
            lock (_lock)
            {
                Dictionary<int, BreakpointCondition> map;
                BreakpointCondition c;
                if (_conditions.TryGetValue(filePath, out map) && map.TryGetValue(line, out c)) return c;
                return null;
            }
        }

        public bool HasBreakpoint(string filePath, int line)
        {
            lock (_lock)
            {
                HashSet<int> lines;
                if (_breakpoints.TryGetValue(filePath, out lines))
                    return lines.Contains(line);
                return false;
            }
        }

        /// <summary>
        /// Gets all breakpoint line numbers for a file (1-based).
        /// </summary>
        public List<int> GetBreakpoints(string filePath)
        {
            lock (_lock)
            {
                HashSet<int> lines;
                if (_breakpoints.TryGetValue(filePath, out lines))
                    return lines.ToList();
                return new List<int>();
            }
        }

        /// <summary>
        /// The ENABLED breakpoint lines for a file (1-based) — i.e. those that should actually be sent
        /// to the debug adapter. Disabled breakpoints are excluded here but still returned by
        /// <see cref="GetBreakpoints"/> so the panel/editor can render them (greyed).
        /// </summary>
        public List<int> GetEnabledBreakpoints(string filePath)
        {
            lock (_lock)
            {
                HashSet<int> lines;
                if (!_breakpoints.TryGetValue(filePath, out lines)) return new List<int>();
                HashSet<int> off;
                _disabled.TryGetValue(filePath, out off);
                List<int> result = new List<int>();
                foreach (int ln in lines)
                    if (off == null || !off.Contains(ln)) result.Add(ln);
                return result;
            }
        }

        /// <summary>True when the breakpoint at (file, line) is enabled (the default). A breakpoint that
        /// isn't set at all is reported enabled (there's nothing disabled).</summary>
        public bool IsEnabled(string filePath, int line)
        {
            lock (_lock)
            {
                HashSet<int> off;
                return !(_disabled.TryGetValue(filePath, out off) && off.Contains(line));
            }
        }

        /// <summary>Enable or disable an existing breakpoint. A disabled breakpoint stays in the list
        /// but is not sent to the adapter, so it no longer stops execution. No-op if no breakpoint
        /// exists at (file, line). Raises <see cref="BreakpointChanged"/> so the panel/editor refresh.</summary>
        public void SetEnabled(string filePath, int line, bool enabled)
        {
            bool changed = false;
            lock (_lock)
            {
                if (!HasBreakpoint(filePath, line)) return;
                if (enabled)
                {
                    HashSet<int> off;
                    if (_disabled.TryGetValue(filePath, out off) && off.Remove(line))
                    {
                        if (off.Count == 0) _disabled.Remove(filePath);
                        changed = true;
                    }
                }
                else
                {
                    HashSet<int> off;
                    if (!_disabled.TryGetValue(filePath, out off)) { off = new HashSet<int>(); _disabled[filePath] = off; }
                    changed = off.Add(line);
                }
            }
            if (changed) OnBreakpointChanged(filePath, line, true);
        }

        /// <summary>Flips the enabled/disabled state of the breakpoint at (file, line). Returns the new
        /// enabled state (true = now enabled).</summary>
        public bool ToggleEnabled(string filePath, int line)
        {
            bool nowEnabled;
            lock (_lock) { nowEnabled = !IsEnabled(filePath, line); }
            SetEnabled(filePath, line, nowEnabled);
            return nowEnabled;
        }

        /// <summary>
        /// Gets all files that have breakpoints set.
        /// </summary>
        public List<string> GetFilesWithBreakpoints()
        {
            lock (_lock)
            {
                return _breakpoints.Keys.ToList();
            }
        }

        /// <summary>
        /// Clears all breakpoints for all files.
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _breakpoints.Clear();
                _conditions.Clear();
                _disabled.Clear();
            }
        }

        private void OnBreakpointChanged(string filePath, int line, bool isAdded)
        {
            var handler = BreakpointChanged;
            if (handler != null)
            {
                handler(this, new BreakpointChangedEventArgs
                {
                    FilePath = filePath,
                    Line = line,
                    IsAdded = isAdded
                });
            }
        }

        /// <summary>Persists all breakpoints (with conditions) to the default settings file.</summary>
        public void Save() { Save(_settingsPath); }

        /// <summary>
        /// Persists all breakpoints to <paramref name="path"/> as JSON — an array of
        /// { file, line, condition?, hit? }. JSON (vs the old <c>file|lines</c> text) round-trips
        /// conditions/hit-counts safely regardless of the characters they contain. The path
        /// overload exists so tests can round-trip to a temp file instead of the user's real one.
        /// </summary>
        public void Save(string path)
        {
            try
            {
                JArray arr = new JArray();
                lock (_lock)
                {
                    foreach (var kvp in _breakpoints)
                    {
                        Dictionary<int, BreakpointCondition> cmap;
                        _conditions.TryGetValue(kvp.Key, out cmap);
                        HashSet<int> off;
                        _disabled.TryGetValue(kvp.Key, out off);
                        foreach (int line in kvp.Value.OrderBy(n => n))
                        {
                            JObject o = new JObject { ["file"] = kvp.Key, ["line"] = line };
                            BreakpointCondition c;
                            if (cmap != null && cmap.TryGetValue(line, out c) && c != null)
                            {
                                if (!string.IsNullOrEmpty(c.Condition)) o["condition"] = c.Condition;
                                if (!string.IsNullOrEmpty(c.HitCondition)) o["hit"] = c.HitCondition;
                                if (!string.IsNullOrEmpty(c.LogMessage)) o["log"] = c.LogMessage;
                            }
                            // Only persist the flag when disabled; absence means enabled (the default).
                            if (off != null && off.Contains(line)) o["enabled"] = false;
                            arr.Add(o);
                        }
                    }
                }
                File.WriteAllText(path, arr.ToString(Formatting.Indented));
            }
            catch { }
        }

        /// <summary>Restores breakpoints (with conditions) from the default settings file.</summary>
        public void Load() { Load(_settingsPath); }

        /// <summary>
        /// Restores breakpoints from <paramref name="path"/>. Reads the JSON format written by
        /// <see cref="Save(string)"/> and still understands the legacy <c>file|line1,line2</c> text
        /// format (conditions reset for legacy entries). Only restores breakpoints whose file still
        /// exists, so stale entries for deleted files are dropped.
        /// </summary>
        public void Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path);
                if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    LoadJson(text);
                    return;
                }
                LoadLegacy(text);
            }
            catch { }
        }

        private void LoadJson(string text)
        {
            JArray arr = JArray.Parse(text);
            lock (_lock)
            {
                foreach (JToken t in arr)
                {
                    string filePath = (string)t["file"];
                    int line = t["line"] != null ? (int)t["line"] : 0;
                    if (string.IsNullOrEmpty(filePath) || line <= 0) continue;
                    if (!File.Exists(filePath)) continue;

                    if (!_breakpoints.ContainsKey(filePath)) _breakpoints[filePath] = new HashSet<int>();
                    _breakpoints[filePath].Add(line);

                    // Restore disabled-state (absent/true => enabled).
                    if (t["enabled"] != null && (bool)t["enabled"] == false)
                    {
                        HashSet<int> off;
                        if (!_disabled.TryGetValue(filePath, out off)) { off = new HashSet<int>(); _disabled[filePath] = off; }
                        off.Add(line);
                    }

                    string cond = (string)t["condition"];
                    string hit = (string)t["hit"];
                    string log = (string)t["log"];
                    if (!string.IsNullOrWhiteSpace(cond) || !string.IsNullOrWhiteSpace(hit) || !string.IsNullOrWhiteSpace(log))
                    {
                        Dictionary<int, BreakpointCondition> map;
                        if (!_conditions.TryGetValue(filePath, out map)) { map = new Dictionary<int, BreakpointCondition>(); _conditions[filePath] = map; }
                        map[line] = new BreakpointCondition
                        {
                            Condition = string.IsNullOrWhiteSpace(cond) ? null : cond,
                            HitCondition = string.IsNullOrWhiteSpace(hit) ? null : hit,
                            LogMessage = string.IsNullOrWhiteSpace(log) ? null : log,
                        };
                    }
                }
            }
        }

        private void LoadLegacy(string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    int sep = trimmed.LastIndexOf('|');
                    if (sep <= 0) continue;

                    string filePath = trimmed.Substring(0, sep);
                    string nums = trimmed.Substring(sep + 1);
                    if (!File.Exists(filePath)) continue;

                    foreach (var numStr in nums.Split(','))
                    {
                        int num;
                        if (int.TryParse(numStr.Trim(), out num) && num > 0)
                        {
                            if (!_breakpoints.ContainsKey(filePath))
                                _breakpoints[filePath] = new HashSet<int>();
                            _breakpoints[filePath].Add(num);
                        }
                    }
                }
            }
        }
    }
}
