using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using HelixPythonNamespace.Ai.Abstractions;

namespace HelixPythonNamespace.Ai.Providers
{
    /// <summary>
    /// Claude, via the official Anthropic SDK (package <c>Anthropic</c>).
    ///
    /// The SDK is used rather than raw HTTP against <c>/v1/messages</c> because it ships a
    /// netstandard2.0 target that net472 consumes natively — verified, no NU1701 fallback. That
    /// choice is what forces this assembly to C# 9 (the SDK's request types use `init` setters and
    /// its streaming surface is <c>IAsyncEnumerable</c>); see the csproj for why the raise is
    /// contained to this project.
    ///
    /// Defaults worth knowing:
    /// <list type="bullet">
    /// <item><b>claude-opus-5</b> is the model. Thinking is ON by default on it — omitting the
    /// parameter runs adaptive — so <see cref="AiRequest.MaxTokens"/> is a ceiling on thinking AND
    /// answer together, not just the answer.</item>
    /// <item><b>Streaming always.</b> Agentic turns are long, and a non-streaming request at these
    /// token counts risks an HTTP timeout.</item>
    /// <item><b>Refusals are not errors.</b> A safety classifier decline returns HTTP 200 with
    /// <c>stop_reason: "refusal"</c> and empty or partial content, so this checks the stop reason
    /// before reading content — indexing content[0] unconditionally is the classic break.</item>
    /// </list>
    /// </summary>
    public sealed class AnthropicProvider : IAiProvider
    {
        /// <summary>
        /// Default model. Claude Opus 5 is the current Opus and the right default for agentic
        /// coding work; do not downgrade for cost without the user asking.
        /// </summary>
        public const string DefaultModel = "claude-opus-5";

        private readonly Func<string> _apiKeyProvider;

        /// <param name="apiKeyProvider">
        /// Resolves the key at call time rather than construction time, so a key entered in
        /// Preferences takes effect without rebuilding the provider. May return null/empty, in
        /// which case the SDK falls back to its own credential chain (ANTHROPIC_API_KEY, then
        /// ANTHROPIC_AUTH_TOKEN, then an `ant auth login` profile) — an unset env var does NOT
        /// mean there are no credentials.
        /// </param>
        public AnthropicProvider(Func<string> apiKeyProvider = null)
        {
            _apiKeyProvider = apiKeyProvider;
        }

        public string Id { get { return "anthropic"; } }

        public string DisplayName { get { return "Claude (Anthropic)"; } }

        public IReadOnlyList<string> Models
        {
            get
            {
                return new List<string>
                {
                    "claude-opus-5",
                    "claude-sonnet-5",
                    "claude-opus-4-8",
                    "claude-haiku-4-5",
                };
            }
        }

        public bool IsConfigured
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ResolveKey())) return true;
                // The SDK also accepts an `ant auth login` profile with no env var set, so a
                // missing key is not proof of missing credentials. Report configured when either
                // documented env var is present and let the call surface a real auth failure.
                return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));
            }
        }

        public string CredentialHint
        {
            get
            {
                return "Set the ANTHROPIC_API_KEY environment variable, paste a key into "
                     + "Tools ▸ Preferences ▸ AI, or run `ant auth login` to store a profile "
                     + "the SDK picks up automatically.";
            }
        }

        private string ResolveKey()
        {
            if (_apiKeyProvider != null)
            {
                string fromSettings = null;
                try { fromSettings = _apiKeyProvider(); } catch { }
                if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings;
            }
            return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }

        public async Task<AiResponse> SendAsync(
            AiRequest request,
            Action<AiStreamEvent> onEvent,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");

            try
            {
                return await SendCoreAsync(request, onEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;   // the user cancelled; not an error to report as one
            }
            catch (Exception ex)
            {
                // Everything else — auth, rate limit, transport, malformed request — becomes a
                // reportable response. The panel prints it; the agent loop stops cleanly.
                var failed = new AiResponse(AiStopReason.Error, new List<AiContentBlock>());
                failed.ErrorMessage = Describe(ex);
                return failed;
            }
        }

        private async Task<AiResponse> SendCoreAsync(
            AiRequest request,
            Action<AiStreamEvent> onEvent,
            CancellationToken cancellationToken)
        {
            AnthropicClient client = BuildClient();
            MessageCreateParams parameters = BuildParams(request);

            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var toolUses = new List<AiToolUseBlock>();
            // Keyed by long: the SDK reports content-block indices as long, not int.
            var partialToolJson = new Dictionary<long, StringBuilder>();
            var toolMeta = new Dictionary<long, KeyValuePair<string, string>>();  // index -> (id, name)
            string stopReason = null;
            var usage = new AiUsage();

            await foreach (RawMessageStreamEvent ev in
                client.Messages.CreateStreaming(parameters).WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A tool call arrives as a content_block_start naming the tool, then a run of
                // input_json deltas that must be concatenated — the arguments are NOT present on
                // the start event, so the block is only complete at content_block_stop.
                if (ev.TryPickContentBlockStart(out var start))
                {
                    if (start.ContentBlock.TryPickToolUse(out ToolUseBlock tu))
                    {
                        toolMeta[start.Index] = new KeyValuePair<string, string>(tu.ID, tu.Name);
                        partialToolJson[start.Index] = new StringBuilder();
                    }
                    continue;
                }

                if (ev.TryPickContentBlockDelta(out var delta))
                {
                    if (delta.Delta.TryPickText(out var td))
                    {
                        text.Append(td.Text);
                        Emit(onEvent, AiStreamEvent.TextDelta(td.Text));
                    }
                    else if (delta.Delta.TryPickThinking(out var thd))
                    {
                        thinking.Append(thd.Thinking);
                        Emit(onEvent, AiStreamEvent.ThinkingDelta(thd.Thinking));
                    }
                    else if (delta.Delta.TryPickInputJson(out var ijd))
                    {
                        StringBuilder buf;
                        if (!partialToolJson.TryGetValue(delta.Index, out buf))
                        {
                            buf = new StringBuilder();
                            partialToolJson[delta.Index] = buf;
                        }
                        buf.Append(ijd.PartialJson);
                    }
                    continue;
                }

                if (ev.TryPickContentBlockStop(out var stop))
                {
                    KeyValuePair<string, string> meta;
                    if (toolMeta.TryGetValue(stop.Index, out meta))
                    {
                        StringBuilder buf;
                        string json = partialToolJson.TryGetValue(stop.Index, out buf)
                            ? buf.ToString()
                            : "{}";
                        var use = new AiToolUseBlock(meta.Key, meta.Value,
                            string.IsNullOrWhiteSpace(json) ? "{}" : json);
                        toolUses.Add(use);
                        Emit(onEvent, AiStreamEvent.ToolUseReady(use));
                    }
                    continue;
                }

                if (ev.TryPickDelta(out var messageDelta))
                {
                    if (messageDelta.Delta != null && messageDelta.Delta.StopReason != null)
                        stopReason = messageDelta.Delta.StopReason.ToString();
                    if (messageDelta.Usage != null)
                        usage.OutputTokens = messageDelta.Usage.OutputTokens;
                    continue;
                }

                if (ev.TryPickStart(out var messageStart) && messageStart.Message != null
                    && messageStart.Message.Usage != null)
                {
                    usage.InputTokens = messageStart.Message.Usage.InputTokens;
                    usage.CacheReadTokens = messageStart.Message.Usage.CacheReadInputTokens ?? 0;
                    usage.CacheWriteTokens = messageStart.Message.Usage.CacheCreationInputTokens ?? 0;
                }
            }

            return Assemble(text, thinking, toolUses, stopReason, usage);
        }

        private static AiResponse Assemble(
            StringBuilder text,
            StringBuilder thinking,
            List<AiToolUseBlock> toolUses,
            string stopReason,
            AiUsage usage)
        {
            // Refusal first: on a decline the content is empty or partial, and treating it as a
            // normal answer prints nothing and looks like a silent failure.
            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
            {
                var refused = new AiResponse(AiStopReason.Refusal, new List<AiContentBlock>());
                refused.ErrorMessage =
                    "Claude declined this request (safety classifier). Rephrasing usually helps; "
                    + "if this is legitimate security or life-sciences work, it may be a false positive.";
                refused.Usage = usage;
                return refused;
            }

            var blocks = new List<AiContentBlock>();
            if (thinking.Length > 0) blocks.Add(new AiThinkingBlock(thinking.ToString()));
            if (text.Length > 0) blocks.Add(new AiTextBlock(text.ToString()));
            foreach (AiToolUseBlock use in toolUses) blocks.Add(use);

            AiStopReason reason;
            if (toolUses.Count > 0) reason = AiStopReason.ToolUse;
            else if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)) reason = AiStopReason.MaxTokens;
            else reason = AiStopReason.EndTurn;

            var response = new AiResponse(reason, blocks);
            response.Usage = usage;
            if (reason == AiStopReason.MaxTokens)
            {
                response.ErrorMessage =
                    "Response hit the max-tokens ceiling and is truncated. On Claude Opus 5 the "
                  + "ceiling covers thinking AND the answer, so raise MaxTokens or lower Effort.";
            }
            return response;
        }

        private AnthropicClient BuildClient()
        {
            string key = ResolveKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                // No explicit key: let the SDK resolve ANTHROPIC_AUTH_TOKEN or an `ant` profile.
                return new AnthropicClient();
            }
            return new AnthropicClient { ApiKey = key };
        }

        private static MessageCreateParams BuildParams(AiRequest request)
        {
            // Thinking, System and Tools are `init`-only on MessageCreateParams, so everything has
            // to be decided before the object exists — no post-construction assignment. Null is the
            // documented "omit this field" value, which is why the optional pieces are computed
            // into locals first rather than conditionally assigned afterwards.
            List<TextBlockParam> system = null;
            if (!string.IsNullOrWhiteSpace(request.System))
            {
                system = new List<TextBlockParam>
                {
                    // Cache the system prompt: it is the stable prefix across every turn of a
                    // session, and re-sending it uncached on each agent iteration is the single
                    // biggest avoidable cost in a tool loop.
                    new TextBlockParam
                    {
                        Text = request.System,
                        CacheControl = new CacheControlEphemeral(),
                    },
                };
            }

            List<ToolUnion> tools = null;
            if (request.Tools != null && request.Tools.Count > 0)
            {
                tools = new List<ToolUnion>();
                foreach (AiToolDefinition def in request.Tools) tools.Add(ToSdkTool(def));
            }

            return new MessageCreateParams
            {
                Model = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel : request.Model,
                MaxTokens = request.MaxTokens,
                Messages = ToSdkMessages(request.Messages),
                OutputConfig = new OutputConfig { Effort = ToSdkEffort(request.Effort) },
                // Adaptive is the only on-mode on current models; `display` defaults to omitted,
                // which streams thinking blocks with empty text and reads as a long pause before
                // any output appears.
                Thinking = request.ShowThinking
                    ? new ThinkingConfigAdaptive { Display = Display.Summarized }
                    : null,
                System = system,
                Tools = tools,
            };
        }

        private static Tool ToSdkTool(AiToolDefinition def)
        {
            var properties = new Dictionary<string, JsonElement>();
            var required = new List<string>();

            // The schema is authored as JSON so tool definitions stay declarative; the SDK wants
            // it decomposed into Properties/Required, so unpack rather than re-describe it.
            using (JsonDocument doc = JsonDocument.Parse(def.InputSchemaJson))
            {
                JsonElement root = doc.RootElement;
                JsonElement props;
                if (root.TryGetProperty("properties", out props)
                    && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in props.EnumerateObject())
                        properties[p.Name] = p.Value.Clone();
                }
                JsonElement req;
                if (root.TryGetProperty("required", out req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement r in req.EnumerateArray())
                        if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString());
                }
            }

            return new Tool
            {
                Name = def.Name,
                Description = def.Description,
                // Target-typed `new()`: the schema type is a nested SDK type whose name is not
                // part of the documented surface, and `Type = "object"` is set by its constructor.
                InputSchema = new()
                {
                    Properties = properties,
                    Required = required,
                },
            };
        }

        private static List<MessageParam> ToSdkMessages(IReadOnlyList<AiMessage> messages)
        {
            var result = new List<MessageParam>();
            foreach (AiMessage message in messages)
            {
                var blocks = new List<ContentBlockParam>();
                foreach (AiContentBlock block in message.Content)
                {
                    var text = block as AiTextBlock;
                    if (text != null)
                    {
                        if (!string.IsNullOrEmpty(text.Text))
                            blocks.Add(new TextBlockParam { Text = text.Text });
                        continue;
                    }

                    var use = block as AiToolUseBlock;
                    if (use != null)
                    {
                        blocks.Add(new ToolUseBlockParam
                        {
                            ID = use.Id,
                            Name = use.Name,
                            Input = ParseToDictionary(use.InputJson),
                        });
                        continue;
                    }

                    var result2 = block as AiToolResultBlock;
                    if (result2 != null)
                    {
                        blocks.Add(new ToolResultBlockParam
                        {
                            ToolUseID = result2.ToolUseId,
                            Content = result2.Content,
                            IsError = result2.IsError,
                        });
                        continue;
                    }

                    // Thinking blocks are deliberately NOT echoed back. Replaying them requires
                    // the original signature, which this abstraction does not carry; the API
                    // tolerates their absence, whereas a reconstructed block is rejected.
                }

                if (blocks.Count == 0) continue;   // the API rejects an empty turn
                result.Add(new MessageParam
                {
                    Role = message.Role == AiRole.User ? Role.User : Role.Assistant,
                    Content = blocks,
                });
            }
            return result;
        }

        private static Dictionary<string, JsonElement> ParseToDictionary(string json)
        {
            var map = new Dictionary<string, JsonElement>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                            map[p.Name] = p.Value.Clone();
                }
            }
            catch (JsonException)
            {
                // A tool call we could not parse is still echoed back as an empty object rather
                // than dropped — a missing tool_use block desynchronizes the whole turn.
            }
            return map;
        }

        private static Effort ToSdkEffort(AiEffort effort)
        {
            switch (effort)
            {
                case AiEffort.Low: return Effort.Low;
                case AiEffort.Medium: return Effort.Medium;
                // SDK 12.40.0 exposes only Low/Medium/High/Max — there is no XHigh member yet,
                // even though the API accepts the level. Mapping it down to High rather than up to
                // Max keeps the request valid and errs toward the cheaper of the two neighbours;
                // revisit when the SDK adds the member.
                case AiEffort.XHigh: return Effort.High;
                case AiEffort.Max: return Effort.Max;
                default: return Effort.High;
            }
        }

        private static void Emit(Action<AiStreamEvent> onEvent, AiStreamEvent ev)
        {
            if (onEvent == null) return;
            // A throwing UI handler must not abort the stream mid-turn: the conversation would be
            // left with a half-received assistant message and no way to continue it.
            try { onEvent(ev); } catch { }
        }

        private static string Describe(Exception ex)
        {
            var message = ex.Message;
            if (ex is System.Net.Http.HttpRequestException)
                return "Could not reach the Anthropic API: " + message;
            return ex.GetType().Name + ": " + message;
        }
    }
}
