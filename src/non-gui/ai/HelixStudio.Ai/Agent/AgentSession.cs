using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HelixPythonNamespace.Ai.Abstractions;
using HelixPythonNamespace.Ai.Tools;

namespace HelixPythonNamespace.Ai.Agent
{
    /// <summary>What the user decided about one proposed tool call.</summary>
    public enum AgentApproval
    {
        Allow = 0,
        Deny = 1,

        /// <summary>Allow this call and stop asking about this tool for the rest of the run.</summary>
        AllowForSession = 2,
    }

    /// <summary>A tool call awaiting the user's decision.</summary>
    public sealed class AgentApprovalRequest
    {
        public AgentApprovalRequest(IAgentTool tool, string argumentsJson, string description)
        {
            Tool = tool;
            ArgumentsJson = argumentsJson;
            Description = description;
        }

        public IAgentTool Tool { get; }
        public string ArgumentsJson { get; }

        /// <summary>The reviewable one-liner, e.g. "Run: python -m pytest -q".</summary>
        public string Description { get; }
    }

    /// <summary>Everything the panel needs to render, without knowing about providers or tools.</summary>
    public enum AgentEventKind
    {
        AssistantText = 0,
        Thinking = 1,
        ToolStarted = 2,
        ToolFinished = 3,
        ToolDenied = 4,
        Notice = 5,
        Error = 6,
        TurnComplete = 7,
    }

    /// <summary>One thing that happened during a run.</summary>
    public sealed class AgentEvent
    {
        public AgentEvent(AgentEventKind kind, string text)
        {
            Kind = kind;
            Text = text ?? string.Empty;
        }

        public AgentEventKind Kind { get; }
        public string Text { get; }
    }

    /// <summary>
    /// Decides whether a tool call runs. Implemented by the UI so the decision can be a dialog, and
    /// by tests so it can be a fixed answer.
    /// </summary>
    public interface IAgentPermissionPolicy
    {
        /// <summary>
        /// Called before every tool call the policy has not already cleared. Implementations must
        /// be safe to await from a background thread and must marshal to the UI thread themselves.
        /// </summary>
        Task<AgentApproval> RequestAsync(AgentApprovalRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Auto-approves read-only tools and asks about everything else.
    ///
    /// This is the default because the read/write asymmetry is the whole point: <c>read_file</c>,
    /// <c>glob</c> and <c>grep</c> cannot change anything, and prompting for them would train the
    /// user to click Allow reflexively — which is precisely how a dangerous call gets waved
    /// through. Writes and command execution always reach the user.
    /// </summary>
    public sealed class AskForWritesPolicy : IAgentPermissionPolicy
    {
        private readonly Func<AgentApprovalRequest, CancellationToken, Task<AgentApproval>> _ask;
        private readonly HashSet<string> _sessionApproved =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AskForWritesPolicy(Func<AgentApprovalRequest, CancellationToken, Task<AgentApproval>> ask)
        {
            _ask = ask ?? throw new ArgumentNullException("ask");
        }

        public async Task<AgentApproval> RequestAsync(AgentApprovalRequest request, CancellationToken cancellationToken)
        {
            if (request.Tool.Risk == AgentToolRisk.ReadOnly) return AgentApproval.Allow;

            // "Allow for session" is deliberately never honoured for Execute. A blanket yes to
            // read_file is a convenience; a blanket yes to run_command hands over the machine for
            // the rest of the run, and the next command is not the one the user approved.
            if (request.Tool.Risk != AgentToolRisk.Execute
                && _sessionApproved.Contains(request.Tool.Name))
                return AgentApproval.Allow;

            AgentApproval decision = await _ask(request, cancellationToken).ConfigureAwait(false);
            if (decision == AgentApproval.AllowForSession && request.Tool.Risk != AgentToolRisk.Execute)
                _sessionApproved.Add(request.Tool.Name);
            return decision;
        }
    }

    /// <summary>
    /// The agent loop: send a turn, run whatever tools the model asks for, send the results back,
    /// repeat until it stops asking.
    ///
    /// Deliberately provider-agnostic and UI-agnostic — it talks to <see cref="IAiProvider"/> and
    /// <see cref="IAgentPermissionPolicy"/> only, which is what lets the same loop drive Claude or
    /// GPT and be tested with neither.
    ///
    /// Conversation history lives here across turns, so a follow-up question keeps context.
    /// </summary>
    public sealed class AgentSession
    {
        /// <summary>
        /// Ceiling on tool round-trips in one run. Not a safety mechanism — approval is — but a
        /// backstop against a model that loops calling the same tool forever, which would
        /// otherwise burn tokens indefinitely with the user watching.
        /// </summary>
        public const int MaxIterations = 40;

        private readonly IAiProvider _provider;
        private readonly IReadOnlyList<IAgentTool> _tools;
        private readonly IAgentPermissionPolicy _policy;
        private readonly List<AiMessage> _history = new List<AiMessage>();
        private readonly Dictionary<string, IAgentTool> _byName =
            new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        public AgentSession(
            IAiProvider provider,
            IReadOnlyList<IAgentTool> tools,
            IAgentPermissionPolicy policy,
            string systemPrompt)
        {
            _provider = provider ?? throw new ArgumentNullException("provider");
            _tools = tools ?? new List<IAgentTool>();
            _policy = policy ?? throw new ArgumentNullException("policy");
            SystemPrompt = systemPrompt;
            foreach (IAgentTool tool in _tools) _byName[tool.Name] = tool;
        }

        public string SystemPrompt { get; set; }
        public string Model { get; set; }
        public AiEffort Effort { get; set; } = AiEffort.High;
        public int MaxTokens { get; set; } = 16000;
        public bool ShowThinking { get; set; }

        /// <summary>The running conversation. Exposed so the panel can show or clear it.</summary>
        public IReadOnlyList<AiMessage> History { get { return _history; } }

        /// <summary>Cumulative token usage for this session, for the panel's cost readout.</summary>
        public AiUsage TotalUsage { get; } = new AiUsage();

        public void Reset() { _history.Clear(); }

        /// <summary>
        /// Run one user prompt to completion, executing tools as the model asks for them.
        ///
        /// Returns when the model stops requesting tools, the iteration ceiling is hit, the user
        /// denies a call the model cannot proceed without, or an error occurs. Cancellation is
        /// cooperative — an in-flight command is killed by the tool itself.
        /// </summary>
        public async Task RunAsync(
            string userPrompt,
            Action<AgentEvent> onEvent,
            CancellationToken cancellationToken)
        {
            if (onEvent == null) onEvent = delegate { };

            if (!_provider.IsConfigured)
            {
                onEvent(new AgentEvent(AgentEventKind.Error,
                    _provider.DisplayName + " is not configured. " + _provider.CredentialHint));
                return;
            }

            _history.Add(AiMessage.FromUserText(userPrompt));

            var definitions = new List<AiToolDefinition>();
            foreach (IAgentTool tool in _tools) definitions.Add(tool.ToDefinition());

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new AiRequest(Model, SystemPrompt, new List<AiMessage>(_history))
                {
                    Tools = definitions,
                    MaxTokens = MaxTokens,
                    Effort = Effort,
                    ShowThinking = ShowThinking,
                };

                AiResponse response = await _provider
                    .SendAsync(request, ev => Forward(ev, onEvent), cancellationToken)
                    .ConfigureAwait(false);

                Accumulate(response.Usage);

                if (response.StopReason == AiStopReason.Error
                    || response.StopReason == AiStopReason.Refusal)
                {
                    onEvent(new AgentEvent(AgentEventKind.Error, response.ErrorMessage
                        ?? "The provider returned no content."));
                    return;
                }

                // Record the assistant turn BEFORE running tools: the API rejects a tool result
                // whose originating tool_use block is missing from the history.
                if (response.Content.Count > 0)
                    _history.Add(new AiMessage(AiRole.Assistant, response.Content));

                IReadOnlyList<AiToolUseBlock> calls = response.ToolUses();
                if (calls.Count == 0)
                {
                    if (response.StopReason == AiStopReason.MaxTokens
                        && !string.IsNullOrEmpty(response.ErrorMessage))
                        onEvent(new AgentEvent(AgentEventKind.Notice, response.ErrorMessage));
                    onEvent(new AgentEvent(AgentEventKind.TurnComplete, string.Empty));
                    return;
                }

                var results = new List<AiContentBlock>();
                foreach (AiToolUseBlock call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(await ExecuteAsync(call, onEvent, cancellationToken).ConfigureAwait(false));
                }

                // Every result goes back in ONE user turn. Splitting them across turns trains the
                // model to stop making parallel tool calls.
                _history.Add(new AiMessage(AiRole.User, results));
            }

            onEvent(new AgentEvent(AgentEventKind.Notice,
                "Stopped after " + MaxIterations + " tool round-trips. Ask again to continue."));
        }

        private async Task<AiContentBlock> ExecuteAsync(
            AiToolUseBlock call,
            Action<AgentEvent> onEvent,
            CancellationToken cancellationToken)
        {
            IAgentTool tool;
            if (!_byName.TryGetValue(call.Name, out tool))
            {
                // Reported back to the model rather than thrown: it can pick a real tool next turn.
                onEvent(new AgentEvent(AgentEventKind.Error, "Unknown tool: " + call.Name));
                return new AiToolResultBlock(call.Id,
                    "No tool named '" + call.Name + "' exists.", true);
            }

            string description;
            try { description = tool.Describe(call.InputJson); }
            catch (Exception) { description = tool.Name; }

            AgentApproval approval;
            try
            {
                approval = await _policy
                    .RequestAsync(new AgentApprovalRequest(tool, call.InputJson, description), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A broken approval path must fail CLOSED. Treating an error as consent would let
                // a UI bug silently authorize file writes and command execution.
                onEvent(new AgentEvent(AgentEventKind.Error,
                    "Approval failed, denying by default: " + ex.Message));
                return new AiToolResultBlock(call.Id, "The user's approval could not be obtained.", true);
            }

            if (approval == AgentApproval.Deny)
            {
                onEvent(new AgentEvent(AgentEventKind.ToolDenied, description));
                return new AiToolResultBlock(call.Id,
                    "The user denied this action. Do not retry it; consider another approach or ask them what they would prefer.",
                    true);
            }

            onEvent(new AgentEvent(AgentEventKind.ToolStarted, description));
            AgentToolResult result;
            try
            {
                result = await tool.RunAsync(call.InputJson, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = AgentToolResult.Error(ex.GetType().Name + ": " + ex.Message);
            }

            onEvent(new AgentEvent(AgentEventKind.ToolFinished,
                description + (result.IsError ? " — failed" : " — done")));
            return new AiToolResultBlock(call.Id, result.Content, result.IsError);
        }

        private void Accumulate(AiUsage usage)
        {
            if (usage == null) return;
            TotalUsage.InputTokens += usage.InputTokens;
            TotalUsage.OutputTokens += usage.OutputTokens;
            TotalUsage.CacheReadTokens += usage.CacheReadTokens;
            TotalUsage.CacheWriteTokens += usage.CacheWriteTokens;
        }

        private static void Forward(AiStreamEvent ev, Action<AgentEvent> onEvent)
        {
            switch (ev.Kind)
            {
                case AiStreamEventKind.TextDelta:
                    onEvent(new AgentEvent(AgentEventKind.AssistantText, ev.Text));
                    break;
                case AiStreamEventKind.ThinkingDelta:
                    onEvent(new AgentEvent(AgentEventKind.Thinking, ev.Text));
                    break;
                case AiStreamEventKind.Notice:
                    onEvent(new AgentEvent(AgentEventKind.Notice, ev.Text));
                    break;
                // ToolUseReady is not forwarded: the loop reports the tool at approval time, when
                // there is something actionable to show, rather than twice.
            }
        }
    }
}
