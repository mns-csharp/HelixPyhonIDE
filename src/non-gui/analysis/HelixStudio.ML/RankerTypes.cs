namespace HelixPythonNamespace.ML
{
    /// <summary>
    /// Lightweight context descriptor passed in from the completion engine: anything
    /// the ranker can use to score a (context, candidate) pair without parsing more
    /// text. Hash buckets are deterministic so the same lexical situation maps to the
    /// same embedding row across runs.
    /// </summary>
    public struct RankerContext
    {
        public int ContextBucket; // hash(prefix tokens) mod ContextBuckets
        public int TriggerKind;   // 0=manual, 1=`.`, 2=`from `, 3=`import `
    }

    public struct RankerCandidate
    {
        public int KindBucket; // hash(CompletionKind enum) mod KindBuckets
        public int NameBucket; // hash(item.label) mod NameBuckets
    }
}
