using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>Persisted per-template usage: creation count, last-used time, and favorite flag (SRS §4).</summary>
    public sealed class TemplateUsage
    {
        public string Id { get; set; } = "";
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public bool Favorite { get; set; }
    }

    /// <summary>
    /// Tracks "recently used" and "favorites" across sessions (SRS §4). A small JSON file keyed by template id
    /// records how many projects were created from each template, when it was last used, and whether it is
    /// pinned as a favorite. UI-independent and unit-testable (pass an explicit path); the wizard uses the
    /// standard <see cref="DefaultPath"/> under %APPDATA%. Corrupt/missing files load as empty (never throw).
    /// </summary>
    public sealed class TemplateUsageStore
    {
        private readonly Dictionary<string, TemplateUsage> _map = new Dictionary<string, TemplateUsage>(StringComparer.Ordinal);
        private string _path;

        public static string DefaultPath()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HelixStudio", "template_usage.json"); }
            catch { return null; }
        }

        /// <summary>Load the store from <paramref name="path"/> (or an empty store if missing/corrupt).</summary>
        public static TemplateUsageStore Load(string path)
        {
            var s = new TemplateUsageStore { _path = path };
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var list = JsonConvert.DeserializeObject<List<TemplateUsage>>(File.ReadAllText(path));
                    if (list != null)
                        foreach (var u in list) if (u != null && !string.IsNullOrEmpty(u.Id)) s._map[u.Id] = u;
                }
            }
            catch { /* corrupt file -> empty store */ }
            return s;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var list = new List<TemplateUsage>(_map.Values);
                File.WriteAllText(_path, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch { /* best-effort persistence */ }
        }

        /// <summary>Record that a project was created from <paramref name="id"/> at <paramref name="whenUtc"/>.</summary>
        public void RecordUse(string id, DateTime whenUtc)
        {
            if (string.IsNullOrEmpty(id)) return;
            TemplateUsage u = Get(id);
            u.Count++;
            u.LastUsedUtc = whenUtc;
        }

        public bool IsFavorite(string id) { TemplateUsage u; return _map.TryGetValue(id, out u) && u.Favorite; }
        public void SetFavorite(string id, bool fav) { if (!string.IsNullOrEmpty(id)) Get(id).Favorite = fav; }
        public bool ToggleFavorite(string id) { var u = Get(id); u.Favorite = !u.Favorite; return u.Favorite; }

        public int Count(string id) { TemplateUsage u; return _map.TryGetValue(id, out u) ? u.Count : 0; }
        public DateTime LastUsed(string id) { TemplateUsage u; return _map.TryGetValue(id, out u) ? u.LastUsedUtc : DateTime.MinValue; }

        /// <summary>Template ids ordered by most-recently-used first, up to <paramref name="n"/>.</summary>
        public List<string> Recent(int n)
        {
            var used = new List<TemplateUsage>();
            foreach (var u in _map.Values) if (u.Count > 0) used.Add(u);
            used.Sort((a, b) => b.LastUsedUtc.CompareTo(a.LastUsedUtc));
            var ids = new List<string>();
            for (int i = 0; i < used.Count && i < n; i++) ids.Add(used[i].Id);
            return ids;
        }

        private TemplateUsage Get(string id)
        {
            TemplateUsage u;
            if (!_map.TryGetValue(id, out u)) { u = new TemplateUsage { Id = id }; _map[id] = u; }
            return u;
        }
    }
}
