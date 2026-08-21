using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Web;

/// <summary>
/// One message pushed to a live viewer over SSE. <see cref="Type"/> tells the client how to render the
/// <see cref="Payload"/> — text events append to the transcript, structured events update the character
/// cards and the combat ticker. The set is deliberately curated: the raw model requests/responses stay in
/// the file trace and are never streamed.
/// </summary>
public sealed record UiEvent(string Type, object? Payload)
{
    // Text-stream events (from the game console).
    public static UiEvent RunHeader(string runId, string scenario) => new("runHeader", new { runId, scenario });
    public static UiEvent Round(int round) => new("round", new { round });
    public static UiEvent Narration(string text) => new("narration", new { text });
    public static UiEvent PrivateObservation(string character, string text) => new("privateObservation", new { character, text });
    public static UiEvent Asks(string character, string text) => new("asks", new { character, text });
    public static UiEvent Acts(string character, string text) => new("acts", new { character, text });
    public static UiEvent Speaks(string character, string text) => new("speaks", new { character, text });
    public static UiEvent Passes(string character, string text) => new("passes", new { character, text });
    public static UiEvent Refused(string character, string text) => new("refused", new { character, text });
    public static UiEvent Notice(string text) => new("notice", new { text });
    public static UiEvent Ending(string text) => new("ending", new { text });

    // Structured events (from the trace) that drive the cards and combat view.
    public static UiEvent State(StateDto state) => new("state", state);
    public static UiEvent TurnStarted(string character) => new("turnStarted", new { character });
    public static UiEvent Attack(AttackDto attack) => new("attack", attack);

    // A character leaving active combat other than by death, so the transcript can read it distinctly.
    public static UiEvent Surrendered(string character) => new("surrendered", new { character });
    public static UiEvent Escaped(string character, string exit) => new("escaped", new { character, exit });
    public static UiEvent ExitOpened(string character, string exit) => new("exitOpened", new { character, exit });

    // Inventory transfers (v0.6), so the transcript can read each distinctly and the item movement is visible.
    public static UiEvent Gave(string character, string recipient, string item) => new("gave", new { character, recipient, item });
    public static UiEvent Dropped(string character, string item) => new("dropped", new { character, item });
    public static UiEvent StoleAttempt(string character, string target, string item, bool succeeded) =>
        new("stole", new { character, target, item, succeeded });

    // Negotiated surrender, abilities and statuses (v0.7). Each reads distinctly in the transcript, because
    // conflating an offer with an accepted surrender is exactly the thing this release exists to separate.
    public static UiEvent SurrenderOffered(string offerId, string character, string recipient, string terms) =>
        new("surrenderOffered", new { offerId, character, recipient, terms });

    public static UiEvent SurrenderOfferSettled(string offerId, string character, string recipient, string state, string cause) =>
        new("surrenderOfferSettled", new { offerId, character, recipient, state, cause });

    public static UiEvent SurrenderAccepted(
        string character, string offerer, IReadOnlyList<string> tribute, string? weapon, string? weaponDisposition) =>
        new("surrenderAccepted", new { character, offerer, tribute, weapon, weaponDisposition });

    public static UiEvent AbilityUsed(
        string character, string ability, string? target, string result, int? remainingUses, int? healing) =>
        new("abilityUsed", new { character, ability, target, result, remainingUses, healing });

    public static UiEvent StatusChanged(
        string transition, string kind, string target, string source, int modifier, string cause) =>
        new("statusChanged", new { transition, kind, target, source, modifier, cause });

    public static UiEvent AttackRedirected(string attacker, string intendedTarget, string guardian) =>
        new("attackRedirected", new { attacker, intendedTarget, guardian });

    public static UiEvent DefendReduced(string target, int reduction) => new("defendReduced", new { target, reduction });

    // Morale and combat volatility (v0.8). Fear changes, threshold crossings, threats and reassurance each
    // read distinctly: a point of fear moving is not the same event as a character breaking, and a threat
    // that told is not the same as one that did not.
    public static UiEvent FearChanged(
        string character, int before, int after, int delta, string cause, string causeDetail,
        string transition, bool absorbed) =>
        new("fearChanged", new { character, before, after, delta, cause, causeDetail, transition, absorbed });

    public static UiEvent Intimidation(
        string character, string target, bool succeeded, int baseChance, int effectiveChance, int roll,
        IReadOnlyList<string> modifiers, string? speech, int targetFearBefore, int targetFearAfter) =>
        new("intimidation", new
        {
            character, target, succeeded, baseChance, effectiveChance, roll, modifiers, speech,
            targetFearBefore, targetFearAfter
        });

    public static UiEvent AllySteadied(
        string character, string target, int targetFearBefore, int targetFearAfter, bool noEffect, string? speech) =>
        new("allySteadied", new { character, target, targetFearBefore, targetFearAfter, noEffect, speech });

    public static UiEvent Completed(
        string terminalCondition, string? outcome, IReadOnlyList<string> winningTeams, IReadOnlyList<string> survivors) =>
        new("completed", new { terminalCondition, outcome, winningTeams, survivors });

    // Environmental cover (v0.9). Occupancy changes, deliberate object damage, and cover taking a blow each
    // read distinctly, the same discipline applied to every other v0.7/v0.8 addition above.
    public static UiEvent CoverOccupancyChanged(string character, string cover, string transition) =>
        new("coverOccupancyChanged", new { character, cover, transition });

    public static UiEvent CoverDamaged(string cover, int durabilityBefore, int durabilityAfter, bool destroyed, string cause) =>
        new("coverDamaged", new { cover, durabilityBefore, durabilityAfter, destroyed, cause });

    public static UiEvent EnvironmentalObjectDamaged(
        string character, string objectName, string weapon, int damage, int durabilityBefore, int durabilityAfter, bool destroyed) =>
        new("environmentalObjectDamaged", new { character, objectName, weapon, damage, durabilityBefore, durabilityAfter, destroyed });
}

/// <summary>An ability on a character card: its name and what is left of it.</summary>
public sealed record AbilityDto(string Id, string Name, string Category, int? RemainingUses, int? MaxUses);

/// <summary>
/// A live status effect as the cards render it. The partner is the other half of a linked relationship, so a
/// guarding pair reads as a pair rather than as two unrelated badges.
/// </summary>
public sealed record StatusDto(
    string Id,
    string Kind,
    string Source,
    string Target,
    int Modifier,
    string Description,
    string? PartnerName);

/// <summary>
/// A surrender offer as the negotiation panel renders it. Offers and agreements are deliberately separate
/// DTOs: an offer has moved nothing and protects nobody, and the UI must never let the two look alike.
/// </summary>
public sealed record SurrenderOfferDto(
    string Id,
    string Offerer,
    string Recipient,
    IReadOnlyList<string> OfferedItems,
    bool ForfeitWeapon,
    string? WeaponName,
    string Terms,
    string State,
    string? ResolutionCause,
    int CreatedRound);

/// <summary>An accepted surrender as the negotiation panel renders it: what actually changed hands.</summary>
public sealed record SurrenderAgreementDto(
    string Id,
    string Offerer,
    string AcceptedBy,
    IReadOnlyList<string> TransferredItems,
    string? ForfeitedWeapon,
    int AcceptedRound);

/// <summary>A character as the cards render it.</summary>
public sealed record CharacterDto(
    string Id,
    string Name,
    string Team,
    string Role,
    int Health,
    int MaxHealth,
    int Armour,
    string? Weapon,
    IReadOnlyList<string> Inventory,
    IReadOnlyList<string> Injuries,
    bool Alive,
    string Disposition,
    IReadOnlyList<AbilityDto> Abilities,
    IReadOnlyList<StatusDto> Statuses,
    bool Disarmed,
    // Morale is an experiment artefact here, not character knowledge: the observer UI is allowed the exact
    // figure that no opponent in the fiction ever sees. It stays on the card after a character dies, yields
    // or flees, so the final state records the nerve they left the fight with.
    int Fear,
    int MaxFear,
    bool Scared,
    bool Outnumbered);

/// <summary>
/// A room object as the object panel renders it: its name and, for a container, whether it stands open —
/// both plainly visible to anyone in the room. Contents are deliberately absent; those are private
/// knowledge, not a public property of the object.
/// </summary>
public sealed record ObjectDto(string Id, string Name, bool IsContainer, bool IsOpen);

/// <summary>An exit as the room-state panel renders it: its name and whether it stands open — both public.</summary>
public sealed record ExitDto(string Id, string Name, bool IsOpen);

/// <summary>
/// An environmental cover object as the room-state panel renders it (v0.9). Every field here is public —
/// unlike a container's contents, nothing about cover is hidden from anyone in the room.
/// </summary>
public sealed record CoverDto(
    string Id,
    string Name,
    string State,
    int Capacity,
    string? Occupant,
    int MaximumDurability,
    int CurrentDurability,
    int HitChanceModifier,
    int Armour);

/// <summary>
/// A snapshot of every character, room object, exit and ground item, sent whenever the authoritative state
/// advances. Ground items (things dropped on the floor) are surfaced separately from ordinary containers,
/// because — unlike a container's private contents — they lie in plain sight of everyone in the room.
/// </summary>
public sealed record StateDto(
    int Version,
    IReadOnlyList<CharacterDto> Characters,
    IReadOnlyList<ObjectDto> Objects,
    IReadOnlyList<ExitDto> Exits,
    IReadOnlyList<string> Ground,
    IReadOnlyList<SurrenderOfferDto> PendingOffers,
    IReadOnlyList<SurrenderOfferDto> SettledOffers,
    IReadOnlyList<SurrenderAgreementDto> Agreements,
    IReadOnlyList<CoverDto> Cover)
{
    public static StateDto From(GameState state) => new(
        state.Version,
        [.. state.Characters.Select(c => new CharacterDto(
            c.Id, c.Name, c.Team, c.Role.ToString(), c.Health, c.MaxHealth, c.Armour,
            c.Weapon?.Name,
            [.. c.Inventory.Select(i => i.DisplayName)],
            [.. c.Injuries.Select(i => i.Description)],
            c.IsAlive,
            c.Disposition.ToString(),
            [.. c.Abilities.Select(a => new AbilityDto(a.AbilityId, a.Name, a.Category.ToString(), a.RemainingUses, a.MaxUses))],
            [.. state.StatusesOn(c.Id).Select(s => ToStatusDto(state, s))],
            c.IsDisarmed,
            c.Fear,
            FearRules.Maximum,
            c.IsScared,
            c.IsOutnumbered))],
        // The floor is surfaced separately as Ground, so exclude it from the ordinary object list.
        [.. state.Room.Objects.Where(o => o is not Container { IsGround: true } and not CoverObject).Select(o => new ObjectDto(
            o.Id, o.Name, o is Container, o is Container { IsOpen: true }))],
        [.. state.Room.Exits.Select(e => new ExitDto(e.Id, e.Name, e.IsOpen))],
        [.. state.Room.Objects.OfType<Container>().Where(c => c.IsGround).SelectMany(c => c.Contents).Select(i => i.DisplayName)],
        [.. state.PendingOffers().Select(o => ToOfferDto(state, o))],
        [.. state.SurrenderOffers.Where(o => !o.IsPending).Select(o => ToOfferDto(state, o))],
        [.. state.SurrenderAgreements.Select(a => ToAgreementDto(state, a))],
        [.. state.Room.Objects.OfType<CoverObject>().Select(c => new CoverDto(
            c.Id, c.Name, c.State.ToString(), c.Capacity,
            c.CurrentOccupantId is null ? null : NameOf(state, c.CurrentOccupantId),
            c.MaximumDurability, c.CurrentDurability, c.HitChanceModifier, c.Armour))]);

    private static StatusDto ToStatusDto(GameState state, StatusEffectInstance status)
    {
        string? partner = null;
        if (status.RelationshipId is not null)
        {
            var other = state.Statuses.FirstOrDefault(s =>
                s.RelationshipId == status.RelationshipId && s.Id != status.Id);
            partner = other is null ? null : NameOf(state, other.TargetCharacterId);
        }

        return new StatusDto(
            status.Id,
            status.Kind.ToString(),
            NameOf(state, status.SourceCharacterId),
            NameOf(state, status.TargetCharacterId),
            status.Modifier,
            status.Describe(),
            partner);
    }

    private static SurrenderOfferDto ToOfferDto(GameState state, SurrenderOffer offer)
    {
        var items = offer.OfferedItemIds.Select(id => ItemName(state, id)).ToList();
        var weapon = offer.ForfeitWeapon ? state.FindById(offer.OffererId)?.Weapon?.Name : null;
        var terms = new List<string>(items);
        if (offer.ForfeitWeapon)
        {
            terms.Add(weapon ?? "their weapon");
        }

        return new SurrenderOfferDto(
            offer.Id,
            NameOf(state, offer.OffererId),
            NameOf(state, offer.RecipientId),
            items,
            offer.ForfeitWeapon,
            weapon,
            terms.Count == 0 ? "nothing" : string.Join(" + ", terms),
            offer.State.ToString(),
            offer.ResolutionCause,
            offer.CreatedRound);
    }

    private static SurrenderAgreementDto ToAgreementDto(GameState state, SurrenderAgreement agreement) => new(
        agreement.Id,
        NameOf(state, agreement.OffererId),
        NameOf(state, agreement.AcceptedById),
        [.. agreement.TransferredItemIds.Select(id => ItemName(state, id))],
        agreement.ForfeitedWeaponId is null ? null : ItemName(state, agreement.ForfeitedWeaponId),
        agreement.AcceptedRound);

    private static string NameOf(GameState state, string characterId) =>
        state.FindById(characterId)?.Name ?? characterId;

    /// <summary>An item's display name wherever it now sits, so a record of a moved thing never renders as an id.</summary>
    private static string ItemName(GameState state, string itemId)
    {
        foreach (var character in state.Characters)
        {
            var carried = character.Inventory.FirstOrDefault(i => i.Id == itemId);
            if (carried is not null)
            {
                return carried.Name;
            }

            if (character.Weapon?.Id == itemId)
            {
                return character.Weapon.Name;
            }
        }

        foreach (var container in state.Room.Objects.OfType<Container>())
        {
            var inside = container.Contents.FirstOrDefault(i => i.Id == itemId);
            if (inside is not null)
            {
                return inside.Name;
            }
        }

        return itemId;
    }
}

/// <summary>One resolved attack, for the combat ticker and card damage flashes.</summary>
/// <remarks>
/// <see cref="Quality"/> is the authoritative word from the single quality draw. <see cref="Glancing"/> is
/// kept alongside it so a viewer rendering an older run — one recorded before critical hits existed — still
/// reads correctly rather than silently showing every blow as solid.
/// </remarks>
public sealed record AttackDto(
    string Attacker,
    string Target,
    bool Hit,
    bool Glancing,
    int Damage,
    int TargetHealth,
    int TargetMaxHealth,
    bool Died,
    string Quality = "Solid",
    bool Critical = false,
    // Environmental cover (v0.9). CoverId is null for an ordinary attack against an uncovered target;
    // Intercepted is true only when the cover — not the roll alone — is what saved the target.
    string? CoverId = null,
    string? CoverName = null,
    bool Intercepted = false);
