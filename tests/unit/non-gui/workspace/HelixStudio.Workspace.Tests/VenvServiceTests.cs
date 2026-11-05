using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-PROJ-03: venv create/select/bind. The bind/discover/interpreter-path logic is verified deterministically
    /// here; the actual <c>python -m venv</c> creation is exercised end-to-end by the CLI against a real interpreter
    /// (see loop 181 evidence) — these tests avoid requiring python so they stay hermetic.
    /// </summary>
    [TestClass]
    public class VenvServiceTests
    {
        [TestMethod]
        public void InterpreterPathIn_PicksPlatformLayout()
        {
            string dir = NewTempDir();
            // Simulate a Windows venv layout.
            string scripts = Path.Combine(dir, "Scripts");
            Directory.CreateDirectory(scripts);
            string py = Path.Combine(scripts, "python.exe");
            File.WriteAllText(py, "");
            Assert.AreEqual(py, VenvService.InterpreterPathIn(dir));
        }

        [TestMethod]
        public void IsVenv_TrueOnlyWithPyvenvCfg()
        {
            string dir = NewTempDir();
            Assert.IsFalse(VenvService.IsVenv(dir));
            File.WriteAllText(Path.Combine(dir, "pyvenv.cfg"), "home = C:/py\nversion = 3.11.0\n");
            Assert.IsTrue(VenvService.IsVenv(dir));
        }

        [TestMethod]
        public void Bind_And_BoundInterpreter_RoundTrip()
        {
            string proj = NewTempDir();
            Assert.IsNull(VenvService.BoundInterpreter(proj), "nothing bound yet");
            string interp = @"C:\envs\demo\Scripts\python.exe";
            Assert.IsTrue(VenvService.Bind(proj, interp));
            Assert.AreEqual(interp, VenvService.BoundInterpreter(proj));
            Assert.IsTrue(File.Exists(Path.Combine(proj, ".helixstudio", "interpreter")));
        }

        [TestMethod]
        public void Discover_FindsVenvByPyvenvCfg()
        {
            string proj = NewTempDir();
            string venv = Path.Combine(proj, ".venv");
            Directory.CreateDirectory(Path.Combine(venv, "Scripts"));
            File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "version = 3.11.0\n");
            File.WriteAllText(Path.Combine(venv, "Scripts", "python.exe"), "");
            var found = VenvService.Discover(proj);
            Assert.AreEqual(1, found.Count);
            Assert.AreEqual(venv, found[0].Directory);
            Assert.IsTrue(found[0].Exists);
        }

        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "helix_venvtest_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
