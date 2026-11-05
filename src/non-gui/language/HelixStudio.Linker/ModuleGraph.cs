namespace HelixPythonNamespace.Linker
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Module dependency graph from each summary's <c>DirectImportFqns</c>. Used for
    /// cycle detection via iterative Tarjan SCC. Port of the C++ <c>ModuleGraph</c>.
    /// </summary>
    public sealed class ModuleGraph
    {
        private readonly Dictionary<string, List<string>> _edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, List<string>> Edges
        {
            get { return _edges; }
        }

        public static ModuleGraph Build(Dictionary<string, ModuleSummary> modules)
        {
            ModuleGraph g = new ModuleGraph();
            foreach (KeyValuePair<string, ModuleSummary> kv in modules)
            {
                List<string> targets = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dep in kv.Value.DirectImportFqns)
                {
                    // Only intra-solution edges can close a cycle; external sinks ignored.
                    if (!modules.ContainsKey(dep)) continue;
                    if (!seen.Add(dep)) continue;
                    targets.Add(dep);
                }
                targets.Sort(StringComparer.Ordinal);
                g._edges[kv.Key] = targets;
            }
            // Ensure every node has an entry (covers isolated modules).
            foreach (KeyValuePair<string, ModuleSummary> kv in modules)
            {
                if (!g._edges.ContainsKey(kv.Key))
                {
                    g._edges[kv.Key] = new List<string>();
                }
            }
            return g;
        }

        public List<List<string>> StronglyConnectedComponents()
        {
            List<string> nodes = new List<string>(_edges.Keys);
            nodes.Sort(StringComparer.Ordinal);

            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<string> onStack = new HashSet<string>(StringComparer.Ordinal);
            List<string> stack = new List<string>();
            int nextIndex = 0;
            List<List<string>> sccs = new List<List<string>>();

            foreach (string start in nodes)
            {
                if (index.ContainsKey(start)) continue;
                List<Frame> work = new List<Frame>();
                index[start] = nextIndex;
                lowlink[start] = nextIndex;
                nextIndex++;
                stack.Add(start);
                onStack.Add(start);
                work.Add(new Frame { Node = start, I = 0 });

                while (work.Count > 0)
                {
                    Frame fr = work[work.Count - 1];
                    List<string> neighbors = _edges[fr.Node];
                    if (fr.I < neighbors.Count)
                    {
                        string next = neighbors[fr.I];
                        fr.I++;
                        work[work.Count - 1] = fr;
                        if (!index.ContainsKey(next))
                        {
                            index[next] = nextIndex;
                            lowlink[next] = nextIndex;
                            nextIndex++;
                            stack.Add(next);
                            onStack.Add(next);
                            work.Add(new Frame { Node = next, I = 0 });
                        }
                        else if (onStack.Contains(next))
                        {
                            lowlink[fr.Node] = Math.Min(lowlink[fr.Node], index[next]);
                        }
                    }
                    else
                    {
                        if (lowlink[fr.Node] == index[fr.Node])
                        {
                            List<string> scc = new List<string>();
                            while (true)
                            {
                                string popped = stack[stack.Count - 1];
                                stack.RemoveAt(stack.Count - 1);
                                onStack.Remove(popped);
                                scc.Add(popped);
                                if (popped == fr.Node) break;
                            }
                            scc.Sort(StringComparer.Ordinal);
                            sccs.Add(scc);
                        }
                        string finished = fr.Node;
                        work.RemoveAt(work.Count - 1);
                        if (work.Count > 0)
                        {
                            Frame parent = work[work.Count - 1];
                            lowlink[parent.Node] = Math.Min(lowlink[parent.Node], lowlink[finished]);
                        }
                    }
                }
            }

            sccs.Sort((a, b) => string.CompareOrdinal(a[0], b[0]));
            return sccs;
        }

        private struct Frame
        {
            public string Node;
            public int I;
        }
    }
}
