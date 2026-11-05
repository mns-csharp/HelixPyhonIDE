using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.IO;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// A structural spine: the set of directories and files, as canonical solution-relative paths.
    ///
    /// The SAME type describes both sides of the comparison — what is on disk
    /// (<see cref="DiskLayoutScanner"/>) and what the tree will draw
    /// (<see cref="TreeLayoutProjector"/>). That is the whole point of the parity contract: the
    /// two are meant to be one structure rendered twice, so if they cannot even be expressed in
    /// one type, the contract is not being stated honestly.
    /// </summary>
    public sealed class StructureLayout
    {
        private readonly HashSet<string> _directories;
        private readonly HashSet<string> _files;

        public StructureLayout()
        {
            // Path IDENTITY — follows the filesystem, so on a case-sensitive volume `Calc` and
            // `calc` stay two entries rather than silently merging into one.
            _directories = new HashSet<string>(PathComparison.Comparer);
            _files = new HashSet<string>(PathComparison.Comparer);
        }

        public IEnumerable<string> Directories
        {
            get { return _directories.OrderBy(p => p, StringComparer.OrdinalIgnoreCase); }
        }

        public IEnumerable<string> Files
        {
            get { return _files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase); }
        }

        public int DirectoryCount { get { return _directories.Count; } }
        public int FileCount { get { return _files.Count; } }

        /// <summary>
        /// Adds a directory and every ancestor up to (but not including) the root.
        ///
        /// Implicit ancestors matter: the tree projects <c>bugmap/core/residuals.py</c> without
        /// ever naming <c>bugmap/core</c>, and a comparison that only saw explicitly-added
        /// directories would report the intermediate folder as a Phantom on one side and an Orphan
        /// on the other — two defects for a directory that is not actually in dispute.
        /// </summary>
        public void AddDirectory(string relativePath)
        {
            string p = StructurePath.Normalize(relativePath);
            while (p.Length > 0)
            {
                if (!_directories.Add(p)) break; // ancestors already present
                p = StructurePath.Parent(p);
            }
        }

        /// <summary>Adds a file and implies every directory above it.</summary>
        public void AddFile(string relativePath)
        {
            string p = StructurePath.Normalize(relativePath);
            if (p.Length == 0) return;
            _files.Add(p);
            AddDirectory(StructurePath.Parent(p));
        }

        public bool HasDirectory(string relativePath)
        {
            return _directories.Contains(StructurePath.Normalize(relativePath));
        }

        public bool HasFile(string relativePath)
        {
            return _files.Contains(StructurePath.Normalize(relativePath));
        }

        /// <summary>True when the path is present as either a directory or a file — the question
        /// a Type-clash check has to ask before it can accuse either side.</summary>
        public bool HasAnything(string relativePath)
        {
            return HasDirectory(relativePath) || HasFile(relativePath);
        }
    }
}
