using System;
using System.IO;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Where tests write their screenshots. Test runs must never write into the repository, so the
    /// root is <c>HELIX_TEST_SHOT_DIR</c> if set, else <c>%TEMP%\HelixPython\test-shots</c>.
    /// The sub-path mirrors the old <c>screenshots/</c> layout, so a deliberate evidence capture
    /// (Chapter 6, §6.2) sets <c>HELIX_TEST_SHOT_DIR=&lt;repo&gt;\screenshots</c> and gets the old paths.
    /// </summary>
    internal static class TestShotDir
    {
        internal static string For(params string[] parts)
        {
            string root = Environment.GetEnvironmentVariable("HELIX_TEST_SHOT_DIR");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Path.GetTempPath(), "HelixPython", "test-shots");
            string[] all = new string[parts.Length + 1];
            all[0] = root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }
    }
}
