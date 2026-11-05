using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Toolchains
{
    /// <summary>How many kits one probe can yield.</summary>
    public enum ProbeYield
    {
        /// <summary>One tool, one version, parsed out of the banner with <see cref="ToolProbe.VersionPattern"/>.</summary>
        SingleVersion = 0,

        /// <summary>`dotnet --list-sdks` — one line per installed SDK, "8.0.100 [C:\Program Files\dotnet\sdk]".</summary>
        DotNetSdkList = 1,

        /// <summary>`py -0p` — the Windows Python launcher's list of every registered interpreter.</summary>
        PythonLauncherList = 2,
    }

    /// <summary>
    /// One executable to look for, and how to read its version out of what it prints.
    ///
    /// This is data, deliberately: adding a language to the Development Environment view is a row
    /// in <see cref="ToolProbeCatalog"/>, not new code. Nothing here launches anything — that is
    /// <see cref="IToolProbeRunner"/>'s job, which is what makes the scanner testable without a
    /// machine that happens to have Rust on it.
    /// </summary>
    public sealed class ToolProbe
    {
        public ToolProbe(string language, string component, string executable, string arguments,
            string versionPattern, ProbeYield yield = ProbeYield.SingleVersion,
            string[] extraDirectoryEnvironmentVariables = null, string[] extraDirectories = null,
            string standardInput = null, string[] excludedDirectories = null)
        {
            Language = language;
            Component = component;
            Executable = executable;
            Arguments = arguments;
            VersionPattern = versionPattern;
            Yield = yield;
            ExtraDirectoryEnvironmentVariables = extraDirectoryEnvironmentVariables ?? new string[0];
            ExtraDirectories = extraDirectories ?? new string[0];
            StandardInput = standardInput;
            ExcludedDirectories = excludedDirectories ?? new string[0];
        }

        public string Language { get; private set; }
        public string Component { get; private set; }

        /// <summary>Bare executable name, resolved through PATH (plus the directories below).</summary>
        public string Executable { get; private set; }

        public string Arguments { get; private set; }

        /// <summary>A regex over the tool's own output whose first group is the version.</summary>
        public string VersionPattern { get; private set; }

        public ProbeYield Yield { get; private set; }

        /// <summary>
        /// Environment variables naming an install root to search when PATH does not have the tool:
        /// CUDA_PATH and JAVA_HOME are set by their installers but their `bin` is often not on PATH.
        /// </summary>
        public string[] ExtraDirectoryEnvironmentVariables { get; private set; }

        /// <summary>
        /// Well-known install directories to search when PATH does not have the tool. Two forms are
        /// understood, both expanded by the runner rather than here:
        /// <list type="bullet">
        /// <item><c>%VAR%</c> anywhere in the path — an entry naming a variable this machine does
        /// not set is dropped rather than searched literally;</item>
        /// <item><c>*</c> inside a path SEGMENT — <c>%ProgramFiles%\Java\*\bin</c> matches every
        /// installed JDK, which is the normal shape of a versioned install root.</item>
        /// </list>
        /// This exists because PATH is not where toolchains live. rustup installs into
        /// <c>%USERPROFILE%\.cargo\bin</c> and Zig is normally an unzipped folder — on the machine
        /// this was written for BOTH were installed and BOTH were reported "not found on this
        /// machine", which is the one answer a scan like this must never get wrong.
        /// </summary>
        public string[] ExtraDirectories { get; private set; }

        /// <summary>
        /// Text written to the tool's stdin before the pipe is closed, for interpreters that have
        /// no version flag at all and would otherwise sit at a REPL prompt (tclsh). Null for every
        /// tool that answers a command-line switch, which is nearly all of them.
        /// </summary>
        public string StandardInput { get; private set; }

        /// <summary>
        /// Directories whose copy of this executable is an impostor — same name, different program.
        /// Expanded like <see cref="ExtraDirectories"/> and skipped wherever they appear, PATH
        /// included.
        ///
        /// The case this exists for is <c>%SystemRoot%\System32\bash.exe</c>, which is not Bash at
        /// all but the WSL launcher: it sits ahead of Git's real bash on PATH and, on a machine
        /// with no distro installed, never answers — so the honest report was "Bash is present but
        /// did not answer in time" while a perfectly good Bash sat one PATH entry further along.
        /// </summary>
        public string[] ExcludedDirectories { get; private set; }
    }

    /// <summary>
    /// The language kits the IDE looks for. Order is display order — the languages HelixPython
    /// itself works in come first, then the rest alphabetically, then the data tier, then git.
    ///
    /// The alphabetical tail is deliberately wider than what HelixPython can currently OPEN: those
    /// entries are candidate kits, and "is the toolchain already on this machine?" is the first
    /// question each of them raises. Adding one is a row here, never new code.
    /// </summary>
    public static class ToolProbeCatalog
    {
        // Version banners are matched loosely on purpose: "3.12.1", "1.75.0-nightly", "12.4.99".
        private const string Semver = @"(\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.\-]+)?)";

        private static string[] Dirs(params string[] directories) { return directories; }

        public static IList<ToolProbe> All()
        {
            var probes = new List<ToolProbe>
            {
                // ---- Python (the IDE's own language) -------------------------------------
                new ToolProbe("Python", "CPython interpreter", "python", "--version", @"Python\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(
                        @"%LOCALAPPDATA%\Programs\Python\Python3*",
                        @"%ProgramFiles%\Python3*",
                        @"%SystemDrive%\Python3*")),
                new ToolProbe("Python", "Registered interpreters", "py", "-0p", null, ProbeYield.PythonLauncherList),
                new ToolProbe("Python", "pip", "pip", "--version", @"pip\s+" + Semver),
                new ToolProbe("Python", "uv", "uv", "--version", @"uv\s+" + Semver),
                new ToolProbe("Python", "Poetry", "poetry", "--version", Semver),
                new ToolProbe("Python", "Conda", "conda", "--version", @"conda\s+" + Semver),

                // ---- C / C++ --------------------------------------------------------------
                new ToolProbe("C / C++", "GCC", "gcc", "--version", Semver,
                    ProbeYield.SingleVersion, null, MinGwDirs()),
                new ToolProbe("C / C++", "G++", "g++", "--version", Semver,
                    ProbeYield.SingleVersion, null, MinGwDirs()),
                new ToolProbe("C / C++", "Clang", "clang", "--version", @"clang version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\LLVM\bin")),
                new ToolProbe("C / C++", "MSVC (cl.exe)", "cl", "", @"Version\s+" + Semver),
                new ToolProbe("C / C++", "CMake", "cmake", "--version", @"cmake version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\CMake\bin")),
                new ToolProbe("C / C++", "Ninja", "ninja", "--version", Semver),
                new ToolProbe("C / C++", "GNU Make", "make", "--version", @"GNU Make\s+" + Semver,
                    ProbeYield.SingleVersion, null, MinGwDirs()),
                new ToolProbe("C / C++", "GDB", "gdb", "--version", Semver,
                    ProbeYield.SingleVersion, null, MinGwDirs()),

                // ---- CUDA -----------------------------------------------------------------
                new ToolProbe("CUDA", "nvcc", "nvcc", "--version", @"release\s+" + Semver,
                    ProbeYield.SingleVersion, new[] { "CUDA_PATH", "CUDA_HOME" },
                    Dirs(@"%ProgramFiles%\NVIDIA GPU Computing Toolkit\CUDA\*\bin")),
                new ToolProbe("CUDA", "Driver / nvidia-smi", "nvidia-smi", "--version",
                    @"CUDA Version\s*:?\s*" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%SystemRoot%\System32")),

                // ---- .NET / C# ------------------------------------------------------------
                new ToolProbe(".NET / C#", "dotnet CLI", "dotnet", "--version", Semver,
                    ProbeYield.SingleVersion, new[] { "DOTNET_ROOT" },
                    Dirs(@"%ProgramFiles%\dotnet", @"%USERPROFILE%\.dotnet")),
                new ToolProbe(".NET / C#", "SDK", "dotnet", "--list-sdks", null, ProbeYield.DotNetSdkList,
                    new[] { "DOTNET_ROOT" }, Dirs(@"%ProgramFiles%\dotnet", @"%USERPROFILE%\.dotnet")),
                new ToolProbe(".NET / C#", "MSBuild", "msbuild", "-version", Semver),

                // ---- Java -----------------------------------------------------------------
                new ToolProbe("Java", "JRE / JDK runtime", "java", "-version", @"version\s+""?" + Semver,
                    ProbeYield.SingleVersion, new[] { "JAVA_HOME" }, JdkDirs()),
                new ToolProbe("Java", "javac compiler", "javac", "-version", @"javac\s+" + Semver,
                    ProbeYield.SingleVersion, new[] { "JAVA_HOME" }, JdkDirs()),
                new ToolProbe("Java", "Maven", "mvn", "-version", @"Apache Maven\s+" + Semver),
                new ToolProbe("Java", "Gradle", "gradle", "-version", @"Gradle\s+" + Semver),

                // ---- Rust -----------------------------------------------------------------
                // rustup installs the whole toolchain into ~/.cargo/bin and does not put it on the
                // machine PATH, which is why these carry directory hints — see ExtraDirectories.
                new ToolProbe("Rust", "rustc", "rustc", "--version", @"rustc\s+" + Semver,
                    ProbeYield.SingleVersion, new[] { "CARGO_HOME", "RUSTUP_HOME" }, CargoDirs()),
                new ToolProbe("Rust", "Cargo", "cargo", "--version", @"cargo\s+" + Semver,
                    ProbeYield.SingleVersion, new[] { "CARGO_HOME", "RUSTUP_HOME" }, CargoDirs()),
                new ToolProbe("Rust", "rustup", "rustup", "--version", @"rustup\s+" + Semver,
                    ProbeYield.SingleVersion, new[] { "CARGO_HOME", "RUSTUP_HOME" }, CargoDirs()),

                // ---- Go -------------------------------------------------------------------
                new ToolProbe("Go", "Go toolchain", "go", "version", @"go(?:\s+version\s+go)?" + Semver,
                    ProbeYield.SingleVersion, new[] { "GOROOT" },
                    Dirs(@"%ProgramFiles%\Go\bin", @"%SystemDrive%\Go\bin",
                         @"%LOCALAPPDATA%\Programs\Go\bin", @"%USERPROFILE%\go\bin")),

                // ---- JavaScript / TypeScript ---------------------------------------------
                new ToolProbe("JavaScript / TypeScript", "Node.js", "node", "--version", @"v?" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\nodejs")),
                new ToolProbe("JavaScript / TypeScript", "npm", "npm", "--version", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\nodejs", @"%APPDATA%\npm")),
                new ToolProbe("JavaScript / TypeScript", "TypeScript (tsc)", "tsc", "--version",
                    @"Version\s+" + Semver, ProbeYield.SingleVersion, null, Dirs(@"%APPDATA%\npm")),
                new ToolProbe("JavaScript / TypeScript", "Deno", "deno", "--version", @"deno\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%USERPROFILE%\.deno\bin")),

                // ---- Julia ----------------------------------------------------------------
                new ToolProbe("Julia", "Julia", "julia", "--version", @"julia version\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%LOCALAPPDATA%\Programs\Julia*\bin", @"%USERPROFILE%\.julia\juliaup\*\bin")),

                // ---- The rest, alphabetically --------------------------------------------
                new ToolProbe("Ada", "GNAT", "gnat", "--version", @"GNAT\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%SystemDrive%\GNAT\*\bin", @"%ProgramFiles%\GNAT\*\bin")),
                new ToolProbe("Ada", "Alire", "alr", "--version", @"alr\s+" + Semver),

                new ToolProbe("Bash", "Bash", "bash", "--version", @"version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\Git\bin"), null,
                    Dirs(@"%SystemRoot%\System32")),   // the WSL launcher, not Bash — see ExcludedDirectories

                new ToolProbe("C3", "c3c", "c3c", "--version", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%USERPROFILE%\c3", @"%SystemDrive%\c3")),

                new ToolProbe("COBOL", "GnuCOBOL", "cobc", "--version", @"cobc\s+\(GnuCOBOL\)\s+" + Semver),

                new ToolProbe("Clojure", "Clojure CLI", "clojure", "--version", @"version\s+" + Semver),
                new ToolProbe("Clojure", "Leiningen", "lein", "--version", @"Leiningen\s+" + Semver),

                new ToolProbe("D", "DMD", "dmd", "--version", @"v" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%SystemDrive%\D\dmd2\windows\bin64")),
                new ToolProbe("D", "LDC", "ldc2", "--version", @"LDC[^\n]*\(" + Semver),

                new ToolProbe("Dart", "Dart SDK", "dart", "--version", @"Dart SDK version:\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%SystemDrive%\tools\dart-sdk\bin", @"%SystemDrive%\flutter\bin",
                         @"%LOCALAPPDATA%\Pub\Cache\bin")),

                new ToolProbe("Elixir", "Elixir", "elixir", "--version", @"Elixir\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\Elixir\bin")),

                // `erl +V` prints the emulator version and exits; every other form opens the shell.
                new ToolProbe("Erlang", "Erlang/OTP emulator", "erl", "+V", @"version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\erl*\bin")),

                new ToolProbe("F#", "F# Interactive", "fsi", "--version",
                    @"F# Interactive[^\n]*version\s+" + Semver),

                new ToolProbe("Fortran", "gfortran", "gfortran", "--version", Semver,
                    ProbeYield.SingleVersion, null, MinGwDirs()),
                new ToolProbe("Fortran", "Intel ifx", "ifx", "--version", Semver),
                new ToolProbe("Fortran", "Flang", "flang", "--version", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\LLVM\bin")),

                new ToolProbe("Groovy", "Groovy", "groovy", "--version", @"Groovy Version:\s+" + Semver),

                new ToolProbe("Haskell", "GHC", "ghc", "--version", Semver,
                    ProbeYield.SingleVersion, null, HaskellDirs()),
                new ToolProbe("Haskell", "Cabal", "cabal", "--version", @"cabal-install version\s+" + Semver,
                    ProbeYield.SingleVersion, null, HaskellDirs()),
                new ToolProbe("Haskell", "Stack", "stack", "--version", @"Version\s+" + Semver,
                    ProbeYield.SingleVersion, null, HaskellDirs()),

                new ToolProbe("Kotlin", "kotlinc", "kotlinc", "-version", @"kotlinc-jvm\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%LOCALAPPDATA%\Programs\kotlinc\bin", @"%ProgramFiles%\Kotlin\bin",
                         @"%SystemDrive%\kotlinc\bin")),

                new ToolProbe("Lua", "Lua", "lua", "-v", @"Lua\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\Lua\*", @"%SystemDrive%\lua")),

                new ToolProbe("OCaml", "OCaml", "ocaml", "-version", @"version\s+" + Semver),
                new ToolProbe("OCaml", "opam", "opam", "--version", Semver),
                new ToolProbe("OCaml", "Dune", "dune", "--version", Semver),

                new ToolProbe("Octave", "GNU Octave", "octave-cli", "--version", @"version\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\GNU Octave\Octave-*\mingw64\bin")),

                new ToolProbe("Perl", "Perl", "perl", "--version", @"\(v" + Semver + @"\)",
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%SystemDrive%\Strawberry\perl\bin", @"%SystemDrive%\Perl64\bin")),

                new ToolProbe("PHP", "PHP", "php", "--version", @"PHP\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%SystemDrive%\php", @"%SystemDrive%\xampp\php", @"%ProgramFiles%\php")),

                new ToolProbe("PowerShell", "PowerShell (pwsh)", "pwsh", "--version",
                    @"PowerShell\s+" + Semver, ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\PowerShell\*")),
                new ToolProbe("PowerShell", "Windows PowerShell", "powershell",
                    "-NoProfile -NonInteractive -Command $PSVersionTable.PSVersion.ToString()", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%SystemRoot%\System32\WindowsPowerShell\v1.0")),

                new ToolProbe("Prolog", "SWI-Prolog", "swipl", "--version", @"version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\swipl\bin")),

                new ToolProbe("R", "R", "R", "--version", @"R version\s+" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\R\R-*\bin", @"%LOCALAPPDATA%\Programs\R\R-*\bin")),

                new ToolProbe("Ruby", "Ruby", "ruby", "--version", @"ruby\s+" + Semver,
                    ProbeYield.SingleVersion, null, RubyDirs()),
                new ToolProbe("Ruby", "Bundler", "bundle", "--version", @"Bundler version\s+" + Semver,
                    ProbeYield.SingleVersion, null, RubyDirs()),

                new ToolProbe("Scala", "Scala", "scala", "-version", @"version\s+" + Semver),
                new ToolProbe("Scala", "sbt", "sbt", "--script-version", Semver),

                new ToolProbe("Scheme", "GNU Guile", "guile", "--version", @"guile[^\n]*\s" + Semver),
                new ToolProbe("Scheme", "Chez Scheme", "chez", "--version", Semver),

                new ToolProbe("Solidity", "solc", "solc", "--version", @"Version:\s*" + Semver),

                new ToolProbe("Swift", "Swift", "swift", "--version", @"Swift version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%LOCALAPPDATA%\Programs\Swift\*\bin")),

                // tclsh has no version flag at all — it would sit at its REPL waiting for a script.
                // Ask it in its own language instead: StandardInput is written, then the pipe closed.
                new ToolProbe("Tcl", "tclsh", "tclsh", "", @"^\s*" + Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%SystemDrive%\Tcl\bin", @"%ProgramFiles%\Tcl\bin"), "puts $tcl_version\n"),

                new ToolProbe("Zig", "Zig", "zig", "version", Semver,
                    ProbeYield.SingleVersion, new[] { "ZIG_PATH", "ZIG_HOME" }, ZigDirs()),

                // ---- Databases, because the application tier is only half of a program ----
                // Not a language kit, but the same question asked in the same place: is the client
                // this project's connection string needs actually installed here?
                new ToolProbe("Databases (RDBMS)", "SQLite", "sqlite3", "--version", Semver,
                    ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\SQLite", @"%SystemDrive%\sqlite", @"%USERPROFILE%\sqlite")),
                new ToolProbe("Databases (RDBMS)", "PostgreSQL (psql)", "psql", "--version",
                    @"PostgreSQL\)?\s+" + Semver, ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\PostgreSQL\*\bin")),
                new ToolProbe("Databases (RDBMS)", "MySQL client", "mysql", "--version", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\MySQL\MySQL Server *\bin")),
                new ToolProbe("Databases (RDBMS)", "MariaDB client", "mariadb", "--version", Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\MariaDB *\bin")),
                new ToolProbe("Databases (RDBMS)", "SQL Server (sqlcmd)", "sqlcmd", "-?",
                    @"Version\s+" + Semver, ProbeYield.SingleVersion, null,
                    Dirs(@"%ProgramFiles%\Microsoft SQL Server\Client SDK\ODBC\*\Tools\Binn")),
                new ToolProbe("Databases (RDBMS)", "Oracle (SQL*Plus)", "sqlplus", "-V",
                    @"Release\s+" + Semver, ProbeYield.SingleVersion, new[] { "ORACLE_HOME" },
                    Dirs(@"%SystemDrive%\app\*\product\*\*\bin")),
                new ToolProbe("Databases (RDBMS)", "Firebird (isql)", "isql", "-z", Semver),

                // ---- Source control, because every kit above assumes it ------------------
                new ToolProbe("Version control", "Git", "git", "--version", @"git version\s+" + Semver,
                    ProbeYield.SingleVersion, null, Dirs(@"%ProgramFiles%\Git\cmd")),
            };
            return probes;
        }

        // Install roots shared by several probes. Methods rather than static arrays so a caller
        // cannot reach through the array it is handed and edit the catalog itself.
        private static string[] CargoDirs()
        {
            return Dirs(@"%USERPROFILE%\.cargo\bin", @"%CARGO_HOME%\bin", "%HOME%/.cargo/bin");
        }

        private static string[] ZigDirs()
        {
            return Dirs(@"%USERPROFILE%\zig", @"%LOCALAPPDATA%\zig", @"%LOCALAPPDATA%\Programs\zig",
                        @"%ProgramFiles%\zig", @"%SystemDrive%\zig", "%HOME%/zig");
        }

        private static string[] MinGwDirs()
        {
            return Dirs(@"%SystemDrive%\msys64\ucrt64\bin", @"%SystemDrive%\msys64\mingw64\bin",
                        @"%SystemDrive%\MinGW\bin", @"%SystemDrive%\w64devkit\bin",
                        @"%ProgramFiles%\mingw-w64\*\mingw64\bin");
        }

        private static string[] JdkDirs()
        {
            return Dirs(@"%ProgramFiles%\Java\*\bin", @"%ProgramFiles%\Eclipse Adoptium\*\bin",
                        @"%ProgramFiles%\Microsoft\jdk*\bin", @"%ProgramFiles%\Amazon Corretto\*\bin");
        }

        private static string[] HaskellDirs()
        {
            return Dirs(@"%USERPROFILE%\.ghcup\bin", @"%APPDATA%\local\bin",
                        @"%LOCALAPPDATA%\Programs\stack\*\bin");
        }

        private static string[] RubyDirs()
        {
            return Dirs(@"%SystemDrive%\Ruby*\bin", @"%USERPROFILE%\.rbenv\shims");
        }

        /// <summary>Languages in catalog order, deduplicated.</summary>
        public static IList<string> Languages()
        {
            var seen = new List<string>();
            foreach (ToolProbe p in All()) if (!seen.Contains(p.Language)) seen.Add(p.Language);
            return seen;
        }
    }
}
