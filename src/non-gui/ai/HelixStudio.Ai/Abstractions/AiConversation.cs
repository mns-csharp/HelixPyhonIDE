using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Ai.Abstractions
{
    /// <summary>Who produced a turn. Tool results are carried as user-role content, as both wire formats require.</summary>
    public enum AiRole
    {
        User = 0,
        Assistant = 1,
    }

    /// <summary>Why the model stopped. Normalized across providers so the agent loop has one switch.</summary>
    public enum AiStopReason
    {
        /// <summary>The model finished its answer. Terminal.</summary>
        EndTurn = 0,

        /// <summary>The model wants tools run. The loop must execute them and send results back.</summary>
        ToolUse = 1,

        /// <summary>Output hit the token ceiling — the answer is truncated, not complete.</summary>
        MaxTokens = 2,

        /// <summary>
        /// A safety classifier declined. Distinct from an error: the HTTP call succeeded and the
        /// content is empty or partial. Callers must branch on this BEFORE reading content.
        /// </summary>
        Refusal = 3,

        /// <summary>The provider call failed. <see cref="AiResponse.ErrorMessage"/> says how.</summary>
        Error = 4,
    }

    /// <summary>
    /// One piece of a turn. A single assistant turn commonly mixes several: some thinking, some
    /// text, and one or more tool calls.
    /// </summary>
    public abstract class AiContentBlock
    {
    }

    /// <summary>Ordinary prose from the model, or the user's prompt.</summary>
    public sealed class AiTextBlock : AiContentBlock
    {
        public AiTextBlock(string text) { Text = text ?? string.Empty; }
        public string Text { get; }
    }

    /// <summary>
    /// Reasoning the model surfaced. Only populated when the provider is asked for a visible
    /// summary; the raw chain of thought is never returned by any current model, so this is a
    /// summary to display, never something to parse or act on.
    /// </summary>
    public sealed class AiThinkingBlock : AiContentBlock
    {
        public AiThinkingBlock(string text) { Text = text ?? string.Empty; }
        public string Text { get; }
    }

    /// <summary>
    /// The model asking for a tool to run. <see cref="Id"/> must be echoed back on the matching
    /// <see cref="AiToolResultBlock"/> — both wire formats reject a turn whose tool calls and
    /// results do not pair up exactly.
    /// </summary>
    public sealed class AiToolUseBlock : AiContentBlock
    {
        public AiToolUseBlock(string id, string name, string inputJson)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            InputJson = string.IsNullOrEmpty(inputJson) ? "{}" : inputJson;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>
        /// The tool arguments as raw JSON. Deliberately not a parsed dictionary: providers differ
        /// in how they escape Unicode and forward slashes, so every consumer must parse rather
        /// than string-match, and keeping it raw makes that unavoidable.
        /// </summary>
        public string InputJson { get; }
    }

    /// <summary>What a tool produced, paired to its call by <see cref="ToolUseId"/>.</summary>
    public sealed class AiToolResultBlock : AiContentBlock
    {
        public AiToolResultBlock(string toolUseId, string content, bool isError = false)
        {
            ToolUseId = toolUseId ?? string.Empty;
            Content = content ?? string.Empty;
            IsError = isError;
        }

        public string ToolUseId { get; }
        public string Content { get; }

        /// <summary>
        /// True when the tool failed. The failure is still sent back rather than dropped — the
        /// model recovers from a described error, but a missing result desynchronizes the turn.
        /// </summary>
        public bool IsError { get; }
    }

    /// <summary>One turn of the conversation.</summary>
    public sealed class AiMessage
    {
        public AiMessage(AiRole role, IReadOnlyList<AiContentBlock> content)
        {
            Role = role;
            Content = content ?? new List<AiContentBlock>();
        }

        public static AiMessage FromUserText(string text)
        {
            return new AiMessage(AiRole.User, new List<AiContentBlock> { new AiTextBlock(text) });
        }

        public AiRole Role { get; }
        public IReadOnlyList<AiContentBlock> Content { get; }
    }

    /// <summary>
    /// A tool the model may call. <see cref="InputSchemaJson"/> is a JSON Schema object passed
    /// through to the provider verbatim.
    /// </summary>
    public sealed class AiToolDefinition
    {
        public AiToolDefinition(string name, string description, string inputSchemaJson)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            InputSchemaJson = string.IsNullOrEmpty(inputSchemaJson)
                ? "{\"type\":\"object\",\"properties\":{}}"
                : inputSchemaJson;
        }

        public string Name { get; }

        /// <summary>
        /// What the tool does AND when to call it. The trigger condition matters as much as the
        /// behaviour: current models reach for tools conservatively, and a description that only
        /// states what a tool does measurably under-triggers compared with one that says when.
        /// </summary>
        public string Description { get; }

        public string InputSchemaJson { get; }
    }

    /// <summary>How hard the model should work. Maps to provider-specific knobs.</summary>
    public enum AiEffort
    {
        Low = 0,
        Medium = 1,
        High = 2,
        XHigh = 3,
        Max = 4,
    }

    /// <summary>One request to a provider.</summary>
    public sealed class AiRequest
    {
        public AiRequest(string model, string system, IReadOnlyList<AiMessage> messages)
        {
            Model = model;
            System = system;
            Messages = messages ?? new List<AiMessage>();
            Tools = new List<AiToolDefinition>();
            MaxTokens = 16000;
            Effort = AiEffort.High;
        }

        public string Model { get; set; }
        public string System { get; set; }
        public IReadOnlyList<AiMessage> Messages { get; set; }
        public IReadOnlyList<AiToolDefinition> Tools { get; set; }

        /// <summary>
        /// Hard ceiling on output. On thinking models this covers thinking AND the answer
        /// together, so a value sized only for the visible answer truncates mid-response.
        /// </summary>
        public int MaxTokens { get; set; }

        public AiEffort Effort { get; set; }

        /// <summary>Ask the provider for a readable summary of the model's reasoning.</summary>
        public bool ShowThinking { get; set; }
    }

    /// <summary>Token accounting for one call, for the panel's cost readout.</summary>
    public sealed class AiUsage
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
    }

    /// <summary>A completed provider call.</summary>
    public sealed class AiResponse
    {
        public AiResponse(AiStopReason stopReason, IReadOnlyList<AiContentBlock> content)
        {
            StopReason = stopReason;
            Content = content ?? new List<AiContentBlock>();
            Usage = new AiUsage();
        }

        public AiStopReason StopReason { get; }
        public IReadOnlyList<AiContentBlock> Content { get; }
        public AiUsage Usage { get; set; }

        /// <summary>Set when <see cref="StopReason"/> is <see cref="AiStopReason.Error"/> or Refusal.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Every tool call in this response, in order.</summary>
        public IReadOnlyList<AiToolUseBlock> ToolUses()
        {
            var uses = new List<AiToolUseBlock>();
            foreach (AiContentBlock block in Content)
            {
                var use = block as AiToolUseBlock;
                if (use != null) uses.Add(use);
            }
            return uses;
        }

        /// <summary>All text blocks joined — what the panel prints as the model's answer.</summary>
        public string Text()
        {
            var sb = new System.Text.StringBuilder();
            foreach (AiContentBlock block in Content)
            {
                var text = block as AiTextBlock;
                if (text != null) sb.Append(text.Text);
            }
            return sb.ToString();
        }
    }

    /// <summary>What a stream emitted. Deltas arrive many times; the rest at most once each.</summary>
    public enum AiStreamEventKind
    {
        TextDelta = 0,
        ThinkingDelta = 1,

        /// <summary>A tool call has been fully received (arguments included).</summary>
        ToolUseReady = 2,

        /// <summary>The provider reported a non-fatal condition worth showing the user.</summary>
        Notice = 3,
    }

    /// <summary>One incremental event from a streaming call.</summary>
    public sealed class AiStreamEvent
    {
        private AiStreamEvent(AiStreamEventKind kind, string text, AiToolUseBlock toolUse)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            ToolUse = toolUse;
        }

        public AiStreamEventKind Kind { get; }
        public string Text { get; }
        public AiToolUseBlock ToolUse { get; }

        public static AiStreamEvent TextDelta(string t) { return new AiStreamEvent(AiStreamEventKind.TextDelta, t, null); }
        public static AiStreamEvent ThinkingDelta(string t) { return new AiStreamEvent(AiStreamEventKind.ThinkingDelta, t, null); }
        public static AiStreamEvent ToolUseReady(AiToolUseBlock u) { return new AiStreamEvent(AiStreamEventKind.ToolUseReady, null, u); }
        public static AiStreamEvent Notice(string t) { return new AiStreamEvent(AiStreamEventKind.Notice, t, null); }
    }
}
