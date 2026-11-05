using System;
using System.Collections.Generic;
using System.Linq;

namespace HelixPythonNamespace.Core.Extensibility
{
    /// <summary>
    /// Lightweight, deterministic contribution registry. It keeps SharpDevelop's useful
    /// AddIn-tree properties (stable IDs, priority ordering and first matching contribution)
    /// without XML manifests, reflection loading, or a second service locator.
    /// </summary>
    public sealed class ContributionRegistry<T> where T : class
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private long _nextOrder;

        public void Register(string id, T contribution, int priority = 0)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A contribution ID is required.", nameof(id));
            if (contribution == null) throw new ArgumentNullException(nameof(contribution));
            lock (_gate)
            {
                if (_entries.ContainsKey(id))
                    throw new InvalidOperationException("A contribution named '" + id + "' is already registered.");
                _entries.Add(id, new Entry(id, contribution, priority, _nextOrder++));
            }
        }

        public bool Unregister(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            lock (_gate) return _entries.Remove(id);
        }

        public IReadOnlyList<T> Snapshot()
        {
            lock (_gate)
                return OrderedEntries().Select(e => e.Value).ToList();
        }

        public T FirstOrDefault(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            lock (_gate)
            {
                foreach (Entry entry in OrderedEntries())
                    if (predicate(entry.Value)) return entry.Value;
            }
            return null;
        }

        private IEnumerable<Entry> OrderedEntries()
        {
            return _entries.Values.OrderByDescending(e => e.Priority).ThenBy(e => e.Order);
        }

        private sealed class Entry
        {
            public Entry(string id, T value, int priority, long order)
            {
                Id = id;
                Value = value;
                Priority = priority;
                Order = order;
            }

            public string Id { get; private set; }
            public T Value { get; private set; }
            public int Priority { get; private set; }
            public long Order { get; private set; }
        }
    }
}
