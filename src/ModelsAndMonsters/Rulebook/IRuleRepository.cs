namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// The store of rule cards. Deliberately small and in-memory — no database, no embeddings, no vector store.
/// It is the authority for which (rule id, version) pairs exist, so guidance that cites an unknown rule or a
/// stale version can be rejected before it reaches the Dungeon Master.
/// </summary>
public interface IRuleRepository
{
    /// <summary>Every rule card in the book, in a stable order.</summary>
    IReadOnlyList<RuleCard> AllCards { get; }

    /// <summary>The generic rejection/failure card, always included in retrieval.</summary>
    RuleCard RejectCard { get; }

    /// <summary>A short aggregate version identifying the whole rulebook, for tracing and cache keys.</summary>
    string RulebookVersion { get; }

    /// <summary>Finds a card by rule id, or null if there is no such rule.</summary>
    RuleCard? Find(string ruleId);

    /// <summary>True when a rule with the given id exists and its current version matches the one cited.</summary>
    bool IsValid(string ruleId, string version);
}
