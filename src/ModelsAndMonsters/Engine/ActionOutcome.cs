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

    /// <summary>
    /// The names of the items the opener now sees inside. In v0.4 these are revealed only to the opener,
    /// not to the room: the orchestration layer delivers them as a private observation. The public
    /// <see cref="Summary"/> deliberately does not name them.
    /// </summary>
    public required IReadOnlyList<string> RevealedContents { get; init; }

    public override string OutcomeType => "open_container";

    /// <summary>
    /// The public account of the opening. It states only that the container was opened and the opener
    /// looked inside — never what is within, which is the opener's private observation.
    /// </summary>
    public override string Summary =>
        $"{ActorName} opened the {ContainerName} and looked inside. " +
        $"Only {ActorName} can see what is within; its contents are not revealed to anyone else by the opening.";
}

/// <summary>
/// The result of an accepted close inspection. It carries what the inspector could discover — an exterior
/// marking, and, for an open container, the current contents — so the orchestration layer can record the
/// right private knowledge and deliver a private observation. The physical object is unchanged; the public
/// <see cref="Summary"/> says only that the actor examined it, never what was found.
/// </summary>
public sealed record InspectObjectOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ObjectId { get; init; }
    public required string ObjectName { get; init; }

    /// <summary>True when the inspected object is a container (so open-state and contents are meaningful).</summary>
    public required bool IsContainer { get; init; }

    /// <summary>Whether the inspected container was open, so its current contents were observable.</summary>
    public required bool IsOpen { get; init; }

    /// <summary>The exterior marking discoverable by inspection, or null when the object bears none.</summary>
    public string? ExteriorClue { get; init; }

    /// <summary>The item names currently inside, meaningful only for an open container. Delivered privately.</summary>
    public IReadOnlyList<string> CurrentContents { get; init; } = [];

    public override string OutcomeType => "inspect_object";

    /// <summary>The public account: only that the actor examined the object closely. Never the findings.</summary>
    public override string Summary =>
        $"{ActorName} examined the {ObjectName} closely. What {ActorName} noticed is theirs alone; " +
        "nothing about the object was revealed to anyone else.";
}

/// <summary>
/// The result of opening an exit. Public: everyone present sees the door swing open, so the
/// <see cref="Summary"/> can be narrated to the whole room. It states only that the door is now open, not
/// that anyone left through it — that is a separate act.
/// </summary>
public sealed record OpenExitOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ExitId { get; init; }
    public required string ExitName { get; init; }
    public required string DestinationDescription { get; init; }

    public override string OutcomeType => "open_exit";

    public override string Summary =>
        $"{ActorName} pulled the {ExitName} open. It now stands open, exposing {DestinationDescription}. " +
        "Nobody has passed through it yet.";
}

/// <summary>
/// The result of a character escaping through an open exit. Public: everyone present sees them go. The
/// character is now <see cref="Domain.CharacterDisposition.Escaped"/> — alive, gone from the room, and no
/// longer a valid target.
/// </summary>
public sealed record EscapeOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ExitId { get; init; }
    public required string ExitName { get; init; }
    public required string DestinationDescription { get; init; }

    public override string OutcomeType => "escape_encounter";

    public override string Summary =>
        $"{ActorName} went through the open {ExitName} and left the encounter, escaping to {DestinationDescription}. " +
        $"{ActorName} is alive but no longer present, and can no longer be reached or targeted.";
}

/// <summary>
/// The result of a character surrendering. Public: everyone present sees them yield. The character is now
/// <see cref="Domain.CharacterDisposition.Surrendered"/> — alive, still present, but out of the fight and no
/// longer a valid target. No weapon or item is taken from them.
/// </summary>
public sealed record SurrenderOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }

    public override string OutcomeType => "surrender";

    public override string Summary =>
        $"{ActorName} surrendered, lowering their weapon and making no further attempt to fight. " +
        $"{ActorName} is alive and still present, but takes no further turns and can no longer be attacked. " +
        $"{ActorName} keeps their weapon and belongings; nothing was taken from them.";
}

/// <summary>
/// The result of one character giving an ordinary inventory item to another. Public: everyone present sees
/// the item change hands, so the <see cref="Summary"/> may be narrated to the whole room and names the item.
/// </summary>
public sealed record GiveItemOutcome : ActionOutcome
{
    public required string GiverId { get; init; }
    public required string GiverName { get; init; }
    public required string RecipientId { get; init; }
    public required string RecipientName { get; init; }
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }

    public override string OutcomeType => "give_item";

    public override string Summary =>
        $"{GiverName} handed the {ItemName} to {RecipientName}, who now carries it. " +
        $"{GiverName} no longer has it.";
}

/// <summary>
/// The result of a character dropping an ordinary inventory item onto the floor. Public: everyone present
/// sees it fall, so the <see cref="Summary"/> may be narrated to the whole room. The item keeps its stable
/// id and can afterwards be taken from the floor through the ordinary item-taking interaction.
/// </summary>
public sealed record DropItemOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string GroundContainerName { get; init; }

    public override string OutcomeType => "drop_item";

    public override string Summary =>
        $"{ActorName} dropped the {ItemName} on {GroundContainerName}, where it now lies in plain sight and " +
        "can be picked up by anyone within reach.";
}

/// <summary>
/// The result of an attempted theft. Public: every theft attempt is noticed in v0.6, so the
/// <see cref="Summary"/> may be narrated to the whole room whether it succeeded or failed. It carries the
/// full randomness record — base chance, any modifiers, effective chance, raw roll — so the outcome is fully
/// reconstructable, and on success the item has moved from the target to the thief.
/// </summary>
public sealed record StealItemOutcome : ActionOutcome
{
    public required string ThiefId { get; init; }
    public required string ThiefName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }

    /// <summary>The base theft chance out of 100 before any modifier.</summary>
    public required int BaseChance { get; init; }

    /// <summary>The modifiers applied to the base chance, each as a short human-readable note. Empty in v0.6.</summary>
    public required IReadOnlyList<string> Modifiers { get; init; }

    /// <summary>The effective chance out of 100 the raw roll was compared against, after any modifiers.</summary>
    public required int EffectiveChance { get; init; }

    /// <summary>The raw d100 roll.</summary>
    public required int Roll { get; init; }

    public required bool Succeeded { get; init; }

    public override string OutcomeType => "steal_item";

    public override string Summary =>
        Succeeded
            ? $"{ThiefName} snatched the {ItemName} from {TargetName} and now carries it. " +
              $"{TargetName} no longer has it. Everyone present saw the theft."
            : $"{ThiefName} lunged for the {ItemName} but {TargetName} kept hold of it — the theft failed. " +
              "Nothing changed hands, and everyone present saw the attempt.";
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
