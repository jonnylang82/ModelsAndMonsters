using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// A complete, immutable snapshot of the authoritative world.
/// </summary>
/// <remarks>
/// The state is immutable so that "before" and "after" snapshots can be traced without defensive
/// copying, and so an accepted action provably produces exactly one new state.
/// </remarks>
public sealed record GameState
{
    public required Room Room { get; init; }

    public required ImmutableArray<Character> Characters { get; init; }

    /// <summary>Incremented every time the engine accepts and applies an action.</summary>
    public int Version { get; init; }

    /// <summary>
    /// Every live status effect in the encounter. Statuses live here rather than on a character because a
    /// linked relationship (Guarding on the guardian, Guarded on the protected ally) spans two characters and
    /// must never exist on one side only.
    /// </summary>
    public ImmutableArray<StatusEffectInstance> Statuses { get; init; } = [];

    /// <summary>
    /// Every surrender offer ever made in the encounter, pending or resolved. Resolved offers are retained
    /// rather than removed so a negotiation's whole history is in the authoritative snapshot.
    /// </summary>
    public ImmutableArray<SurrenderOffer> SurrenderOffers { get; init; } = [];

    /// <summary>The durable record of each accepted surrender, in the order they were accepted.</summary>
    public ImmutableArray<SurrenderAgreement> SurrenderAgreements { get; init; } = [];

    /// <summary>
    /// Every surrender DEMAND ever made — a winner telling an opponent to yield — pending or resolved.
    /// Termless and pressure-only (see <see cref="SurrenderDemand"/>); retained like offers so the whole
    /// negotiation history is in the authoritative snapshot.
    /// </summary>
    public ImmutableArray<SurrenderDemand> SurrenderDemands { get; init; } = [];

    /// <summary>
    /// Every intimidation attempt made in the encounter, successful or not, in the order they were made.
    /// Authoritative state rather than a trace-only record, because the one-attempt-per-pair rule is enforced
    /// from it: a failed threat is spent exactly as surely as a successful one.
    /// </summary>
    public ImmutableArray<IntimidationAttempt> IntimidationAttempts { get; init; } = [];

    public Character? FindById(string id) =>
        Characters.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a character by id or by name, case-insensitively. The Dungeon Master refers to
    /// characters by name, so the engine must accept both and reject anything it cannot resolve.
    /// </summary>
    public Character? Resolve(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Characters.FirstOrDefault(c => string.Equals(c.Id, needle, StringComparison.OrdinalIgnoreCase))
            ?? Characters.FirstOrDefault(c => string.Equals(c.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    public Character RequireById(string id) =>
        FindById(id) ?? throw new InvalidOperationException($"No character with id '{id}' exists in the current state.");

    /// <summary>The distinct teams present in the encounter, in first-appearance order.</summary>
    public IReadOnlyList<string> Teams()
    {
        var seen = new List<string>();
        foreach (var character in Characters)
        {
            if (!seen.Contains(character.Team, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(character.Team);
            }
        }

        return seen;
    }

    /// <summary>Living members of a team.</summary>
    public IEnumerable<Character> LivingOnTeam(string team) =>
        Characters.Where(c => c.IsAlive && string.Equals(c.Team, team, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Active members of a team — those still fighting. This, not the living count, is what the v0.5
    /// terminal condition is evaluated over: a team with only surrendered or escaped members has left the
    /// fight even though those members are alive.
    /// </summary>
    public IEnumerable<Character> ActiveOnTeam(string team) =>
        Characters.Where(c => c.CanAct && string.Equals(c.Team, team, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everyone still physically present — active or surrendered — and therefore reachable by public events.</summary>
    public IEnumerable<Character> PresentCharacters() => Characters.Where(c => c.IsPresent);

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the character of the same id.</summary>
    public GameState WithCharacter(Character updated)
    {
        for (var index = 0; index < Characters.Length; index++)
        {
            if (string.Equals(Characters[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Characters = Characters.SetItem(index, updated) };
            }
        }

        throw new InvalidOperationException($"No character with id '{updated.Id}' exists in the current state.");
    }

    /// <summary>The world objects present in the room.</summary>
    public ImmutableArray<WorldObject> Objects => Room.Objects;

    /// <summary>
    /// Resolves a world object by id or by name, case-insensitively, reporting ambiguity rather than
    /// guessing. An exact id match always wins and is never ambiguous; failing that, a name is matched,
    /// and a name shared by two or more objects resolves to nothing with <c>Ambiguous</c> set so the
    /// engine can refuse it. This mirrors how characters are resolved: the engine never silently picks
    /// one of several equally valid objects.
    /// </summary>
    public ObjectResolution ResolveObject(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return new ObjectResolution(null, Ambiguous: false);
        }

        var needle = idOrName.Trim();

        var byId = Room.Objects.FirstOrDefault(o => string.Equals(o.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return new ObjectResolution(byId, Ambiguous: false);
        }

        var byName = Room.Objects
            .Where(o => string.Equals(o.Name, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            0 => new ObjectResolution(null, Ambiguous: false),
            1 => new ObjectResolution(byName[0], Ambiguous: false),
            _ => new ObjectResolution(null, Ambiguous: true)
        };
    }

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the container of the same id in the room.</summary>
    public GameState WithContainer(Container updated)
    {
        var objects = Room.Objects;
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index] is Container existing &&
                string.Equals(existing.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Room = Room with { Objects = objects.SetItem(index, updated) } };
            }
        }

        throw new InvalidOperationException($"No container with id '{updated.Id}' exists in the current room.");
    }

    /// <summary>The exits out of the room.</summary>
    public ImmutableArray<EncounterExit> Exits => Room.Exits;

    /// <summary>
    /// Resolves an exit by id or name, case-insensitively, reporting ambiguity rather than guessing — the
    /// same discipline the engine applies to characters and objects. An exact id match always wins; failing
    /// that a name is matched, and a name shared by two or more exits resolves to nothing with
    /// <c>Ambiguous</c> set so the engine can refuse it.
    /// </summary>
    public ExitResolution ResolveExit(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return new ExitResolution(null, Ambiguous: false);
        }

        var needle = idOrName.Trim();

        var byId = Room.Exits.FirstOrDefault(e => string.Equals(e.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return new ExitResolution(byId, Ambiguous: false);
        }

        var byName = Room.Exits
            .Where(e => string.Equals(e.Name, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            0 => new ExitResolution(null, Ambiguous: false),
            1 => new ExitResolution(byName[0], Ambiguous: false),
            _ => new ExitResolution(null, Ambiguous: true)
        };
    }

    // -----------------------------------------------------------------------------------------------
    // Status effects
    // -----------------------------------------------------------------------------------------------

    /// <summary>Every live status on a character, in application order.</summary>
    public IEnumerable<StatusEffectInstance> StatusesOn(string characterId) =>
        Statuses.Where(s => string.Equals(s.TargetCharacterId, characterId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one live status of a kind on a character, or null. No supported status stacks with itself.</summary>
    public StatusEffectInstance? StatusOn(string characterId, StatusEffectKind kind) =>
        StatusesOn(characterId).FirstOrDefault(s => s.Kind == kind);

    /// <summary>Every live status sourced from a character, whoever it is on.</summary>
    public IEnumerable<StatusEffectInstance> StatusesFrom(string characterId) =>
        Statuses.Where(s => string.Equals(s.SourceCharacterId, characterId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a new state with the status added. Callers check for an existing one of the same kind first.</summary>
    public GameState WithStatus(StatusEffectInstance status) =>
        this with { Statuses = Statuses.Add(status) };

    /// <summary>Returns a new state with every status whose id appears in <paramref name="statusIds"/> removed.</summary>
    public GameState WithoutStatuses(IEnumerable<string> statusIds)
    {
        var ids = statusIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Count == 0
            ? this
            : this with { Statuses = [.. Statuses.Where(s => !ids.Contains(s.Id))] };
    }

    // -----------------------------------------------------------------------------------------------
    // Surrender offers and agreements
    // -----------------------------------------------------------------------------------------------

    /// <summary>Finds a surrender offer by its stable id, whatever state it is in.</summary>
    public SurrenderOffer? FindOffer(string offerId) =>
        string.IsNullOrWhiteSpace(offerId)
            ? null
            : SurrenderOffers.FirstOrDefault(o => string.Equals(o.Id, offerId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every offer still open for acceptance.</summary>
    public IEnumerable<SurrenderOffer> PendingOffers() => SurrenderOffers.Where(o => o.IsPending);

    /// <summary>The offerer's one pending offer, or null. Only one pending offer may exist per offerer.</summary>
    public SurrenderOffer? PendingOfferFrom(string offererId) =>
        PendingOffers().FirstOrDefault(o => string.Equals(o.OffererId, offererId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every pending offer awaiting a decision from this recipient.</summary>
    public IEnumerable<SurrenderOffer> PendingOffersTo(string recipientId) =>
        PendingOffers().Where(o => string.Equals(o.RecipientId, recipientId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a new state with the offer appended.</summary>
    public GameState WithOffer(SurrenderOffer offer) =>
        this with { SurrenderOffers = SurrenderOffers.Add(offer) };

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the offer of the same id.</summary>
    public GameState WithUpdatedOffer(SurrenderOffer updated)
    {
        for (var index = 0; index < SurrenderOffers.Length; index++)
        {
            if (string.Equals(SurrenderOffers[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { SurrenderOffers = SurrenderOffers.SetItem(index, updated) };
            }
        }

        throw new InvalidOperationException($"No surrender offer with id '{updated.Id}' exists in the current state.");
    }

    /// <summary>Returns a new state with the agreement appended.</summary>
    public GameState WithAgreement(SurrenderAgreement agreement) =>
        this with { SurrenderAgreements = SurrenderAgreements.Add(agreement) };

    // -----------------------------------------------------------------------------------------------
    // Surrender demands (pressure-only ultimatums)
    // -----------------------------------------------------------------------------------------------

    /// <summary>The pending demand with this id, or null.</summary>
    public SurrenderDemand? FindDemand(string demandId) =>
        string.IsNullOrWhiteSpace(demandId)
            ? null
            : SurrenderDemands.FirstOrDefault(d => string.Equals(d.Id, demandId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every demand still awaiting the target's answer.</summary>
    public IEnumerable<SurrenderDemand> PendingDemands() => SurrenderDemands.Where(d => d.IsPending);

    /// <summary>The demander's one pending demand, or null. Only one pending demand may exist per demander.</summary>
    public SurrenderDemand? PendingDemandFrom(string demanderId) =>
        PendingDemands().FirstOrDefault(d => string.Equals(d.DemanderId, demanderId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every pending demand made against this target — the ultimatums surfaced to them on their turn.</summary>
    public IEnumerable<SurrenderDemand> PendingDemandsAgainst(string targetId) =>
        PendingDemands().Where(d => string.Equals(d.TargetId, targetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a new state with the demand appended.</summary>
    public GameState WithDemand(SurrenderDemand demand) =>
        this with { SurrenderDemands = SurrenderDemands.Add(demand) };

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the demand of the same id.</summary>
    public GameState WithUpdatedDemand(SurrenderDemand updated)
    {
        for (var index = 0; index < SurrenderDemands.Length; index++)
        {
            if (string.Equals(SurrenderDemands[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { SurrenderDemands = SurrenderDemands.SetItem(index, updated) };
            }
        }

        throw new InvalidOperationException($"No surrender demand with id '{updated.Id}' exists in the current state.");
    }

    // -----------------------------------------------------------------------------------------------
    // Morale
    // -----------------------------------------------------------------------------------------------

    /// <summary>Returns a new state with the intimidation attempt appended.</summary>
    public GameState WithIntimidationAttempt(IntimidationAttempt attempt) =>
        this with { IntimidationAttempts = IntimidationAttempts.Add(attempt) };

    /// <summary>True when this actor has already tried to frighten this target once in the encounter.</summary>
    public bool HasAttemptedIntimidation(string actorId, string targetId) =>
        IntimidationAttempts.Any(a =>
            string.Equals(a.ActorId, actorId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.TargetId, targetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The active opponents this actor could still try to frighten. Used to withdraw the affordance once
    /// there is nobody left to try it on, so a model is never shown an action it can only be refused for.
    /// </summary>
    public IEnumerable<Character> RemainingIntimidationTargets(string actorId)
    {
        var actor = FindById(actorId);
        return actor is null
            ? []
            : Characters.Where(c =>
                c.IsCombatTarget &&
                !string.Equals(c.Id, actorId, StringComparison.OrdinalIgnoreCase) &&
                !actor.IsAllyOf(c) &&
                !HasAttemptedIntimidation(actorId, c.Id));
    }

    /// <summary>
    /// The active allies this actor could steady — those with fear left to shed. An ally already unafraid is
    /// excluded, because steadying them would spend a whole turn to change nothing.
    /// </summary>
    public IEnumerable<Character> RemainingSteadyTargets(string actorId)
    {
        var actor = FindById(actorId);
        return actor is null
            ? []
            : Characters.Where(c =>
                c.CanAct && c.IsPresent &&
                !string.Equals(c.Id, actorId, StringComparison.OrdinalIgnoreCase) &&
                actor.IsAllyOf(c) &&
                c.Fear > FearRules.Minimum);
    }

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the cover object of the same id in the room.</summary>
    public GameState WithCover(CoverObject updated)
    {
        var objects = Room.Objects;
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index] is CoverObject existing &&
                string.Equals(existing.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Room = Room with { Objects = objects.SetItem(index, updated) } };
            }
        }

        throw new InvalidOperationException($"No cover object with id '{updated.Id}' exists in the current room.");
    }

    /// <summary>
    /// The cover object a character currently occupies, or null when they occupy none. There is at most one,
    /// since occupying cover is exclusive.
    /// </summary>
    public CoverObject? CoverOccupiedBy(string characterId) =>
        Room.Objects.OfType<CoverObject>()
            .FirstOrDefault(c => string.Equals(c.CurrentOccupantId, characterId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the exit of the same id in the room.</summary>
    public GameState WithExit(EncounterExit updated)
    {
        var exits = Room.Exits;
        for (var index = 0; index < exits.Length; index++)
        {
            if (string.Equals(exits[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Room = Room with { Exits = exits.SetItem(index, updated) } };
            }
        }

        throw new InvalidOperationException($"No exit with id '{updated.Id}' exists in the current room.");
    }
}

/// <summary>
/// The outcome of resolving a world-object reference: the object it named (if any), and whether the
/// reference was ambiguous. Ambiguity is distinct from "not found" because it must be refused with a
/// different explanation — asking the character to say which one they mean rather than saying there is
/// no such object.
/// </summary>
public readonly record struct ObjectResolution(WorldObject? Object, bool Ambiguous)
{
    public bool Found => Object is not null;
}

/// <summary>
/// The outcome of resolving an exit reference: the exit it named (if any), and whether the reference was
/// ambiguous. As with <see cref="ObjectResolution"/>, ambiguity is distinct from "not found" so it can be
/// refused with a different explanation.
/// </summary>
public readonly record struct ExitResolution(EncounterExit? Exit, bool Ambiguous)
{
    public bool Found => Exit is not null;
}

/// <summary>
/// One recorded attempt to frighten an opponent, with its whole randomness record.
/// </summary>
/// <remarks>
/// Kept in the authoritative state, not only in the trace, because the rule that an actor may try this once
/// per target per encounter has to be enforceable from the snapshot alone. A failure is recorded exactly like
/// a success for the same reason: the chance is spent either way, which is what stops the action becoming a
/// free re-roll a model can grind at.
/// </remarks>
public sealed record IntimidationAttempt
{
    public required string Id { get; init; }

    public required string ActorId { get; init; }

    public required string TargetId { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    /// <summary>The base chance out of 100 before any state-derived modifier.</summary>
    public required int BaseChance { get; init; }

    /// <summary>The effective chance after modifiers and clamping — what the raw roll was compared against.</summary>
    public required int EffectiveChance { get; init; }

    public required int Roll { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The public-channel id of the threat that carried it. The words are roleplay and change no odds.</summary>
    public int? AssociatedSpeechEventId { get; init; }
}
