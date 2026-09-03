namespace ModelsAndMonsters.Domain;

/// <summary>
/// The status effects the engine understands. Deliberately closed: a status is only ever one of these
/// kinds, so narration can never invent one and no model can create a new kind.
/// </summary>
public enum StatusEffectKind
{
    /// <summary>Held by the guardian: this character is shielding one ally and will take the next attack aimed at them.</summary>
    Guarding,

    /// <summary>Held by the protected ally: the next eligible attack aimed at this character is redirected to the guardian.</summary>
    Guarded,

    /// <summary>Adds to the holder's next attack hit chance. Consumed by that attack, hit or miss.</summary>
    Rallied,

    /// <summary>Subtracts from the holder's next attack hit chance. Consumed by that attack, hit or miss.</summary>
    OffBalance,

    /// <summary>Reduces the final damage of the next successful attack against the holder by its modifier.</summary>
    Defending,

    /// <summary>
    /// The public shadow of <see cref="Character.Fear"/> at or above <see cref="FearRules.ScaredThreshold"/>.
    /// It carries no mechanical modifier of its own and it never expires on a clock: the engine applies and
    /// removes it as the number crosses the threshold, and nothing else may. It exists so that everyone
    /// present can observe that a character has lost their nerve without ever learning the number behind it.
    /// </summary>
    Scared,

    /// <summary>
    /// Held by an occupant of environmental cover (v0.9). Carries no mechanical modifier of its own — the
    /// engine reads the cover object's <c>HitChanceModifier</c> directly when resolving an attack against the
    /// occupant — and never expires on a clock: it lasts exactly as long as the occupancy relationship it
    /// shadows (<see cref="StatusExpiryRule.WhileConditionHolds"/>), ended only by an explicit leave, an
    /// exposing action, the cover's destruction, or the occupant leaving active play.
    /// </summary>
    InCover,

    /// <summary>
    /// Held by a character reeling from a stunning blow: they lose their entire next turn (v0.11). It carries
    /// no hit-chance or damage modifier of its own — its whole effect is the skipped turn — and it never
    /// expires on the turn clock (<see cref="StatusExpiryRule.WhileConditionHolds"/>). The engine consumes it
    /// at the start of the holder's next turn, in the same breath that it forces the skip, so a stun costs
    /// exactly one turn and no more. The holder stays alive, present and a valid target throughout: being
    /// stunned takes the turn, not the character.
    /// </summary>
    Stunned,
    Sleeping,
    FaerieFire,
    DivineFavor
}

/// <summary>
/// When a status effect falls away if nothing has consumed it. Every supported status names exactly one of
/// these, so expiry is deterministic and never a matter of interpretation.
/// </summary>
public enum StatusExpiryRule
{
    /// <summary>Removed at the start of the SOURCE character's next turn (the guard relationship).</summary>
    StartOfSourceNextTurn,

    /// <summary>Removed at the start of the TARGET character's next turn (Defending).</summary>
    StartOfTargetNextTurn,

    /// <summary>Removed at the end of the TARGET character's next turn (Rallied, OffBalance).</summary>
    EndOfTargetNextTurn,

    /// <summary>
    /// Never swept by the turn clock. The engine removes it when the condition behind it stops holding —
    /// today that is only <see cref="StatusEffectKind.Scared"/>, which lasts exactly as long as the fear that
    /// caused it. Turn upkeep matches on the three rules above, so a status carrying this one is simply never
    /// due, which is what keeps morale out of the expiry sweep without a special case inside it.
    /// </summary>
    WhileConditionHolds
}

/// <summary>Who can see a status effect. Every v0.7 status is applied by a public, observable act.</summary>
public enum StatusVisibility
{
    Public,
    Private
}

/// <summary>
/// One live status effect instance — authoritative engine state, never narration.
/// </summary>
/// <remarks>
/// <para>
/// An instance is a fact about one target, put there by one source, with an exact modifier and an exact
/// expiry rule. It carries a stable <see cref="Id"/> so its application, consumption, expiry or removal can
/// be traced as one continuous story, and a <see cref="RelationshipId"/> so the two halves of a linked
/// effect (Guarding on the guardian, Guarded on the protected ally) can be removed together.
/// </para>
/// <para>
/// Statuses live on <see cref="GameState.Statuses"/> rather than on <see cref="Character"/>, because a
/// linked relationship spans two characters and must never be able to exist on one side only.
/// </para>
/// </remarks>
public sealed record StatusEffectInstance
{
    public int? ExpiresAtRound { get; init; }
    public bool RequiresConcentration { get; init; }
    public required string Id { get; init; }

    public required StatusEffectKind Kind { get; init; }

    /// <summary>The character who caused the status. For <see cref="StatusEffectKind.Defending"/> this is the defender themselves.</summary>
    public required string SourceCharacterId { get; init; }

    /// <summary>The character the status is on.</summary>
    public required string TargetCharacterId { get; init; }

    public required int AppliedRound { get; init; }

    /// <summary>The global turn number the status was applied on, so expiry can require a LATER turn.</summary>
    public required int AppliedTurn { get; init; }

    /// <summary>
    /// The signed mechanical value: +15 for Rallied, -15 for OffBalance, -1 damage for Defending, 0 for the
    /// guard relationship (which redirects rather than modifies).
    /// </summary>
    public required int Modifier { get; init; }

    public required StatusExpiryRule ExpiryRule { get; init; }

    /// <summary>True once the status has done its one job. A consumed status is removed, never left lying about.</summary>
    public bool Consumed { get; init; }

    public StatusVisibility Visibility { get; init; } = StatusVisibility.Public;

    /// <summary>Links the two halves of a paired effect (Guarding/Guarded). Null for an unpaired status.</summary>
    public string? RelationshipId { get; init; }

    /// <summary>The ability that applied this status, when one did. Null for a status applied by a plain action.</summary>
    public string? SourceAbilityId { get; init; }

    /// <summary>
    /// The world object this status concerns, when it is about one rather than only about a character — the
    /// cover object behind <see cref="StatusEffectKind.InCover"/> (v0.9). Null for every other status.
    /// </summary>
    public string? RelatedObjectId { get; init; }

    /// <summary>
    /// A two- or three-word label for a console line or a UI badge — "off balance", not "OffBalance".
    /// The enum name is not a phrase, and printing it made a transcript read "no longer offbalance".
    /// </summary>
    public string ShortLabel => Kind switch
    {
        StatusEffectKind.Sleeping => "asleep",
        StatusEffectKind.FaerieFire => "outlined in light",
        StatusEffectKind.DivineFavor => "divine favor",
        StatusEffectKind.Guarding => "guarding an ally",
        StatusEffectKind.Guarded => "guarded by an ally",
        StatusEffectKind.Rallied => "rallied",
        StatusEffectKind.OffBalance => "off balance",
        StatusEffectKind.Defending => "behind their guard",
        StatusEffectKind.Scared => "scared",
        StatusEffectKind.InCover => "behind cover",
        StatusEffectKind.Stunned => "stunned",
        _ => Kind.ToString()
    };

    /// <summary>A short human-readable line for prompts, traces and the observer UI.</summary>
    public string Describe() => Kind switch
    {
        StatusEffectKind.Sleeping => "asleep — cannot act; damage or a companion's waking action ends the slumber",
        StatusEffectKind.FaerieFire => "outlined in light — attacks against this target are easier while the caster concentrates",
        StatusEffectKind.DivineFavor => "divine favor — weapon hits add radiant damage while concentrating",
        StatusEffectKind.Guarding => "guarding an ally — the next enemy blow aimed at them lands on this character instead",
        StatusEffectKind.Guarded => "guarded by an ally — the next enemy blow aimed at this character is taken by the guardian",
        StatusEffectKind.Rallied => $"rallied — next attack is {Modifier:+#;-#;0} to land",
        StatusEffectKind.OffBalance => $"off balance — next attack is {Modifier:+#;-#;0} to land",
        StatusEffectKind.Defending => $"defending — the next blow that lands deals {Math.Abs(Modifier)} less damage",
        StatusEffectKind.Scared => "scared — shaken, and increasingly concerned with staying alive. It changes no odds by itself",
        StatusEffectKind.InCover => "behind cover — harder to hit while it stands, until it is left, broken by an exposing act, or destroyed",
        StatusEffectKind.Stunned => "stunned — reeling from a stunning blow, and will lose their entire next turn before it wears off",
        _ => Kind.ToString()
    };
}

/// <summary>What happened to a status effect, for the trace.</summary>
public enum StatusEventKind
{
    /// <summary>The status was put on a character.</summary>
    Applied,

    /// <summary>The status did its one job and was removed.</summary>
    Consumed,

    /// <summary>The status reached its expiry rule unused and was removed.</summary>
    Expired,

    /// <summary>The status was removed because it could no longer be sustained (its holder or source left the fight).</summary>
    Removed
}

/// <summary>One status-effect transition, carried out of the engine so the orchestration layer can trace it.</summary>
public sealed record StatusEvent(StatusEventKind Kind, StatusEffectInstance Status, string Cause);
