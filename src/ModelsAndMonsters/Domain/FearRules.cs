namespace ModelsAndMonsters.Domain;

/// <summary>
/// The fixed bounds of the encounter-scoped morale scale, in one place so nothing carries its own copy.
/// </summary>
/// <remarks>
/// Fear is authoritative engine state on <see cref="Character.Fear"/>, and the <see cref="StatusEffectKind.Scared"/>
/// status is only its public shadow: the engine keeps the two in step, and nothing else may apply or remove
/// that status. The exact number is the character's own knowledge and the harness's; everyone else sees the
/// status and nothing more.
/// </remarks>
public static class FearRules
{
    public const int Minimum = 0;

    public const int Maximum = 5;

    /// <summary>At or above this, a character is publicly <see cref="StatusEffectKind.Scared"/>.</summary>
    public const int ScaredThreshold = 3;

    /// <summary>
    /// The share of a character's maximum health one blow must take to frighten them, whatever its quality.
    /// A blow that is BOTH critical and this large still frightens them once — the causes are recorded
    /// together, never counted twice.
    /// </summary>
    public const double LargeHitFraction = 0.25;

    public static int Clamp(int fear) => Math.Clamp(fear, Minimum, Maximum);

    public static bool IsScared(int fear) => fear >= ScaredThreshold;

    /// <summary>The damage from one blow that counts as large against a given maximum health.</summary>
    public static int LargeHitThreshold(int maxHealth) =>
        maxHealth <= 0 ? int.MaxValue : (int)Math.Ceiling(maxHealth * LargeHitFraction);
}

/// <summary>
/// Why a character's fear changed. A closed set: every change names exactly one of these, so the report can
/// group changes by cause without reading prose, and no cause can be invented by narration.
/// </summary>
public enum FearChangeCause
{
    /// <summary>Survived a critical hit.</summary>
    CriticalHitReceived,

    /// <summary>Survived one blow that took at least a quarter of their maximum health.</summary>
    LargeHitReceived,

    /// <summary>Both of the above at once, from one resolved attack. Counted once.</summary>
    CriticalAndLargeHitReceived,

    /// <summary>Went from not outnumbered to outnumbered among the active combatants.</summary>
    BecameOutnumbered,

    /// <summary>Was the target of a successful <c>intimidate_character</c>.</summary>
    Intimidated,

    /// <summary>Landed a critical hit of their own.</summary>
    LandedCriticalHit,

    /// <summary>An ally spent their turn steadying them.</summary>
    SteadiedByAlly,

    /// <summary>Vark's Rally Grunt, which steadies its target as well as sharpening their next blow.</summary>
    RallyGrunt,

    /// <summary>Seeded by the scenario before the encounter began.</summary>
    ScenarioSeed
}

/// <summary>Whether a fear change crossed the public <see cref="FearRules.ScaredThreshold"/>, and which way.</summary>
public enum ScaredTransition
{
    None,
    BecameScared,
    RecoveredFromScared
}

/// <summary>
/// One authoritative change to a character's fear, with everything needed to explain it without a roll.
/// </summary>
/// <remarks>
/// Every fear change produces one of these, whether or not randomness was involved: a deterministic cause
/// still has to be traceable, or morale becomes a number that moves for reasons the record cannot show.
/// <see cref="Delta"/> is what was asked for and <see cref="Before"/>/<see cref="After"/> are what the clamp
/// allowed, so a change absorbed at the ceiling or the floor is visible as such rather than missing.
/// </remarks>
public sealed record FearChange
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required FearChangeCause Cause { get; init; }

    /// <summary>A short factual note naming the authoritative source of the change. Never narration.</summary>
    public required string CauseDetail { get; init; }

    /// <summary>The signed change requested, before clamping.</summary>
    public required int Delta { get; init; }

    public required int Before { get; init; }

    public required int After { get; init; }

    /// <summary>The character who caused it, when one did (the attacker, intimidator or steadying ally).</summary>
    public string? SourceCharacterId { get; init; }

    public string? SourceCharacterName { get; init; }

    /// <summary>The action type the change happened under, e.g. "attack_character".</summary>
    public string? RelatedActionType { get; init; }

    public required ScaredTransition Transition { get; init; }

    /// <summary>True when the clamp absorbed the whole change, so the value did not move.</summary>
    public bool Absorbed => Before == After;
}
