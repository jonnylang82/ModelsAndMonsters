namespace ModelsAndMonsters.Engine;

/// <summary>
/// Tunable combat parameters that are not per-character.
/// </summary>
/// <param name="GlancingBlowChance">
/// Chance out of 100 that a landed hit is a glancing blow, dealing half damage. 0 disables glancing
/// blows entirely (every hit is full damage).
/// </param>
public sealed record CombatRules(int GlancingBlowChance)
{
    /// <summary>Default rules: a modest glancing-blow chance.</summary>
    public static CombatRules Default { get; } = new(GlancingBlowChance: 25);

    /// <summary>Rules with no randomness beyond the hit roll — every hit is full damage.</summary>
    public static CombatRules NoGlancing { get; } = new(GlancingBlowChance: 0);

    /// <summary>Half damage, rounded to the nearest whole point (0.5 rounds up).</summary>
    public static int GlancingDamage(int fullDamage) =>
        (int)Math.Round(fullDamage / 2.0, MidpointRounding.AwayFromZero);
}
