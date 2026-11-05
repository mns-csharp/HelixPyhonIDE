using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// "Which file does Run launch for this project?" — one dispatch point, per language.
    ///
    /// This used to be a two-way choice in MainForm: native (CMake) projects resolved through the
    /// CMake target graph, and **everything else was assumed to be Python**. A native project
    /// therefore failed to run with "An executable package needs a main.py with a def main()",
    /// which is advice for a language the project isn't written in. Each language now answers for
    /// itself, and a language with no rule returns false rather than borrowing Python's.
    ///
    /// HelixPython ships two languages (see PRODUCT.md): Python, and the C++/CUDA it converts to.
    /// The Rust/C#/Java/Julia arms that once lived here went with their products.
    /// </summary>
    public static class EntryPointResolver
    {
        /// <summary>
        /// The project's runnable entry-point file, or false when it has none (a library, or an
        /// executable whose entry point is missing). Never throws.
        /// </summary>
        public static bool TryFindEntryFile(ProjectModel project, out string entryPath)
        {
            entryPath = null;
            if (project == null) return false;
            try
            {
                switch (project.Language)
                {
                    case ProjectLanguage.Python: return ProjectModel.TryFindEntryFile(project, out entryPath);
                    default: return false;   // Cpp/CUDA resolve through their CMake target graph
                }
            }
            catch { entryPath = null; return false; }
        }

        /// <summary>
        /// What to tell the user when this project cannot be run, in the terms of ITS language.
        /// Null when the language has no specific advice.
        /// </summary>
        public static string MissingEntryPointAdvice(ProjectLanguage language)
        {
            switch (language)
            {
                case ProjectLanguage.Python:
                    return "An executable package needs a main.py with a def main() (or a __main__.py). "
                         + "Right-click a package -> 'Set as Startup Package'.";
                default:
                    return null;
            }
        }
    }
}
