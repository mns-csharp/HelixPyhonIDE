using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Result of scanning a directory for Python packages. Each descriptor
    /// corresponds to a single <c>.pyproj</c> that will be emitted.
    /// </summary>
    public sealed class PackageDescriptor
    {
        public PackageDescriptor(string name, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
            if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("directoryPath");
            Name = name;
            DirectoryPath = directoryPath;
            PyFiles = new List<string>();
            SubDirectories = new List<string>();
        }

        /// <summary>Simple name used for display and as the .pyproj file stem.</summary>
        public string Name { get; }

        /// <summary>Absolute path to the package directory (contains __init__.py or is synthesized).</summary>
        public string DirectoryPath { get; }

        /// <summary>Relative paths (to <see cref="DirectoryPath"/>) of the .py files belonging to this package.</summary>
        public List<string> PyFiles { get; }

        /// <summary>Relative paths (to <see cref="DirectoryPath"/>) of subdirectories under this package.</summary>
        public List<string> SubDirectories { get; }

        /// <summary>True when the package was synthesized because the directory had loose .py files with no __init__.py.</summary>
        public bool IsSynthesized { get; set; }

        /// <summary>Absolute path where the .pyproj will be written.</summary>
        public string PyProjFilePath { get; set; }

        /// <summary>Project GUID used in both the .sln and the .pyproj's &lt;ProjectGuid&gt; element.</summary>
        public Guid ProjectGuid { get; set; }
    }
}
