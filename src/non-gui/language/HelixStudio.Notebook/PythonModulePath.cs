using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>
    /// What a Python file on disk turns out to be, once you ask the only two questions that matter
    /// for importing it: which directory has to be on <c>sys.path</c>, and what the import statement
    /// then reads.
    ///
    /// <para>
    /// Both are answered from the file alone. A user who wants
    /// <c>from cms.tensor import device</c> to work should not have to know that
    /// <c>cms</c> is a package, that <c>tensor.py</c> lives inside it, that the directory
    /// above it is the import root, or that the root's own name
    /// (<c>03_Computational_Molecular_Science_in_56_Days</c>) is not even a legal Python
    /// identifier and therefore cannot be part of the answer. They point at the file.
    /// </para>
    /// </summary>
    public sealed class PythonModuleResolution
    {
        /// <summary>True when this file can be imported and <see cref="ModuleName"/> says how.</summary>
        public bool Succeeded { get { return Error == null; } }

        /// <summary>Why the file cannot be imported, in a sentence a user can act on. Null on success.</summary>
        public string Error { get; internal set; }

        /// <summary>The file that was resolved, full and rooted.</summary>
        public string FilePath { get; internal set; }

        /// <summary>The directory that must go on <c>sys.path</c>.</summary>
        public string RootDirectory { get; internal set; }

        /// <summary>The dotted name, e.g. <c>cms.tensor</c>.</summary>
        public string ModuleName { get; internal set; }

        /// <summary>The first segment — <c>cms</c> — which is the name the kernel will bind.</summary>
        public string TopLevelName { get; internal set; }

        /// <summary>True when the file picked was an <c>__init__.py</c>, so the module IS the package.</summary>
        public bool IsPackageInitializer { get; internal set; }

        /// <summary>
        /// The import line the user can now write, for the status bar and the confirmation. It is
        /// the whole point of the feature, so it is spelled out rather than left to be inferred.
        /// </summary>
        public string ImportStatement
        {
            get
            {
                if (!Succeeded) return null;
                return ModuleName.IndexOf('.') < 0
                    ? "import " + ModuleName
                    : "from " + ModuleName + " import <name>";
            }
        }
    }

    /// <summary>
    /// Works out how to import a Python file that lives anywhere on the disk.
    ///
    /// <para>
    /// <b>The rule is Python's own, not a heuristic.</b> A directory is part of a package's dotted
    /// name exactly when it contains an <c>__init__.py</c>. So the walk goes UP from the chosen
    /// file, collecting directory names for as long as that holds, and stops at the first directory
    /// that does not — which is, by definition, the directory Python needs on <c>sys.path</c>.
    /// For <c>…\code\cms\tensor.py</c>: <c>cms</c> has an <c>__init__.py</c> and <c>code</c> does
    /// not, so the root is <c>…\code</c> and the module is <c>cms.tensor</c>.
    /// </para>
    ///
    /// <para>
    /// <b>No project-root guessing.</b> An earlier draft of this walked further using markers
    /// (<c>pyproject.toml</c>, <c>.git</c>) to cope with PEP 420 namespace packages, and on the very
    /// case this was written for it produced <c>code.cms.tensor</c> rooted at the book directory —
    /// confidently wrong. A rule that is right by construction and occasionally incomplete beats a
    /// rule that is usually right and silently wrong: a namespace package resolves to its leaf
    /// module here, the user SEES the import statement that results, and the "add a folder" escape
    /// hatch covers the rest in one click.
    /// </para>
    ///
    /// <para>
    /// Everything here is bounded file-existence checks along one ancestor chain — no directory
    /// enumeration, no recursion, nothing that can take a noticeable amount of time on a network
    /// share or a directory with fifty thousand entries.
    /// </para>
    /// </summary>
    public static class PythonModulePath
    {
        /// <summary>
        /// A cap on the walk. Deep enough for any real package (the deepest in the fixture corpora
        /// is four), shallow enough that a pathological symlink loop cannot spin.
        /// </summary>
        private const int MaximumPackageDepth = 64;

        /// <summary>Extensions that name an importable source file.</summary>
        private static readonly string[] ImportableExtensions = { ".py", ".pyw" };

        /// <summary>
        /// Python's reserved words. A file called <c>class.py</c> is a perfectly legal file and an
        /// impossible module, and saying so beats registering a path that can never work.
        /// </summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
            "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
            "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
            "return", "try", "while", "with", "yield",
        };

        /// <summary>Resolve one Python file into a <c>sys.path</c> root and a dotted module name.</summary>
        public static PythonModuleResolution Resolve(string pythonFilePath)
        {
            var result = new PythonModuleResolution();

            if (string.IsNullOrEmpty(pythonFilePath))
            {
                result.Error = "No file was chosen.";
                return result;
            }

            string full;
            try { full = Path.GetFullPath(pythonFilePath); }
            catch (Exception ex) { result.Error = "That path cannot be read: " + ex.Message; return result; }

            result.FilePath = full;

            if (!File.Exists(full))
            {
                result.Error = "There is no file at " + full + ".";
                return result;
            }
            if (!IsImportableExtension(full))
            {
                result.Error = Path.GetFileName(full) + " is not a Python source file — pick a .py file.";
                return result;
            }

            string directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory))
            {
                result.Error = "That file has no folder to import it from.";
                return result;
            }

            string leaf = Path.GetFileNameWithoutExtension(full);
            var parts = new List<string>();

            // An __init__.py IS its directory's module: the package name comes from the folder, and
            // the walk starts one level up from where it otherwise would.
            if (string.Equals(leaf, "__init__", StringComparison.OrdinalIgnoreCase))
            {
                result.IsPackageInitializer = true;
            }
            else
            {
                if (!IsIdentifier(leaf))
                {
                    result.Error = Path.GetFileName(full) + " cannot be imported: `" + leaf
                                 + "` is not a valid Python module name.";
                    return result;
                }
                parts.Add(leaf);
            }

            for (int depth = 0; depth < MaximumPackageDepth; depth++)
            {
                if (!File.Exists(Path.Combine(directory, "__init__.py"))) break;

                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar,
                                                                 Path.AltDirectorySeparatorChar));
                // A package folder whose name is not an identifier cannot appear in a dotted name.
                // Python could not import it either, so this directory is where the path stops.
                if (!IsIdentifier(name)) break;

                parts.Insert(0, name);

                string parent = Path.GetDirectoryName(directory);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                    break;                      // a drive root: there is nowhere further up to go
                directory = parent;
            }

            if (parts.Count == 0)
            {
                // An __init__.py whose own folder is not a legal package name — the file is real,
                // the package it would define is not.
                result.Error = "The folder holding that __init__.py is not a valid Python package name.";
                return result;
            }

            result.RootDirectory = directory;
            result.ModuleName = string.Join(".", parts.ToArray());
            result.TopLevelName = parts[0];
            return result;
        }

        /// <summary>True when <paramref name="path"/> names a file this resolver can import.</summary>
        public static bool IsImportableExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension = Path.GetExtension(path);
            foreach (string candidate in ImportableExtensions)
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The top-level names a directory makes importable, for the pane that has to show what a
        /// registered root actually bought the user.
        ///
        /// <para>
        /// ONE level, never recursive, and capped: a root can be a repository with tens of
        /// thousands of entries under it, and a pane that walks all of it to draw a label is how a
        /// side panel freezes an IDE. <paramref name="limit"/> is a hard stop, and the caller is
        /// told the total so it can say "and 40 more" rather than quietly showing a prefix.
        /// </para>
        /// </summary>
        public static IList<string> ImportableNames(string directory, int limit, out int total)
        {
            total = 0;
            var names = new List<string>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return names;

            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (!File.Exists(Path.Combine(child, "__init__.py"))) continue;
                    string name = Path.GetFileName(child);
                    if (!IsIdentifier(name)) continue;
                    total++;
                    if (names.Count < limit) names.Add(name);
                }
                foreach (string child in Directory.EnumerateFiles(directory, "*.py"))
                {
                    string name = Path.GetFileNameWithoutExtension(child);
                    if (!IsIdentifier(name)) continue;
                    total++;
                    if (names.Count < limit) names.Add(name);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            return names;
        }

        /// <summary>A legal Python identifier that is not a reserved word.</summary>
        public static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (!char.IsLetter(text[0]) && text[0] != '_') return false;
            for (int i = 1; i < text.Length; i++)
                if (!char.IsLetterOrDigit(text[i]) && text[i] != '_') return false;
            return !Keywords.Contains(text);
        }
    }
}
