namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;

    public sealed class WorkbenchDocument
    {
        public string Key { get; internal set; }
        public string OwnerId { get; internal set; }
        public TabPage View { get; internal set; }
    }

    /// <summary>Owns open workbench documents independently from their tab presentation.</summary>
    public sealed class WorkbenchDocumentRegistry : Dictionary<string, TabPage>
    {
        private readonly Dictionary<string, WorkbenchDocument> documents =
            new Dictionary<string, WorkbenchDocument>(StringComparer.OrdinalIgnoreCase);

        public WorkbenchDocumentRegistry() : base(StringComparer.OrdinalIgnoreCase) { }
        public event EventHandler Changed;

        public new TabPage this[string key]
        {
            get { return base[key]; }
            set { Register(key, value, "editor"); }
        }

        public void Register(string key, TabPage view, string ownerId)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A document key is required.", "key");
            if (view == null) throw new ArgumentNullException("view");
            base[key] = view;
            documents[key] = new WorkbenchDocument { Key = key, OwnerId = string.IsNullOrWhiteSpace(ownerId) ? "editor" : ownerId, View = view };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public new bool Remove(string key)
        {
            bool removed = base.Remove(key);
            documents.Remove(key);
            if (removed) Changed?.Invoke(this, EventArgs.Empty);
            return removed;
        }

        public new void Clear()
        {
            if (Count == 0) return;
            base.Clear(); documents.Clear(); Changed?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyCollection<WorkbenchDocument> Documents => new List<WorkbenchDocument>(documents.Values);
        public IReadOnlyList<WorkbenchDocument> OwnedBy(string ownerId)
        {
            var result = new List<WorkbenchDocument>();
            foreach (WorkbenchDocument document in documents.Values)
                if (string.Equals(document.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase)) result.Add(document);
            return result;
        }
    }
}
