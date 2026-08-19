namespace ModelsAndMonsters.Engine;

/// <summary>
/// A complete record of one random draw the engine made, with enough context to reproduce and explain
/// it in isolation.
/// </summary>
/// <remarks>
/// The engine attaches these to the <see cref="EngineResult"/> so the orchestration layer can trace
/// them without the engine depending on the tracing subsystem. Recording only the final hit/miss result
/// would make behavioural comparisons across runs much harder, so every draw carries its purpose, the
/// actors involved, the outcome it was selecting, the range it was drawn from, the raw value, the
/// modifier (threshold) applied, the result, and the generator's sequence position before and after —
/// which, with the seed, pins the exact generator state on both sides of the draw.
/// </remarks>
public sealed record RngDraw
{
    /// <summary>What this draw was for, e.g. "attack.hit-check" or "attack.glancing-check".</summary>
    public required string Purpose { get; init; }

    /// <summary>The action type that caused the draw, e.g. "attack_character".</summary>
    public required string ActionType { get; init; }

    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public string? TargetId { get; init; }

    public string? TargetName { get; init; }

    /// <summary>The outcome the draw was selecting between, e.g. "hit or miss".</summary>
    public required string OutcomeSelected { get; init; }

    /// <summary>Number of sides on the die drawn (the candidate space is <c>[RangeMin, RangeMax]</c>).</summary>
    public required int Sides { get; init; }

    public required int RangeMin { get; init; }

    public required int RangeMax { get; init; }

    /// <summary>The raw value drawn, before any interpretation.</summary>
    public required int RawRoll { get; init; }

    /// <summary>The threshold the raw roll was compared against (the hit chance, the glancing chance, the effective theft chance).</summary>
    public required int Threshold { get; init; }

    /// <summary>
    /// The base chance out of 100 before any modifier, when the threshold was derived from a configurable
    /// base rather than being a fixed per-actor stat. Null for draws with no distinct base (attack rolls,
    /// where the threshold is the actor's hit chance directly).
    /// </summary>
    public int? BaseChance { get; init; }

    /// <summary>
    /// Every modifier applied to <see cref="BaseChance"/> to reach <see cref="Threshold"/>, each a short
    /// human-readable note. Empty when no modifier applied. Recorded so a draw's effective chance can be
    /// reconstructed from its base rather than only observed as a final number.
    /// </summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>How the raw roll was turned into the result, e.g. "roll &lt;= 80 lands".</summary>
    public required string Comparison { get; init; }

    /// <summary>The selected result, e.g. "hit", "miss", "glancing", "solid".</summary>
    public required string Result { get; init; }

    /// <summary>The seed the generator derives from, so the draw can be reproduced.</summary>
    public required long Seed { get; init; }

    /// <summary>The generator's draw count immediately before this draw.</summary>
    public required long SequenceBefore { get; init; }

    /// <summary>The generator's draw count immediately after this draw.</summary>
    public required long SequenceAfter { get; init; }
}
