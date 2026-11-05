namespace HelixPythonNamespace.UI
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;

    /// <summary>
    /// Loads checked-in toolbar artwork without retaining a lock on the PNG file.
    /// This source is compiled into both UI assemblies so neither assembly needs a
    /// public design-time dependency on an icon-provider component.
    /// </summary>
    internal static class ToolbarIconDiskLoader
    {
        private const string DefaultSubfolder = "product\\toolbar";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, byte[]> FileBytes =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string[]> SearchDirectoriesBySubfolder =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static Image Load(string fileName)
        {
            return Load(DefaultSubfolder, fileName);
        }

        /// <summary>Loads a PNG from "assets/&lt;subfolder&gt;/&lt;fileName&gt;" (e.g. subfolder
        /// "product\\banner" for assets/product/banner/...). <paramref name="subfolder"/> may
        /// itself contain a separator, e.g. "product\\toolbar". Note that the separator is a C#
        /// ESCAPE: written with one backslash, "product\banner" is the two characters "product"
        /// and a BACKSPACE, the load throws, and the caller's catch turns that into a missing
        /// image rather than an error. That is exactly how the Welcome banner went missing.</summary>
        public static Image Load(string subfolder, string fileName)
        {
            string cacheKey = subfolder + "|" + fileName;
            byte[] bytes;
            lock (SyncRoot)
            {
                if (!FileBytes.TryGetValue(cacheKey, out bytes))
                {
                    string path = FindIconPath(subfolder, fileName);
                    bytes = File.ReadAllBytes(path);
                    FileBytes.Add(cacheKey, bytes);
                }
            }

            using (var stream = new MemoryStream(bytes, false))
            using (Image decoded = Image.FromStream(stream))
                return new Bitmap(decoded);
        }

        private static string FindIconPath(string subfolder, string fileName)
        {
            string[] directories = GetSearchDirectories(subfolder);
            foreach (string directory in directories)
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path)) return path;
            }

            throw new FileNotFoundException(
                "Could not locate asset '" + fileName + "' under assets/" + subfolder + ". Searched: " +
                string.Join("; ", directories));
        }

        private static string[] GetSearchDirectories(string subfolder)
        {
            string[] cached;
            if (SearchDirectoriesBySubfolder.TryGetValue(subfolder, out cached)) return cached;

            var searched = new List<string>();
            AddCandidate(searched, AppDomain.CurrentDomain.BaseDirectory, subfolder);
            AddCandidate(searched, Environment.CurrentDirectory, subfolder);
            AddRepositoryCandidates(searched, AppDomain.CurrentDomain.BaseDirectory, subfolder);
            AddRepositoryCandidates(searched, Environment.CurrentDirectory, subfolder);
            cached = searched.ToArray();
            SearchDirectoriesBySubfolder[subfolder] = cached;
            return cached;
        }

        private static void AddCandidate(List<string> candidates, string root, string subfolder)
        {
            if (string.IsNullOrEmpty(root)) return;
            string candidate = Path.GetFullPath(Path.Combine(Path.Combine(root, "assets"), subfolder));
            if (!candidates.Contains(candidate)) candidates.Add(candidate);
        }

        private static void AddRepositoryCandidates(List<string> candidates, string startPath, string subfolder)
        {
            if (string.IsNullOrEmpty(startPath)) return;
            DirectoryInfo directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                    AddCandidate(candidates, directory.FullName, subfolder);
                directory = directory.Parent;
            }
        }
    }
}
