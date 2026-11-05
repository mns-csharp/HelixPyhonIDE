using System;
using System.IO;
using HelixPythonNamespace.Core.IO;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Path arithmetic for the parity engine, in ONE canonical form: solution-relative,
    /// forward-slash separated, no leading or trailing slash, <c>""</c> for the solution root
    /// itself.
    ///
    /// Two reasons this is not inlined at the call sites:
    ///
    /// <list type="bullet">
    /// <item>Every comparison in this engine is a path comparison, so the identity-vs-convention
    /// distinction from <see cref="PathComparison"/> is load-bearing here more than anywhere else.
    /// Directory identity goes through <see cref="PathComparison.Comparison"/>; the deliberate
    /// exception is <see cref="EqualsIgnoringCase"/>, used only to tell a Case defect from a Name
    /// defect, which is a convention question.</item>
    /// <item><b>A junction is not a copy.</b> Every absolute path entering the engine is put
    /// through <see cref="Canonical"/> first, or the engine can compute a "repair" of a solution
    /// into itself.</item>
    /// </list>
    /// </summary>
    internal static class StructurePath
    {
        /// <summary>
        /// The absolute path in its settled form: full, no trailing separator. Never throws —
        /// an unresolvable path is returned trimmed rather than taking the caller down, because a
        /// scan must survive one bad entry.
        /// </summary>
        public static string Canonical(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
            try { return PathComparison.TrimTrailing(Path.GetFullPath(absolutePath)); }
            catch { return PathComparison.TrimTrailing(absolutePath); }
        }

        /// <summary>
        /// <paramref name="absolutePath"/> expressed relative to <paramref name="rootDirectory"/>
        /// in canonical form, or null when it is not inside the root at all. Null is a meaningful
        /// answer — a project directory outside its own solution is a defect, not an exception.
        /// </summary>
        public static string Relative(string rootDirectory, string absolutePath)
        {
            string root = Canonical(rootDirectory);
            string full = Canonical(absolutePath);
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(full)) return null;
            if (PathComparison.AreSame(root, full)) return string.Empty;
            if (!PathComparison.IsUnderDirectory(full, root)) return null;
            return Normalize(full.Substring(root.Length + 1));
        }

        /// <summary>Forward-slash separated, no leading/trailing slash.</summary>
        public static string Normalize(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return string.Empty;
            string s = relativePath.Replace('\\', '/').Trim('/');
            return s;
        }

        /// <summary>Join canonical relative segments, skipping empties.</summary>
        public static string Combine(string a, string b)
        {
            string x = Normalize(a);
            string y = Normalize(b);
            if (x.Length == 0) return y;
            if (y.Length == 0) return x;
            return x + "/" + y;
        }

        /// <summary>The absolute path for a canonical solution-relative path.</summary>
        public static string ToAbsolute(string rootDirectory, string relativePath)
        {
            string root = Canonical(rootDirectory);
            string rel = Normalize(relativePath);
            if (rel.Length == 0) return root;
            return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public static string[] Segments(string relativePath)
        {
            return PathComparison.SplitSegments(Normalize(relativePath));
        }

        /// <summary>The parent of a canonical relative path; <c>""</c> for a top-level entry.</summary>
        public static string Parent(string relativePath)
        {
            string s = Normalize(relativePath);
            int i = s.LastIndexOf('/');
            return i < 0 ? string.Empty : s.Substring(0, i);
        }

        /// <summary>The last segment of a canonical relative path.</summary>
        public static string Leaf(string relativePath)
        {
            string s = Normalize(relativePath);
            int i = s.LastIndexOf('/');
            return i < 0 ? s : s.Substring(i + 1);
        }

        /// <summary>Path IDENTITY comparison — follows the filesystem's case rules.</summary>
        public static bool AreSame(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), PathComparison.Comparison);
        }

        /// <summary>
        /// Deliberately case-INSENSITIVE on every platform. Used for exactly one question:
        /// "do these two names differ ONLY by case?", which decides Case vs Name. That is a
        /// convention question, not an identity one — see <see cref="PathComparison"/>.
        /// </summary>
        public static bool EqualsIgnoringCase(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The part of <paramref name="path"/> below <paramref name="ancestor"/>, for a path the
        /// caller has already established is under it.
        ///
        /// It exists for the empty ancestor. The solution ROOT is <c>""</c> in this canonical
        /// form, and the obvious inline form — <c>path.Substring(ancestor.Length + 1)</c> — then
        /// skips one character too many and silently eats the first letter of every path
        /// (<c>main.py</c> → <c>ain.py</c>). Written out twice in the checker, that turned every
        /// file of a root-anchored project into an Orphan.
        /// </summary>
        public static string Remainder(string path, string ancestor)
        {
            string p = Normalize(path);
            string a = Normalize(ancestor);
            if (a.Length == 0) return p;
            return p.Substring(a.Length + 1);
        }

        /// <summary>True when <paramref name="path"/> lies strictly inside <paramref name="ancestor"/>.</summary>
        public static bool IsUnder(string path, string ancestor)
        {
            string p = Normalize(path);
            string a = Normalize(ancestor);
            if (a.Length == 0) return p.Length > 0;
            return p.StartsWith(a + "/", PathComparison.Comparison);
        }
    }
}
