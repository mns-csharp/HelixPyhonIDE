namespace HelixPythonNamespace.App.Commands
{
    using System;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Interaction;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Undoable type rename. `ITypeService.RenameType` is symmetric — calling it with
    /// (newName → oldName) reverses both the in-memory model and the on-disk source
    /// rewrites (including base-type references in other files), so Undo is just
    /// "rename back."
    ///
    /// The command is pushed via CommandDispatcher.PushWithoutExecute from OnTreeRename
    /// after the rename has already happened interactively; the first Execute call is
    /// a no-op, and Undo / Redo flip the name symmetrically.
    /// </summary>
    public sealed class RenameTypeCommand : ICommand
    {
        // The service that performs the symmetric rename across model and source files.
        private readonly ITypeService _typeService;
        private readonly SolutionModel _solution;
        private readonly TypeModel _type;
        // The two names to flip between; Undo renames to _oldName, Redo to _newName.
        private readonly string _oldName;
        private readonly string _newName;
        private readonly Action _refreshUi;

        public RenameTypeCommand(
            ITypeService typeService,
            SolutionModel solution,
            TypeModel type,
            string oldName,
            string newName,
            Action refreshUi)
        {
            if (typeService == null) throw new ArgumentNullException("typeService");
            if (solution == null) throw new ArgumentNullException("solution");
            if (type == null) throw new ArgumentNullException("type");
            if (string.IsNullOrEmpty(oldName)) throw new ArgumentException("oldName required");
            if (string.IsNullOrEmpty(newName)) throw new ArgumentException("newName required");

            _typeService = typeService;
            _solution = solution;
            _type = type;
            _oldName = oldName;
            _newName = newName;
            _refreshUi = refreshUi;
        }

        public string Description
        {
            get { return string.Format("Rename {0} → {1}", _oldName, _newName); }
        }

        public void Execute()
        {
            // Idempotency guard: the interactive rename already applied _newName, so the
            // first Execute (via PushWithoutExecute) short-circuits; only Redo does work.
            if (string.Equals(_type.Name, _newName, StringComparison.Ordinal))
            {
                return;
            }
            _typeService.RenameType(_solution, _type, _newName);
            InvokeRefresh();
        }

        public void Undo()
        {
            // Skip if already at the old name (nothing to reverse).
            if (string.Equals(_type.Name, _oldName, StringComparison.Ordinal))
            {
                return;
            }
            _typeService.RenameType(_solution, _type, _oldName);
            InvokeRefresh();
        }

        private void InvokeRefresh()
        {
            if (_refreshUi != null) _refreshUi();
        }
    }
}
