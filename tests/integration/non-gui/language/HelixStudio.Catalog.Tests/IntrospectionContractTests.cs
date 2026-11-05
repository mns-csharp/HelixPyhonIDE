namespace HelixPythonNamespace.Catalog.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// What <c>tools/catalog/introspect.py</c> actually emits, run against THIS machine's
    /// interpreter — which is the only way to test it, because the whole point of the scanner is
    /// real-tool parity with the installation in front of the user.
    ///
    /// <para>
    /// The gap these were written from: typing <c>np.__version__</c> offered <c>arange</c>,
    /// <c>array</c>, <c>asanyarray</c>… and no <c>__version__</c>. The scanner skipped every name
    /// starting with an underscore, and emitted only functions, builtins and classes — so a
    /// module's dunders, its constants (<c>pi</c>, <c>e</c>, <c>inf</c>, <c>newaxis</c>) and its
    /// submodules (<c>linalg</c>, <c>random</c>, <c>fft</c>) were all invisible. Measured on
    /// numpy 2.2.6: 166 symbols before, 551 after.
    /// </para>
    ///
    /// <para>
    /// These skip rather than fail when the module is not installed. A machine without numpy is a
    /// machine that cannot answer the question; reporting that as a defect in the scanner would be
    /// a false alarm, and the stdlib cases below still cover the contract everywhere.
    /// </para>
    /// </summary>
    [TestClass]
    public class IntrospectionContractTests
    {
        private sealed class Symbol
        {
            public string ModuleFqn;
            public string Name;
            public string Kind;
            public string Summary;
        }

        private static string ScriptPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, @"tools\catalog\introspect.py")))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "tools/catalog/introspect.py not found above " + AppDomain.CurrentDomain.BaseDirectory);
            return Path.Combine(dir.FullName, @"tools\catalog\introspect.py");
        }

        private static string Interpreter()
        {
            foreach (string candidate in Probe())
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            return null;
        }

        private static IEnumerable<string> Probe()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string candidate;
                try { candidate = Path.Combine(entry.Trim(), "python.exe"); }
                catch (ArgumentException) { continue; }
                yield return candidate;
            }
        }

        /// <summary>Run the real script and collect its symbols. Null when there is no interpreter.</summary>
        private static List<Symbol> Scan(string moduleName)
        {
            string interpreter = Interpreter();
            if (interpreter == null) return null;

            var info = new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = "\"" + ScriptPath() + "\" " + moduleName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            var symbols = new List<Symbol>();
            using (Process process = Process.Start(info))
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    JObject message;
                    try { message = JObject.Parse(line); }
                    catch (Exception) { continue; }
                    if (message.Value<string>("event") == "error") return null;   // module not installed
                    if (message.Value<string>("event") != "symbols") continue;
                    var array = message["symbols"] as JArray;
                    if (array == null) continue;
                    foreach (JToken token in array)
                        symbols.Add(new Symbol
                        {
                            ModuleFqn = token.Value<string>("moduleFqn") ?? "",
                            Name = token.Value<string>("name") ?? "",
                            Kind = token.Value<string>("kind") ?? "",
                            Summary = token.Value<string>("summary") ?? "",
                        });
                }
                process.WaitForExit(120000);
            }
            return symbols;
        }

        private static Symbol Find(List<Symbol> symbols, string moduleFqn, string name)
        {
            foreach (Symbol symbol in symbols)
                if (symbol.ModuleFqn == moduleFqn && symbol.Name == name) return symbol;
            return null;
        }

        private static string Names(List<Symbol> symbols)
        {
            var names = new List<string>();
            for (int i = 0; i < symbols.Count && i < 40; i++) names.Add(symbols[i].Name);
            return string.Join(", ", names.ToArray()) + (symbols.Count > 40 ? ", …" : "");
        }

        // ---- the reported bug ---------------------------------------------------------------

        [TestMethod]
        public void ANumpyModuleDunder_IsScanned_AndCarriesItsValue()
        {
            List<Symbol> symbols = Scan("numpy");
            if (symbols == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            Symbol version = Find(symbols, "numpy", "__version__");
            Assert.IsNotNull(version, "np.__version__ must complete; got " + Names(symbols));
            Assert.AreEqual("data", version.Kind);
            // The value IS the documentation for a version string: "2.2.6" beside the name is the
            // whole reason anyone types it.
            StringAssert.StartsWith(version.Summary, "'", "the summary should be the repr: " + version.Summary);
        }

        [TestMethod]
        public void NumpysConstantsAndSubmodules_AreScanned()
        {
            List<Symbol> symbols = Scan("numpy");
            if (symbols == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            foreach (string name in new[] { "pi", "e", "inf", "nan", "newaxis" })
                Assert.IsNotNull(Find(symbols, "numpy", name), "np." + name + " is a constant, not a function");

            foreach (string name in new[] { "linalg", "random", "fft" })
            {
                Symbol submodule = Find(symbols, "numpy", name);
                Assert.IsNotNull(submodule, "np." + name + " is a submodule and was invisible");
                Assert.AreEqual("module", submodule.Kind, name);
            }

            // The functions that always worked must still be there — the fix adds, never replaces.
            Assert.IsNotNull(Find(symbols, "numpy", "arange"));
            Assert.AreEqual("class", Find(symbols, "numpy", "ndarray").Kind);
        }

        /// <summary>
        /// <c>__dunder__</c> is public, <c>_x</c> and <c>__x</c> are not — the same rule Core's
        /// <c>SemanticModel.Visibility</c> applies to the user's own code. Dropping the private
        /// ones is not tidiness: they are somebody else's internals and completing them invites a
        /// call that breaks on the next release.
        /// </summary>
        [TestMethod]
        public void PrivateAndProtectedNames_AreStillSkipped()
        {
            List<Symbol> symbols = Scan("numpy");
            if (symbols == null) { Assert.Inconclusive("numpy is not installed on this machine"); return; }

            foreach (Symbol symbol in symbols)
            {
                if (symbol.ModuleFqn != "numpy") continue;
                bool dunder = symbol.Name.Length >= 4 && symbol.Name.StartsWith("__") && symbol.Name.EndsWith("__");
                if (dunder) continue;
                Assert.IsFalse(symbol.Name.StartsWith("_"),
                    "a protected/private name leaked into the catalog: " + symbol.Name);
            }
        }

        // ---- the same contract without numpy, so it is checked on every machine ---------------

        [TestMethod]
        public void AStdlibModuleAlsoYieldsItsDunderAndItsConstants()
        {
            List<Symbol> symbols = Scan("math");
            if (symbols == null) { Assert.Inconclusive("no interpreter found on PATH"); return; }

            Assert.IsNotNull(Find(symbols, "math", "pi"), "math.pi is a constant; got " + Names(symbols));
            Assert.IsNotNull(Find(symbols, "math", "tau"));
            Assert.IsNotNull(Find(symbols, "math", "__doc__"), "a module dunder is public API");
            Assert.IsNotNull(Find(symbols, "math", "sqrt"), "functions must keep working");
            Assert.AreEqual("data", Find(symbols, "math", "pi").Kind);
            Assert.AreEqual("3.141592653589793", Find(symbols, "math", "pi").Summary);
        }

    }
}
