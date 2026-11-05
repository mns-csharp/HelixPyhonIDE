using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Extensibility;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    public sealed class PackageDependencyInfo
    {
        public string Name;
        public string Value;
        public string Identity;
    }

    public interface IPackageManagerAdapter
    {
        string Id { get; }
        ProjectLanguage Language { get; }
        string ValueLabel { get; }
        List<PackageDependencyInfo> List(string manifestPath);
        bool Add(string manifestPath, string name, string value);
        bool Remove(string manifestPath, string identity);
    }

    public static class PackageManagerAdapterRegistry
    {
        private static readonly ContributionRegistry<IPackageManagerAdapter> Registered =
            new ContributionRegistry<IPackageManagerAdapter>();

        static PackageManagerAdapterRegistry()
        {
            // HelixPython ships the PEP 621 adapter only. The nuget/cargo/maven/gradle/julia
            // adapters belong to their own products; the registry stays open so a host can
            // still contribute one.
            Register(new PythonAdapter());
        }

        public static void Register(IPackageManagerAdapter adapter, int priority = 0)
        { Registered.Register(adapter.Id, adapter, priority); }

        public static IPackageManagerAdapter For(ProjectLanguage language)
        { return Registered.FirstOrDefault(a => a.Language == language); }

        public static IPackageManagerAdapter For(ProjectLanguage language, string manifestPath)
        {
            // The combined build disambiguated Maven vs Gradle by manifest name here. With a
            // single registered adapter there is nothing to disambiguate; the overload stays so
            // call sites are identical across the six product forks.
            return For(language);
        }

        public static IReadOnlyList<IPackageManagerAdapter> All { get { return Registered.Snapshot(); } }

        private sealed class PythonAdapter : IPackageManagerAdapter
        {
            public string Id { get { return "python-pep621"; } }
            public ProjectLanguage Language { get { return ProjectLanguage.Python; } }
            public string ValueLabel { get { return "Version/specifier"; } }
            public List<PackageDependencyInfo> List(string path) { var r = new List<PackageDependencyInfo>(); foreach (var d in Python.PyprojectDependencyService.List(path)) r.Add(new PackageDependencyInfo { Name = d.Name, Value = d.Specifier, Identity = d.Name }); return r; }
            public bool Add(string path, string name, string value) { return Python.PyprojectDependencyService.Add(path, name + (value ?? "")); }
            public bool Remove(string path, string identity) { return Python.PyprojectDependencyService.Remove(path, identity); }
        }
    }
}
