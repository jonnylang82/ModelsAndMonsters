namespace ModelsAndMonsters.Engine;

/// <summary>
/// The mechanical result of an accepted action. The engine — not the Dungeon Master — decides these
/// numbers; the DM only turns them into prose.
/// </summary>
public abstract record ActionOutcome
{
    public abstract string OutcomeType { get; }

    /// <summary>A flat factual statement of what happened, handed to the DM to narrate.</summary>
    public abstract string Summary { get; }
}

public sealed record AttackOutcome : ActionOutcome
{
    public required string AttackerId { get; init; }
    public required string AttackerName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }
    public required string WeaponName { get; init; }
    public required int WeaponDamage { get; init; }
    public required int TargetArmour { get; init; }
    public required int DamageDealt { get; init; }
    public required int TargetHealthBefore { get; init; }
    public required int TargetHealthAfter { get; init; }
    public required int TargetMaxHealth { get; init; }
    public required bool TargetDied { get; init; }
    public string? InjuryInflicted { get; init; }

    public override string OutcomeType => "attack";

    public override string Summary
    {
        get
        {
            var status = TargetDied
                ? $"{TargetName} is dead."
                : $"{TargetName} is alive with {TargetHealthAfter}/{TargetMaxHealth} health.";
            var injury = InjuryInflicted is null ? "" : $" New lasting injury recorded: {InjuryInflicted}.";
            return $"{AttackerName} hit {TargetName} with {WeaponName}. " +
                   $"Weapon damage {WeaponDamage} minus armour {TargetArmour} = {DamageDealt} damage dealt. " +
                   $"{TargetName} health {TargetHealthBefore} -> {TargetHealthAfter}. {status}{injury}";
        }
    }
}

public sealed record HealOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ItemName { get; init; }
    public required int HealingAmount { get; init; }
    public required int HealthBefore { get; init; }
    public required int HealthAfter { get; init; }
    public required int MaxHealth { get; init; }

    public override string OutcomeType => "heal";

    public override string Summary =>
        $"{ActorName} used {ItemName} (heals {HealingAmount}). " +
        $"{ActorName} health {HealthBefore} -> {HealthAfter} of {MaxHealth}. The item was consumed.";
}
