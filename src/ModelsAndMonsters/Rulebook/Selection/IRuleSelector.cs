namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// How the cards for one consultation are chosen. Configuration, not a runtime decision: a run picks one
/// mode and every consultation in it uses that mode, so a run's cost is one number and not an average over
/// strategies.
/// </summary>
public enum RuleSelectionMode
{
    /// <summary>
    /// Send the entire (small) rulebook every time and let the resolver do all the selection. The v0.6–v0.7
    /// production path, and still the default: it is the only one that cannot possibly hide a needed card.
    /// </summary>
    WholeRulebook,

    /// <summary>
    /// Select through DECLARED action-to-rule metadata when the caller already knows which action family it
    /// is asking about. No language is read at all. Only usable where the action family is genuinely known —
    /// it is not, and cannot be, a way of guessing the family from an intent.
    /// </summary>
    StructuredRouting,

    /// <summary>
    /// Show a model one line per card — id, action and summary — ask which ids are relevant, then resolve
    /// with only those full cards, expanded through their declared related rules. The model still does the
    /// semantic work; it just reads a few hundred characters instead of thirty thousand.
    /// </summary>
    CompactIndex,

    /// <summary>
    /// Embed each card once, embed the incoming intent, take a small top-K and expand through declared
    /// related rules. Requires a configured embedding provider; without one the selector falls back.
    /// </summary>
    Embedding
}

/// <summary>The cards chosen for one consultation, and the complete account of how they were chosen.</summary>
/// <remarks>
/// Every field here exists so a selection can be audited after the fact. A strategy that quietly dropped the
/// one card a decision turned on must be visible as having done so, which means recording not just what was
/// selected but why, what the declared links added, and whether the strategy gave up and sent everything.
/// </remarks>
public sealed record RuleSelection
{
    public required RuleSelectionMode Mode { get; init; }

    /// <summary>The cards handed to the resolver, in catalog order.</summary>
    public required IReadOnlyList<RuleCard> Cards { get; init; }

    /// <summary>The ids the strategy itself picked, before related-rule expansion.</summary>
    public required IReadOnlyList<string> DirectlySelectedRuleIds { get; init; }

    /// <summary>The ids added purely by following declared related-rule links.</summary>
    public IReadOnlyList<string> ExpandedRuleIds { get; init; } = [];

    /// <summary>
    /// Why each id was picked, in the strategy's own terms — a similarity score, a model-chosen id, a
    /// declared route. One entry per directly-selected id where the strategy can say something.
    /// </summary>
    public IReadOnlyList<string> SelectionReasons { get; init; } = [];

    /// <summary>True when the strategy gave up and sent the whole bounded rulebook instead.</summary>
    public bool FellBack { get; init; }

    /// <summary>Why it fell back. Null when it did not.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>Model calls the SELECTION itself made, not counting the resolver call that follows.</summary>
    public int ModelCalls { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public double LatencyMs { get; init; }

    /// <summary>The total size, in characters, of the card blocks selected.</summary>
    public int TotalInputChars => Cards.Sum(c => c.ToResolverBlock().Length);
}

/// <summary>
/// Chooses the rule cards for one intent.
/// </summary>
/// <remarks>
/// Every implementation must be able to fall back to the whole bounded rulebook, and must say so when it
/// does. That is the safety property the whole investigation turns on: a cheaper selection is only worth
/// having if its failure mode is "costs what it always cost" rather than "silently answered without the
/// rule that mattered".
/// </remarks>
public interface IRuleSelector
{
    RuleSelectionMode Mode { get; }

    Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken);
}
