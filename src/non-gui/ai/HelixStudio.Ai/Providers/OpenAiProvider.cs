using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HelixPythonNamespace.Ai.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Ai.Providers
{
    /// <summary>
    /// OpenAI, via the Chat Completions REST API.
    ///
    /// Hand-written rather than using the OpenAI SDK on purpose: this assembly already carries
    /// System.Text.Json and Newtonsoft transitively, and a second vendor SDK would roughly double
    /// the net472 binding-redirect surface for what is one streaming POST. The wire format is
    /// stable and small enough that owning it is cheaper than owning the dependency.
    ///
    /// Differences from the Anthropic provider that the shared abstraction hides:
    /// <list type="bullet">
    /// <item>Tool results are their own <c>role: "tool"</c> message keyed by <c>tool_call_id</c>,
    /// not content blocks inside a user turn.</item>
    /// <item>The system prompt is the first message, not a separate field.</item>
    /// <item>Tool-call arguments stream as <c>delta.tool_calls[].function.arguments</c> fragments
    /// addressed by array index, and the <c>id</c>/<c>name</c> arrive only on the first fragment.</item>
    /// <item>There is no thinking-summary channel, so <see cref="AiRequest.ShowThinking"/> is
    /// silently inert here rather than faked.</item>
    /// </list>
    /// </summary>
    public sealed class OpenAiProvider : IAiProvider
    {
        public const string DefaultModel = "gpt-5";
        private const string Endpoint = "https://api.openai.com/v1/chat/completions";

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly Func<string> _apiKeyProvider;

        public OpenAiProvider(Func<string> apiKeyProvider = null)
        {
            _apiKeyProvider = apiKeyProvider;
        }

        private static HttpClient CreateHttpClient()
        {
            // net472 defaults to SSL3/TLS1.0 unless the app opts in, and api.openai.com refuses
            // anything below TLS 1.2 — the symptom is an opaque "existing connection was forcibly
            // closed" rather than a handshake error, so it is set explicitly here.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Older platform without the enum member; nothing to do but proceed.
            }

            // One shared client: a per-call HttpClient exhausts sockets under an agent loop's
            // request rate. Timeout is effectively disabled because streaming agentic turns can
            // legitimately run for minutes; cancellation is the CancellationToken's job.
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        public string Id { get { return "openai"; } }

        public string DisplayName { get { return "GPT (OpenAI)"; } }

        public IReadOnlyList<string> Models
        {
            get
            {
                return new List<string> { "gpt-5", "gpt-5-mini", "gpt-4.1", "o4-mini" };
            }
        }

        public bool IsConfigured { get { return !string.IsNullOrWhiteSpace(ResolveKey()); } }

        public string CredentialHint
        {
            get
            {
                return "Set the OPENAI_API_KEY environment variable, or paste a key into "
                     + "Tools ▸ Preferences ▸ AI.";
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
            return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
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
                throw;
            }
            catch (Exception ex)
            {
                var failed = new AiResponse(AiStopReason.Error, new List<AiContentBlock>());
                failed.ErrorMessage = ex.GetType().Name + ": " + ex.Message;
                return failed;
            }
        }

        private async Task<AiResponse> SendCoreAsync(
            AiRequest request,
            Action<AiStreamEvent> onEvent,
            CancellationToken cancellationToken)
        {
            string key = ResolveKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                var unconfigured = new AiResponse(AiStopReason.Error, new List<AiContentBlock>());
                unconfigured.ErrorMessage = "No OpenAI API key. " + CredentialHint;
                return unconfigured;
            }

            string body = BuildRequestJson(request);

            using (var message = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");

                // ResponseHeadersRead is what makes this a stream: the default buffers the whole
                // response before returning, which would defeat streaming entirely.
                using (HttpResponseMessage response = await Http
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return await ErrorFromAsync(response).ConfigureAwait(false);

                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return ReadStream(reader, onEvent, cancellationToken);
                    }
                }
            }
        }

        private static async Task<AiResponse> ErrorFromAsync(HttpResponseMessage response)
        {
            string payload = string.Empty;
            try { payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }

            string detail = null;
            try
            {
                JObject parsed = JObject.Parse(payload);
                JToken error = parsed["error"];
                if (error != null) detail = (string)error["message"];
            }
            catch (JsonException)
            {
                // Non-JSON error body (a proxy or gateway page) — fall back to the raw text.
            }

            var failed = new AiResponse(AiStopReason.Error, new List<AiContentBlock>());
            failed.ErrorMessage = "OpenAI returned " + (int)response.StatusCode + " "
                + response.StatusCode + (string.IsNullOrWhiteSpace(detail)
                    ? (string.IsNullOrWhiteSpace(payload) ? string.Empty : ": " + Truncate(payload, 400))
                    : ": " + detail);
            return failed;
        }

        private static AiResponse ReadStream(
            TextReader reader,
            Action<AiStreamEvent> onEvent,
            CancellationToken cancellationToken)
        {
            var text = new StringBuilder();
            var toolArgs = new Dictionary<int, StringBuilder>();
            var toolMeta = new Dictionary<int, KeyValuePair<string, string>>();  // index -> (id, name)
            var order = new List<int>();
            string finishReason = null;
            var usage = new AiUsage();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = reader.ReadLine();
                if (line == null) break;                       // stream closed
                if (line.Length == 0) continue;                // SSE event separator
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                string data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                JObject chunk;
                try { chunk = JObject.Parse(data); }
                catch (JsonException) { continue; }            // keepalive or partial frame

                JToken usageToken = chunk["usage"];
                if (usageToken != null && usageToken.Type == JTokenType.Object)
                {
                    usage.InputTokens = (long?)usageToken["prompt_tokens"] ?? usage.InputTokens;
                    usage.OutputTokens = (long?)usageToken["completion_tokens"] ?? usage.OutputTokens;
                }

                var choices = chunk["choices"] as JArray;
                if (choices == null || choices.Count == 0) continue;
                JToken choice = choices[0];

                string finish = (string)choice["finish_reason"];
                if (!string.IsNullOrEmpty(finish)) finishReason = finish;

                JToken delta = choice["delta"];
                if (delta == null) continue;

                var content = (string)delta["content"];
                if (!string.IsNullOrEmpty(content))
                {
                    text.Append(content);
                    Emit(onEvent, AiStreamEvent.TextDelta(content));
                }

                var calls = delta["tool_calls"] as JArray;
                if (calls == null) continue;
                foreach (JToken call in calls)
                {
                    // Fragments are addressed by array index; id and name appear only on the
                    // first fragment for that index, arguments accumulate across the rest.
                    int index = (int?)call["index"] ?? 0;
                    if (!order.Contains(index)) order.Add(index);

                    JToken fn = call["function"];
                    var id = (string)call["id"];
                    var name = fn != null ? (string)fn["name"] : null;
                    if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                    {
                        KeyValuePair<string, string> existing;
                        toolMeta.TryGetValue(index, out existing);
                        toolMeta[index] = new KeyValuePair<string, string>(
                            string.IsNullOrEmpty(id) ? existing.Key : id,
                            string.IsNullOrEmpty(name) ? existing.Value : name);
                    }

                    var argFragment = fn != null ? (string)fn["arguments"] : null;
                    if (string.IsNullOrEmpty(argFragment)) continue;
                    StringBuilder buf;
                    if (!toolArgs.TryGetValue(index, out buf))
                    {
                        buf = new StringBuilder();
                        toolArgs[index] = buf;
                    }
                    buf.Append(argFragment);
                }
            }

            var toolUses = new List<AiToolUseBlock>();
            foreach (int index in order)
            {
                KeyValuePair<string, string> meta;
                if (!toolMeta.TryGetValue(index, out meta) || string.IsNullOrEmpty(meta.Value)) continue;
                StringBuilder buf;
                string json = toolArgs.TryGetValue(index, out buf) ? buf.ToString() : "{}";
                var use = new AiToolUseBlock(meta.Key, meta.Value,
                    string.IsNullOrWhiteSpace(json) ? "{}" : json);
                toolUses.Add(use);
                Emit(onEvent, AiStreamEvent.ToolUseReady(use));
            }

            var blocks = new List<AiContentBlock>();
            if (text.Length > 0) blocks.Add(new AiTextBlock(text.ToString()));
            foreach (AiToolUseBlock use in toolUses) blocks.Add(use);

            AiStopReason reason;
            if (toolUses.Count > 0) reason = AiStopReason.ToolUse;
            else if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)) reason = AiStopReason.MaxTokens;
            else if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase)) reason = AiStopReason.Refusal;
            else reason = AiStopReason.EndTurn;

            var result = new AiResponse(reason, blocks);
            result.Usage = usage;
            if (reason == AiStopReason.MaxTokens)
                result.ErrorMessage = "Response hit the token ceiling and is truncated.";
            else if (reason == AiStopReason.Refusal)
                result.ErrorMessage = "OpenAI's content filter blocked this response.";
            return result;
        }

        private static string BuildRequestJson(AiRequest request)
        {
            var root = new JObject();
            root["model"] = string.IsNullOrWhiteSpace(request.Model) ? DefaultModel : request.Model;
            root["stream"] = true;
            // Without this the streamed response carries no usage at all, so the panel's cost
            // readout would silently stay at zero.
            root["stream_options"] = new JObject { ["include_usage"] = true };
            root["max_completion_tokens"] = request.MaxTokens;

            var messages = new JArray();
            if (!string.IsNullOrWhiteSpace(request.System))
            {
                messages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = request.System,
                });
            }

            foreach (AiMessage message in request.Messages)
            {
                // Tool results are separate top-level `role: "tool"` messages here, so a single
                // abstract turn can expand into several wire messages.
                var toolResults = new List<AiToolResultBlock>();
                var text = new StringBuilder();
                var toolCalls = new JArray();

                foreach (AiContentBlock block in message.Content)
                {
                    var t = block as AiTextBlock;
                    if (t != null) { text.Append(t.Text); continue; }

                    var use = block as AiToolUseBlock;
                    if (use != null)
                    {
                        toolCalls.Add(new JObject
                        {
                            ["id"] = use.Id,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = use.Name,
                                ["arguments"] = use.InputJson,
                            },
                        });
                        continue;
                    }

                    var res = block as AiToolResultBlock;
                    if (res != null) { toolResults.Add(res); }
                    // Thinking blocks have no OpenAI equivalent and are dropped.
                }

                foreach (AiToolResultBlock res in toolResults)
                {
                    messages.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = res.ToolUseId,
                        ["content"] = res.Content,
                    });
                }

                if (text.Length == 0 && toolCalls.Count == 0) continue;

                var wire = new JObject
                {
                    ["role"] = message.Role == AiRole.User ? "user" : "assistant",
                };
                // An assistant turn that only calls tools must send content: null, not "".
                if (text.Length > 0) wire["content"] = text.ToString();
                else if (toolCalls.Count > 0) wire["content"] = null;
                if (toolCalls.Count > 0) wire["tool_calls"] = toolCalls;
                messages.Add(wire);
            }

            root["messages"] = messages;

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var tools = new JArray();
                foreach (AiToolDefinition def in request.Tools)
                {
                    JObject schema;
                    try { schema = JObject.Parse(def.InputSchemaJson); }
                    catch (JsonException) { schema = new JObject { ["type"] = "object" }; }

                    tools.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = def.Name,
                            ["description"] = def.Description,
                            ["parameters"] = schema,
                        },
                    });
                }
                root["tools"] = tools;
            }

            return root.ToString(Formatting.None);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        private static void Emit(Action<AiStreamEvent> onEvent, AiStreamEvent ev)
        {
            if (onEvent == null) return;
            try { onEvent(ev); } catch { }
        }
    }
}
