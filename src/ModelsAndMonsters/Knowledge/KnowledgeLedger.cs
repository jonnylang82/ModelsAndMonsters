using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Knowledge;

/// <summary>
/// The application-owned store of what has been discovered and who knows it. It is a growing ledger,
/// separate from the immutable authoritative <see cref="GameState"/>: game state is the current mechanical
/// truth, and this is the accumulating record of observations made about it.
/// </summary>
/// <remarks>
/// <para>
/// Facts are shared and observer-neutral; <see cref="CharacterKnowledge"/> records attach a character, a
/// source and a moment to a fact. A fact is minted at most once per distinct observation: an exterior
/// marking is one static fact, and a container's contents become a new fact only when the contents
/// actually differ from any previously recorded observation of that container (matched by a content
/// signature). That is what stops repeat inspection of unchanged contents from accreting duplicate facts.
/// </para>
/// <para>
/// A character holds at most one record per fact. Learning something already known is a no-op that reports
/// "nothing new", and it never rewrites the world version at which the character first observed it — a
/// historical observation must not silently update when the world later changes.
/// </para>
/// <para>
/// Orchestration is strictly sequential, so no locking is needed.
/// </para>
/// </remarks>
public sealed class KnowledgeLedger
{
    private readonly Dictionary<string, KnowledgeFact> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CharacterKnowledge> _records = [];
    private readonly HashSet<(string Character, string Fact)> _held = new();

    /// <summary>Per-subject map from a content signature to the fact id first minted for it.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _contentsFactBySignature =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-subject monotonic counter for numbering distinct contents observations (v1, v2, …).</summary>
    private readonly Dictionary<string, int> _contentsVersionCounter = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<KnowledgeFact> Facts => _facts.Values;

    public IReadOnlyList<CharacterKnowledge> Records => _records;

    public KnowledgeFact? FindFact(string factId) =>
        _facts.TryGetValue(factId, out var fact) ? fact : null;

    /// <summary>Every record this character holds, in the order they were learned.</summary>
    public IEnumerable<CharacterKnowledge> RecordsFor(string characterId) =>
        _records.Where(r => string.Equals(r.CharacterId, characterId, StringComparison.OrdinalIgnoreCase));

    public bool Knows(string characterId, string factId) =>
        _held.Contains((Key(characterId), Key(factId)));

    /// <summary>
    /// The result of minting-or-finding a fact: the fact itself, and whether it was newly created (so the
    /// caller can trace a <c>KnowledgeFactCreated</c> exactly once).
    /// </summary>
    public readonly record struct FactResult(KnowledgeFact Fact, bool WasCreated);

    /// <summary>
    /// The stable exterior-marking fact for a container. Static (world version 0): a marking does not
    /// change, so the same fact is returned however many times, and to whichever character, it is reached.
    /// </summary>
    public FactResult GetOrAddMarkingFact(string subjectId, string clue)
    {
        var id = $"{subjectId}-marking";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = subjectId,
            FactType = FactType.ContainerExteriorMarking,
            Description = clue,
            WorldVersion = 0
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// The contents fact for a container at a moment, identified by what it actually holds. Contents that
    /// match an earlier observation of the same container return that earlier fact unchanged; contents that
    /// differ mint a new, separately numbered fact. The supplied world version is recorded only when the
    /// fact is first minted, so an unchanged re-observation does not rewrite it.
    /// </summary>
    public FactResult GetOrAddContentsFact(string subjectId, string subjectName, IReadOnlyList<InventoryItem> contents, int worldVersion)
    {
        var signature = ContentSignature(contents);
        var bySignature = _contentsFactBySignature.TryGetValue(subjectId, out var map)
            ? map
            : _contentsFactBySignature[subjectId] = new Dictionary<string, string>(StringComparer.Ordinal);

        if (bySignature.TryGetValue(signature, out var existingId))
        {
            return new FactResult(_facts[existingId], WasCreated: false);
        }

        var next = _contentsVersionCounter.GetValueOrDefault(subjectId) + 1;
        _contentsVersionCounter[subjectId] = next;
        var id = $"{subjectId}-contents-v{next}";

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = subjectId,
            FactType = FactType.ContainerContents,
            Description = DescribeContents(subjectName, contents),
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        bySignature[signature] = id;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public item-removal fact: an identifiable item was taken from a container and is now carried, in
    /// plain view of the room. Keyed by item and the world version at which the removal happened.
    /// </summary>
    public FactResult GetOrAddRemovalFact(string itemId, string itemName, string actorName, string containerName, string subjectId, int worldVersion)
    {
        var id = $"{itemId}-removed-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = subjectId,
            FactType = FactType.ItemRemoved,
            Description = $"{actorName} removed the {itemName} from the {containerName} and now carries it.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// Records that a character knows a fact. Returns the new record when this is genuinely new knowledge;
    /// null when the character already held it — in which case the caller should tell them they learned
    /// nothing new, and no duplicate record is created and the world version they first observed it at is
    /// left untouched.
    /// </summary>
    public CharacterKnowledge? Learn(string characterId, string factId, KnowledgeSource source, int round, int turn, int observedWorldVersion)
    {
        var key = (Key(characterId), Key(factId));
        if (!_held.Add(key))
        {
            return null;
        }

        var record = new CharacterKnowledge
        {
            CharacterId = characterId,
            FactId = factId,
            Source = source,
            LearnedAtRound = round,
            LearnedAtTurn = turn,
            ObservedWorldVersion = observedWorldVersion
        };
        _records.Add(record);
        return record;
    }

    /// <summary>A stable signature for a container's contents, order-independent, so equal sets match.</summary>
    private static string ContentSignature(IReadOnlyList<InventoryItem> contents) =>
        contents.Count == 0
            ? "(empty)"
            : string.Join("+", contents.Select(i => i.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

    private static string DescribeContents(string subjectName, IReadOnlyList<InventoryItem> contents) =>
        contents.Count == 0
            ? $"The {subjectName} was empty."
            : $"The {subjectName} held {NaturalJoin([.. contents.Select(i => i.Name)])}.";

    private static string NaturalJoin(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "nothing",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };

    private static string Key(string value) => value.Trim();
}
