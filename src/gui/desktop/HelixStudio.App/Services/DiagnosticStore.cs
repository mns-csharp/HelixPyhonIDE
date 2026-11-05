namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using HelixPythonNamespace.App.Dialogs;

    /// <summary>Source-bucketed diagnostic collection modelled after SharpDevelop's task service.</summary>
    public sealed class DiagnosticStore
    {
        private readonly Dictionary<string, List<BuildResultItem>> buckets =
            new Dictionary<string, List<BuildResultItem>>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler Changed;

        public void Replace(string source, IEnumerable<BuildResultItem> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A diagnostic source is required.", "source");
            var rows = (diagnostics ?? Enumerable.Empty<BuildResultItem>()).Where(x => x != null).ToList();
            foreach (var row in rows) row.Source = source;
            buckets[source] = rows;
            OnChanged();
        }

        public void Clear(string source = null)
        {
            if (source == null) buckets.Clear();
            else buckets.Remove(source);
            OnChanged();
        }

        public IReadOnlyList<BuildResultItem> Snapshot(string source = null, string severity = null, string text = null)
        {
            IEnumerable<BuildResultItem> rows = source == null
                ? buckets.Values.SelectMany(x => x)
                : buckets.TryGetValue(source, out var bucket) ? bucket : Enumerable.Empty<BuildResultItem>();
            if (!string.IsNullOrWhiteSpace(severity))
                rows = rows.Where(x => string.Equals(x.Severity, severity, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(text))
                rows = rows.Where(x => Contains(x.Code, text) || Contains(x.Message, text) || Contains(x.File, text));
            return rows.ToList();
        }

        public IReadOnlyList<string> Sources => buckets.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        private static bool Contains(string value, string text) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
