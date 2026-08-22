using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.AI;

/// <summary>
/// Detects silent input truncation by comparing what we sent against what the provider says it
/// received — not by trusting any configured context window.
/// </summary>
/// <remarks>
/// <para>
/// Ollama drops the oldest messages when a request exceeds its context window and reports only the
/// post-truncation size in <c>prompt_eval_count</c>. Nothing in the response signals it. The only
/// reliable evidence is the gap between the size of what we sent and the input size the response
/// reports: if the reported size is well below what we sent, history was discarded before the model
/// saw it. Comparing against the reported size rather than the configured window also catches setups
/// that truncate at a fraction of the window (a known Ollama behaviour) and cases where no window was
/// configured at all.
/// </para>
/// <para>
/// The token count is a deliberately rough estimate (a characters-per-token divisor plus a small
/// per-message framing allowance), because an exact tokenizer is a dependency this experiment does not
/// need. The divisor is calibrated to the local tokenizer actually in use: a plain 4.0 chars/token
/// over-counts qwen's tokenizer on this harness's prose-plus-JSON content by a consistent ~10%, measured
/// against <c>prompt_eval_count</c> on projections that provably fit the window (a 2-message ~5k Dungeon
/// Master projection reported ~5,040 for a 4.0-estimate of ~5,570). Left uncalibrated, that bias scales
/// with prompt size and eventually crosses the drop threshold, misreporting a large-but-fine projection
/// as truncated. Calibrating removes the systematic bias, so a gap now means real dropped history rather
/// than tokenizer disagreement; the few percent of residual error is absorbed by the threshold below.
/// </para>
/// </remarks>
public static class ContextTruncation
{
    // Calibrated to qwen's tokenizer on this harness's content (~4.4 chars/token), not the generic 4.0
    // English rule of thumb, which over-counted by ~10% and produced false truncation warnings on the
    // larger projections v0.4 introduced. See the remarks above.
    private const double CharactersPerToken = 4.4;
    private const int PerMessageOverheadTokens = 4;

    /// <summary>
    /// Absolute floor for the "sent minus received" gap that counts as truncation.
    /// </summary>
    /// <remarks>
    /// A secondary guard for small prompts, where the fractional threshold is a handful of tokens. With the
    /// estimator now calibrated to the local tokenizer (see the class remarks), residual estimation noise is
    /// only a few percent, so this floor rarely decides anything on its own; it stays at 500 — comfortably
    /// above small-prompt noise and below the smallest genuine drop measured (a half-window truncation of a
    /// small prompt removes ~600+) — so a real drop is still caught while tokenizer disagreement is not.
    /// </remarks>
    private const int MinimumDroppedTokens = 500;

    /// <summary>Gap as a fraction of what we sent, to absorb estimation error on larger prompts.</summary>
    private const double DroppedFractionThreshold = 0.08;

    /// <summary>Output room reserved when an agent's MaxOutputTokens is unknown.</summary>
    private const int DefaultOutputReserveTokens = 1024;

    /// <summary>
    /// How close input+output must come to the window to count as having filled it. Small, because the
    /// measured cases land exactly on the ceiling (8152 + 40 = 8192); the slack only absorbs a provider
    /// counting a stop token or two differently.
    /// </summary>
    private const int ContextCeilingSlackTokens = 16;

    /// <summary>
    /// Below this fraction of its output budget, a reply that finished with Length plainly did not stop
    /// because of that budget. Deliberately generous: half a budget left unused is not an output limit.
    /// </summary>
    private const double OutputBudgetSpentFraction = 0.5;

    /// <summary>
    /// How near the output budget counts as having spent it. A few tokens, because a provider may stop on a
    /// token boundary just short of the cap.
    /// </summary>
    private const int OutputBudgetSlackTokens = 8;

    /// <summary>A safety margin subtracted from a derived budget, absorbing residual estimation error.</summary>
    private const int HistoryBudgetSafetyMarginTokens = 256;

    /// <summary>Never derive a budget below this, so a tiny window does not summarise every turn to nothing.</summary>
    private const int MinimumHistoryBudgetTokens = 1024;

    /// <summary>
    /// A conservative starting estimate, in tokens, for the request overhead our message-only estimate does
    /// not see — the tool-call schemas and the model's chat-template scaffolding — used before any call has
    /// measured the real figure from the provider's reported input size. See <see cref="EffectiveHistoryBudget"/>.
    /// </summary>
    public const int DefaultPromptOverheadTokens = 1500;

    /// <summary>
    /// The effective history-summarisation budget for an agent, in the same message-only units as
    /// <see cref="EstimateSentTokens"/>. When a context window is known, the budget is derived from it so the
    /// FULL request — the messages the estimate sees, PLUS the tool schemas and chat-template scaffolding it
    /// does not (<paramref name="observedPromptOverhead"/>, measured from the provider's reported input),
    /// PLUS room for the model's reply — stays safely inside the window. Without a window the configured
    /// budget is used unchanged (unless <paramref name="unboundedWithoutWindow"/> says otherwise). The
    /// configured budget is always an upper bound when a window IS known: a large window does not license
    /// an unbounded history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fix for a real miscalibration: summarising against a message-only estimate (which omits the
    /// tool scaffolding) let a "trimmed to 5,500" history sit at ~7,600 real tokens — right under an 8,192
    /// window, with almost no output room, so replies truncated. Reserving the overhead and the output room
    /// makes compaction target the real prompt size, keeping the request inside the window on its own.
    /// </para>
    /// <para>
    /// <paramref name="unboundedWithoutWindow"/> carries
    /// <see cref="Configuration.HarnessOptions.UnboundedHistoryOnHostedModels"/>: without it, "no window
    /// known" (a hosted agent with <see cref="Configuration.HarnessOptions.EnforceContextWindowOnHostedModels"/>
    /// off) silently falls back to the flat <paramref name="configuredBudget"/> anyway — a local-model-sized
    /// ceiling reintroduced on exactly the runs meant to be exempt from one.
    /// </para>
    /// </remarks>
    public static int EffectiveHistoryBudget(
        int configuredBudget, int? contextWindow, int? maxOutputTokens, int observedPromptOverhead,
        bool unboundedWithoutWindow = false)
    {
        if (contextWindow is not int window)
        {
            return unboundedWithoutWindow ? int.MaxValue : configuredBudget;
        }

        var outputReserve = maxOutputTokens is int max and > 0 ? max : DefaultOutputReserveTokens;
        var overhead = Math.Max(0, observedPromptOverhead);
        var derived = window - outputReserve - overhead - HistoryBudgetSafetyMarginTokens;
        return Math.Min(configuredBudget, Math.Max(MinimumHistoryBudgetTokens, derived));
    }

    /// <summary>A rough token estimate for a whole message collection.</summary>
    public static int EstimateSentTokens(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var total = 0;
        foreach (var message in messages)
        {
            total += PerMessageOverheadTokens + EstimateMessageTokens(message);
        }

        return total;
    }

    /// <summary>
    /// True when the reported input size is far enough below what we sent to indicate the provider
    /// discarded part of the conversation.
    /// </summary>
    /// <param name="estimatedSentTokens">Rough estimate of what we sent, from <see cref="EstimateSentTokens"/>.</param>
    /// <param name="reportedInputTokens">The input size the provider reported processing.</param>
    /// <param name="estimatedDroppedTokens">How much appears to have been dropped (may be negative).</param>
    public static bool WasInputTruncated(int estimatedSentTokens, long reportedInputTokens, out int estimatedDroppedTokens)
    {
        estimatedDroppedTokens = estimatedSentTokens - (int)reportedInputTokens;
        var threshold = Math.Max(MinimumDroppedTokens, (int)(estimatedSentTokens * DroppedFractionThreshold));
        return estimatedDroppedTokens > threshold;
    }

    /// <summary>
    /// Distinguishes the two very different reasons a reply can finish with <c>Length</c>: the model used up
    /// the output budget it was given, or the input had already filled the context window so there was
    /// almost nothing left to generate into. Only the first is fixed by raising MaxOutputTokens.
    /// </summary>
    /// <remarks>
    /// A live run showed the difference starkly — <c>input 8152 + output 40 = 8192</c>, exactly the window,
    /// with the output budget nowhere near spent. The harness reported "truncated at the output-token limit"
    /// and advised raising MaxOutputTokens, which would have made it worse: the window is shared, so a larger
    /// output reservation leaves *less* room for the prompt. The real remedy is a smaller request.
    /// </remarks>
    /// <param name="inputTokens">Input tokens the provider reported.</param>
    /// <param name="outputTokens">Output tokens the provider reported.</param>
    /// <param name="contextWindow">The window the agent is configured for, when known.</param>
    /// <param name="maxOutputTokens">The per-call output budget, when set.</param>
    public static bool WasContextExhausted(long? inputTokens, long? outputTokens, int? contextWindow, int? maxOutputTokens)
    {
        if (inputTokens is not { } input || outputTokens is not { } output || contextWindow is not { } window || window <= 0)
        {
            return false;
        }

        // Right at the ceiling: input and output together account for the whole window.
        if (input + output < window - ContextCeilingSlackTokens)
        {
            return false;
        }

        // And the output budget was plainly not the binding constraint — the reply stopped far short of it.
        return maxOutputTokens is not { } cap || output < cap * OutputBudgetSpentFraction;
    }

    /// <summary>
    /// True when a reply used up essentially all of the output budget it was given — the signature of a
    /// reply cut off at that budget, usable when the provider reports no finish reason.
    /// </summary>
    public static bool WasOutputBudgetSpent(long? outputTokens, int? maxOutputTokens) =>
        outputTokens is { } output
        && maxOutputTokens is { } cap
        && cap > 0
        && output >= cap - OutputBudgetSlackTokens;

    /// <summary>
    /// The harness's own characters-to-tokens estimate, exposed so an offline measurement can size a request
    /// the same way the live truncation guard does. It is an estimate, not a tokeniser: a figure derived from
    /// it is comparable with another figure derived from it, and roughly comparable with a provider's report.
    /// </summary>
    public static int EstimateTokensForCharacters(int characters) =>
        characters <= 0 ? 0 : (int)Math.Ceiling(characters / CharactersPerToken);

    /// <summary>The inverse of <see cref="EstimateTokensForCharacters"/>: how many characters a token budget affords.</summary>
    public static int EstimateCharactersForTokens(int tokens) =>
        tokens <= 0 ? 0 : (int)(tokens * CharactersPerToken);

    private static int EstimateMessageTokens(ChatMessage message)
    {
        var characters = 0;
        foreach (var content in message.Contents)
        {
            characters += content switch
            {
                TextContent text => text.Text?.Length ?? 0,
                TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                FunctionCallContent call => call.Name.Length + EstimateArgumentCharacters(call.Arguments),
                FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                _ => content.ToString()?.Length ?? 0
            };
        }

        return (int)Math.Ceiling(characters / CharactersPerToken);
    }

    private static int EstimateArgumentCharacters(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return 0;
        }

        var characters = 0;
        foreach (var (key, value) in arguments)
        {
            // key, value, and a few characters of JSON punctuation around them.
            characters += key.Length + (value?.ToString()?.Length ?? 0) + 4;
        }

        return characters;
    }
}
