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
            WorldVersion = worldVersion,
            ItemIds = [.. contents.Select(i => i.Id)]
        };
        _facts[id] = fact;
        bySignature[signature] = id;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public item-removal fact: an identifiable item was taken from a container and is now carried, in
    /// plain view of the room. Keyed by item and the world version at which the removal happened. The fact's
    /// subject is the <em>item</em>, so knowing it counts as knowing the item exists and is carried — a
    /// legitimate informational basis for a later theft attempt (see <see cref="KnowsItem"/>).
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
            SubjectId = itemId,
            FactType = FactType.ItemRemoved,
            Description = $"{actorName} removed the {itemName} from the {containerName} and now carries it.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public item-possession fact: a character is openly carrying an item, plainly visible to everyone in
    /// the room. Minted for seeded starting inventory so that what someone carries is knowable to others.
    /// Keyed by the item, so it is static (world version of first observation); who currently holds it comes
    /// from authoritative state, but that the item exists and is carried is a stable, observable fact.
    /// </summary>
    public FactResult GetOrAddItemPossessionFact(string itemId, string itemName, string ownerName, int worldVersion)
    {
        var id = $"{itemId}-carried";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = itemId,
            FactType = FactType.ItemPossession,
            Description = $"{ownerName} is carrying the {itemName}.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public item-give fact: one character handed an item to another in plain view. Keyed by item and the
    /// world version at which the give happened. Its subject is the item, so it establishes basis to know it.
    /// </summary>
    public FactResult GetOrAddGiveFact(string itemId, string itemName, string giverName, string recipientName, int worldVersion)
    {
        var id = $"{itemId}-given-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = itemId,
            FactType = FactType.ItemGiven,
            Description = $"{giverName} gave the {itemName} to {recipientName}, who now carries it.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public item-drop fact: a character dropped an item onto the floor in plain view. Keyed by item and
    /// the world version at which the drop happened. Its subject is the item.
    /// </summary>
    public FactResult GetOrAddDropFact(string itemId, string itemName, string actorName, int worldVersion)
    {
        var id = $"{itemId}-dropped-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = itemId,
            FactType = FactType.ItemDropped,
            Description = $"{actorName} dropped the {itemName} on the floor, where it lies in plain sight.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public theft-attempt fact: a theft was tried in plain view and either succeeded or failed. Keyed by
    /// item and the world version at which it was resolved. Its subject is the item, so witnessing a theft
    /// attempt — even a failed one — is itself a legitimate basis to know the item exists.
    /// </summary>
    public FactResult GetOrAddTheftFact(string itemId, string itemName, string thiefName, string targetName, bool succeeded, int worldVersion)
    {
        var id = $"{itemId}-theft-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var description = succeeded
            ? $"{thiefName} stole the {itemName} from {targetName} and now carries it."
            : $"{thiefName} tried to steal the {itemName} from {targetName} but failed; {targetName} kept it.";

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = itemId,
            FactType = FactType.ItemTheftAttempted,
            Description = description,
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// Whether a character holds any knowledge record about the given item — a legitimate informational basis
    /// for naming or acting on that item. True when the character knows any fact whose subject is the item:
    /// having seen it carried, seen it taken, given, dropped, or having been shown it. This is what stops a
    /// character stealing an item it has no way of knowing exists, without leaking the item's presence.
    /// </summary>
    public bool KnowsItem(string characterId, string itemId)
    {
        foreach (var record in RecordsFor(characterId))
        {
            var fact = FindFact(record.FactId);
            if (fact is not null && string.Equals(fact.SubjectId, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a character has directly observed that a specific item is (or was) inside a specific container —
    /// a legitimate basis to reach for it. True when the character holds a <see cref="FactType.ContainerContents"/>
    /// record for that container whose observed contents include the item (from opening it, inspecting it while
    /// open, or knowing it from before the fight). This is what lets <c>take_item</c> refuse an item a character
    /// has no way to identify inside a container, so a Dungeon Master's slip cannot become a valid state change.
    /// </summary>
    public bool KnowsItemInContainer(string characterId, string containerId, string itemId)
    {
        foreach (var record in RecordsFor(characterId))
        {
            var fact = FindFact(record.FactId);
            if (fact is { FactType: FactType.ContainerContents }
                && string.Equals(fact.SubjectId, containerId, StringComparison.OrdinalIgnoreCase)
                && fact.ItemIds.Any(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A public container-opened fact: a container was opened in plain view of the room. Anyone present can
    /// see the lid is up — unlike the contents, which stay private to whoever looked inside. Keyed by
    /// container and the world version at which it was opened, so a container yields one such fact.
    /// </summary>
    public FactResult GetOrAddOpenedFact(string subjectId, string subjectName, int worldVersion)
    {
        var id = $"{subjectId}-opened-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = subjectId,
            FactType = FactType.ContainerOpened,
            Description = $"The {subjectName} has been opened.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public exit-opened fact: an exit was opened in plain view of the room. Keyed by exit and the world
    /// version at which it was opened, so one opening yields one fact.
    /// </summary>
    public FactResult GetOrAddExitOpenedFact(string exitId, string exitName, int worldVersion)
    {
        var id = $"{exitId}-opened-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = exitId,
            FactType = FactType.ExitOpened,
            Description = $"The {exitName} has been opened and now stands open.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public surrender fact: a character yielded in plain view. Keyed by character and the world version
    /// at which they surrendered.
    /// </summary>
    public FactResult GetOrAddSurrenderFact(string characterId, string characterName, int worldVersion)
    {
        var id = $"{characterId}-surrendered-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = characterId,
            FactType = FactType.CharacterSurrendered,
            Description = $"{characterName} has surrendered and left the fight.",
            WorldVersion = worldVersion
        };
        _facts[id] = fact;
        return new FactResult(fact, WasCreated: true);
    }

    /// <summary>
    /// A public escape fact: a character left the encounter through an exit in plain view. Keyed by character
    /// and the world version at which they escaped.
    /// </summary>
    public FactResult GetOrAddEscapeFact(string characterId, string characterName, string exitName, int worldVersion)
    {
        var id = $"{characterId}-escaped-wv{worldVersion}";
        if (_facts.TryGetValue(id, out var existing))
        {
            return new FactResult(existing, WasCreated: false);
        }

        var fact = new KnowledgeFact
        {
            Id = id,
            SubjectId = characterId,
            FactType = FactType.CharacterEscaped,
            Description = $"{characterName} escaped through the {exitName} and is no longer present.",
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
