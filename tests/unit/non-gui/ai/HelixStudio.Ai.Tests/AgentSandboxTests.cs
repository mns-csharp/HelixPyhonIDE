using System;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Ai.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The agent's sandbox boundary — <see cref="AgentWorkspace.TryResolve"/>.
    ///
    /// This is the security-critical predicate in the whole AI feature: tool arguments are model
    /// output, and the model can be steered by any text it reads (a file, a web page pasted into
    /// the prompt, a malicious docstring in a dependency). Everything that keeps a tool from
    /// touching C:\Windows or the user's SSH keys is this one method, so it is tested directly
    /// rather than only through the tools that call it.
    /// </summary>
    [TestClass]
    public class AgentSandboxTests
    {
        private string _root;
        private AgentWorkspace _workspace;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix-agent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _workspace = new AgentWorkspace(_root);
        }

        private bool Resolves(string path, out string full)
        {
            string refusal;
            return _workspace.TryResolve(path, out full, out refusal);
        }

        [TestMethod]
        public void APlainRelativePath_ResolvesInsideTheWorkspace()
        {
            string full;
            Assert.IsTrue(Resolves(@"src\calc\main.py", out full));
            StringAssert.StartsWith(full, _root);
        }

        [TestMethod]
        public void DotDotTraversal_IsRefused()
        {
            string full;
            Assert.IsFalse(Resolves(@"..\..\Windows\System32\drivers\etc\hosts", out full));
            Assert.IsFalse(Resolves(@"src\..\..\escaped.txt", out full));
        }

        [TestMethod]
        public void AnAbsolutePathOutsideTheRoot_IsRefused()
        {
            string full;
            Assert.IsFalse(Resolves(@"C:\Windows\System32\config\SAM", out full));
        }

        [TestMethod]
        public void ASiblingWhoseNameSharesThePrefix_IsRefused()
        {
            // The classic off-by-one in every sandbox check: a raw StartsWith on the root string
            // lets `C:\sol_backup` through when the root is `C:\sol`. The separator must be part
            // of the comparison.
            string sibling = _root + "_backup";
            Directory.CreateDirectory(sibling);
            try
            {
                string full;
                Assert.IsFalse(Resolves(Path.Combine(sibling, "secrets.txt"), out full),
                    "a sibling directory sharing the root's name prefix is NOT inside the root");
            }
            finally
            {
                try { Directory.Delete(sibling, true); } catch { }
            }
        }

        [TestMethod]
        public void TheRootItself_Resolves()
        {
            string full;
            Assert.IsTrue(Resolves(_root, out full));
        }

        [TestMethod]
        public void AnEmptyOrNullPath_IsRefusedWithAReason()
        {
            string full, refusal;
            Assert.IsFalse(_workspace.TryResolve(null, out full, out refusal));
            Assert.IsFalse(string.IsNullOrEmpty(refusal), "a refusal must explain itself");
            Assert.IsFalse(_workspace.TryResolve("   ", out full, out refusal));
        }

        [TestMethod]
        public void ARefusalNamesTheBoundary_SoTheModelCanCorrectItself()
        {
            string full, refusal;
            _workspace.TryResolve(@"..\outside.txt", out full, out refusal);
            StringAssert.Contains(refusal, "outside the solution directory");
            StringAssert.Contains(refusal, _root);
        }

        // ---- the tools honour the boundary, not just the predicate --------------

        [TestMethod]
        public void ReadFileTool_RefusesToEscapeTheWorkspace()
        {
            var tool = new ReadFileTool(_workspace);
            AgentToolResult result = tool
                .RunAsync("{\"path\":\"../../../Windows/win.ini\"}", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Content, "Refused");
        }

        [TestMethod]
        public void WriteFileTool_RefusesToEscapeTheWorkspace_AndWritesNothing()
        {
            string target = Path.Combine(Path.GetTempPath(),
                "helix-agent-escape-" + Guid.NewGuid().ToString("N") + ".txt");
            var tool = new WriteFileTool(_workspace, new EditJournal());

            AgentToolResult result = tool
                .RunAsync("{\"path\":" + Quote(target) + ",\"content\":\"pwned\"}", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.IsError);
            Assert.IsFalse(File.Exists(target), "a refused write must not touch the filesystem");
        }

        [TestMethod]
        public void MalformedArguments_AreReportedNotThrown()
        {
            // Tool input is model output. A parse failure is an ordinary condition the model must
            // be told about, never an exception that unwinds the agent loop.
            var tool = new ReadFileTool(_workspace);
            AgentToolResult result = tool.RunAsync("{not json", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Content, "valid JSON");
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\") + "\"";
        }
    }
}
