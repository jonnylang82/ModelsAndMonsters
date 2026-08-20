namespace ModelsAndMonsters.Engine;

/// <summary>
/// How well a blow that already landed actually connected. One draw selects among all three, so a hit is
/// never a glancing blow and a critical hit by two separate rolls.
/// </summary>
public enum AttackQuality
{
    /// <summary>Half post-armour damage.</summary>
    Glancing,

    /// <summary>Full post-armour damage.</summary>
    Solid,

    /// <summary>Double post-armour damage — and morale moves both ways around it.</summary>
    Critical
}

/// <summary>
/// Tunable combat parameters that are not per-character.
/// </summary>
/// <param name="GlancingBlowChance">
/// The size of the LOW band of the quality draw, out of 100: a landed blow glances when the raw quality roll
/// is at or under this. 0 removes the band entirely.
/// </param>
/// <param name="BaseStealChance">
/// The base chance out of 100 that an attempted theft succeeds, before any modifier (v0.6 applies none).
/// This is the one probability the inventory system consults; it is deliberately a flat configurable
/// number, not a derived skill, because v0.6 introduces no Dexterity, Perception or general stat system.
/// </param>
/// <param name="CriticalHitChance">
/// The size of the HIGH band of the same quality draw, out of 100: a landed blow is critical when the raw
/// quality roll is above <c>100 - CriticalHitChance</c>. 0 removes the band entirely. With the defaults the
/// two bands are equal and the middle band is everything left over — 1–25 glancing, 26–75 solid, 76–100
/// critical — which is deliberately more volatile than v0.7 and raises average post-armour damage slightly,
/// so encounters resolve faster without every weapon being made stronger.
/// </param>
/// <param name="BaseIntimidationChance">
/// The base chance out of 100 that <c>intimidate_character</c> succeeds, before the state-derived modifiers
/// in <see cref="IntimidationRules"/>. Nothing about the words spoken ever reaches it.
/// </param>
public sealed record CombatRules(
    int GlancingBlowChance,
    int BaseStealChance = 40,
    int CriticalHitChance = 25,
    int BaseIntimidationChance = IntimidationRules.DefaultBaseChance)
{
    /// <summary>Default rules: equal glancing and critical bands, and the default base theft chance.</summary>
    public static CombatRules Default { get; } = new(GlancingBlowChance: 25);

    /// <summary>
    /// Rules with no quality variance beyond the hit roll — every landed blow is a solid, full-damage hit.
    /// The quality draw is still taken, so the number of draws an attack makes never depends on configuration.
    /// </summary>
    public static CombatRules NoGlancing { get; } = new(GlancingBlowChance: 0, CriticalHitChance: 0);

    /// <summary>The lowest quality roll that is critical. Above 100 when the critical band is disabled.</summary>
    public int CriticalThreshold => 100 - CriticalHitChance + 1;

    /// <summary>
    /// Selects the quality of a landed blow from one raw d100. The low band wins a tie with the high band,
    /// which can only happen if both are configured so wide that they overlap.
    /// </summary>
    public AttackQuality QualityFor(int rawRoll) =>
        rawRoll <= GlancingBlowChance ? AttackQuality.Glancing
        : rawRoll >= CriticalThreshold && CriticalHitChance > 0 ? AttackQuality.Critical
        : AttackQuality.Solid;

    /// <summary>How the raw quality roll is turned into a result, for the draw's trace.</summary>
    /// <remarks>
    /// A quality roll is not measured against a threshold — it lands in one of three bands — so it is
    /// described as the band it fell in, with that band's range. Rendering it the way a pass/fail draw is
    /// rendered produced lines like "rolled 99 vs 25 → critical", which invites the reading that 99 beat 25
    /// when 25 is the top of the GLANCING band at the opposite end of the roll.
    /// </remarks>
    public string DescribeQualityComparison(int rawRoll, AttackQuality quality) => quality switch
    {
        AttackQuality.Glancing => $"roll {rawRoll} in glancing band 1-{GlancingBlowChance}",
        AttackQuality.Critical => $"roll {rawRoll} in critical band {CriticalThreshold}-100",
        _ => $"roll {rawRoll} in solid band {GlancingBlowChance + 1}-{(CriticalHitChance > 0 ? CriticalThreshold - 1 : 100)}"
    };

    /// <summary>The bands as a readable candidate list for the draw's trace, e.g. "1-25 glancing; 26-75 solid; 76-100 critical".</summary>
    public string DescribeQualityBands()
    {
        var parts = new List<string>();
        if (GlancingBlowChance > 0)
        {
            parts.Add($"1-{GlancingBlowChance} glancing");
        }

        var solidFrom = GlancingBlowChance + 1;
        var solidTo = CriticalHitChance > 0 ? CriticalThreshold - 1 : 100;
        parts.Add(solidFrom > solidTo ? "no solid band" : $"{solidFrom}-{solidTo} solid");
        if (CriticalHitChance > 0)
        {
            parts.Add($"{CriticalThreshold}-100 critical");
        }

        return string.Join("; ", parts);
    }

    /// <summary>Applies a quality multiplier to post-armour damage, using the existing rounding rule.</summary>
    public static int DamageFor(AttackQuality quality, int postArmourDamage) => quality switch
    {
        AttackQuality.Glancing => GlancingDamage(postArmourDamage),
        AttackQuality.Critical => CriticalDamage(postArmourDamage),
        _ => postArmourDamage
    };

    /// <summary>Half damage, rounded to the nearest whole point (0.5 rounds up).</summary>
    public static int GlancingDamage(int fullDamage) =>
        (int)Math.Round(fullDamage / 2.0, MidpointRounding.AwayFromZero);

    /// <summary>Double damage. Armour has already been subtracted, so a blow armour stopped stays stopped.</summary>
    public static int CriticalDamage(int fullDamage) => fullDamage * 2;
}

/// <summary>
/// The state-derived modifiers on an intimidation attempt, and nothing else. Every one of them is read from
/// the authoritative snapshot: how frightened the target already is, whether they are outnumbered, how badly
/// hurt they are, and whether the intimidator has themselves lost their nerve.
/// </summary>
/// <remarks>
/// There is deliberately no modifier for what was said. Eloquence, length, vocabulary, punctuation and the
/// identity of the model behind the character are all invisible here on purpose: the words are roleplay, and
/// a mechanic that quietly rewarded particular phrasings would make the experiment unreadable.
/// </remarks>
public static class IntimidationRules
{
    public const int DefaultBaseChance = 35;

    /// <summary>Added for each point of fear the target already carries.</summary>
    public const int PerTargetFearPoint = 10;

    /// <summary>Added when the target is currently outnumbered among the active combatants.</summary>
    public const int TargetOutnumbered = 15;

    /// <summary>Added when the target is badly wounded or worse, by the project's existing health bands.</summary>
    public const int TargetBadlyWounded = 10;

    /// <summary>Subtracted when the intimidator is themselves publicly Scared.</summary>
    public const int IntimidatorScared = -10;

    public const int MinimumChance = 10;

    public const int MaximumChance = 90;

    public static int Clamp(int chance) => Math.Clamp(chance, MinimumChance, MaximumChance);
}
