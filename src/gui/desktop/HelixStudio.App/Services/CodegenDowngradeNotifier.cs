namespace HelixPythonNamespace.App.Services
{
    using System.Collections.Generic;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Dialog-backed notifier that runs on the UI thread. Constructor receives an
    /// owner form so the modal dialog centers correctly. Requires a Form.Invoke
    /// marshal when the notifier is invoked off the UI thread.
    /// </summary>
    public sealed class CodegenDowngradeNotifier : ICodegenDowngradeNotifier
    {
        private readonly Form _owner;

        public CodegenDowngradeNotifier(Form owner)
        {
            _owner = owner;
        }

        public bool ConfirmDowngrades(ProjectModel project, IList<string> downgradeDescriptions)
        {
            if (_owner == null || _owner.IsDisposed)
            {
                // No UI available — default to accepting.
                return true;
            }

            // The dialog must be shown on the UI thread. If called from a worker thread,
            // marshal via Invoke and capture the user's choice back out of the closure.
            if (_owner.InvokeRequired)
            {
                bool accepted = false;
                _owner.Invoke(new MethodInvoker(delegate
                {
                    accepted = ShowDialog(project, downgradeDescriptions);
                }));
                return accepted;
            }
            // Already on the UI thread — show directly.
            return ShowDialog(project, downgradeDescriptions);
        }

        // Show the modal dialog and report whether the user accepted the downgrades.
        private bool ShowDialog(ProjectModel project, IList<string> downgradeDescriptions)
        {
            using (var dlg = new CodegenDowngradeDialog(project, downgradeDescriptions))
            {
                dlg.ShowDialog(_owner);
                return dlg.Accepted;
            }
        }
    }
}
