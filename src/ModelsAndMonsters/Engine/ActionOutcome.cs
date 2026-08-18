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

    /// <summary>The d100 hit roll and the attacker's chance, so the outcome is fully reconstructable.</summary>
    public required int HitRoll { get; init; }
    public required int HitChance { get; init; }
    public required bool Hit { get; init; }

    /// <summary>The glancing roll and chance, null when the attack missed (no glancing roll was made).</summary>
    public int? GlancingRoll { get; init; }
    public required int GlancingChance { get; init; }
    public required bool Glancing { get; init; }

    /// <summary>Damage before any glancing reduction, i.e. <c>max(0, weapon - armour)</c>.</summary>
    public required int BaseDamage { get; init; }

    /// <summary>Damage actually applied: 0 on a miss, halved on a glancing blow.</summary>
    public required int DamageDealt { get; init; }
    public required int TargetHealthBefore { get; init; }
    public required int TargetHealthAfter { get; init; }
    public required int TargetMaxHealth { get; init; }
    public required bool TargetDied { get; init; }
    public string? InjuryInflicted { get; init; }

    /// <summary>
    /// When a lethal blow drops a character who was carrying items, those items are moved into a lootable
    /// corpse container. This names that container; <see cref="DroppedItems"/> lists what it holds. Null /
    /// empty when nobody died or the dead character carried nothing.
    /// </summary>
    public string? CorpseContainerName { get; init; }

    public IReadOnlyList<string> DroppedItems { get; init; } = [];

    public override string OutcomeType => "attack";

    public override string Summary
    {
        get
        {
            if (!Hit)
            {
                return $"{AttackerName} attacked {TargetName} with {WeaponName} but MISSED " +
                       $"(rolled {HitRoll} against a hit chance of {HitChance}). No damage. " +
                       $"{TargetName} is unharmed with {TargetHealthAfter}/{TargetMaxHealth} health.";
            }

            var quality = Glancing ? "a GLANCING blow (half damage)" : "a solid hit";
            var status = TargetDied
                ? $"{TargetName} is dead."
                : $"{TargetName} is alive with {TargetHealthAfter}/{TargetMaxHealth} health.";
            var injury = InjuryInflicted is null ? "" : $" New lasting injury recorded: {InjuryInflicted}.";
            var loot = TargetDied && DroppedItems.Count > 0
                ? $" As {TargetName} falls, what they carried — {string.Join(", ", DroppedItems)} — spills from " +
                  $"their body and can be taken from {CorpseContainerName}."
                : "";
            return $"{AttackerName} hit {TargetName} with {WeaponName} — {quality}. " +
                   $"Weapon damage {WeaponDamage} minus armour {TargetArmour} = {BaseDamage}, " +
                   $"{DamageDealt} damage dealt. " +
                   $"{TargetName} health {TargetHealthBefore} -> {TargetHealthAfter}. {status}{injury}{loot}";
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

public sealed record OpenContainerOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ContainerId { get; init; }
    public required string ContainerName { get; init; }

    /// <summary>The names of the items now visible inside, revealed to everyone by the opening.</summary>
    public required IReadOnlyList<string> RevealedContents { get; init; }

    public override string OutcomeType => "open_container";

    public override string Summary
    {
        get
        {
            var contents = RevealedContents.Count == 0
                ? "It is empty."
                : $"Inside is {NaturalJoin(RevealedContents)}.";
            return $"{ActorName} opened the {ContainerName}. {contents} " +
                   "The contents are now visible to everyone in the room.";
        }
    }

    private static string NaturalJoin(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "nothing",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };
}

public sealed record TakeItemOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ContainerId { get; init; }
    public required string ContainerName { get; init; }
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }

    /// <summary>The item names left in the container after the transfer, for a complete record.</summary>
    public required IReadOnlyList<string> RemainingContents { get; init; }

    public override string OutcomeType => "take_item";

    public override string Summary
    {
        get
        {
            var remaining = RemainingContents.Count == 0
                ? $"The {ContainerName} is now empty."
                : $"Still inside the {ContainerName}: {string.Join(", ", RemainingContents)}.";
            return $"{ActorName} took the {ItemName} from the {ContainerName} and now carries it. {remaining}";
        }
    }
}
