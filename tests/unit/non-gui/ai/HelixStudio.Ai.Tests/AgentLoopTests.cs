using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HelixPythonNamespace.Ai.Abstractions;
using HelixPythonNamespace.Ai.Agent;
using HelixPythonNamespace.Ai.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The agent loop and its approval gate, driven by a scripted provider.
    ///
    /// A fake <see cref="IAiProvider"/> is the point of the abstraction: the loop's behaviour —
    /// does it ask before writing? does it fail closed? does it stop looping? — is exactly the part
    /// that must not depend on a network call, an API key, or which vendor is selected.
    /// </summary>
    [TestClass]
    public class AgentLoopTests
    {
        /// <summary>Replays a fixed script of responses, one per turn, recording what it was sent.</summary>
        private sealed class ScriptedProvider : IAiProvider
        {
            private readonly Queue<AiResponse> _script;
            public ScriptedProvider(params AiResponse[] responses)
            {
                _script = new Queue<AiResponse>(responses);
            }

            public List<AiRequest> Received { get; } = new List<AiRequest>();

            public string Id { get { return "scripted"; } }
            public string DisplayName { get { return "Scripted"; } }
            public IReadOnlyList<string> Models { get { return new List<string> { "test-model" }; } }
            public bool IsConfigured { get; set; } = true;
            public string CredentialHint { get { return "n/a"; } }

            public Task<AiResponse> SendAsync(AiRequest request, Action<AiStreamEvent> onEvent, CancellationToken ct)
            {
                Received.Add(request);
                if (_script.Count == 0)
                    return Task.FromResult(new AiResponse(AiStopReason.EndTurn, new List<AiContentBlock>()));
                return Task.FromResult(_script.Dequeue());
            }
        }

        private static AiResponse ToolCall(string id, string name, string argsJson)
        {
            return new AiResponse(AiStopReason.ToolUse,
                new List<AiContentBlock> { new AiToolUseBlock(id, name, argsJson) });
        }

        private static AiResponse Say(string text)
        {
            return new AiResponse(AiStopReason.EndTurn,
                new List<AiContentBlock> { new AiTextBlock(text) });
        }

        private sealed class FixedPolicy : IAgentPermissionPolicy
        {
            private readonly AgentApproval _answer;
            public FixedPolicy(AgentApproval answer) { _answer = answer; }
            public List<AgentApprovalRequest> Asked { get; } = new List<AgentApprovalRequest>();

            public Task<AgentApproval> RequestAsync(AgentApprovalRequest request, CancellationToken ct)
            {
                Asked.Add(request);
                return Task.FromResult(_answer);
            }
        }

        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        private AgentSession Build(IAiProvider provider, IAgentPermissionPolicy policy, out EditJournal journal)
        {
            return AgentFactory.Create(provider, _root, "test-solution", policy, out journal);
        }

        private static List<AgentEvent> Run(AgentSession session, string prompt)
        {
            var events = new List<AgentEvent>();
            session.RunAsync(prompt, e => { lock (events) events.Add(e); }, CancellationToken.None)
                .GetAwaiter().GetResult();
            return events;
        }

        // ---- the approval gate --------------------------------------------------

        [TestMethod]
        public void AWrite_IsNotExecutedUntilApproved()
        {
            var provider = new ScriptedProvider(
                ToolCall("t1", "write_file", "{\"path\":\"new.txt\",\"content\":\"hello\"}"),
                Say("done"));
            var policy = new FixedPolicy(AgentApproval.Deny);
            EditJournal journal;
            AgentSession session = Build(provider, policy, out journal);

            Run(session, "create a file");

            Assert.AreEqual(1, policy.Asked.Count, "the write must be put to the user");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "new.txt")),
                "a denied write must not touch the filesystem");
        }

        [TestMethod]
        public void ADeniedTool_TellsTheModelNotToRetry()
        {
            var provider = new ScriptedProvider(
                ToolCall("t1", "write_file", "{\"path\":\"a.txt\",\"content\":\"x\"}"),
                Say("understood"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Deny), out journal);

            Run(session, "write a file");

            // The denial must reach the model as a tool_result, or the turn desynchronizes.
            AiRequest second = provider.Received[1];
            AiMessage last = second.Messages[second.Messages.Count - 1];
            var result = (AiToolResultBlock)last.Content[0];
            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Content, "denied");
            StringAssert.Contains(result.Content, "Do not retry");
        }

        [TestMethod]
        public void ReadOnlyTools_AreNotPutToTheUser()
        {
            // Prompting for every read would train the user to click Allow without reading, which
            // is how a dangerous call gets waved through.
            File.WriteAllText(Path.Combine(_root, "a.txt"), "content");
            var provider = new ScriptedProvider(
                ToolCall("t1", "read_file", "{\"path\":\"a.txt\"}"),
                Say("read it"));
            var asked = new List<AgentApprovalRequest>();
            var policy = new AskForWritesPolicy((r, ct) =>
            {
                asked.Add(r);
                return Task.FromResult(AgentApproval.Allow);
            });
            EditJournal journal;
            AgentSession session = Build(provider, policy, out journal);

            Run(session, "read a.txt");

            Assert.AreEqual(0, asked.Count, "read_file is read-only and must not prompt");
        }

        [TestMethod]
        public void AllowForSession_NeverAppliesToCommandExecution()
        {
            // A blanket yes to run_command hands over the machine for the rest of the run, and the
            // next command is not the one the user approved.
            var provider = new ScriptedProvider(
                ToolCall("t1", "run_command", "{\"command\":\"echo one\"}"),
                ToolCall("t2", "run_command", "{\"command\":\"echo two\"}"),
                Say("done"));
            int asks = 0;
            var policy = new AskForWritesPolicy((r, ct) =>
            {
                asks++;
                return Task.FromResult(AgentApproval.AllowForSession);
            });
            EditJournal journal;
            AgentSession session = Build(provider, policy, out journal);

            Run(session, "run two commands");

            Assert.AreEqual(2, asks,
                "every run_command is confirmed individually even after AllowForSession");
        }

        [TestMethod]
        public void AFailingApprovalPath_DeniesRatherThanAllows()
        {
            // Fail closed: a UI bug must never become silent authorization to write files.
            var provider = new ScriptedProvider(
                ToolCall("t1", "write_file", "{\"path\":\"boom.txt\",\"content\":\"x\"}"),
                Say("ok"));
            var policy = new ThrowingPolicy();
            EditJournal journal;
            AgentSession session = Build(provider, policy, out journal);

            Run(session, "write");

            Assert.IsFalse(File.Exists(Path.Combine(_root, "boom.txt")),
                "an approval failure must not execute the tool");
        }

        private sealed class ThrowingPolicy : IAgentPermissionPolicy
        {
            public Task<AgentApproval> RequestAsync(AgentApprovalRequest r, CancellationToken ct)
            {
                throw new InvalidOperationException("approval UI is broken");
            }
        }

        // ---- the loop itself ----------------------------------------------------

        [TestMethod]
        public void ToolResults_GoBackInASingleUserTurn()
        {
            // Splitting parallel results across turns trains the model to stop calling tools in
            // parallel, so the loop must batch them.
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
            var twoCalls = new AiResponse(AiStopReason.ToolUse, new List<AiContentBlock>
            {
                new AiToolUseBlock("t1", "read_file", "{\"path\":\"a.txt\"}"),
                new AiToolUseBlock("t2", "read_file", "{\"path\":\"b.txt\"}"),
            });
            var provider = new ScriptedProvider(twoCalls, Say("done"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            Run(session, "read both");

            AiRequest second = provider.Received[1];
            AiMessage last = second.Messages[second.Messages.Count - 1];
            Assert.AreEqual(AiRole.User, last.Role);
            Assert.AreEqual(2, last.Content.Count, "both results ride in one user turn");
        }

        [TestMethod]
        public void TheAssistantTurnIsRecordedBeforeItsToolResults()
        {
            // The API rejects a tool_result whose originating tool_use block is absent from history.
            File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
            var provider = new ScriptedProvider(
                ToolCall("t1", "read_file", "{\"path\":\"a.txt\"}"),
                Say("done"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            Run(session, "read it");

            AiRequest second = provider.Received[1];
            AiMessage assistantTurn = second.Messages[second.Messages.Count - 2];
            Assert.AreEqual(AiRole.Assistant, assistantTurn.Role);
            Assert.IsInstanceOfType(assistantTurn.Content[0], typeof(AiToolUseBlock));
        }

        [TestMethod]
        public void AnUnknownTool_IsReportedToTheModelNotThrown()
        {
            var provider = new ScriptedProvider(
                ToolCall("t1", "delete_everything", "{}"),
                Say("sorry"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            Run(session, "go");

            AiRequest second = provider.Received[1];
            AiMessage last = second.Messages[second.Messages.Count - 1];
            var result = (AiToolResultBlock)last.Content[0];
            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Content, "delete_everything");
        }

        [TestMethod]
        public void AnUnconfiguredProvider_ExplainsItselfInsteadOfCalling()
        {
            var provider = new ScriptedProvider(Say("never reached")) { IsConfigured = false };
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            List<AgentEvent> events = Run(session, "hello");

            Assert.AreEqual(0, provider.Received.Count, "no call without credentials");
            Assert.IsTrue(events.Exists(e => e.Kind == AgentEventKind.Error));
        }

        [TestMethod]
        public void AProviderError_StopsTheLoopAndSurfaces()
        {
            var failed = new AiResponse(AiStopReason.Error, new List<AiContentBlock>());
            failed.ErrorMessage = "rate limited";
            var provider = new ScriptedProvider(failed, Say("unreachable"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            List<AgentEvent> events = Run(session, "go");

            Assert.AreEqual(1, provider.Received.Count, "the loop stops on error");
            Assert.IsTrue(events.Exists(e => e.Kind == AgentEventKind.Error && e.Text.Contains("rate limited")));
        }

        [TestMethod]
        public void ARefusal_IsSurfacedRatherThanPrintedAsAnEmptyAnswer()
        {
            var refused = new AiResponse(AiStopReason.Refusal, new List<AiContentBlock>());
            refused.ErrorMessage = "declined by safety classifier";
            var provider = new ScriptedProvider(refused);
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            List<AgentEvent> events = Run(session, "go");

            Assert.IsTrue(events.Exists(e => e.Kind == AgentEventKind.Error && e.Text.Contains("declined")));
        }

        [TestMethod]
        public void TheLoopStopsAtTheIterationCeiling()
        {
            // A model that loops forever must not burn tokens indefinitely.
            var script = new List<AiResponse>();
            for (int i = 0; i < AgentSession.MaxIterations + 5; i++)
                script.Add(ToolCall("t" + i, "glob", "{\"pattern\":\"*\"}"));
            var provider = new ScriptedProvider(script.ToArray());
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            List<AgentEvent> events = Run(session, "loop");

            Assert.AreEqual(AgentSession.MaxIterations, provider.Received.Count);
            Assert.IsTrue(events.Exists(e => e.Kind == AgentEventKind.Notice
                && e.Text.Contains("Stopped after")));
        }

        // ---- undo ----------------------------------------------------------------

        [TestMethod]
        public void UndoAll_RestoresOriginalsAndDeletesCreatedFiles()
        {
            string existing = Path.Combine(_root, "existing.txt");
            File.WriteAllText(existing, "ORIGINAL");

            var provider = new ScriptedProvider(
                ToolCall("t1", "write_file", "{\"path\":\"existing.txt\",\"content\":\"CHANGED\"}"),
                ToolCall("t2", "write_file", "{\"path\":\"created.txt\",\"content\":\"NEW\"}"),
                Say("done"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            Run(session, "make changes");

            Assert.AreEqual("CHANGED", File.ReadAllText(existing));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "created.txt")));

            journal.UndoAll();

            Assert.AreEqual("ORIGINAL", File.ReadAllText(existing), "the original content comes back");
            Assert.IsFalse(File.Exists(Path.Combine(_root, "created.txt")),
                "a file the agent created is removed, not left with empty content");
        }

        [TestMethod]
        public void UndoAll_RestoresTheOriginal_EvenWhenAFileWasWrittenTwice()
        {
            // Newest-first ordering is what makes this land on the ORIGINAL rather than the
            // intermediate revision.
            string path = Path.Combine(_root, "twice.txt");
            File.WriteAllText(path, "V0");

            var provider = new ScriptedProvider(
                ToolCall("t1", "write_file", "{\"path\":\"twice.txt\",\"content\":\"V1\"}"),
                ToolCall("t2", "write_file", "{\"path\":\"twice.txt\",\"content\":\"V2\"}"),
                Say("done"));
            EditJournal journal;
            AgentSession session = Build(provider, new FixedPolicy(AgentApproval.Allow), out journal);

            Run(session, "write twice");
            Assert.AreEqual("V2", File.ReadAllText(path));

            journal.UndoAll();
            Assert.AreEqual("V0", File.ReadAllText(path));
        }

        // ---- edit_file precision -------------------------------------------------

        [TestMethod]
        public void EditFile_RefusesAnAmbiguousMatchRatherThanGuessing()
        {
            string path = Path.Combine(_root, "dup.py");
            File.WriteAllText(path, "x = 1\nx = 1\n");
            var tool = new EditFileTool(new AgentWorkspace(_root), new EditJournal());

            AgentToolResult result = tool.RunAsync(
                "{\"path\":\"dup.py\",\"old_text\":\"x = 1\",\"new_text\":\"x = 2\"}",
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.IsError);
            StringAssert.Contains(result.Content, "more than once");
            Assert.AreEqual("x = 1\nx = 1\n", File.ReadAllText(path), "the file is untouched");
        }

        [TestMethod]
        public void EditFile_PreservesTheFilesOwnLineEndings()
        {
            // A CRLF-ified Python file is a whole-file diff for anyone sharing the repo.
            string path = Path.Combine(_root, "crlf.py");
            File.WriteAllText(path, "a = 1\r\nb = 2\r\n");
            var tool = new WriteFileTool(new AgentWorkspace(_root), new EditJournal());

            tool.RunAsync("{\"path\":\"crlf.py\",\"content\":\"a = 9\\nb = 2\\n\"}",
                CancellationToken.None).GetAwaiter().GetResult();

            string after = File.ReadAllText(path);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(after, "(?<!\r)\n"),
                "a CRLF file stays CRLF");
        }
    }
}
