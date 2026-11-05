using System;
using System.IO;

namespace HelixPythonNamespace.Core.IO
{
    /// <summary>
    /// The single authority for comparing filesystem PATHS, and the one place that knows whether
    /// this platform's filesystem is case-sensitive.
    ///
    /// The distinction this class exists to enforce — the two are NOT the same question, and
    /// conflating them is what made the policy engine subtly wrong off Windows:
    ///
    /// <list type="bullet">
    /// <item><b>Path identity</b> — "is this file inside that project?", "are these two member
    /// directories the same one?". Must follow the FILESYSTEM's rules. On a case-sensitive volume
    /// <c>sol/calc</c> and <c>sol/Calc</c> are two different directories, and comparing them
    /// case-insensitively silently attributes a file to a project that does not contain it: rule 1
    /// stops reporting a non-member file, and rule 3 blames the wrong project. Use
    /// <see cref="Comparison"/>, <see cref="Comparer"/>, <see cref="AreSame"/>,
    /// <see cref="IsUnderDirectory"/>.</item>
    /// <item><b>Naming conventions</b> — "is this segment called <c>vendor</c>?", "does this stem
    /// look like a test file?". These are heuristics about INTENT, not about which file this is,
    /// and they stay case-insensitive on every platform on purpose. A directory named
    /// <c>Vendor</c> means the same thing to a human as <c>vendor</c>; being generous costs at
    /// most an unpoliced file, while being strict would block the Run button over code the user
    /// does not own. Those callers keep using <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// directly, and say so.</item>
    /// </list>
    ///
    /// Today the product is net472/WinForms, so <see cref="IsCaseSensitive"/> is always false at
    /// runtime and the identity comparisons behave exactly as the hard-coded
    /// <c>OrdinalIgnoreCase</c> they replaced. The value is that the decision is now made once,
    /// deliberately, in a place that is correct if the engine is ever run anywhere else — rather
    /// than being an assumption baked into five separate call sites that nobody would think to
    /// revisit.
    /// </summary>
    public static class PathComparison
    {
        /// <summary>
        /// True when this platform's filesystem distinguishes <c>Foo</c> from <c>foo</c>.
        ///
        /// Windows (NTFS/FAT) is case-insensitive; POSIX filesystems are case-sensitive. macOS is
        /// case-INsensitive by default and is deliberately treated as sensitive here: this product
        /// does not target it, and over-strictness can only fail to merge two spellings of one
        /// path, whereas under-strictness merges two genuinely different directories — the failure
        /// that lets unviewable code execute.
        /// </summary>
        public static bool IsCaseSensitive
        {
            get { return _caseSensitive; }
        }

        private static readonly bool _caseSensitive = DetectCaseSensitivity();

        private static bool DetectCaseSensitivity()
        {
            // Path.DirectorySeparatorChar is the cheapest reliable signal available on net472 and
            // Mono alike: '\' only on Windows.
            if (Path.DirectorySeparatorChar == '\\') return false;
            PlatformID id = Environment.OSVersion.Platform;
            return id != PlatformID.Win32NT && id != PlatformID.Win32Windows
                && id != PlatformID.Win32S && id != PlatformID.WinCE;
        }

        /// <summary>The <see cref="StringComparison"/> to use when comparing paths for IDENTITY.</summary>
        public static StringComparison Comparison
        {
            get { return _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase; }
        }

        /// <summary>The matching <see cref="StringComparer"/>, for dictionaries keyed by path.</summary>
        public static StringComparer Comparer
        {
            get { return _caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase; }
        }

        /// <summary>True when two paths name the same file or directory (trailing separators ignored).</summary>
        public static bool AreSame(string a, string b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(TrimTrailing(a), TrimTrailing(b), Comparison);
        }

        /// <summary>
        /// True when <paramref name="path"/> lies strictly inside <paramref name="directory"/>.
        ///
        /// The separator is appended before comparing so a sibling whose name merely starts with
        /// the directory's name does not match — <c>sol/calc_engine/x.py</c> is not inside
        /// <c>sol/calc</c>. Every prefix test in the policy engine needs that guard, and doing it
        /// here means no caller can forget it.
        /// </summary>
        public static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory)) return false;
            string prefix = TrimTrailing(directory) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, Comparison);
        }

        /// <summary>
        /// Splits a path into its components, always honouring BOTH <c>\</c> and <c>/</c>
        /// regardless of platform.
        ///
        /// <see cref="Path.DirectorySeparatorChar"/> and <see cref="Path.AltDirectorySeparatorChar"/>
        /// are the same character (<c>/</c>) on POSIX, so splitting on those two alone leaves a
        /// Windows-style path — one persisted into a <c>.toml</c>, a ledger or a settings file on
        /// Windows and then read on Linux — as a single unsplittable segment. Anything deciding by
        /// path component (vendored-code detection, test-directory conventions) would then see no
        /// components at all and silently answer "no".
        /// </summary>
        public static string[] SplitSegments(string path)
        {
            if (string.IsNullOrEmpty(path)) return new string[0];
            return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>The path without any trailing directory separator (either flavour).</summary>
        public static string TrimTrailing(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.TrimEnd('\\', '/');
        }
    }
}
