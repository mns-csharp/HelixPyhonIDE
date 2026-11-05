namespace HelixPythonNamespace.App.Commands
{
    using System;
    using System.IO;
    using Newtonsoft.Json;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Interaction;

    /// <summary>
    /// Coarse-grained snapshot-based undoable edit. Captures the source-file text
    /// and TypeModel JSON before and after the user interacts with TypeModelEditorDialog.
    /// Execute applies the "after" snapshot; Undo restores the "before" snapshot.
    ///
    /// Known limitation: only the primary type's source file is snapshotted. Cross-file
    /// effects of the edit (e.g. base-type renames that touch other files) are NOT
    /// captured here, so an undo may leave dangling references in those other files.
    /// For the common case of intra-file edits (rename, add/remove/edit member), this
    /// round-trips cleanly.
    /// </summary>
    public sealed class EditTypeSnapshotCommand : ICommand
    {
        // The live model object mutated in place (via JSON populate) on execute/undo.
        private readonly TypeModel _type;
        // The single source file whose full text is swapped between snapshots.
        private readonly string _filePath;
        // Full file text and serialized model captured before/after the dialog edit;
        // undo restores the "before" pair, redo re-applies the "after" pair.
        private readonly string _beforeSource;
        private readonly string _afterSource;
        private readonly string _beforeModelJson;
        private readonly string _afterModelJson;
        // Called after each apply so the tree/canvas re-reads the mutated model.
        private readonly Action _refreshUi;

        public EditTypeSnapshotCommand(
            TypeModel type,
            string filePath,
            string beforeSource,
            string afterSource,
            string beforeModelJson,
            string afterModelJson,
            Action refreshUi)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath required");

            // Coalesce nullable source text to empty so File.WriteAllText never NREs;
            // the JSON snapshots stay nullable and are guarded in ApplyModelSnapshot.
            _type = type;
            _filePath = filePath;
            _beforeSource = beforeSource ?? string.Empty;
            _afterSource = afterSource ?? string.Empty;
            _beforeModelJson = beforeModelJson;
            _afterModelJson = afterModelJson;
            _refreshUi = refreshUi;
        }

        public string Description
        {
            get { return string.Format("Edit type {0}", _type.Name); }
        }

        public void Execute()
        {
            // Apply the "after" snapshot. Called on Redo (and on initial push via Execute).
            WriteFile(_filePath, _afterSource);
            ApplyModelSnapshot(_type, _afterModelJson);
            InvokeRefresh();
        }

        // Restore the "before" snapshot: rewrite the file and repopulate the model.
        public void Undo()
        {
            WriteFile(_filePath, _beforeSource);
            ApplyModelSnapshot(_type, _beforeModelJson);
            InvokeRefresh();
        }

        private static void WriteFile(string path, string text)
        {
            try { File.WriteAllText(path, text); }
            catch
            {
                // Write failure leaves the in-memory model and the disk out of sync;
                // nothing more we can safely do from undo/redo context.
            }
        }

        // Overwrite the live model's fields from the captured JSON in place, keeping
        // the same object reference so anything already holding _type stays valid.
        private static void ApplyModelSnapshot(TypeModel type, string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                JsonConvert.PopulateObject(json, type);
            }
            catch
            {
                // If the JSON shape changed (e.g. model fields added between captures),
                // fall back to leaving the live model untouched.
            }
        }

        private void InvokeRefresh()
        {
            if (_refreshUi != null) _refreshUi();
        }
    }
}
