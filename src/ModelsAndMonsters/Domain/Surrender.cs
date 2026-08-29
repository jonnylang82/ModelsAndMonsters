using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// Where a surrender offer stands. An offer is a proposal, not a fact about the fight: only
/// <see cref="Accepted"/> ever changes anyone's disposition or moves anything.
/// </summary>
public enum SurrenderOfferState
{
    /// <summary>Made, and still open for the named recipient to accept on their turn.</summary>
    Pending,

    /// <summary>The named recipient accepted it. The terms have been enforced atomically.</summary>
    Accepted,

    /// <summary>The recipient's side acted against the offerer, which rejects the offer. Nothing transferred.</summary>
    Rejected,

    /// <summary>The recipient completed a turn without accepting. Nothing transferred.</summary>
    Expired,

    /// <summary>
    /// The offer can no longer be enforced — a party left active play, or a promised asset is no longer
    /// owned by the offerer. Nothing transferred.
    /// </summary>
    Invalidated
}

/// <summary>
/// A concrete, enforceable surrender offer: what the offerer will hand over, to whom, if they are spared.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the contract. The public plea, argument or threat that accompanied it is ordinary
/// speech on the public channel and is referenced by <see cref="AssociatedSpeechEventId"/> — it supplies the
/// persuasion, never the terms. Speech is not contract state and can never be enforced.
/// </para>
/// <para>
/// Creating an offer transfers nothing, disarms nobody, and leaves the offerer active and targetable. Only
/// <see cref="SurrenderAgreement"/>, created by an accepted offer, records assets that actually moved.
/// </para>
/// </remarks>
public sealed record SurrenderOffer
{
    public required string Id { get; init; }

    public required string OffererId { get; init; }

    /// <summary>The one named opponent who may accept. Nobody else can, however much they would like to.</summary>
    public required string RecipientId { get; init; }

    /// <summary>The stable ids of the ordinary inventory items promised. May be empty when only the weapon is offered.</summary>
    public ImmutableArray<string> OfferedItemIds { get; init; } = [];

    /// <summary>Whether the offerer promised to give up the weapon in their hand.</summary>
    public required bool ForfeitWeapon { get; init; }

    public required int CreatedRound { get; init; }

    /// <summary>The global turn number the offer was made on.</summary>
    public required int CreatedTurn { get; init; }

    /// <summary>
    /// True when the offer lapses once the recipient has finished a turn without accepting. Always true in
    /// v0.7; carried explicitly so the expiry rule is state, not a hidden convention.
    /// </summary>
    public bool ExpiresAfterRecipientTurn { get; init; } = true;

    /// <summary>The public-channel id of the offerer's speech on the same turn, when they spoke. Never the terms.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    public required SurrenderOfferState State { get; init; }

    /// <summary>Why the offer left <see cref="SurrenderOfferState.Pending"/>. Null while it is still pending.</summary>
    public string? ResolutionCause { get; init; }

    /// <summary>The round the offer left Pending on. Null while pending.</summary>
    public int? ResolvedRound { get; init; }

    /// <summary>The global turn the offer left Pending on. Null while pending.</summary>
    public int? ResolvedTurn { get; init; }

    public bool IsPending => State == SurrenderOfferState.Pending;

    /// <summary>True when the offer contains at least one immediate, enforceable concession.</summary>
    public bool HasConcession => ForfeitWeapon || OfferedItemIds.Length > 0;
}

/// <summary>
/// The durable record of an accepted surrender: who yielded, to whom, and exactly what changed hands.
/// </summary>
/// <remarks>
/// It is historical evidence and report data. Authoritative ownership always lives in
/// <see cref="GameState"/> — this never becomes a second source of truth for who holds what, only for what
/// happened and when.
/// </remarks>
public sealed record SurrenderAgreement
{
    public required string Id { get; init; }

    public required string OfferId { get; init; }

    public required string OffererId { get; init; }

    public required string AcceptedById { get; init; }

    /// <summary>The stable ids of the items that actually moved to the accepter.</summary>
    public ImmutableArray<string> TransferredItemIds { get; init; } = [];

    /// <summary>The stable id of the forfeited weapon, or null when no weapon was promised.</summary>
    public string? ForfeitedWeaponId { get; init; }

    public required int AcceptedRound { get; init; }

    public required int AcceptedTurn { get; init; }

    /// <summary>The public-channel id of the plea, argument or threat that accompanied the offer, if any.</summary>
    public int? AssociatedSpeechEventId { get; init; }
}

/// <summary>
/// One surrender-offer state transition, carried out of the engine so the orchestration layer can trace it
/// and tell the room. Every departure from <see cref="SurrenderOfferState.Pending"/> produces exactly one.
/// </summary>
public sealed record OfferTransition(
    SurrenderOffer Offer,
    SurrenderOfferState Previous,
    SurrenderOfferState New,
    string Cause);

/// <summary>Where a surrender DEMAND stands — a winner telling an opponent to yield.</summary>
public enum SurrenderDemandState
{
    /// <summary>Made, and surfaced to the target until they take their next turn.</summary>
    Pending,

    /// <summary>The target answered it by yielding — offering their own surrender to the demander.</summary>
    Answered,

    /// <summary>The target finished a turn without yielding. The demand lapses; nothing was compelled.</summary>
    Expired,

    /// <summary>A party left active play (died, surrendered, or fled), so the demand can mean nothing more.</summary>
    Invalidated
}

/// <summary>
/// A pressure-only demand that one named opponent give up the fight.
/// </summary>
/// <remarks>
/// Unlike a <see cref="SurrenderOffer"/> this carries NO terms and moves nothing. It is a formal ultimatum,
/// correctly attributed to the DEMANDER — who stays armed, active and targetable — and surfaced to the TARGET
/// on their next turn so they can choose to yield or fight on. Fear compels nothing here, exactly as
/// everywhere else: the demand only makes the choice explicit. The actual surrender, if the target complies,
/// still goes through <see cref="SurrenderOffer"/> (offer_surrender) on the target's own turn, on whatever
/// terms they choose. A demand never sets terms it cannot enforce, because it enforces nothing.
/// </remarks>
public sealed record SurrenderDemand
{
    public required string Id { get; init; }

    /// <summary>The character issuing the demand. Unchanged by it — still armed, active and a valid target.</summary>
    public required string DemanderId { get; init; }

    /// <summary>The one named opponent being told to yield. Only their own later choice can act on it.</summary>
    public required string TargetId { get; init; }

    public required int CreatedRound { get; init; }

    /// <summary>The global turn number the demand was made on.</summary>
    public required int CreatedTurn { get; init; }

    /// <summary>The public-channel id of the demander's speech on the same turn, when they spoke. Never the terms — there are none.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    public required SurrenderDemandState State { get; init; }

    /// <summary>Why the demand left <see cref="SurrenderDemandState.Pending"/>. Null while it is still pending.</summary>
    public string? ResolutionCause { get; init; }

    /// <summary>The round the demand left Pending on. Null while pending.</summary>
    public int? ResolvedRound { get; init; }

    /// <summary>The global turn the demand left Pending on. Null while pending.</summary>
    public int? ResolvedTurn { get; init; }

    public bool IsPending => State == SurrenderDemandState.Pending;
}

/// <summary>
/// One surrender-demand state transition, carried out of the engine so the orchestration layer can trace it
/// and tell the room. Every departure from <see cref="SurrenderDemandState.Pending"/> produces exactly one.
/// </summary>
public sealed record DemandTransition(
    SurrenderDemand Demand,
    SurrenderDemandState Previous,
    SurrenderDemandState New,
    string Cause);
