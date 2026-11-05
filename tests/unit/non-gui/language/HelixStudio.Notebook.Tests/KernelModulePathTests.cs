using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Notebooks.Kernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The kernel half of importing from disk, driven through the real
    /// <see cref="NotebookKernelClient"/> against a real Python process.
    ///
    /// <para>
    /// The claim being tested is an ORDERING one, and it cannot be checked by reading the code:
    /// the host answers requests one at a time in the order they arrive, so if a cell's
    /// <c>execute</c> can reach it before the <c>sys_path</c> that sets the notebook's import path,
    /// then every cold start is a race and the first cell of a fresh runtime fails an import that
    /// works on the second try. That is exactly the kind of bug that gets reported as "it works
    /// sometimes".
    /// </para>
    /// </summary>
    [TestClass]
    public class KernelModulePathTests
    {
        private string _temp;

        [TestInitialize]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), "helix_kernelpath_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TestCleanup]
        public void RemoveTemp()
        {
            try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The first `python.exe` on PATH, or null. No interpreter means no test, not a failure.</summary>
        private static string FindPython()
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(directory)) continue;
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), "python.exe"); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// A stand-in for the real host that records the ORDER of the requests it is handed and
        /// nothing else. Substituted for the host script rather than added to it: the real host
        /// would have to be taught to report its own protocol traffic, and a test hook inside the
        /// thing under test is a second code path that can drift from the first.
        /// </summary>
        private string WriteRecorderHost(string logPath)
        {
            string script = Path.Combine(_temp, "recorder_host.py");
            File.WriteAllText(script, string.Join("\n", new[]
            {
                "import sys, json",
                "log = open(r'" + logPath + "', 'w')",
                "sys.stdout.write(json.dumps({'type': 'ready', 'implementation': 'plain',",
                "                             'python': '0.0.0'}) + '\\n')",
                "sys.stdout.flush()",
                "for line in sys.stdin:",
                "    line = line.strip().lstrip('\\ufeff')",
                "    if not line:",
                "        continue",
                "    try:",
                "        request = json.loads(line)",
                "    except ValueError:",
                "        continue",
                "    kind = request.get('type')",
                "    log.write(kind + '\\n')",
                "    log.flush()",
                "    if kind == 'shutdown':",
                "        break",
                "log.close()",
                "",
            }));
            return script;
        }

        [TestMethod]
        public void TheImportPathReachesTheKernelBeforeTheFirstCellDoes()
        {
            string python = FindPython();
            if (python == null) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

            string log = Path.Combine(_temp, "requests.log");
            string host = WriteRecorderHost(log);

            using (var client = new NotebookKernelClient(python, host, _temp))
            {
                // Registered while nothing is running — the ordinary case, since a user sets this up
                // while reading a notebook and only then runs a cell.
                client.SetModuleSearchPaths(new[] { _temp });

                Assert.IsTrue(client.Execute("cell-1", "print(1)"), "the client should have started a kernel");
                WaitForLines(log, 2);
                client.Stop();

                IList<string> requests = ReadLines(log);
                Assert.IsTrue(requests.Count >= 2, "expected at least sys_path and execute, got: "
                                                   + string.Join(",", new List<string>(requests).ToArray()));
                Assert.AreEqual("sys_path", requests[0],
                    "a cell must never reach the host before the import path it depends on");
                Assert.AreEqual("execute", requests[1]);
            }
        }

        /// <summary>
        /// Registering a path never starts a Python process. A user browsing to a module while
        /// reading a notebook has not asked to run anything, and launching an interpreter on their
        /// behalf is both surprising and, on a machine already training a model, expensive.
        /// </summary>
        [TestMethod]
        public void SettingTheImportPathDoesNotStartAKernel()
        {
            string python = FindPython();
            if (python == null) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

            string log = Path.Combine(_temp, "requests.log");
            string host = WriteRecorderHost(log);

            using (var client = new NotebookKernelClient(python, host, _temp))
            {
                client.SetModuleSearchPaths(new[] { _temp });

                Thread.Sleep(150);
                Assert.IsFalse(File.Exists(log), "no host process should have been launched at all");
                Assert.IsFalse(client.IsReady);
            }
        }

        /// <summary>A restart must re-apply the paths, or an import that worked stops working.</summary>
        [TestMethod]
        public void EveryKernelTheClientStartsGetsTheImportPathAgain()
        {
            string python = FindPython();
            if (python == null) { Assert.Inconclusive("no Python interpreter on this machine"); return; }

            string log = Path.Combine(_temp, "requests.log");
            string host = WriteRecorderHost(log);

            using (var client = new NotebookKernelClient(python, host, _temp))
            {
                client.SetModuleSearchPaths(new[] { _temp });
                client.Execute("cell-1", "print(1)");
                WaitForLines(log, 2);

                client.Restart();                       // the second kernel knows nothing of the first
                client.Execute("cell-2", "print(2)");
                WaitForLines(log, 2);
                client.Stop();

                IList<string> requests = ReadLines(log);
                Assert.AreEqual("sys_path", requests[0],
                    "the restarted kernel must be told the import path before it runs anything");
            }
        }

        private static void WaitForLines(string path, int count)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (ReadLines(path).Count >= count) return;
                Thread.Sleep(50);
            }
        }

        private static IList<string> ReadLines(string path)
        {
            var lines = new List<string>();
            if (!File.Exists(path)) return lines;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (line.Length > 0) lines.Add(line);
                }
            }
            catch (IOException) { }
            return lines;
        }
    }
}
