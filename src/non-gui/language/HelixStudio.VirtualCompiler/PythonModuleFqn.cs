using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// Python-canonical module FQN derivation. Walks upward from the file's
    /// directory while each ancestor contains an <c>__init__.py</c> and joins
    /// the collected names with dots. Matches CPython's regular-package
    /// semantics (PEP 328). PEP 420 namespace packages are out of scope for
    /// this MVP — the walk stops at the first <c>__init__</c>-free directory.
    /// Callers with a source-layout (files under <c>src/</c> with no
    /// <c>src/__init__.py</c>) get canonical results automatically because
    /// <c>src/</c> isn't treated as part of the package path.
    /// </summary>
    public static class PythonModuleFqn
    {
        /// <summary>
        /// Returns the dotted Python import path for <paramref name="filePath"/>,
        /// or an empty string if the input is null/empty or cannot be normalized.
        /// </summary>
        public static string Derive(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return string.Empty;
            string full;
            try { full = Path.GetFullPath(filePath); }
            catch { return string.Empty; }

            string dir = Path.GetDirectoryName(full);
            string stem = Path.GetFileNameWithoutExtension(full);
            if (string.IsNullOrEmpty(dir))
            {
                return stem == "__init__" ? string.Empty : stem;
            }

            List<string> parts = new List<string>();
            while (!string.IsNullOrEmpty(dir) &&
                   File.Exists(Path.Combine(dir, "__init__.py")))
            {
                parts.Insert(0, Path.GetFileName(dir));
                dir = Path.GetDirectoryName(dir);
            }

            if (parts.Count == 0)
            {
                return stem == "__init__" ? string.Empty : stem;
            }

            string pkg = string.Join(".", parts);
            return stem == "__init__" ? pkg : pkg + "." + stem;
        }
    }
}
