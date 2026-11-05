using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Historical interface retained so the App's dialog wiring compiles.
    /// Python CRUD has no "codegen downgrade" concept, so implementations are
    /// effectively no-ops — but leaving the interface in place avoids touching
    /// every consumer during the refactor.
    /// </summary>
    public interface ICodegenDowngradeNotifier
    {
        bool ConfirmDowngrades(ProjectModel project, IList<string> downgradeDescriptions);
    }
}
