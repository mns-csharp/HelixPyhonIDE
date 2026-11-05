using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Extensibility;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Testing
{
    public interface IWorkspaceTestAdapter
    {
        string Id { get; }
        ProjectLanguage Language { get; }
        WorkspaceTestRun Run(string projectDirectory, bool wholeWorkspace);
    }

    /// <summary>Ordered test-runner contributions shared by the desktop and future hosts.</summary>
    public static class WorkspaceTestAdapterRegistry
    {
        private static readonly ContributionRegistry<IWorkspaceTestAdapter> Registered =
            new ContributionRegistry<IWorkspaceTestAdapter>();

        static WorkspaceTestAdapterRegistry()
        {
            // No built-in adapters in HelixPython: Python test execution runs through
            // HelixPythonNamespace.Testing (pytest/unittest child processes), not through this
            // workspace-level registry, which exists for external-toolchain runners
            // (cargo/dotnet/maven/julia) that belong to the other Helix products.
            // The registry itself stays live so hosts can still contribute adapters.
        }

        public static void Register(IWorkspaceTestAdapter adapter, int priority = 0)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            Registered.Register(adapter.Id, adapter, priority);
        }

        public static IWorkspaceTestAdapter For(ProjectLanguage language)
        {
            return Registered.FirstOrDefault(a => a.Language == language);
        }

        public static IWorkspaceTestAdapter ForFilePath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string ext = System.IO.Path.GetExtension(filePath);
            foreach (LanguageWorkspaceDescriptor descriptor in LanguageDescriptors.All)
            {
                foreach (string sourceExt in descriptor.SourceFileExtensions)
                    if (string.Equals(ext, sourceExt, StringComparison.OrdinalIgnoreCase))
                        return For(descriptor.Language);
            }
            return null;
        }

        public static IReadOnlyList<IWorkspaceTestAdapter> All { get { return Registered.Snapshot(); } }

        private sealed class DelegateAdapter : IWorkspaceTestAdapter
        {
            private readonly Func<string, bool, WorkspaceTestRun> _run;
            public DelegateAdapter(string id, ProjectLanguage language, Func<string, bool, WorkspaceTestRun> run)
            { Id = id; Language = language; _run = run; }
            public string Id { get; private set; }
            public ProjectLanguage Language { get; private set; }
            public WorkspaceTestRun Run(string projectDirectory, bool wholeWorkspace) { return _run(projectDirectory, wholeWorkspace); }
        }
    }
}
