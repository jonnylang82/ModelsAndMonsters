using ModelsAndMonsters.AI;

namespace ModelsAndMonsters.Rulebook;

/// <summary>The raw outcome of one stateless resolver model call, before semantic validation.</summary>
public sealed record ResolverResult
{
    /// <summary>The complete user request sent to the resolver (intent plus the rule cards). No live state.</summary>
    public required string RequestText { get; init; }

    /// <summary>The size of the whole request in characters (system prompt plus user request).</summary>
    public required int RequestChars { get; init; }

    /// <summary>The complete raw response text from the resolver.</summary>
    public required string RawResponse { get; init; }

    /// <summary>The guidance parsed from the response, or null when the response could not be parsed as the schema.</summary>
    public RuleGuidance? Parsed { get; init; }

    /// <summary>True when the model call itself threw (a provider error), as opposed to returning unparseable text.</summary>
    public required bool ModelCallFailed { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public double LatencyMs { get; init; }
}

/// <summary>
/// The Rulebook Resolver: a stateless model call that answers abstract rule questions. It receives only the
/// character's raw intent, a bounded set of rule cards and the response schema — never live game state,
/// conversation history, private knowledge, hidden state, hit points, target availability or RNG. It has no
/// game-engine tools and retains nothing between calls. It advises on rules; it does not control the world.
/// </summary>
public interface IRulebookResolver
{
    /// <summary>This resolver's model profile, so a consultation can trace exactly which model and parameters it used.</summary>
    AgentModelProfile Profile { get; }

    Task<ResolverResult> ResolveAsync(string intent, IReadOnlyList<RuleCard> cards, CancellationToken cancellationToken);
}
