namespace HelixPythonNamespace.Workspace.Execution
{
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of the Run/Debug startup pre-flight (PSM-08) — "what does a launch start, and if
    /// nothing, why not".
    ///
    /// This type used to be <c>MainForm.RunPreflight</c>, nested inside the WinForms shell. It moved
    /// here so the headless launcher (<c>helixstudio --solution … --execute-package …</c>) and the
    /// IDE reach the same verdict from the same code: the four solution-wide rules
    /// (PY8001…PY8004) are HARD blockers, and a second implementation of them beside the first is
    /// exactly the drift this project has already paid for once with rule 2.
    /// </summary>
    public sealed class SolutionRunPlan
    {
        public bool CanRun;
        public string StartupName;

        /// <summary>
        /// The dotted module to launch with <c>python -m</c>, or null when the entry point is a
        /// plain script. See <see cref="SolutionRunPlanner.ResolveRunModule"/> — it is null exactly
        /// when the entry module is not inside a package.
        /// </summary>
        public string RunModule;

        public string EntryFile;

        /// <summary>Human-readable, non-null when <see cref="CanRun"/> is false.</summary>
        public string BlockReason;

        /// <summary>
        /// The startup package's <c>[tool.helixstudio].run-args</c>, verbatim. Appended after the
        /// entry file by Run, Run in Terminal, Debug, and the headless launcher. Null/empty when unset.
        /// </summary>
        public string RunArguments;

        /// <summary>
        /// The directory the program runs in — the startup PROJECT's folder, Visual Studio's
        /// default. Relative paths in <see cref="RunArguments"/> resolve against this.
        /// </summary>
        public string WorkingDirectory;

        /// <summary>Solution-policy violations that blocked the launch (PY8001/PY8002/PY8003).</summary>
        public List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> PolicyViolations;
    }

    /// <summary>
    /// The two things the planner cannot work out for itself, because they belong to the host
    /// rather than to the solution.
    /// </summary>
    public sealed class SolutionRunOptions
    {
        /// <summary>
        /// Rule 2 (PY8002). Tools ▸ Preferences ▸ General can relax it for solutions of existing
        /// procedural code; a headless run has no preferences, so it enforces the rule. Rules 1, 3
        /// and 4 are not configurable in either host.
        /// </summary>
        public bool EnforceObjectOrientation = true;

        /// <summary>
        /// Non-null when structure parity (PY8004) is unresolved for the open solution — the whole
        /// sentence to report, remedy included. The planner does not compute this: in the IDE it is
        /// the outcome of a load-time scan the user may have been prompted about, and in a headless
        /// run it is the outcome of the same scan with no prompt available. Both hosts hand the
        /// answer in; the planner only refuses to plan a launch while it is set.
        /// </summary>
        public string StructureBlockReason;

        /// <summary>How the policy-violation summary should point the user at the full list.</summary>
        public PolicyMessageStyle MessageStyle = new PolicyMessageStyle();
    }

    /// <summary>
    /// Host-specific wording for <see cref="SolutionRunPlanner.DescribePolicyViolations"/>. The
    /// DECISION is shared; only the two sentences that name a piece of UI differ.
    /// </summary>
    public sealed class PolicyMessageStyle
    {
        /// <summary>
        /// The Problems-panel quick-fix caption to name for a PY8001 violation, or null to omit the
        /// clause entirely — a console has no context menu to right-click.
        /// </summary>
        public string QuickFixCaption;

        /// <summary>Closing sentence pointing at the full list, or null to omit it.</summary>
        public string FullListHint;

        /// <summary>
        /// The list marker. A WinForms label renders "•" fine; a console renders whatever its code
        /// page makes of it, and on the default Windows code page that is a blank — which turned a
        /// bulleted list into a stray leading space. Hosts that cannot promise UTF-8 pass "*".
        /// </summary>
        public string Bullet = "• ";
    }
}
