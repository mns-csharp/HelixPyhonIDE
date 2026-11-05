using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Templates
{
    /// <summary>
    /// Which templates "Add New Project" offers inside an EXISTING solution, by the
    /// solution's language. A new project added to a solution must be able to
    /// participate in it — a Python workspace can host Python members plus a linked
    /// CUDA library; a native CUDA/C++ workspace can host either native language's
    /// app or library plus a Python app.
    /// Order is the display order on the wizard's Select Template page.
    /// </summary>
    public static class TemplateEligibility
    {
        /// <summary>
        /// Eligible template ids for a solution of <paramref name="solutionLanguage"/>
        /// ("Python", "CUDA", "C++" — case-insensitive), or null for an unknown
        /// language (= no restriction).
        /// </summary>
        public static IReadOnlyList<string> EligibleTemplateIds(string solutionLanguage)
        {
            switch ((solutionLanguage ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "python":
                    return new[] { "py.console", "py.notebook", "py.classlib", "py.test", "cuda.classlib", "py.flask", "py.gui.tk" };
                case "cuda":
                case "cuda c":
                case "cuda c++":
                    return new[] { "cuda.console", "cuda.classlib", "cuda.dll", "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test", "py.console" };
                case "c++":
                case "cpp":
                    return new[] { "cpp.console", "cpp.classlib", "cpp.dll", "cpp.test", "cuda.console", "cuda.classlib", "cuda.dll", "py.console" };
                default:
                    return null;
            }
        }
    }
}
