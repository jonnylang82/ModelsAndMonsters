using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.AI;

/// <summary>
/// Wraps an Anthropic <see cref="IChatClient"/> and marks the stable leading prefix — the tool schemas
/// and the system prompt — for Anthropic prompt caching.
/// </summary>
/// <remarks>
/// <para>
/// Anthropic caches nothing unless a request carries explicit <c>cache_control</c> breakpoints (unlike
/// OpenAI, which caches automatically). For a given agent the system prompt is identical on every call,
/// and the tool schemas that precede it in Anthropic's cache order rarely change, so marking the system
/// message turns that whole prefix into a cache write once and cheap cache reads (~10% of the input
/// price) thereafter. For the Dungeon Master — which re-sends the same system prompt on every projection
/// — this is the single biggest saving.
/// </para>
/// <para>
/// Only the leading system message is marked, and it is rebuilt fresh for every request
/// (<see cref="ModelsAndMonsters.Agents.AgentConversation.BuildRequestMessages"/>), so marking it never
/// mutates the retained conversation and never accumulates past Anthropic's four-breakpoint limit.
/// Caching the growing character histories is a further lever left for later; it needs careful
/// breakpoint placement to stay within that limit.
/// </para>
/// </remarks>
public sealed class AnthropicPromptCachingChatClient : DelegatingChatClient
{
    public AnthropicPromptCachingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(MarkStablePrefix(messages), options, cancellationToken)
            .ConfigureAwait(false);
        RecoverCacheReadCount(response);
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(MarkStablePrefix(messages), options, cancellationToken);

    /// <summary>
    /// Marks the leading system message's content for ephemeral caching and returns the message list.
    /// The system message is the always-identical, always-first block, so exactly one breakpoint is set.
    /// </summary>
    private static IReadOnlyList<ChatMessage> MarkStablePrefix(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        foreach (var message in list)
        {
            if (message.Role == ChatRole.System && message.Contents.Count > 0)
            {
                // Assign the result back: WithCacheControl may return a new marked instance rather than
                // mutating in place, and discarding it would silently mark nothing.
                message.Contents[^1] = message.Contents[^1].WithCacheControl(new CacheControlEphemeral());
                break;
            }
        }

        return list;
    }

    /// <summary>
    /// Recovers the cache-read token count from the raw Anthropic response into the usage's additional
    /// counts. The client library surfaces <c>cache_creation_input_tokens</c> but not
    /// <c>cache_read_input_tokens</c>, so without this the trace and report see cache writes but never the
    /// reads they enable — making working caching look like pure waste.
    /// </summary>
    private static void RecoverCacheReadCount(ChatResponse response)
    {
        if (response.RawRepresentation is not Message { Usage.CacheReadInputTokens: { } read } || read <= 0)
        {
            return;
        }

        var usage = response.Usage ??= new UsageDetails();
        usage.AdditionalCounts ??= [];
        usage.AdditionalCounts["CacheReadInputTokens"] = read;
    }

    /// <summary>
    /// Whether a content block carries an Anthropic cache-control marker. A small diagnostic seam so the
    /// marking can be asserted from tests. The SDK stashes the marker in the content's additional
    /// properties (the reader is internal), so its presence there is the observable signal.
    /// </summary>
    public static bool HasCacheControl(AIContent content) => content.AdditionalProperties is { Count: > 0 };
}
