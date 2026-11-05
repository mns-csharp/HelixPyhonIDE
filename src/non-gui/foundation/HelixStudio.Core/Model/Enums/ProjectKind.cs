using System;

namespace HelixPythonNamespace.Core.Model.Enums
{
    /// <summary>
    /// Template / role of a Python project. Persisted in <c>pyproject.toml</c>
    /// under <c>[tool.helixstudio].kind</c>. Drives save-time invariants:
    /// <see cref="App"/> requires a <c>def main()</c> somewhere; libraries and
    /// Flask apps must not define one.
    /// </summary>
    public enum ProjectKind
    {
        /// <summary>A reusable package. No <c>def main()</c> allowed.</summary>
        ClassLibrary = 0,

        /// <summary>A runnable app. Must expose at least one <c>def main()</c>.</summary>
        App = 1,

        /// <summary>A Flask web app. WSGI entry is the Flask instance, so no <c>def main()</c> is expected.</summary>
        FlaskWebApp = 2,

        /// <summary>A desktop GUI app (tkinter / PySide6 / PyQt6 / wxPython). Requires <c>def main()</c> as the event-loop bootstrap.</summary>
        GuiApp = 3,

        /// <summary>
        /// A CUDA-C/C++ executable built from a <c>CMakeLists.txt</c> (FR-PM-01). Native, not Python:
        /// the entry point is <c>int main()</c> and there is no <c>pyproject.toml</c> invariant.
        /// </summary>
        CudaExecutable = 4,

        /// <summary>A CUDA-C/C++ library (static/shared) built from a <c>CMakeLists.txt</c> (FR-PM-01). Native.</summary>
        CudaLibrary = 5,

        /// <summary>
        /// A generic runnable project that is neither a Python package nor a CMake target.
        /// Runnable like <see cref="App"/> but exempt from every Python <c>def main()</c>/pyproject
        /// invariant; a non-Python library reuses <see cref="ClassLibrary"/>. Retained as the
        /// <c>kind = "exe"</c> serialization value so a solution written by an older build still
        /// round-trips — HelixPython itself scaffolds only Python and CMake projects.
        /// </summary>
        Executable = 6,

        /// <summary>
        /// A unit-test package — the ONE kind allowed to contain test files. Every other kind is
        /// barred from holding them (PY8003), because a library or app that ships its own tests
        /// cannot be built, packaged, or reasoned about separately from its test harness, and the
        /// IDE cannot offer per-project Run Tests / Debug Tests against a project that is
        /// simultaneously the subject and the test.
        ///
        /// A test project is NOT executable: it is launched by the test runner, never by Run or
        /// Debug, so it can never be the startup project. It references the project(s) it exercises
        /// (PSM-41…46) and holds only test files.
        ///
        /// Persisted as <c>[tool.helixstudio] kind = "test"</c>. Solutions written before this kind
        /// existed stored test packages as <c>kind = "library"</c>; those are recognized at load
        /// time by their <c>&lt;project&gt;_tests</c> name (see <see cref="ProjectKinds.LooksLikeTestProjectName"/>)
        /// so the new rule does not retroactively break them.
        /// </summary>
        TestProject = 7,
    }

    public static class ProjectKinds
    {
        /// <summary>Serialize to the token written in <c>pyproject.toml [tool.helixstudio].kind</c>.</summary>
        public static string ToToken(ProjectKind kind)
        {
            switch (kind)
            {
                case ProjectKind.App: return "app";
                case ProjectKind.FlaskWebApp: return "flask";
                case ProjectKind.GuiApp: return "gui";
                case ProjectKind.CudaExecutable: return "cuda-exe";
                case ProjectKind.CudaLibrary: return "cuda-lib";
                case ProjectKind.Executable: return "exe";
                case ProjectKind.TestProject: return "test";
                case ProjectKind.ClassLibrary: return "library";
                default: return "library";
            }
        }

        /// <summary>Parse the token written by <see cref="ToToken"/>. Unknown / missing values default to <see cref="ProjectKind.ClassLibrary"/>.</summary>
        public static ProjectKind FromToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return ProjectKind.ClassLibrary;
            string t = token.Trim().ToLowerInvariant();
            if (t == "app") return ProjectKind.App;
            if (t == "flask" || t == "flask-web" || t == "flaskwebapp") return ProjectKind.FlaskWebApp;
            if (t == "gui" || t == "gui-app" || t == "guiapp") return ProjectKind.GuiApp;
            if (t == "cuda-exe" || t == "cuda-executable" || t == "cudaexecutable") return ProjectKind.CudaExecutable;
            if (t == "cuda-lib" || t == "cuda-library" || t == "cudalibrary") return ProjectKind.CudaLibrary;
            if (t == "exe" || t == "executable") return ProjectKind.Executable;
            if (t == "test" || t == "tests" || t == "testproject" || t == "unittest")
                return ProjectKind.TestProject;
            return ProjectKind.ClassLibrary;
        }

        /// <summary>
        /// True for the one kind allowed to contain test files. Every other kind is barred from
        /// holding them — see <c>HelixPythonNamespace.Core.Policy.TestFilePlacementRule</c> (PY8003).
        /// </summary>
        public static bool IsTest(ProjectKind kind)
        {
            return kind == ProjectKind.TestProject;
        }

        /// <summary>
        /// Back-compatibility bridge for solutions written before <see cref="ProjectKind.TestProject"/>
        /// existed. Test packages were stored as <c>kind = "library"</c> and identified only by the
        /// VS-mirroring <c>&lt;project&gt;_tests</c> naming convention, so without this every such
        /// package would start failing PY8003 the moment the rule shipped — the IDE would block Run
        /// and Debug on solutions the user never changed.
        ///
        /// Applied at load time (<c>SolutionService.NormalizeTestProjects</c>) and only ever to a
        /// <see cref="ProjectKind.ClassLibrary"/>: an explicit kind in the TOML always wins.
        /// </summary>
        public static bool LooksLikeTestProjectName(string projectName)
        {
            if (string.IsNullOrEmpty(projectName)) return false;
            string n = projectName.Trim();
            return n.EndsWith("_tests", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("_test", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".tests", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("-tests", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith("-test", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for native CUDA-C/C++ project kinds (FR-PM-01). Native projects are
        /// CMakeLists-driven (see <see cref="HelixPythonNamespace.Workspace.Cuda.CMakeListsReader"/>)
        /// rather than pyproject.toml-driven, and skip the Python <c>def main()</c> invariants.
        /// </summary>
        public static bool IsNative(ProjectKind kind)
        {
            return kind == ProjectKind.CudaExecutable || kind == ProjectKind.CudaLibrary;
        }

        /// <summary>
        /// True for a runnable (executable) package — one the IDE can Run/Debug. Per the Python
        /// solution model (docs/python-solution-model.md, PSM-02/03), an executable package has a
        /// runnable entry point; a <see cref="ClassLibrary"/> (and CUDA library) does not.
        /// App/GuiApp/FlaskWebApp are the runnable Python kinds; CudaExecutable is the native one.
        /// <see cref="TestProject"/> is deliberately NOT runnable — it is launched by the test
        /// runner (Run Tests / Debug Tests), never by Run or Debug, so it is never the startup.
        /// </summary>
        public static bool IsExecutable(ProjectKind kind)
        {
            return kind == ProjectKind.App
                || kind == ProjectKind.GuiApp
                || kind == ProjectKind.FlaskWebApp
                || kind == ProjectKind.CudaExecutable
                || kind == ProjectKind.Executable;
        }

        /// <summary>
        /// True for the Python kinds that use a <c>main.py</c> / <c>def main()</c> entry point and are
        /// therefore subject to the main-module rules (docs/python-solution-model.md, PSM-03/05/06).
        /// <see cref="App"/> and <see cref="GuiApp"/> qualify; <see cref="FlaskWebApp"/> is WSGI (its
        /// entry is the Flask instance, no <c>def main()</c>) and native CUDA uses <c>int main()</c>,
        /// so both are exempt from the Python main-module rules.
        /// </summary>
        public static bool RequiresMainModule(ProjectKind kind)
        {
            return kind == ProjectKind.App || kind == ProjectKind.GuiApp;
        }
    }
}
