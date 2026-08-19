namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// Sends the whole rulebook to the resolver on every call. There is no semantic routing: the retriever does
/// not decide which actions the resolver may consider — that is the resolver's job. With only a dozen small,
/// stateless cards the entire catalog is a few thousand tokens, so sending all of them keeps the request
/// bounded and constant per round while guaranteeing the relevant card is always in front of the resolver.
/// </summary>
/// <remarks>
/// The configured <c>maxCards</c>/<c>maxInputChars</c> are a hard ceiling, not a trimming budget: if the
/// catalog ever outgrows them, construction throws so the mismatch is caught loudly at startup, rather than
/// silently dropping cards and risking hiding the one action an intent needs. Grow the ceiling (or split the
/// rulebook and reintroduce real retrieval) deliberately, not by accident.
/// </remarks>
public sealed class RuleRetriever : IRuleRetriever
{
    private readonly IReadOnlyList<RuleCard> _allCards;
    private readonly IReadOnlyList<string> _allRuleIds;
    private readonly int _totalChars;

    public RuleRetriever(IRuleRepository repository, int maxCards, int maxInputChars)
    {
        ArgumentNullException.ThrowIfNull(repository);

        _allCards = repository.AllCards;
        _allRuleIds = [.. _allCards.Select(c => c.RuleId)];
        _totalChars = _allCards.Sum(c => c.ToPromptBlock().Length);

        if (_allCards.Count > maxCards)
        {
            throw new InvalidOperationException(
                $"The rulebook has {_allCards.Count} cards but the configured ceiling (RulebookMaxCards) is {maxCards}. " +
                "The retriever sends every card and never trims — raise the ceiling, or split the rulebook and add real retrieval.");
        }

        if (_totalChars > maxInputChars)
        {
            throw new InvalidOperationException(
                $"The rulebook's cards total {_totalChars} characters but the configured ceiling (RulebookMaxInputChars) is {maxInputChars}. " +
                "The retriever sends every card and never trims — raise the ceiling, or shorten the cards.");
        }
    }

    /// <summary>Returns the whole rulebook, unchanged, whatever the intent. Selection is the resolver's job.</summary>
    public RuleRetrievalResult Retrieve(string intent) => new()
    {
        SelectedCards = _allCards,
        ConsideredRuleIds = _allRuleIds,
        TotalInputChars = _totalChars,
        Trimmed = false
    };
}
