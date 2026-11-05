namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>One row of Preferences ▸ Install Add-in.</summary>
    internal sealed class AddInCatalogEntry
    {
        public string Id;
        public string DisplayName;
        public Version Version;
        public AddInKind Kind;
        public bool Loaded;
        public string Folder;

        /// <summary>The row text: <c>DisplayName — version — Bundled|User</c> (spec 001, §4.6).</summary>
        public override string ToString()
        {
            return DisplayName + " — " + (Version != null ? Version.ToString() : "?") + " — " + Kind;
        }
    }

    /// <summary>
    /// Every installed add-in, loaded or not, minus the removed ones (spec 001, §4.6): the loaded
    /// add-ins, then whatever else the two roots hold (an add-in that failed to start is still
    /// installed). A folder queued for deletion is gone as far as the user is concerned.
    /// </summary>
    internal static class AddInCatalog
    {
        internal static List<AddInCatalogEntry> List(MainForm form)
        {
            UserSettings settings = form.AddInUserSettings;
            List<AddInCatalogEntry> entries = form.LoadedAddIns.Select(a => new AddInCatalogEntry
            {
                Id = a.Id, DisplayName = a.DisplayName, Version = a.Version, Kind = a.Kind, Loaded = true, Folder = a.Folder,
            }).ToList();

            foreach (KeyValuePair<string, AddInKind> root in AddInLoader.Roots)
            {
                if (!Directory.Exists(root.Key)) continue;
                string[] folders;
                try { folders = Directory.GetDirectories(root.Key); }
                catch (Exception) { continue; }
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                foreach (string folder in folders)
                {
                    if (IsPendingDelete(settings, folder)) continue;
                    if (entries.Any(e => e.Folder != null && string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (string dll in AddInLoader.CandidateDlls(folder))
                    {
                        AddInProbe probe = AddInInstaller.Probe(dll);
                        foreach (KeyValuePair<string, string> addIn in probe.AddIns)
                        {
                            if (AddInLoader.IsRemoved(settings, addIn.Key) || entries.Any(e => e.Id == addIn.Key)) continue;
                            entries.Add(new AddInCatalogEntry
                            {
                                Id = addIn.Key, DisplayName = addIn.Value, Version = probe.Version, Kind = root.Value, Loaded = false, Folder = folder,
                            });
                        }
                    }
                }
            }
            return entries;
        }

        /// <summary>The rows whose name or Id contains <paramref name="text"/>, case-insensitive.</summary>
        internal static List<AddInCatalogEntry> Filter(IEnumerable<AddInCatalogEntry> entries, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return entries.ToList();
            string t = text.Trim();
            return entries.Where(e => Contains(e.DisplayName, t) || Contains(e.Id, t)).ToList();
        }

        private static bool Contains(string value, string text)
        {
            return value != null && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPendingDelete(UserSettings settings, string folder)
        {
            return settings != null && settings.PendingAddInDeletes != null
                && settings.PendingAddInDeletes.Contains(folder, StringComparer.OrdinalIgnoreCase);
        }
    }
}
