using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// The single dispatch seam for "which language owns this workspace path?". Every App-layer
    /// open/drag-drop/recent-file decision goes through here, so per-language recognition rules
    /// live in exactly one provider each — fixing the C++ arm can no longer silently change the
    /// Python arm, and vice versa.
    ///
    /// Detection order matters and is fixed: C++ first (a CMakeLists.txt-bearing directory is
    /// unambiguous), then Python (pyproject.toml / .sln / .slnx). New languages register at the
    /// end unless their manifest can collide with an earlier one.
    /// </summary>
    public static class LanguageWorkspaceRegistry
    {
        private static readonly object Sync = new object();
        private static readonly List<ILanguageWorkspaceProvider> Ordered =
            new List<ILanguageWorkspaceProvider>
            {
                // HelixPython detects C++ (the target of the Convert-to-C/C++/CUDA commands and
                // the build-via-C++ path) and Python. The Rust/C#/Java/Julia manifest providers
                // ship in their own products; the registry stays open for host contributions.
                new CppWorkspaceProvider(),
                new PythonWorkspaceProvider(),
            };

        /// <summary>Snapshot of the providers in detection order.</summary>
        public static IReadOnlyList<ILanguageWorkspaceProvider> Providers
        {
            get { lock (Sync) return new List<ILanguageWorkspaceProvider>(Ordered); }
        }

        /// <summary>Append a provider for a new language (or replace the one for its language).</summary>
        public static void Register(ILanguageWorkspaceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            lock (Sync)
            {
                Ordered.RemoveAll(p => p.Language == provider.Language);
                Ordered.Add(provider);
            }
        }

        /// <summary>Provider for a known language, or null when none is registered.</summary>
        public static ILanguageWorkspaceProvider For(ProjectLanguage language)
        {
            lock (Sync)
            {
                foreach (ILanguageWorkspaceProvider p in Ordered)
                    if (p.Language == language) return p;
            }
            return null;
        }

        /// <summary>
        /// First provider whose <see cref="ILanguageWorkspaceProvider.CanOpen"/> accepts
        /// <paramref name="path"/> (disk-aware), or null. Null means "not directly openable" —
        /// callers fall through to their historical default (Python load error / Import wizard),
        /// so unknown paths keep their exact pre-layer error messages.
        /// </summary>
        public static ILanguageWorkspaceProvider TryDetect(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (ILanguageWorkspaceProvider p in Providers)
            {
                try { if (p.CanOpen(path)) return p; }
                catch { /* a provider must never veto the others */ }
            }
            return null;
        }

        /// <summary>
        /// Name-only recognition across all languages — the registry-backed replacement for
        /// MainForm.IsProjectPath's inline list (CMakeLists.txt | pyproject.toml | *.sln | *.slnx).
        /// No disk I/O, exactly like the original.
        /// </summary>
        public static bool IsRecognizedProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (ILanguageWorkspaceProvider p in Providers)
            {
                try { if (p.RecognizesManifest(path)) return true; }
                catch { }
            }
            return false;
        }
    }
}
