namespace HelixPythonNamespace.Launcher.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Launcher;

    /// <summary>
    /// Shared plumbing for the launcher suites: where the fixture solutions are, how to invoke
    /// <see cref="SolutionLauncher.Run"/> and capture what it wrote, and how to take a throwaway
    /// copy of a fixture so a test can plant a fault in it.
    /// </summary>
    internal static class LauncherFixtures
    {
        private static readonly string GeneratedRoot = CreateGeneratedRoot();

        /// <summary><c>tests/fixtures/python</c>, located by walking up from the test binary.</summary>
        internal static string Root
        {
            get
            {
                return GeneratedRoot;
            }
        }

        internal static string CleanSolution { get { return Path.Combine(Root, "structure-parity", "clean"); } }
        internal static string SkewedSolution { get { return Path.Combine(Root, "structure-parity", "skewed"); } }

        private static string CreateGeneratedRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-launcher-fixtures-" + Guid.NewGuid().ToString("N"));
            string clean = Path.Combine(root, "structure-parity", "clean");
            string skewed = Path.Combine(root, "structure-parity", "skewed");
            CreateSolution(clean, false);
            CreateSolution(skewed, true);
            return root;
        }

        private static void CreateSolution(string root, bool skewed)
        {
            string memberRoot = skewed ? Path.Combine(root, "src") : root;
            Directory.CreateDirectory(memberRoot);
            File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                "[project]\nname = \"launcher-fixture\"\nversion = \"0.1.0\"\nrequires-python = \">=3.8\"\n\n"
              + "[tool.uv.workspace]\nmembers = [\"" + (skewed ? "src/" : "")
              + "calc_engine\", \"" + (skewed ? "src/" : "") + "calc_app\", \""
              + (skewed ? "src/" : "") + "calc_tests\"]\n\n"
              + "[tool.helixstudio]\n");

            CreateMember(memberRoot, "calc_engine", "library", "");
            CreateMember(memberRoot, "calc_app", "app", "startup = true\nrun-args = \"--from-manifest\"\n",
                "class App:\n    @staticmethod\n    def run():\n        print('hello')\n\n\ndef main():\n    App.run()\n\n\nif __name__ == \"__main__\":\n    main()\n");
            CreateMember(memberRoot, "calc_tests", "test", "", null, true);
        }

        private static void CreateMember(string root, string name, string kind, string extra,
                                         string main = null, bool tests = false)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                "[project]\nname = \"" + name + "\"\nversion = \"0.1.0\"\nrequires-python = \">=3.8\"\n\n"
              + "[tool.helixstudio]\nkind = \"" + kind + "\"\n" + extra);
            if (name == "calc_app")
            {
                File.WriteAllText(Path.Combine(dir, "__init__.py"), "");
                File.WriteAllText(Path.Combine(dir, "main.py"), main);
            }
            else if (tests)
            {
                File.WriteAllText(Path.Combine(dir, "test_one.py"), "def test_one():\n    assert True\n");
                File.WriteAllText(Path.Combine(dir, "test_two.py"), "def test_two():\n    assert True\n");
            }
            else
            {
                File.WriteAllText(Path.Combine(dir, "__init__.py"), "from .calculator import Calculator\n");
                File.WriteAllText(Path.Combine(dir, "calculator.py"), "class Calculator:\n    def add(self, a, b):\n        return a + b\n");
            }
        }

        /// <summary>
        /// Captures what the launcher decided to start, instead of starting it. The verbs that
        /// drive an engine do not go through this at all — only <c>run</c> does — so a test of
        /// <c>--compile</c> is a test of the real compiler.
        /// </summary>
        internal sealed class RecordingLauncher : IProgramLauncher
        {
            public string Executable;
            public List<string> Arguments;
            public string WorkingDirectory;
            public IReadOnlyDictionary<string, string> Environment;
            public bool Launched;
            public int ExitCode = 0;

            public int Launch(string executable, IList<string> arguments, string workingDirectory,
                              IReadOnlyDictionary<string, string> environment)
            {
                Launched = true;
                Executable = executable;
                Arguments = new List<string>(arguments);
                WorkingDirectory = workingDirectory;
                Environment = environment;
                return ExitCode;
            }
        }

        internal sealed class Result
        {
            public int Code;
            public string Out;
            public string Err;
            public RecordingLauncher Launcher;
        }

        /// <summary>
        /// Drive the launcher WITHOUT writing an environment capture.
        ///
        /// A run legitimately writes environment.lock.toml into the solution directory, and several
        /// suites here run the executing verbs against the TRACKED fixtures rather than copies. Left
        /// on, the suite litters those directories and costs a full toolchain scan per run —
        /// measured, two capture tests then failed in the full suite and passed alone, because a
        /// FixtureCopy had inherited somebody else's lock file.
        ///
        /// Tests that are about the capture use <see cref="RunCapturing"/>.
        /// </summary>
        internal static Result Run(params string[] args)
        {
            // Only where it could write something: adding a flag to a no-argument or --help
            // invocation changes what is being tested, which is how this first went wrong.
            var withoutCapture = new List<string>(args);
            if (withoutCapture.Contains("--solution") && !withoutCapture.Contains("--no-capture"))
                withoutCapture.Add("--no-capture");
            return RunCapturing(withoutCapture.ToArray());
        }

        /// <summary>Drive the launcher exactly as given, capture and all.</summary>
        internal static Result RunCapturing(params string[] args)
        {
            var outw = new StringWriter();
            var errw = new StringWriter();
            var launcher = new RecordingLauncher();
            int code = SolutionLauncher.Run(args, outw, errw, launcher);
            return new Result
            {
                Code = code,
                Out = outw.ToString(),
                Err = errw.ToString(),
                Launcher = launcher,
            };
        }

        /// <summary>
        /// A throwaway copy of a fixture solution, so a test can introduce a fault without editing
        /// the tracked fixture — <c>structure-parity\clean</c> is load-bearing for other suites.
        /// Lives under %TEMP%; deleted on dispose, and a failure to delete is not a test failure.
        /// </summary>
        internal sealed class FixtureCopy : IDisposable
        {
            public readonly string Path;

            public FixtureCopy(string source)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "helixstudio-launcher-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(source, Path);
            }

            private static void CopyDirectory(string from, string to)
            {
                Directory.CreateDirectory(to);
                foreach (string file in Directory.GetFiles(from))
                    File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)), true);
                foreach (string dir in Directory.GetDirectories(from))
                {
                    string name = System.IO.Path.GetFileName(dir);
                    // __pycache__ is not part of the solution and copying it wastes time — and a
                    // stale .pyc for a file this test is about to rewrite is worse than wasteful.
                    if (string.Equals(name, "__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                    CopyDirectory(dir, System.IO.Path.Combine(to, name));
                }
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { }
            }
        }
    }
}
