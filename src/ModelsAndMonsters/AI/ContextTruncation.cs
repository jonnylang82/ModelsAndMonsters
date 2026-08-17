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
/// The token count is a deliberately rough estimate (~4 characters per token plus a small per-message
/// framing allowance), because an exact tokenizer is a dependency this experiment does not need. It
/// tends to <em>under</em>-count dense JSON tool arguments, which is the safe direction: the check
/// only fires when the estimate exceeds the reported size by a wide margin, so under-counting yields
/// false negatives (a missed warning) rather than false alarms.
/// </para>
/// </remarks>
public static class ContextTruncation
{
    private const double CharactersPerToken = 4.0;
    private const int PerMessageOverheadTokens = 4;

    /// <summary>
    /// Absolute floor for the "sent minus received" gap that counts as truncation.
    /// </summary>
    /// <remarks>
    /// Set above the estimation noise a small, bounded prompt produces. The Dungeon Master's projections
    /// re-embed a ~2.6k-token state block; the estimator over-counts that particular content by ~10%
    /// (~300 tokens), which is pure noise — those projections are far below any real context window and
    /// cannot be truncated — so a floor of 300 misfired on every one. 500 sits above that noise while
    /// staying below the smallest genuine drop measured (a half-window truncation of a small prompt
    /// removes ~600+), so real truncation is still caught.
    /// </remarks>
    private const int MinimumDroppedTokens = 500;

    /// <summary>Gap as a fraction of what we sent, to absorb estimation error on larger prompts.</summary>
    private const double DroppedFractionThreshold = 0.08;

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
