using System.Windows.Forms;
using HelixPythonNamespace.App.Dialogs;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// UI-backed conflict resolver. Shows <see cref="PyProjectConflictDialog"/>
    /// once per conflict; when the user checks "Apply to all", subsequent
    /// <see cref="Resolve"/> calls return the latched choice without prompting.
    /// </summary>
    public sealed class InteractivePyProjectConflictResolver : IPyProjectConflictResolver
    {
        private readonly IWin32Window _owner;
        private PyProjectConflictResolution? _latched;

        public InteractivePyProjectConflictResolver(IWin32Window owner)
        {
            _owner = owner;
        }

        public PyProjectConflictResolution Resolve(string existingPath)
        {
            // Once the user chose "Apply to all", reuse that choice without prompting again.
            if (_latched.HasValue) return _latched.Value;

            using (PyProjectConflictDialog dlg = new PyProjectConflictDialog(existingPath))
            {
                // The dialog is modal on the UI thread; marshal via Invoke if a worker
                // thread is driving the import, otherwise show it directly.
                DialogResult result;
                if (_owner is Form form && form.InvokeRequired)
                {
                    result = (DialogResult)form.Invoke(new System.Func<DialogResult>(() => dlg.ShowDialog(_owner)));
                }
                else
                {
                    result = dlg.ShowDialog(_owner);
                }

                if (result != DialogResult.OK)
                {
                    // Cancel → treat as Keep (safest default).
                    return PyProjectConflictResolution.Keep;
                }
                // Latch the choice for future conflicts only when the user opted in.
                if (dlg.ApplyToAll) _latched = dlg.Resolution;
                return dlg.Resolution;
            }
        }
    }
}
