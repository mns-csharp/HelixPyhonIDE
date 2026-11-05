using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>Where a module a package imports actually comes from.</summary>
    public enum DependencyOrigin
    {
        /// <summary>Ships with CPython itself.</summary>
        StandardLibrary = 0,

        /// <summary>Installed from an index into site-packages / the venv.</summary>
        ThirdParty = 1,

        /// <summary>Another workspace member of this same solution.</summary>
        SolutionPackage = 2,
    }

    /// <summary>
    /// One module a package imports, and the names it pulls out of it.
    ///
    /// Built from the real <c>import</c> / <c>from … import …</c> statements of the package's own
    /// source, not from the dependency list declared in pyproject.toml: what a package DECLARES and
    /// what it actually USES are different questions, and the Solution Explorer is answering the
    /// second one.
    /// </summary>
    public sealed class ModuleDependency
    {
        public ModuleDependency(string moduleName, DependencyOrigin origin)
        {
            ModuleName = moduleName ?? string.Empty;
            Origin = origin;
            ImportedNames = new List<string>();
        }

        /// <summary>Dotted module name as written: <c>os.path</c>, <c>lib_core.shapes</c>.</summary>
        public string ModuleName { get; private set; }

        /// <summary>
        /// Settable because it cannot be decided while the package is being read: whether
        /// <c>lib_core</c> is a sibling member of this solution or something from an index depends
        /// on the OTHER projects, which do not all exist yet. The loader assigns the final value
        /// once the whole solution is known.
        /// </summary>
        public DependencyOrigin Origin { get; set; }

        /// <summary>
        /// The names imported FROM that module (`from x import a, b` -> a, b), deduplicated and
        /// sorted. Empty for a plain `import x`, where the module itself is the only name bound.
        /// </summary>
        public List<string> ImportedNames { get; private set; }

        public override string ToString() { return ModuleName; }
    }

    /// <summary>
    /// The interpreter a package runs on, as read-only facts for display. Populated at solution
    /// load; nothing here is a setting the tree can change.
    /// </summary>
    public sealed class PythonEnvironmentInfo
    {
        /// <summary>Absolute path of the interpreter, when one could be determined.</summary>
        public string InterpreterPath { get; set; }

        /// <summary>The virtual environment directory backing it, when it is one.</summary>
        public string VirtualEnvPath { get; set; }

        /// <summary>The venv's display name (its folder name, e.g. <c>.venv</c>).</summary>
        public string VirtualEnvName { get; set; }

        /// <summary>The <c>requires-python</c> constraint from pyproject.toml, verbatim.</summary>
        public string RequiresPython { get; set; }

        /// <summary>True when nothing at all could be determined — the tree then says so plainly.</summary>
        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrEmpty(InterpreterPath)
                    && string.IsNullOrEmpty(VirtualEnvPath)
                    && string.IsNullOrEmpty(RequiresPython);
            }
        }
    }
}
