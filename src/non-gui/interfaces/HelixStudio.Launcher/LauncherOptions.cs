namespace HelixPythonNamespace.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    /// What the launcher was asked to do. Exactly one per invocation.
    ///
    /// The split that matters is READING versus EXECUTING, because it decides which verbs the four
    /// hard blockers gate — see <see cref="LauncherVerbs.Executes"/>.
    /// </summary>
    public enum LauncherVerb
    {
        /// <summary>Start the package's entry point. The default when no verb is given.</summary>
        Run,
        /// <summary>Virtual-compile every source file and report the diagnostics.</summary>
        Compile,
        /// <summary>Run the native Python linter over every source file.</summary>
        Lint,
        /// <summary>Review the submission and print a verdict.</summary>
        CodeReview,
        /// <summary>Run the package's entry point under cProfile and print the hot functions.</summary>
        Profile,
        /// <summary>Run pytest.</summary>
        RunTest,
    }

    public static class LauncherVerbs
    {
        /// <summary>
        /// True for the verbs that EXECUTE the user's code rather than read it.
        ///
        /// Only these are gated by PY8001–PY8004, and that asymmetry is the point rather than an
        /// oversight: the rules exist so the IDE never executes source it cannot show,
        /// compile-check or debug — and refusing to *compile-check* source because it fails a
        /// compile-check would leave the user with no way to find out what is wrong. Compile, Lint
        /// and Code Review therefore REPORT the violations (Compile reports them as errors, exactly
        /// as Build ▸ Compile does in the IDE) instead of refusing to look.
        /// </summary>
        public static bool Executes(LauncherVerb verb)
        {
            return verb == LauncherVerb.Run || verb == LauncherVerb.Profile || verb == LauncherVerb.RunTest;
        }

        /// <summary>The flag spelling, for messages.</summary>
        public static string Flag(LauncherVerb verb)
        {
            switch (verb)
            {
                case LauncherVerb.Compile: return "--compile";
                case LauncherVerb.Lint: return "--lint";
                case LauncherVerb.CodeReview: return "--code-review";
                case LauncherVerb.Profile: return "--profile";
                case LauncherVerb.RunTest: return "--run-test";
                default: return "--run";
            }
        }
    }

    /// <summary>The parsed command line, or the reason it could not be parsed.</summary>
    public sealed class LauncherOptions
    {
        /// <summary>The root <c>pyproject.toml</c>, resolved to a full path.</summary>
        public string SolutionPath;

        /// <summary>What to do. <see cref="LauncherVerb.Run"/> unless a verb flag was given.</summary>
        public LauncherVerb Verb = LauncherVerb.Run;

        /// <summary>
        /// The workspace member to act on — its <c>[project] name</c>, which by PY8004 is also its
        /// directory name. Null means the whole solution for the reading verbs, and the startup
        /// package for the executing ones, which is what pressing the equivalent button in the IDE
        /// with nothing selected does. EVERY verb accepts it, and every verb works without it.
        /// </summary>
        public string PackageName;

        /// <summary>List the members and what each one is, then exit without doing anything else.</summary>
        public bool ListPackages;

        /// <summary>
        /// Describe the work instead of doing it: for <c>run</c>/<c>--profile</c> the argv, working
        /// directory and <c>PYTHONPATH</c>; for the reading verbs the files that would be read.
        /// </summary>
        public bool DryRun;

        /// <summary>
        /// Relax rule 2 (PY8002) the way Tools ▸ Preferences ▸ General does for solutions of
        /// existing procedural code. Rules 1, 3 and 4 have no such switch in either host.
        /// </summary>
        public bool AllowNonObjectOriented;

        /// <summary>
        /// Arguments for the PROGRAM, everything after a bare <c>--</c>. They REPLACE the package's
        /// <c>[tool.helixstudio].run-args</c> rather than appending to it: a run-args line is the
        /// package's stored default, and a caller who spells out argv on the command line means
        /// that argv. Empty when no <c>--</c> was given, and then run-args applies. For
        /// <c>--run-test</c> they are passed to pytest instead.
        /// </summary>
        public List<string> ProgramArguments = new List<string>();

        /// <summary>True when the caller passed <c>--</c>, even with nothing after it.</summary>
        public bool ProgramArgumentsGiven;

        /// <summary>
        /// <c>--no-capture</c>: skip writing environment.lock.toml beside the manifest.
        ///
        /// For a read-only checkout, and for a loop that runs the same solution hundreds of times
        /// and does not want the probe cost each go round.
        /// </summary>
        public bool NoEnvironmentCapture;

        /// <summary><c>--quiet</c>: only the program's own output.</summary>
        public bool Quiet;

        public bool ShowHelp;

        /// <summary>Non-null when the command line could not be understood.</summary>
        public string Error;

        public static LauncherOptions Parse(IList<string> args)
        {
            var o = new LauncherOptions();
            if (args == null || args.Count == 0) { o.ShowHelp = true; return o; }

            string solution = null;
            bool verbSeen = false;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];

                if (a == "--") { o.ProgramArgumentsGiven = true; for (int j = i + 1; j < args.Count; j++) o.ProgramArguments.Add(args[j]); break; }
                if (a == "-h" || a == "--help" || a == "help") { o.ShowHelp = true; return o; }

                // Both spellings of every path-ish flag: `--flag value` and `--flag=value`.
                string value;
                if (TryValue(args, ref i, a, "--solution", out value)) { if (value == null) { o.Error = "--solution needs a path."; return o; } solution = value; continue; }

                // The package selector. `--execute_package` is the spelling the feature request
                // used and `--package` reads better beside `--compile`; they are one slot, because
                // two would eventually disagree.
                if (TryValue(args, ref i, a, "--package", out value) ||
                    TryValue(args, ref i, a, "--execute-package", out value) ||
                    TryValue(args, ref i, a, "--execute_package", out value))
                {
                    if (value == null) { o.Error = "--package needs a package name."; return o; }
                    o.PackageName = value;
                    continue;
                }

                LauncherVerb verb;
                if (TryVerb(args, ref i, a, out verb, out value))
                {
                    // Two verbs is not a pipeline, it is a typo. Refusing beats silently doing the
                    // second one and reporting an exit code the caller will read as the first's.
                    if (verbSeen && verb != o.Verb)
                    {
                        o.Error = "only one action at a time: " + LauncherVerbs.Flag(o.Verb)
                            + " and " + LauncherVerbs.Flag(verb) + " were both given.";
                        return o;
                    }
                    verbSeen = true;
                    o.Verb = verb;
                    if (value != null) o.PackageName = value;
                    continue;
                }

                if (a == "--list-packages" || a == "--list_packages") { o.ListPackages = true; continue; }
                if (a == "--dry-run" || a == "--dry_run") { o.DryRun = true; continue; }
                if (a == "--allow-non-oop" || a == "--allow_non_oop") { o.AllowNonObjectOriented = true; continue; }
                if (a == "--no-capture" || a == "--no_capture") { o.NoEnvironmentCapture = true; continue; }
                if (a == "--quiet") { o.Quiet = true; continue; }

                if (a.Length > 0 && a[0] == '-') { o.Error = "unknown flag: " + a; return o; }

                // A bare path is taken as the solution, so `helixstudio path\to\pyproject.toml` works.
                if (solution == null) { solution = a; continue; }
                o.Error = "unexpected extra argument: " + a;
                return o;
            }

            if (string.IsNullOrEmpty(solution)) { o.Error = "missing --solution."; return o; }

            string resolved = ResolveSolutionPath(solution);
            if (resolved == null)
            {
                o.Error = Directory.Exists(solution)
                    ? "no pyproject.toml in " + solution
                    : "solution not found: " + solution;
                return o;
            }
            o.SolutionPath = resolved;
            return o;
        }

        // Every verb flag, in both the hyphen and underscore spelling, each optionally carrying the
        // package name so `--compile my_pkg` and `--compile=my_pkg` mean what they look like.
        private static readonly KeyValuePair<string, LauncherVerb>[] VerbFlags =
        {
            new KeyValuePair<string, LauncherVerb>("--run", LauncherVerb.Run),
            new KeyValuePair<string, LauncherVerb>("--compile", LauncherVerb.Compile),
            new KeyValuePair<string, LauncherVerb>("--lint", LauncherVerb.Lint),
            new KeyValuePair<string, LauncherVerb>("--code-review", LauncherVerb.CodeReview),
            new KeyValuePair<string, LauncherVerb>("--code_review", LauncherVerb.CodeReview),
            new KeyValuePair<string, LauncherVerb>("--profile", LauncherVerb.Profile),
            new KeyValuePair<string, LauncherVerb>("--run-test", LauncherVerb.RunTest),
            new KeyValuePair<string, LauncherVerb>("--run_test", LauncherVerb.RunTest),
            new KeyValuePair<string, LauncherVerb>("--run-tests", LauncherVerb.RunTest),
            new KeyValuePair<string, LauncherVerb>("--run_tests", LauncherVerb.RunTest),
        };

        private static bool TryVerb(IList<string> args, ref int i, string a,
                                    out LauncherVerb verb, out string package)
        {
            verb = LauncherVerb.Run;
            package = null;
            foreach (KeyValuePair<string, LauncherVerb> candidate in VerbFlags)
            {
                if (a == candidate.Key)
                {
                    verb = candidate.Value;
                    // A following bare token is the package name — but ONLY when it cannot be the
                    // solution path. `helixstudio --compile D:\repo\pyproject.toml` must still mean
                    // "compile that solution", so anything that looks like a path is left alone for
                    // the solution slot. `--compile=<name>` has no such ambiguity.
                    if (i + 1 < args.Count && LooksLikePackageName(args[i + 1])) package = args[++i];
                    return true;
                }
                if (a.StartsWith(candidate.Key + "=", StringComparison.Ordinal))
                {
                    verb = candidate.Value;
                    string v = a.Substring(candidate.Key.Length + 1);
                    if (v.Length > 0) package = v;
                    return true;
                }
            }
            return false;
        }

        // A package name has no directory separators, no drive letter, and is not something already
        // on disk. Anything else is a path and belongs to --solution.
        private static bool LooksLikePackageName(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (token[0] == '-') return false;
            if (token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0) return false;
            if (token.IndexOf(':') >= 0) return false;
            try { if (File.Exists(token) || Directory.Exists(token)) return false; }
            catch { return false; }
            return true;
        }

        /// <summary>
        /// Accept either the manifest itself or the folder holding it — a user who types the
        /// solution directory means the solution, and making them append the filename would be
        /// pedantry.
        /// </summary>
        private static string ResolveSolutionPath(string given)
        {
            try
            {
                if (File.Exists(given)) return Path.GetFullPath(given);
                if (Directory.Exists(given))
                {
                    string candidate = Path.Combine(Path.GetFullPath(given), "pyproject.toml");
                    return File.Exists(candidate) ? candidate : null;
                }
            }
            catch { }
            return null;
        }

        // `--flag value` and `--flag=value`. Returns false when `a` is not this flag at all;
        // returns true with a null value when the flag is present but its value is missing.
        private static bool TryValue(IList<string> args, ref int i, string a, string flag, out string value)
        {
            value = null;
            if (a == flag)
            {
                if (i + 1 >= args.Count) return true;
                value = args[++i];
                return true;
            }
            if (a.StartsWith(flag + "=", StringComparison.Ordinal))
            {
                string v = a.Substring(flag.Length + 1);
                if (v.Length > 0) value = v;
                return true;
            }
            return false;
        }

        public static void PrintUsage(TextWriter w)
        {
            w.WriteLine("helixstudio - drive a HelixStudio Python solution without the IDE.");
            w.WriteLine();
            w.WriteLine("Usage:");
            w.WriteLine("  helixstudio --solution <pyproject.toml|dir> [action] [--package <name>] [options]");
            w.WriteLine();
            w.WriteLine("Actions (one at a time; the default is to run):");
            w.WriteLine("  --run                      Start the package's entry point. Run in the IDE.");
            w.WriteLine("  --compile                  Virtual-compile and report diagnostics. Build > Compile.");
            w.WriteLine("  --lint                     Run the native Python linter. Analysis > Lint.");
            w.WriteLine("  --code-review              Review and print a verdict. Analysis > Code Review.");
            w.WriteLine("  --profile                  Run the entry point under cProfile. Profiler.");
            w.WriteLine("  --run-test                 Run pytest. Tests > Run All Tests.");
            w.WriteLine("  Each also accepts the package inline: --compile <name> or --compile=<name>.");
            w.WriteLine("  --code_review / --run_test / --run_tests are accepted spellings.");
            w.WriteLine();
            w.WriteLine("Scope:");
            w.WriteLine("  --package <name>           The workspace member to act on. EVERY action accepts it,");
            w.WriteLine("                             and every action works without it:");
            w.WriteLine("                               omitted -> the whole solution for --compile/--lint/");
            w.WriteLine("                                          --code-review/--run-test, and the startup");
            w.WriteLine("                                          package for --run/--profile.");
            w.WriteLine("                             --execute-package and --execute_package are the same slot.");
            w.WriteLine();
            w.WriteLine("Options:");
            w.WriteLine("  --solution <path>          Root pyproject.toml of the solution, or the folder holding it.");
            w.WriteLine("  --list-packages            List the members, marking runnable and startup, then exit.");
            w.WriteLine("  --dry-run                  Describe the work instead of doing it.");
            w.WriteLine("  --allow-non-oop            Relax PY8002, as Tools > Preferences > General does in the IDE.");
            w.WriteLine("  --                         Everything after this is the program's own argv (pytest's,");
            w.WriteLine("                             for --run-test), replacing [tool.helixstudio].run-args.");
            w.WriteLine("  -h, --help                 This message.");
            w.WriteLine();
            w.WriteLine("The four solution-wide rules:");
            w.WriteLine("  PY8001 every source file belongs to a member project");
            w.WriteLine("  PY8002 user-written code is object-oriented");
            w.WriteLine("  PY8003 only a test project contains test files");
            w.WriteLine("  PY8004 the on-disk layout matches the Solution Explorer");
            w.WriteLine("They BLOCK the actions that execute your code (--run, --profile, --run-test), exactly as");
            w.WriteLine("they block Run and Debug in the IDE. The actions that only READ your code (--compile,");
            w.WriteLine("--lint, --code-review) report them instead: refusing to compile-check source because it");
            w.WriteLine("failed a compile-check would leave you no way to find out what is wrong. This launcher");
            w.WriteLine("never restructures your directories; correcting PY8004 needs the IDE.");
            w.WriteLine();
            w.WriteLine("Exit codes: the program's own exit code when one ran; otherwise");
            w.WriteLine("  0 clean   1 findings/errors reported   2 bad command line");
            w.WriteLine("  3 solution did not load   4 action blocked   5 no Python interpreter");
            w.WriteLine();
            w.WriteLine("Examples:");
            w.WriteLine("  helixstudio --solution D:\\repos\\sample_workspace\\pyproject.toml --execute_package sample_app");
            w.WriteLine("  helixstudio --solution D:\\repos\\sample_workspace --compile");
            w.WriteLine("  helixstudio --solution D:\\repos\\sample_workspace --code-review 00-sample-common");
            w.WriteLine("  helixstudio --solution D:\\repos\\sample_workspace --run-test --package sample_project_tests");
        }
    }
}
