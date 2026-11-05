namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;

    /// <summary>
    /// Tracks the current hierarchical navigation state for the canvas.
    /// Immutable -- drill-down and back navigation return new instances.
    /// </summary>
    public sealed class NavigationViewState
    {
        private readonly NavigationLevel _level;
        private readonly string _scopeKey;
        private readonly string _solutionName;
        private readonly string _projectName;
        private readonly string _namespaceName;
        private readonly List<string> _breadcrumbPath;

        /// <summary>
        /// Initializes a new NavigationViewState at the Solution level.
        /// </summary>
        public NavigationViewState(string solutionName)
        {
            if (string.IsNullOrEmpty(solutionName))
            {
                throw new ArgumentException("solutionName cannot be null or empty.");
            }

            _level = NavigationLevel.Solution;
            _scopeKey = solutionName;
            _solutionName = solutionName;
            _projectName = null;
            _namespaceName = null;
            _breadcrumbPath = new List<string>();
            _breadcrumbPath.Add(solutionName);
            SavedViewport = new ViewportState();
        }

        private NavigationViewState(
            NavigationLevel level,
            string scopeKey,
            string solutionName,
            string projectName,
            string namespaceName,
            List<string> breadcrumb)
        {
            _level = level;
            _scopeKey = scopeKey;
            _solutionName = solutionName;
            _projectName = projectName;
            _namespaceName = namespaceName;
            _breadcrumbPath = breadcrumb;
            SavedViewport = new ViewportState();
        }

        /// <summary>
        /// Current drill-down level.
        /// </summary>
        public NavigationLevel Level
        {
            get { return _level; }
        }

        /// <summary>
        /// Identifies what we are looking at: solution name, project name,
        /// namespace name, or fully qualified type name.
        /// </summary>
        public string ScopeKey
        {
            get { return _scopeKey; }
        }

        /// <summary>
        /// Always set when a solution is loaded.
        /// </summary>
        public string SolutionName
        {
            get { return _solutionName; }
        }

        /// <summary>
        /// Set when at Project level or below.
        /// </summary>
        public string ProjectName
        {
            get { return _projectName; }
        }

        /// <summary>
        /// Set when at Namespace level or below.
        /// </summary>
        public string NamespaceName
        {
            get { return _namespaceName; }
        }

        /// <summary>
        /// Breadcrumb segments for display (e.g. "Solution > Project > Namespace").
        /// </summary>
        public List<string> BreadcrumbPath
        {
            get { return _breadcrumbPath; }
        }

        /// <summary>
        /// Per-level saved viewport state (zoom, pan).
        /// </summary>
        public ViewportState SavedViewport { get; set; }

        /// <summary>
        /// Returns a display string for the breadcrumb bar,
        /// joining path segments with " > ".
        /// </summary>
        public string GetBreadcrumbDisplay()
        {
            return string.Join(" > ", _breadcrumbPath.ToArray());
        }

        /// <summary>
        /// Drill down from Solution to Project level (shows all projects).
        /// </summary>
        public NavigationViewState DrillDownToProjects()
        {
            if (_level != NavigationLevel.Solution)
            {
                return this;
            }

            var breadcrumb = new List<string>(_breadcrumbPath);
            breadcrumb.Add("Projects");

            return new NavigationViewState(
                NavigationLevel.Project,
                _solutionName,
                _solutionName,
                null,
                null,
                breadcrumb);
        }

        /// <summary>
        /// Drill down from Project to Namespace level for a specific project.
        /// </summary>
        public NavigationViewState DrillDownToNamespaces(string projectName)
        {
            if (_level != NavigationLevel.Project)
            {
                return this;
            }

            if (string.IsNullOrEmpty(projectName))
            {
                return this;
            }

            var breadcrumb = new List<string>(_breadcrumbPath);
            breadcrumb.Add(projectName);

            return new NavigationViewState(
                NavigationLevel.Namespace,
                projectName,
                _solutionName,
                projectName,
                null,
                breadcrumb);
        }

        /// <summary>
        /// Drill down from Namespace to Type level for a specific namespace.
        /// </summary>
        public NavigationViewState DrillDownToTypes(string namespaceName)
        {
            if (_level != NavigationLevel.Namespace)
            {
                return this;
            }

            if (string.IsNullOrEmpty(namespaceName))
            {
                return this;
            }

            var breadcrumb = new List<string>(_breadcrumbPath);
            breadcrumb.Add(namespaceName);

            return new NavigationViewState(
                NavigationLevel.Type,
                namespaceName,
                _solutionName,
                _projectName,
                namespaceName,
                breadcrumb);
        }

        /// <summary>
        /// Navigate back to the parent level. Returns null if already at Solution level.
        /// </summary>
        public NavigationViewState NavigateBack()
        {
            switch (_level)
            {
                case NavigationLevel.Type:
                {
                    // Back to Namespace level (showing namespaces of the same project)
                    var breadcrumb = new List<string>();
                    for (int i = 0; i < _breadcrumbPath.Count - 1; i++)
                    {
                        breadcrumb.Add(_breadcrumbPath[i]);
                    }
                    return new NavigationViewState(
                        NavigationLevel.Namespace,
                        _projectName,
                        _solutionName,
                        _projectName,
                        null,
                        breadcrumb);
                }
                case NavigationLevel.Namespace:
                {
                    // Back to Project level (showing all projects)
                    var breadcrumb = new List<string>();
                    for (int i = 0; i < _breadcrumbPath.Count - 1; i++)
                    {
                        breadcrumb.Add(_breadcrumbPath[i]);
                    }
                    return new NavigationViewState(
                        NavigationLevel.Project,
                        _solutionName,
                        _solutionName,
                        null,
                        null,
                        breadcrumb);
                }
                case NavigationLevel.Project:
                {
                    // Back to Solution level
                    var breadcrumb = new List<string>();
                    breadcrumb.Add(_solutionName);
                    return new NavigationViewState(
                        NavigationLevel.Solution,
                        _solutionName,
                        _solutionName,
                        null,
                        null,
                        breadcrumb);
                }
                default:
                    return null;
            }
        }
    }
}
