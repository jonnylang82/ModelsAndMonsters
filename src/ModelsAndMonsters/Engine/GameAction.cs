namespace ModelsAndMonsters.Engine;

/// <summary>
/// A structured, state-changing request against the game engine.
/// </summary>
/// <remarks>
/// Only the Dungeon Master produces these, by translating a character's natural-language intent into
/// one of the deliberately small number of supported actions. Character agents never see this type.
/// References are free-form strings (id or name) because the DM speaks in names; resolving and
/// rejecting them is the engine's job.
/// </remarks>
public abstract record GameAction
{
    public abstract string ActionType { get; }

    /// <summary>Short human-readable form used in traces and console diagnostics.</summary>
    public abstract string Describe();
}

/// <summary>The single combat action supported by v0.1. An accepted attack always hits.</summary>
public sealed record AttackCharacterAction(string AttackerRef, string TargetRef, string WeaponRef) : GameAction
{
    public override string ActionType => "attack_character";

    public override string Describe() => $"AttackCharacter(attacker={AttackerRef}, target={TargetRef}, weapon={WeaponRef})";
}

/// <summary>Consumes an inventory item. v0.1 only understands healing items used on oneself.</summary>
public sealed record UseItemAction(string ActorRef, string ItemRef, string? TargetRef = null) : GameAction
{
    public override string ActionType => "use_item";

    public override string Describe() => $"UseItem(actor={ActorRef}, item={ItemRef}, target={TargetRef ?? "self"})";
}

/// <summary>Opens a closed container in the actor's room. Uses no randomness.</summary>
/// <remarks>
/// Opening reveals the contents only to the opener, not to the whole room: the engine reports the contents
/// on the outcome, and the orchestration layer delivers them privately. The mechanical change is only the
/// lid.
/// </remarks>
public sealed record OpenContainerAction(string ActorRef, string ContainerRef) : GameAction
{
    public override string ActionType => "open_container";

    public override string Describe() => $"OpenContainer(actor={ActorRef}, container={ContainerRef})";
}

/// <summary>
/// Examines an object in the room closely, discovering information that cannot be read from the general
/// room description. Uses no randomness and never mutates the object: it consumes the actor's turn and
/// yields a private observation to the inspector. A character requests it through natural-language intent
/// ("I wipe the grime away and study the markings"); only the Dungeon Master ever names this action.
/// </summary>
public sealed record InspectObjectAction(string ActorRef, string ObjectRef) : GameAction
{
    public override string ActionType => "inspect_object";

    public override string Describe() => $"InspectObject(actor={ActorRef}, object={ObjectRef})";
}

/// <summary>
/// Transfers one item from an open container into the actor's inventory. Uses no randomness. The
/// removal and the addition are one atomic state change, so two characters can never both acquire it.
/// </summary>
public sealed record TakeItemAction(string ActorRef, string ContainerRef, string ItemRef) : GameAction
{
    public override string ActionType => "take_item";

    public override string Describe() => $"TakeItem(actor={ActorRef}, container={ContainerRef}, item={ItemRef})";
}

/// <summary>
/// Opens a closed exit so it can be passed through. Uses no randomness. Opening never moves anyone — it
/// only changes the door from shut to open — and it is a separate act from escaping through it.
/// </summary>
public sealed record OpenExitAction(string ActorRef, string ExitRef) : GameAction
{
    public override string ActionType => "open_exit";

    public override string Describe() => $"OpenExit(actor={ActorRef}, exit={ExitRef})";
}

/// <summary>
/// Passes a character through an already-open exit, removing them from the encounter. Uses no randomness.
/// There are no escape rolls, no opportunity attacks and no pursuit: an open exit is simply walked through.
/// </summary>
public sealed record EscapeEncounterAction(string ActorRef, string ExitRef) : GameAction
{
    public override string ActionType => "escape_encounter";

    public override string Describe() => $"EscapeEncounter(actor={ActorRef}, exit={ExitRef})";
}

/// <summary>
/// The current actor offering to surrender to one named opponent on concrete, enforceable terms: ordinary
/// inventory items handed over, the weapon in their hand given up, or both.
/// </summary>
/// <remarks>
/// Making the offer transfers nothing, disarms nobody, changes no disposition and grants no protection — the
/// offerer stays active and targetable while it is pending. It only creates the pending
/// <see cref="Domain.SurrenderOffer"/> the named recipient may accept on their own turn. It consumes the
/// offerer's turn, and uses no randomness. The plea or threat that accompanies it is ordinary speech, carried
/// only as <paramref name="AssociatedSpeechEventId"/>; speech is never contract state.
/// </remarks>
public sealed record OfferSurrenderAction(
    string OffererRef,
    string RecipientRef,
    IReadOnlyList<string> OfferedItemRefs,
    bool ForfeitWeapon,
    int? AssociatedSpeechEventId = null) : GameAction
{
    public override string ActionType => "offer_surrender";

    public override string Describe() =>
        $"OfferSurrender(offerer={OffererRef}, recipient={RecipientRef}, " +
        $"items=[{string.Join(", ", OfferedItemRefs)}], forfeitWeapon={ForfeitWeapon})";
}

/// <summary>
/// The current actor accepting a pending surrender offer addressed to them. Uses no randomness. Acceptance is
/// all-or-nothing: every promised item and the promised weapon move in one atomic state change, or nothing
/// does and the offer is refused or invalidated. It consumes the accepter's turn.
/// </summary>
public sealed record AcceptSurrenderAction(string RecipientRef, string OfferRef) : GameAction
{
    public override string ActionType => "accept_surrender";

    public override string Describe() => $"AcceptSurrender(recipient={RecipientRef}, offer={OfferRef})";
}

/// <summary>
/// The current actor using one of their own abilities, optionally on a target. One generic action covers every
/// ability; the engine dispatches on the ability's <see cref="Domain.AbilityEffectKind"/> to a concrete
/// handler. Whether it consults randomness depends on the ability — only an ability that performs a weapon
/// attack draws, and then only the ordinary attack draws.
/// </summary>
public sealed record UseAbilityAction(string ActorRef, string AbilityRef, string? TargetRef = null) : GameAction
{
    public override string ActionType => "use_ability";

    public override string Describe() => $"UseAbility(actor={ActorRef}, ability={AbilityRef}, target={TargetRef ?? "none"})";
}

/// <summary>
/// The current actor bracing behind their guard instead of striking. Available to every active character, as
/// often as they like; it consumes the turn, uses no randomness, and applies
/// <see cref="Domain.StatusEffectKind.Defending"/>.
/// </summary>
public sealed record DefendAction(string ActorRef) : GameAction
{
    public override string ActionType => "defend";

    public override string Describe() => $"Defend(actor={ActorRef})";
}

/// <summary>
/// The current actor handing one of its ordinary inventory items to another character present in the room.
/// Uses no randomness. The item moves atomically from giver to recipient; recipient consent is not modelled.
/// The giver is always the current actor — the engine never moves an item on behalf of a different giver.
/// </summary>
public sealed record GiveItemAction(string GiverRef, string RecipientRef, string ItemRef) : GameAction
{
    public override string ActionType => "give_item";

    public override string Describe() => $"GiveItem(giver={GiverRef}, recipient={RecipientRef}, item={ItemRef})";
}

/// <summary>
/// The current actor dropping one of its ordinary inventory items onto the room's ground-loot location.
/// Uses no randomness. The item keeps its stable id and can subsequently be taken through <c>take_item</c>.
/// </summary>
public sealed record DropItemAction(string ActorRef, string ItemRef) : GameAction
{
    public override string ActionType => "drop_item";

    public override string Describe() => $"DropItem(actor={ActorRef}, item={ItemRef})";
}

/// <summary>
/// The current actor attempting to steal one ordinary inventory item from another character. This is the one
/// inventory transfer that consults randomness: exactly one seeded draw against a base theft chance decides
/// success. The attempt is always publicly noticed whether it succeeds or fails, and it consumes the turn
/// either way. Equipped weapons cannot be stolen.
/// </summary>
public sealed record StealItemAction(string ThiefRef, string TargetRef, string ItemRef) : GameAction
{
    public override string ActionType => "steal_item";

    public override string Describe() => $"StealItem(thief={ThiefRef}, target={TargetRef}, item={ItemRef})";
}

/// <summary>
/// The current actor trying to frighten one active opponent with an open threat. Exactly one seeded draw
/// decides it, against a base chance modified only by the authoritative state — never by what was said.
/// </summary>
/// <remarks>
/// The threat itself is ordinary structured speech, already delivered on the public channel this turn and
/// carried here only as <paramref name="AssociatedSpeechEventId"/>; <paramref name="SpeechAddressedToId"/> is
/// the addressee the speaker declared, when they declared one, so a threat aimed at somebody else can be
/// refused rather than quietly re-pointed. Success raises the target's fear by one and does nothing else:
/// it never forces a surrender, an escape, a hand-over, a disarm or a skipped turn.
/// </remarks>
public sealed record IntimidateCharacterAction(
    string ActorRef,
    string TargetRef,
    int? AssociatedSpeechEventId = null,
    string? SpeechAddressedToId = null) : GameAction
{
    public override string ActionType => "intimidate_character";

    public override string Describe() => $"IntimidateCharacter(actor={ActorRef}, target={TargetRef})";
}

/// <summary>
/// The current actor spending their whole turn steadying one active ally, reducing that ally's fear by one.
/// Uses no randomness at all: reassurance is not a gamble in v0.8.
/// </summary>
/// <remarks>
/// As with intimidation, the words of encouragement are ordinary structured speech carried only by id. The
/// engine validates that an utterance exists and that its declared addressee, when there is one, is the ally
/// being steadied — never what the words actually say.
/// </remarks>
public sealed record SteadyAllyAction(
    string ActorRef,
    string TargetRef,
    int? AssociatedSpeechEventId = null,
    string? SpeechAddressedToId = null) : GameAction
{
    public override string ActionType => "steady_ally";

    public override string Describe() => $"SteadyAlly(actor={ActorRef}, target={TargetRef})";
}
