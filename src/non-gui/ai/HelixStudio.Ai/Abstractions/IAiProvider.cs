using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HelixPythonNamespace.Ai.Abstractions
{
    /// <summary>
    /// One AI vendor, reduced to the surface the IDE actually needs: stream a turn, and tell the
    /// caller whether the model finished or wants tools run.
    ///
    /// Everything provider-specific — wire format, streaming transport, tool-call encoding, thinking
    /// configuration, error taxonomy — is the implementation's problem. The agent loop above this
    /// interface never learns which vendor it is talking to, which is what makes adding Gemini or a
    /// local model a new file rather than an edit to the loop.
    ///
    /// Implementations must be safe to call concurrently from the UI thread's async continuations;
    /// they must not touch WinForms state.
    /// </summary>
    public interface IAiProvider
    {
        /// <summary>Stable id used in settings and the provider registry, e.g. "anthropic".</summary>
        string Id { get; }

        /// <summary>Human-readable name for the Preferences dropdown, e.g. "Claude (Anthropic)".</summary>
        string DisplayName { get; }

        /// <summary>
        /// Model ids this provider offers, best first. The first entry is the default when the user
        /// has not chosen one.
        /// </summary>
        IReadOnlyList<string> Models { get; }

        /// <summary>
        /// True when the provider has the credentials it needs. False means the panel should tell
        /// the user how to configure it rather than letting a call fail with a raw 401.
        /// </summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Where this provider reads its key from, phrased for a user who needs to set it up
        /// (e.g. "Set ANTHROPIC_API_KEY, or run `ant auth login`").
        /// </summary>
        string CredentialHint { get; }

        /// <summary>
        /// Run one turn, reporting incremental output through <paramref name="onEvent"/>.
        ///
        /// This is ONE turn, not the agent loop: a response with
        /// <see cref="AiStopReason.ToolUse"/> means the caller must execute the tools and call
        /// again with the results appended. Looping is the caller's job so that tool approval,
        /// cancellation, and logging stay outside the provider.
        ///
        /// Never throws for an API-level failure — a failed or refused call comes back as an
        /// <see cref="AiResponse"/> with <see cref="AiStopReason.Error"/> or
        /// <see cref="AiStopReason.Refusal"/> and a populated message, so the panel can print
        /// something useful instead of unwinding. <see cref="OperationCanceledException"/> from
        /// <paramref name="cancellationToken"/> is the one exception and does propagate.
        /// </summary>
        /// <param name="onEvent">
        /// Called on a background thread as content arrives; may be null when the caller only wants
        /// the final response. Implementations must tolerate a handler that throws.
        /// </param>
        Task<AiResponse> SendAsync(
            AiRequest request,
            Action<AiStreamEvent> onEvent,
            CancellationToken cancellationToken);
    }
}
