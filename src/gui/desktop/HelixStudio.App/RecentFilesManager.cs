using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HelixPythonNamespace.App
{
    /// <summary>
    /// Manages the recent files list.
    /// Stores in %APPDATA%/HelixStudio/recent-solutions.json
    /// </summary>
    public sealed class RecentFilesManager
    {
        // Cap the MRU list length so the menu and the JSON file stay bounded. 100: the Welcome
        // page scrolls, so the list is no longer limited by what fits one screen of rows.
        private const int MaxEntries = 100;
        private const string AppDataFolder = "HelixPython";
        private const string SolutionsFileName = "recent-solutions.json";

        /// <summary>The MRU of notebooks, kept apart from the solution MRU — see the ctor.</summary>
        public const string NotebooksFileName = "recent-notebooks.json";

        private readonly string _fileName;
        private List<string> _recentFiles;

        public RecentFilesManager() : this(SolutionsFileName) { }

        /// <summary>
        /// One MRU list per <paramref name="fileName"/>.
        ///
        /// <para>
        /// Notebooks get their own file rather than sharing the solutions list. They are opened far
        /// more often than solutions and are usually not part of one at all — mixing them would
        /// push a user's actual projects off the end of "Recent Solutions" within an afternoon, and
        /// the two menus answer different questions.
        /// </para>
        /// </summary>
        public RecentFilesManager(string fileName)
        {
            _fileName = string.IsNullOrEmpty(fileName) ? SolutionsFileName : fileName;
            _recentFiles = new List<string>();
            Load();
        }

        /// <summary>
        /// Get the list of recent files.
        /// </summary>
        public IReadOnlyList<string> GetRecentFiles()
        {
            return _recentFiles.AsReadOnly();
        }

        /// <summary>
        /// Add a file to the recent list (moves to top if already present).
        /// </summary>
        public void AddRecent(string slnPath)
        {
            if (string.IsNullOrWhiteSpace(slnPath))
            {
                return;
            }

            // Normalize path
            slnPath = Path.GetFullPath(slnPath);

            // Remove if already exists
            _recentFiles.RemoveAll(f => string.Equals(f, slnPath, StringComparison.OrdinalIgnoreCase));

            // Add to top
            _recentFiles.Insert(0, slnPath);

            // Trim to max
            while (_recentFiles.Count > MaxEntries)
            {
                _recentFiles.RemoveAt(_recentFiles.Count - 1);
            }

            Save();
        }

        /// <summary>
        /// Remove a file from the recent list.
        /// </summary>
        public void RemoveRecent(string slnPath)
        {
            _recentFiles.RemoveAll(f => string.Equals(f, slnPath, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        /// <summary>
        /// A short, disambiguating label for a recent-solution path. Because every HelixStudio
        /// solution is named "pyproject.toml", the bare file name is useless in a menu — a list of
        /// identical "pyproject.toml" rows. Use the containing folder (the project's real identity)
        /// for pyproject.toml, and the file name for anything else (e.g. a named .sln). The full path
        /// stays available as the tooltip.
        /// </summary>
        public static string DisplayName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path ?? "";
            string name = Path.GetFileName(path);
            if (string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(path);
                string folder = string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(folder)) return folder;
            }
            return name;
        }

        /// <summary>
        /// Remove entries that no longer exist on disk.
        /// </summary>
        public void PruneInvalidPaths()
        {
            var removed = _recentFiles.RemoveAll(f => !File.Exists(f));
            if (removed > 0)
            {
                Save();
            }
        }

        /// <summary>
        /// Clear all recent files.
        /// </summary>
        public void Clear()
        {
            _recentFiles.Clear();
            Save();
        }

        // Read the persisted MRU list on construction; a missing or corrupt file just
        // yields an empty list (catch below) rather than blocking startup.
        private void Load()
        {
            try
            {
                var filePath = GetFilePath();
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var loaded = JsonConvert.DeserializeObject<List<string>>(json);
                    if (loaded != null)
                    {
                        _recentFiles = loaded;
                    }
                }
            }
            catch
            {
                _recentFiles = new List<string>();
            }
        }

        // Serialize the list back to %APPDATA%, creating the folder on first save.
        private void Save()
        {
            try
            {
                var filePath = GetFilePath();
                var directory = Path.GetDirectoryName(filePath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_recentFiles, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch
            {
                // Silently fail - recent files is not critical
            }
        }

        // Resolve the storage location: HELIXSTUDIO_STATE_DIR when set (tests/isolated runs —
        // the same override UserSettingsManager honors; without it, test runs polluted the real
        // per-user list), else the per-user roaming-AppData folder.
        private string GetFilePath()
        {
            var stateDir = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            if (!string.IsNullOrEmpty(stateDir))
                return Path.Combine(Environment.ExpandEnvironmentVariables(stateDir), _fileName);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppDataFolder, _fileName);
        }
    }
}
