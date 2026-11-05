namespace HelixPythonNamespace.Refactoring
{
    using System.Collections.Generic;

    /// <summary>An ordered (key, name) dispatch pair. Port of the C++ <c>std::pair&lt;std::string, std::string&gt;</c>.</summary>
    public sealed class CaseEntry
    {
        public string Key;
        public string Value;

        public CaseEntry() { }

        public CaseEntry(string key, string value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// One offered structural transformation for a selected source span. Port of the C++
    /// <c>StructuralCandidate</c>.
    /// <para>
    /// Unlike the vectorization engine (which rewrites small loop bodies), structural
    /// transforms operate at function/class/module granularity: each candidate replaces one
    /// contiguous top-level statement (a function or a class) with a transformed block. The
    /// matched line range is carried so the host (or the CLI) can splice the replacement
    /// into the surrounding file.
    /// </para>
    /// <para>
    /// Every transform comes in a FORWARD and a REVERSE flavour. A clean round-trip
    /// (forward then reverse, or reverse then forward) reproduces the original text for the
    /// canonical shapes the matchers accept; transforms whose reverse cannot recover the
    /// exact original carry an <c>STR-*-LOSSY</c> caveat and <see cref="Lossy"/> == true.
    /// </para>
    /// </summary>
    public sealed class StructuralCandidate
    {
        public enum Kind
        {
            // --- GoF design patterns ---
            P1_FactoryIntroduce,    // if/elif create-fn -> registry factory class
            P1_FactoryRemove,       // registry factory class -> if/elif create-fn
            P6_FacadeIntroduce,     // free orchestration fn -> facade class method
            P6_FacadeRemove,        // facade class method -> free function
            P2_StrategyIntroduce,   // if/elif dispatch -> strategy table + dispatch fn
            P2_StrategyRemove,      // strategy table + dispatch fn -> if/elif dispatch
            P5_TemplateIntroduce,   // skeleton class -> ABC + abstract step stubs
            P5_TemplateRemove,      // ABC + abstract step stubs -> skeleton class
            P3_ObserverIntroduce,   // subject class -> subject + observer infra
            P3_ObserverRemove,      // subject + observer infra -> subject class
            P4_DecoratorIntroduce,  // component class -> component + delegating decorator
            P4_DecoratorRemove,     // component + delegating decorator -> component class

            // --- Paradigm (procedural <-> OOP) ---
            O1_FunctionsToClass,    // free functions sharing prefix+self -> class
            O1_ClassToFunctions,    // class with methods -> free functions
            O2_DictToClass,         // dict factory fn -> data class
            O2_ClassToDict,         // data class -> dict factory fn

            // --- SOLID ---
            S2_DipIntroduce,        // plain class -> Protocol + class implementing it
            S2_DipInline,           // Protocol + impl -> bare class
            S1_SrpSplit,            // god class -> one class per cohesion cluster
            S1_SrpMerge,            // run of prefix-sharing classes -> one merged class
            S3_IspSegregate,        // fat Protocol -> one role protocol per method
            S3_IspCollapse,         // run of prefix-sharing protocols -> one fat protocol
        }

        public Kind PatternKind;

        /// <summary>User-facing menu label.</summary>
        public string Label;

        /// <summary>The transformed block, re-indented.</summary>
        public string ReplacementText;

        /// <summary>e.g. <c>{"from typing import Protocol"}</c>.</summary>
        public List<string> RequiredImports = new List<string>();

        /// <summary>
        /// Additional top-level blocks a transform may need to insert (e.g. a new
        /// Protocol/ABC introduced alongside an edited class). When non-empty the host
        /// inserts these before the spliced replacement. If a host cannot do multi-edit
        /// apply, <see cref="ReplacementText"/> already contains the full combined result,
        /// so this is purely informational for previews.
        /// </summary>
        public List<string> AuxEdits = new List<string>();

        /// <summary>100 = strict.</summary>
        public int Confidence = 100;

        /// <summary>Populated for lossy / relaxed matches.</summary>
        public List<CandidateIssue> Caveats = new List<CandidateIssue>();

        /// <summary>True when the reverse transform cannot recover the byte-exact original.</summary>
        public bool Lossy;

        /// <summary>
        /// 1-based inclusive line range (in the supplied selection) that
        /// <see cref="ReplacementText"/> replaces. For whole-file selections this is the
        /// span of the matched function/class.
        /// </summary>
        public int StartLine;

        public int EndLine;
    }

    /// <summary>One entry in the catalog of supported structural patterns.</summary>
    public sealed class SupportedStructuralPattern
    {
        public StructuralCandidate.Kind PatternKind;

        /// <summary>e.g. "P1 — Factory (introduce)".</summary>
        public string Name;

        public string ExampleSource;
        public string Replacement;

        public SupportedStructuralPattern() { }

        public SupportedStructuralPattern(StructuralCandidate.Kind kind, string name,
                                          string exampleSource, string replacement)
        {
            PatternKind = kind;
            Name = name;
            ExampleSource = exampleSource;
            Replacement = replacement;
        }
    }
}
