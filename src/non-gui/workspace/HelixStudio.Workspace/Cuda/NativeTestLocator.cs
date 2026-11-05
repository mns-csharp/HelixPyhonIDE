using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>Source location of one native test's defining macro.</summary>
    public sealed class NativeTestLocation
    {
        public string FilePath;
        public int Line;          // 1-based
    }

    /// <summary>
    /// Maps ctest test names back to the source line their defining macro sits on. ctest output
    /// carries NO source locations, so without this the Tests panel's Line column stays blank for
    /// C++/CUDA suites and double-clicking a row has nowhere to navigate. Covers both frameworks
    /// the IDE's templates/fixtures use:
    ///   Catch2 — <c>TEST_CASE("name", ...)</c> / <c>TEMPLATE_TEST_CASE("name", ...)</c> keyed by
    ///            the quoted name; <c>SCENARIO("name")</c> keyed as "Scenario: name" (the name
    ///            catch_discover_tests registers).
    ///   GTest  — <c>TEST(Suite, Name)</c> / <c>TEST_F</c> / <c>TEST_P</c> / <c>TYPED_TEST</c>
    ///            keyed as "Suite.Name" (the name gtest_discover_tests registers).
    /// </summary>
    public static class NativeTestLocator
    {
        private static readonly Regex Catch2Re = new Regex(
            @"^\s*(?<macro>TEST_CASE|TEMPLATE_TEST_CASE|SCENARIO)\s*\(\s*""(?<name>(?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);
        private static readonly Regex GtestRe = new Regex(
            @"^\s*(?:TEST|TEST_F|TEST_P|TYPED_TEST)\s*\(\s*(?<suite>\w+)\s*,\s*(?<name>\w+)\s*\)",
            RegexOptions.Compiled);

        private static readonly string[] SourceExtensions = { ".cpp", ".cc", ".cxx", ".cu" };

        /// <summary>
        /// Scans every C++/CUDA source under <paramref name="rootDir"/> (skipping build output,
        /// fetched dependencies and VCS internals) and indexes each test macro's name → location.
        /// A duplicate name keeps the first definition seen.
        /// </summary>
        public static Dictionary<string, NativeTestLocation> BuildIndex(string rootDir)
        {
            var index = new Dictionary<string, NativeTestLocation>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) return index;
            Scan(rootDir, index);
            return index;
        }

        private static void Scan(string dir, Dictionary<string, NativeTestLocation> index)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { return; }
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(SourceExtensions, ext) < 0) continue;
                IndexFile(file, index);
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "build", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "_deps", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "CMakeFiles", StringComparison.OrdinalIgnoreCase)) continue;
                Scan(sub, index);
            }
        }

        private static void IndexFile(string file, Dictionary<string, NativeTestLocation> index)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { return; }
            for (int i = 0; i < lines.Length; i++)
            {
                Match c = Catch2Re.Match(lines[i]);
                if (c.Success)
                {
                    string name = c.Groups["name"].Value.Replace("\\\"", "\"");
                    if (c.Groups["macro"].Value == "SCENARIO") name = "Scenario: " + name;
                    AddFirst(index, name, file, i + 1);
                    continue;
                }
                Match g = GtestRe.Match(lines[i]);
                if (g.Success)
                    AddFirst(index, g.Groups["suite"].Value + "." + g.Groups["name"].Value, file, i + 1);
            }
        }

        private static void AddFirst(Dictionary<string, NativeTestLocation> index,
            string name, string file, int line)
        {
            if (index.ContainsKey(name)) return;
            index[name] = new NativeTestLocation { FilePath = Path.GetFullPath(file), Line = line };
        }
    }
}
