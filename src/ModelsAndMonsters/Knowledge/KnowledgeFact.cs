namespace ModelsAndMonsters.Knowledge;

/// <summary>
/// What a knowledge fact is about. Deliberately tiny — a handful of discoverable kinds, with no general
/// ontology, inference engine or confidence scoring.
/// </summary>
public enum FactType
{
    /// <summary>An exterior marking on an object, legible only to someone who inspects it closely.</summary>
    ContainerExteriorMarking,

    /// <summary>What a container held at a particular world version. A historical observation, not a live view.</summary>
    ContainerContents,

    /// <summary>
    /// A container was opened in plain view. Its open state is public — known to the whole room — even though
    /// what it holds stays private to whoever actually looked inside.
    /// </summary>
    ContainerOpened,

    /// <summary>A publicly observable transfer: a named, identifiable item was removed and is now carried.</summary>
    ItemRemoved,

    /// <summary>An exit was opened in plain view of the room. Its open state is public, like a container's.</summary>
    ExitOpened,

    /// <summary>A character surrendered in plain view — a public, observable change in who is still fighting.</summary>
    CharacterSurrendered,

    /// <summary>A character escaped through an exit in plain view — a public, observable departure from the fight.</summary>
    CharacterEscaped,

    /// <summary>
    /// A character is openly carrying an item — visible to everyone in the small, no-distance room. Minted
    /// for seeded starting inventory so that what a character plainly carries is knowable to others, which is
    /// what gives a would-be thief a legitimate informational basis to attempt a theft. Keyed by the item.
    /// </summary>
    ItemPossession,

    /// <summary>An item was given from one character to another in plain view — a public, observable transfer.</summary>
    ItemGiven,

    /// <summary>An item was dropped onto the floor in plain view — a public, observable transfer.</summary>
    ItemDropped,

    /// <summary>A theft was attempted in plain view — always noticed in v0.6, whether it succeeded or failed.</summary>
    ItemTheftAttempted
}

/// <summary>
/// How a character came to know a fact. The source is what separates first-hand knowledge from a mere
/// public event, and both from backstory a character simply began with. Speech heard from another
/// character is deliberately NOT a source here — hearsay never becomes an authoritative knowledge record
/// (see <see cref="Orchestration.CharacterKnowledgeView"/>), it stays as reported speech.
/// </summary>
public enum KnowledgeSource
{
    /// <summary>Known from before the encounter began. Vark begins knowing what his own cases hold.</summary>
    Backstory,

    /// <summary>Learned by spending a turn examining an object closely.</summary>
    DirectInspection,

    /// <summary>Learned by opening a container and looking inside it.</summary>
    OpenedContainer,

    /// <summary>Learned because it happened in plain view of everyone in the room.</summary>
    PublicEvent
}

/// <summary>
/// One discoverable fact about the world, with a stable id so its discovery and delivery can be traced
/// reliably. A fact is observer-neutral: <em>who</em> knows it, <em>how</em>, and <em>when</em> live on the
/// <see cref="CharacterKnowledge"/> records that reference it, so the same true fact can be reached by
/// several characters through different routes without being duplicated.
/// </summary>
/// <remarks>
/// A knowledge fact is an observation made at a particular world version. It does not update when the
/// world later changes: a later change produces a <em>new</em> fact (a new contents observation), and the
/// old one remains true as something that was once seen. This is what lets a character's knowledge become
/// stale without silently rewriting what they remember.
/// </remarks>
public sealed record KnowledgeFact
{
    public required string Id { get; init; }

    /// <summary>The object the fact is about, by id (e.g. a container id).</summary>
    public required string SubjectId { get; init; }

    public required FactType FactType { get; init; }

    /// <summary>A plain, observer-neutral statement of the fact, suitable for a prompt, trace or report.</summary>
    public required string Description { get; init; }

    /// <summary>The world version this fact describes. 0 for static facts such as an exterior marking.</summary>
    public required int WorldVersion { get; init; }

    /// <summary>
    /// The item ids this fact structurally pertains to — the contents observed, for a
    /// <see cref="FactType.ContainerContents"/> fact. Empty for facts about no specific item. Kept separately
    /// from the prose <see cref="Description"/> so a character's basis to know a specific item is inside a
    /// container can be checked deterministically (see <see cref="KnowledgeLedger.KnowsItemInContainer"/>),
    /// which is what lets <c>take_item</c> refuse an item the character had no legitimate way to identify.
    /// </summary>
    public IReadOnlyList<string> ItemIds { get; init; } = [];
}

/// <summary>
/// A single character's record that they know a particular fact: which fact, how they learned it, when,
/// and the world version they observed it at. One record per (character, fact) — a character never holds
/// two records for the same fact, which is what keeps repeat inspection from accreting duplicates.
/// </summary>
public sealed record CharacterKnowledge
{
    public required string CharacterId { get; init; }

    public required string FactId { get; init; }

    public required KnowledgeSource Source { get; init; }

    public required int LearnedAtRound { get; init; }

    public required int LearnedAtTurn { get; init; }

    /// <summary>
    /// The world version at which <em>this character</em> observed the fact. It may differ from the fact's
    /// own <see cref="KnowledgeFact.WorldVersion"/> when two characters observe the same unchanged contents
    /// at different moments.
    /// </summary>
    public required int ObservedWorldVersion { get; init; }
}
