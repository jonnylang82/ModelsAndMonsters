using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>Why the authoritative engine refused to apply an otherwise well-formed action.</summary>
public enum EngineRejectionReason
{
    UnknownActor,
    ActorIsDead,
    UnknownTarget,
    TargetIsDead,
    TargetIsSelf,
    ActorHasNoWeapon,
    WeaponNotPossessed,
    ItemNotPossessed,
    ItemHasNoSupportedEffect,
    ItemTargetNotSupported,

    // Focus items that restore a spent ability charge (v0.11).
    NoDepletedAbilityToRestore,

    // Object and container interaction (v0.3).
    UnknownContainer,
    ContainerReferenceAmbiguous,
    ContainerAlreadyOpen,
    ContainerClosed,
    ItemNotInContainer,
    ItemReferenceAmbiguous,

    // Close inspection (v0.4).
    UnknownObject,
    ObjectReferenceAmbiguous,
    NothingToInspect,

    // Non-lethal outcomes and exits (v0.5).
    TargetHasSurrendered,
    TargetHasEscaped,
    ActorNotActive,
    UnknownExit,
    ExitReferenceAmbiguous,
    ExitAlreadyOpen,
    ExitClosed,

    // Inventory transfers (v0.6).
    UnknownRecipient,
    RecipientNotPresent,
    RecipientIsSelf,
    TargetNotPresent,
    EquippedWeaponCannotBeTransferred,

    // Negotiated surrender (v0.7).
    OfferHasNoConcession,
    RecipientIsNotAnOpponent,
    OfferedItemNotOwned,
    OfferedItemNotTransferable,
    OfferedWeaponNotHeld,
    DuplicatePendingOffer,
    UnknownOffer,
    OfferNotAddressedToActor,
    OfferNoLongerPending,
    OffererNotAvailable,
    PromisedAssetNoLongerAvailable,

    // Abilities and statuses (v0.7).
    UnknownAbility,
    AbilityNotHeld,
    AbilityHasNoUsesLeft,
    AbilityTargetRequired,
    AbilityTargetNotAllowed,
    AbilityTargetNotAlly,
    AbilityTargetNotOpponent,
    AbilityTargetIsSelf,
    AbilityTargetNotAvailable,
    TargetAlreadyAtFullHealth,
    AbilityAlreadyActive,
    TargetAlreadyGuarded,

    // Morale, intimidation and reassurance (v0.8).
    TargetIsNotAnOpponent,
    TargetIsNotAnAlly,
    AlreadyAttemptedIntimidation,
    IntimidationRequiresSpeech,
    SpeechAddressedToSomebodyElse,

    // Environmental cover (v0.9).
    UnknownCover,
    CoverReferenceAmbiguous,
    CoverCannotBeUsed,
    AlreadyInThatCover,
    CoverFull,
    NotInCover,
    ObjectCannotBeDamaged,
    ObjectAlreadyDestroyed,
    CannotDamageOwnCover,

    UnsupportedAction
}

/// <summary>
/// The outcome of submitting a <see cref="GameAction"/> to the engine, including full before/after
/// state snapshots. v0.1 state is tiny, so whole snapshots are traced rather than diffs.
/// </summary>
public sealed record EngineResult
{
    public required GameAction Action { get; init; }

    public required bool Accepted { get; init; }

    public EngineRejectionReason? RejectionReason { get; init; }

    /// <summary>Plain explanation suitable for feeding back to the Dungeon Master.</summary>
    public string? RejectionMessage { get; init; }

    public required GameState StateBefore { get; init; }

    /// <summary>Identical to <see cref="StateBefore"/> when the action was rejected.</summary>
    public required GameState StateAfter { get; init; }

    public ActionOutcome? Outcome { get; init; }

    /// <summary>
    /// Every random draw this action made, in order. Empty for actions that consult no randomness
    /// (item use) and for rejected actions (validation happens before any roll). The orchestration
    /// layer traces these so no draw is recorded only as its final effect.
    /// </summary>
    public IReadOnlyList<RngDraw> RngDraws { get; init; } = [];

    /// <summary>
    /// Every status-effect transition this action caused, in order: applied, consumed, expired or removed.
    /// Empty for actions that touch no status. The orchestration layer traces these so a status is never
    /// recorded only as its final effect on a number.
    /// </summary>
    public IReadOnlyList<StatusEvent> StatusEvents { get; init; } = [];

    /// <summary>
    /// Every surrender-offer state transition this action caused — an offer created, accepted, rejected by a
    /// hostile act, or invalidated because a party left active play. Empty when no offer was touched.
    /// </summary>
    public IReadOnlyList<OfferTransition> OfferTransitions { get; init; } = [];

    /// <summary>
    /// Every change to a character's fear this action caused, in order — from a critical blow, a heavy one,
    /// a threat that landed, an ally's steadying word, or the moment the odds turned against them. Empty when
    /// morale did not move. Recorded even when no randomness was involved, so a deterministic change to
    /// authoritative state is never knowable only by its effect on a later number.
    /// </summary>
    public IReadOnlyList<FearChange> FearChanges { get; init; } = [];

    public static EngineResult Reject(GameAction action, GameState state, EngineRejectionReason reason, string message) =>
        new()
        {
            Action = action,
            Accepted = false,
            RejectionReason = reason,
            RejectionMessage = message,
            StateBefore = state,
            StateAfter = state
        };

    public static EngineResult Accept(GameAction action, GameState before, GameState after, ActionOutcome outcome,
        IReadOnlyList<RngDraw>? rngDraws = null,
        IReadOnlyList<StatusEvent>? statusEvents = null,
        IReadOnlyList<OfferTransition>? offerTransitions = null,
        IReadOnlyList<FearChange>? fearChanges = null) =>
        new()
        {
            Action = action,
            Accepted = true,
            StateBefore = before,
            StateAfter = after,
            Outcome = outcome,
            RngDraws = rngDraws ?? [],
            StatusEvents = statusEvents ?? [],
            OfferTransitions = offerTransitions ?? [],
            FearChanges = fearChanges ?? []
        };
}

/// <summary>
/// The upkeep the engine performed at the start or end of one actor's turn: the status effects that expired,
/// and the surrender offers that lapsed. Deterministic and never random.
/// </summary>
/// <remarks>
/// Upkeep is the engine's job, not the orchestration layer's, because expiry is authoritative state change
/// with exact rules. The orchestration layer only traces what came back and tells the room.
/// </remarks>
public sealed record TurnUpkeep
{
    public static readonly TurnUpkeep None = new();

    public IReadOnlyList<StatusEvent> StatusEvents { get; init; } = [];

    public IReadOnlyList<OfferTransition> OfferTransitions { get; init; } = [];

    /// <summary>
    /// True when start-of-turn upkeep found the acting character stunned from an earlier turn and consumed the
    /// <see cref="Domain.StatusEffectKind.Stunned"/> status: the character loses this whole turn and takes no
    /// action, but stays alive, present and targetable. Only ever set by <see cref="GameEngine.BeginActorTurn"/>.
    /// </summary>
    public bool ActorIncapacitated { get; init; }

    public bool IsEmpty => StatusEvents.Count == 0 && OfferTransitions.Count == 0 && !ActorIncapacitated;
}
