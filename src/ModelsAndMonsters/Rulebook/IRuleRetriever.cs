namespace ModelsAndMonsters.Rulebook;

/// <summary>The outcome of gathering the rule cards to send to the resolver for one intent.</summary>
public sealed record RuleRetrievalResult
{
    /// <summary>The cards sent to the resolver. With the whole-rulebook retriever, this is every card, every time.</summary>
    public required IReadOnlyList<RuleCard> SelectedCards { get; init; }

    /// <summary>Every rule id sent — the whole catalog, since there is no semantic routing selecting a subset.</summary>
    public required IReadOnlyList<string> ConsideredRuleIds { get; init; }

    /// <summary>The total size, in characters, of the card blocks sent.</summary>
    public required int TotalInputChars { get; init; }

    /// <summary>
    /// Always false: the retriever never trims. Retained on the record so the consultation trace can state
    /// plainly that nothing was dropped; an oversized catalog fails loudly at construction instead.
    /// </summary>
    public required bool Trimmed { get; init; }
}

/// <summary>
/// Gathers the rule cards for a natural-language intent. There is no database, no embeddings and no semantic
/// routing: the whole (small) rulebook is sent to the resolver every time, so the model — not a keyword index
/// — decides which action fits. A configured maximum card count and input size act as a hard ceiling that
/// fails loudly if the catalog outgrows a bounded resolver request, rather than silently dropping cards.
/// </summary>
public interface IRuleRetriever
{
    RuleRetrievalResult Retrieve(string intent);
}
