namespace HelixPythonNamespace.Catalog
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// In-memory index of catalogued external symbols, with FQN and bare-name
    /// lookup plus JSON persistence. Port of the C++ <c>helix::catalog::PythonCatalog</c>.
    /// </summary>
    public sealed class PythonCatalog
    {
        private const int SchemaVersion = 1;

        // CatalogScanner.HandleEvent calls Add() from a Process.OutputDataReceived callback —
        // a ThreadPool thread, NOT the UI thread — while completion code reads this catalog from
        // the UI thread. List<>/Dictionary<> aren't thread-safe for concurrent read+write, so
        // every accessor below takes this lock. (Cheap: catalog reads/writes are infrequent
        // relative to typing, and never block the UI thread for long — Add() is O(1)-ish, and the
        // read paths below all copy out under the lock rather than returning live iterators.)
        private readonly object _gate = new object();
        private readonly List<CatalogSymbol> _entries = new List<CatalogSymbol>();
        private readonly Dictionary<string, int> _byFqn = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _byBareName = new Dictionary<string, int>(StringComparer.Ordinal);

        public void Add(CatalogSymbol s)
        {
            lock (_gate)
            {
                string fqn = s.FullyQualifiedName();
                string bare = s.Name;
                _entries.Add(s);
                int idx = _entries.Count - 1;
                if (!string.IsNullOrEmpty(fqn))
                {
                    _byFqn[fqn] = idx;
                }
                if (!string.IsNullOrEmpty(bare) && !_byBareName.ContainsKey(bare))
                {
                    _byBareName[bare] = idx; // first registration wins (try_emplace)
                }
            }
        }

        /// <summary>Lookup by fully-qualified name (e.g. "os.path.join"). Null if absent.</summary>
        public CatalogSymbol Lookup(string fqn)
        {
            lock (_gate)
            {
                int idx;
                return _byFqn.TryGetValue(fqn, out idx) ? _entries[idx] : null;
            }
        }

        /// <summary>Lookup by bare name (first registered). Null if absent.</summary>
        public CatalogSymbol LookupByName(string bareName)
        {
            lock (_gate)
            {
                int idx;
                return _byBareName.TryGetValue(bareName, out idx) ? _entries[idx] : null;
            }
        }

        /// <summary>Resolve <c>from &lt;moduleFqn&gt; import &lt;member&gt;</c>.</summary>
        public CatalogSymbol ResolveImport(string moduleFqn, string memberName)
        {
            if (string.IsNullOrEmpty(moduleFqn))
            {
                return LookupByName(memberName);
            }
            return Lookup(moduleFqn + "." + memberName);
        }

        /// <summary>
        /// Every top-level symbol scanned directly off the given module (e.g. every
        /// "numpy.X" entry for moduleFqn "numpy") — used for member-dot completion
        /// (<c>np.&lt;caret&gt;</c>) once <paramref name="moduleFqn"/> has been scanned.
        /// Does not include nested-class methods (those are indexed under
        /// "moduleFqn.ClassName", not "moduleFqn"). Materialized eagerly (not `yield return`)
        /// so the whole scan runs inside the lock rather than across however long the caller
        /// takes to consume a deferred iterator.
        /// </summary>
        public List<CatalogSymbol> MembersOfModule(string moduleFqn)
        {
            var result = new List<CatalogSymbol>();
            if (string.IsNullOrEmpty(moduleFqn)) return result;
            lock (_gate)
            {
                foreach (CatalogSymbol s in _entries)
                    if (string.Equals(s.ModuleFqn, moduleFqn, StringComparison.Ordinal))
                        result.Add(s);
            }
            return result;
        }

        /// <summary>True once at least one symbol for this module FQN has been scanned in.</summary>
        public bool HasModule(string moduleFqn)
        {
            if (string.IsNullOrEmpty(moduleFqn)) return false;
            lock (_gate)
            {
                foreach (CatalogSymbol s in _entries)
                    if (string.Equals(s.ModuleFqn, moduleFqn, StringComparison.Ordinal))
                        return true;
            }
            return false;
        }

        public int Size
        {
            get { lock (_gate) { return _byFqn.Count; } }
        }

        /// <summary>All symbols in insertion order (a snapshot copy, not a live view).</summary>
        public IReadOnlyList<CatalogSymbol> Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }

        public void Merge(PythonCatalog other)
        {
            if (other == null)
            {
                return;
            }
            CatalogSymbol[] snapshot;
            lock (other._gate) { snapshot = other._entries.ToArray(); }
            foreach (CatalogSymbol s in snapshot)
            {
                Add(s);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _byFqn.Clear();
                _byBareName.Clear();
            }
        }

        public bool Save(string path)
        {
            try
            {
                CatalogSymbol[] snapshot;
                lock (_gate) { snapshot = _entries.ToArray(); }
                JArray symbols = new JArray();
                foreach (CatalogSymbol s in snapshot)
                {
                    symbols.Add(SymbolToJson(s));
                }
                JObject root = new JObject
                {
                    ["meta"] = new JObject { ["schema"] = SchemaVersion, ["size"] = snapshot.Length },
                    ["symbols"] = symbols,
                };
                File.WriteAllText(path, root.ToString(Formatting.None));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool Load(string path, Action<int, int> onProgress = null)
        {
            JObject j;
            try
            {
                j = JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
            JToken symsToken = j["symbols"];
            if (symsToken == null || symsToken.Type != JTokenType.Array)
            {
                return false;
            }
            Clear();
            JArray syms = (JArray)symsToken;
            int total = syms.Count;
            JToken meta = j["meta"];
            if (meta != null && meta.Type == JTokenType.Object)
            {
                total = meta.Value<int?>("size") ?? total;
            }
            int done = 0;
            foreach (JToken js in syms)
            {
                Add(SymbolFromJson((JObject)js));
                done++;
                if (onProgress != null && (done % 1000) == 0)
                {
                    onProgress(done, total);
                }
            }
            if (onProgress != null)
            {
                onProgress(done, total);
            }
            return true;
        }

        private static JObject SymbolToJson(CatalogSymbol s)
        {
            JArray ps = new JArray();
            foreach (CatalogParam p in s.Params)
            {
                ps.Add(new JObject
                {
                    ["name"] = p.Name,
                    ["type"] = p.TypeAnnotation,
                    ["default"] = p.DefaultValue,
                    ["description"] = p.Description,
                });
            }
            return new JObject
            {
                ["moduleFqn"] = s.ModuleFqn,
                ["name"] = s.Name,
                ["kind"] = s.Kind,
                ["signature"] = s.Signature,
                ["summary"] = s.Summary,
                ["returnSummary"] = s.ReturnSummary,
                ["params"] = ps,
                ["fullDoc"] = s.FullDoc,
            };
        }

        private static CatalogSymbol SymbolFromJson(JObject j)
        {
            CatalogSymbol s = new CatalogSymbol
            {
                ModuleFqn = j.Value<string>("moduleFqn") ?? "",
                Name = j.Value<string>("name") ?? "",
                Kind = j.Value<string>("kind") ?? "",
                Signature = j.Value<string>("signature") ?? "",
                Summary = j.Value<string>("summary") ?? "",
                ReturnSummary = j.Value<string>("returnSummary") ?? "",
                FullDoc = j.Value<string>("fullDoc") ?? "",
            };
            JToken ps = j["params"];
            if (ps != null && ps.Type == JTokenType.Array)
            {
                foreach (JToken jp in (JArray)ps)
                {
                    s.Params.Add(new CatalogParam
                    {
                        Name = jp.Value<string>("name") ?? "",
                        TypeAnnotation = jp.Value<string>("type") ?? "",
                        DefaultValue = jp.Value<string>("default") ?? "",
                        Description = jp.Value<string>("description") ?? "",
                    });
                }
            }
            return s;
        }
    }
}
