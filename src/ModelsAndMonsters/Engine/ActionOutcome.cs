using ModelsAndMonsters.Domain;

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

    /// <summary>The d100 hit roll and the attacker's EFFECTIVE chance, so the outcome is fully reconstructable.</summary>
    public required int HitRoll { get; init; }
    public required int HitChance { get; init; }
    public required bool Hit { get; init; }

    /// <summary>
    /// The attacker's own hit chance before any status modifier. Equal to <see cref="HitChance"/> when no
    /// status applied, so a pre-v0.7-shaped attack reads identically.
    /// </summary>
    public int BaseHitChance { get; init; }

    /// <summary>Every status modifier folded into the effective hit chance, in deterministic application order.</summary>
    public IReadOnlyList<RngModifier> HitModifiers { get; init; } = [];

    /// <summary>The quality roll and the glancing band, null when the attack missed (no quality roll was made).</summary>
    public int? GlancingRoll { get; init; }
    public required int GlancingChance { get; init; }

    /// <summary>The size of the critical band, out of 100. 0 when critical hits are configured off.</summary>
    public int CriticalChance { get; init; }

    /// <summary>
    /// How well the blow connected, from the one quality draw. <see cref="AttackQuality.Solid"/> on a miss,
    /// where no quality draw was made at all.
    /// </summary>
    public AttackQuality Quality { get; init; } = AttackQuality.Solid;

    /// <summary>True when the one quality draw selected a glancing blow. Derived from <see cref="Quality"/>.</summary>
    public bool Glancing => Quality == AttackQuality.Glancing;

    /// <summary>True when the one quality draw selected a critical hit. Derived from <see cref="Quality"/>.</summary>
    public bool Critical => Quality == AttackQuality.Critical;

    /// <summary>Damage before any glancing reduction, i.e. <c>max(0, weapon - armour)</c>.</summary>
    public required int BaseDamage { get; init; }

    /// <summary>Damage actually applied: 0 on a miss, halved on a glancing blow, doubled on a critical hit.</summary>
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

    /// <summary>
    /// The ability this blow was delivered through, when it was not a plain attack (Dirty Strike). Null for an
    /// ordinary <c>attack_character</c>. Recorded so an ability-driven strike is never mistaken for a second
    /// combat path — it uses the very same resolution and the very same draws.
    /// </summary>
    public string? ViaAbilityId { get; init; }

    public string? ViaAbilityName { get; init; }

    /// <summary>
    /// The status this blow applied on landing, when the ability applies one (OffBalance). Null when none was
    /// applied, including when the blow missed.
    /// </summary>
    public string? StatusApplied { get; init; }

    /// <summary>
    /// Who the attacker actually aimed at. Differs from <see cref="TargetId"/> only when a guardian's Guard
    /// Ally relationship redirected the blow, in which case the target fields are the guardian who took it.
    /// </summary>
    public string? IntendedTargetId { get; init; }

    public string? IntendedTargetName { get; init; }

    /// <summary>True when a guard relationship moved this blow from the intended target onto a guardian.</summary>
    public bool Redirected { get; init; }

    /// <summary>The damage a Defending status turned aside, after armour and the quality multiplier. 0 when none applied.</summary>
    public int DefendReduction { get; init; }

    /// <summary>
    /// The fear this resolved attack moved, by character - the surviving target frightened by a critical or
    /// heavy blow, and the attacker steadied by landing a critical one. Empty when morale did not move.
    /// Carried on the outcome so the narration facts and the report read from the record the engine made.
    /// </summary>
    public IReadOnlyList<FearChange> FearChanges { get; init; } = [];

    public override string OutcomeType => "attack";

    public override string Summary
    {
        get
        {
            var via = ViaAbilityName is null ? "" : $" using {ViaAbilityName}";
            var redirect = Redirected
                ? $" {AttackerName} aimed at {IntendedTargetName}, but {TargetName} was guarding them, so the " +
                  $"blow fell on {TargetName} instead."
                : "";
            var chance = HitModifiers.Count == 0
                ? $"a hit chance of {HitChance}"
                : $"an effective hit chance of {HitChance} (base {BaseHitChance}, " +
                  $"{string.Join(", ", HitModifiers.Select(m => m.Note))})";

            if (!Hit)
            {
                return $"{AttackerName} attacked {TargetName}{via} with {WeaponName} but MISSED " +
                       $"(rolled {HitRoll} against {chance}). No damage. " +
                       $"{TargetName} is unharmed with {TargetHealthAfter}/{TargetMaxHealth} health.{redirect}";
            }

            var quality = Quality switch
            {
                AttackQuality.Glancing => "a GLANCING blow (half damage)",
                AttackQuality.Critical => "a CRITICAL hit (double damage)",
                _ => "a solid hit"
            };
            var status = TargetDied
                ? $"{TargetName} is dead."
                : $"{TargetName} is alive with {TargetHealthAfter}/{TargetMaxHealth} health.";
            var injury = InjuryInflicted is null ? "" : $" New lasting injury recorded: {InjuryInflicted}.";
            var defended = DefendReduction > 0
                ? $" {TargetName} was braced behind their guard, turning aside {DefendReduction} of the damage."
                : "";
            var applied = StatusApplied is null
                ? ""
                : $" The blow left {TargetName} {StatusApplied} — their next attack is less likely to land.";
            var morale = FearChanges.Count == 0
                ? ""
                : " " + string.Join(" ", FearChanges.Select(f => f.Delta > 0
                    ? $"{f.CharacterName} is visibly shaken by it."
                    : $"{f.CharacterName} takes heart from it."));
            var loot = TargetDied && DroppedItems.Count > 0
                ? $" As {TargetName} falls, what they carried — {string.Join(", ", DroppedItems)} — spills from " +
                  $"their body and can be taken from {CorpseContainerName}."
                : "";
            return $"{AttackerName} hit {TargetName}{via} with {WeaponName} — {quality} (rolled {HitRoll} against {chance}). " +
                   $"Weapon damage {WeaponDamage} minus armour {TargetArmour} = {BaseDamage}, " +
                   $"{DamageDealt} damage dealt. " +
                   $"{TargetName} health {TargetHealthBefore} -> {TargetHealthAfter}. {status}{defended}{applied}{injury}{morale}{loot}{redirect}";
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
/// The result of making a surrender offer. Public: everyone present hears the terms. Nothing has changed
/// hands, nobody is disarmed, and the offerer is still an active, targetable combatant — the offer is a
/// proposal the named recipient may accept on their own turn, and nothing more.
/// </summary>
public sealed record OfferSurrenderOutcome : ActionOutcome
{
    public required string OfferId { get; init; }
    public required string OffererId { get; init; }
    public required string OffererName { get; init; }
    public required string RecipientId { get; init; }
    public required string RecipientName { get; init; }

    /// <summary>The names of the ordinary inventory items promised, in offer order.</summary>
    public required IReadOnlyList<string> OfferedItemNames { get; init; }

    public required bool ForfeitWeapon { get; init; }

    /// <summary>The name of the weapon promised, or null when no weapon was offered.</summary>
    public string? WeaponName { get; init; }

    /// <summary>The public-channel id of the plea or threat made on the same turn, when there was one.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    public override string OutcomeType => "offer_surrender";

    /// <summary>The exact terms, rendered for narration. The plea itself was already spoken publicly.</summary>
    public string TermsDescription
    {
        get
        {
            var parts = new List<string>();
            if (OfferedItemNames.Count > 0)
            {
                parts.Add(string.Join(", ", OfferedItemNames));
            }

            if (ForfeitWeapon && WeaponName is not null)
            {
                parts.Add($"their {WeaponName}");
            }

            return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
        }
    }

    public override string Summary =>
        $"{OffererName} offered to give up the fight to {RecipientName}, promising {TermsDescription} in return " +
        $"for being spared. NOTHING has changed hands yet and {OffererName} is NOT disarmed: the offer is only a " +
        $"proposal. {OffererName} is still an active combatant and can still be attacked. Only {RecipientName} " +
        $"may accept it, on {RecipientName}'s own turn; if {RecipientName} does anything else, the offer lapses.";
}

/// <summary>
/// The result of an accepted surrender. Public: everyone present sees the tribute change hands and the
/// weapon, if promised, hit the floor. The offerer is now
/// <see cref="Domain.CharacterDisposition.Surrendered"/> — alive, still present, out of the fight and no
/// longer a valid target — and their promised assets have moved atomically to the accepter.
/// </summary>
public sealed record AcceptSurrenderOutcome : ActionOutcome
{
    public required string OfferId { get; init; }
    public required string AgreementId { get; init; }
    public required string OffererId { get; init; }
    public required string OffererName { get; init; }
    public required string AccepterId { get; init; }
    public required string AccepterName { get; init; }

    /// <summary>The names of the items that actually moved to the accepter.</summary>
    public required IReadOnlyList<string> TransferredItemNames { get; init; }

    /// <summary>The name of the forfeited weapon, or null when none was promised.</summary>
    public string? ForfeitedWeaponName { get; init; }

    /// <summary>Where a forfeited weapon now lies, when one was forfeited.</summary>
    public string? GroundContainerName { get; init; }

    public int? AssociatedSpeechEventId { get; init; }

    public override string OutcomeType => "accept_surrender";

    public override string Summary
    {
        get
        {
            var tribute = TransferredItemNames.Count == 0
                ? ""
                : $" {string.Join(", ", TransferredItemNames)} passed from {OffererName} to {AccepterName}, who now carries them.";
            var weapon = ForfeitedWeaponName is null
                ? $" {OffererName} lowered their weapon."
                : $" {OffererName} gave up their {ForfeitedWeaponName}, which now lies on {GroundContainerName} " +
                  $"and can be picked up by anyone; {OffererName} is now DISARMED and holds no weapon.";
            return $"{AccepterName} accepted {OffererName}'s surrender on the promised terms.{tribute}{weapon} " +
                   $"{OffererName} has SURRENDERED: alive and still present, but takes no further turns and can no " +
                   $"longer be attacked.";
        }
    }
}

/// <summary>
/// The result of an accepted Guard Ally: a linked guarding relationship now stands between the guardian and
/// one ally. Public and observable. No randomness — the redirection happens later, inside an ordinary attack.
/// </summary>
public sealed record GuardAllyOutcome : ActionOutcome
{
    public required string GuardianId { get; init; }
    public required string GuardianName { get; init; }
    public required string AllyId { get; init; }
    public required string AllyName { get; init; }
    public required string RelationshipId { get; init; }

    public override string OutcomeType => "guard_ally";

    public override string Summary =>
        $"{GuardianName} spent the turn standing over {AllyName}, ready to take the next blow aimed at them. " +
        $"The next attack an enemy makes against {AllyName} will land on {GuardianName} instead, using " +
        $"{GuardianName}'s armour and health. The guard is used up by that one blow, and otherwise falls away " +
        $"at the start of {GuardianName}'s next turn.";
}

/// <summary>
/// The result of an accepted Healing Prayer: a fixed amount of health restored, with no randomness, and one
/// charge of a once-per-encounter spell spent. Public and observable.
/// </summary>
public sealed record HealingPrayerOutcome : ActionOutcome
{
    public required string CasterId { get; init; }
    public required string CasterName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }
    public required string AbilityName { get; init; }
    public required int HealingAmount { get; init; }
    public required int HealthBefore { get; init; }
    public required int HealthAfter { get; init; }
    public required int MaxHealth { get; init; }
    public required int RemainingUses { get; init; }

    public override string OutcomeType => "healing_prayer";

    public override string Summary
    {
        get
        {
            var who = string.Equals(CasterId, TargetId, StringComparison.OrdinalIgnoreCase)
                ? "themselves"
                : TargetName;
            return $"{CasterName} worked {AbilityName} over {who}, restoring {HealthAfter - HealthBefore} health. " +
                   $"{TargetName} health {HealthBefore} -> {HealthAfter} of {MaxHealth}. No dice were rolled. " +
                   $"{CasterName} has {RemainingUses} use(s) of {AbilityName} left this encounter.";
        }
    }
}

/// <summary>
/// The result of an accepted Rally Grunt: one ally's next attack is markedly more likely to land. Public and
/// observable, with no randomness when applied — the modifier is folded into that ally's next attack draw.
/// </summary>
public sealed record RallyOutcome : ActionOutcome
{
    public required string CommanderId { get; init; }
    public required string CommanderName { get; init; }
    public required string AllyId { get; init; }
    public required string AllyName { get; init; }
    public required string AbilityName { get; init; }
    public required int Modifier { get; init; }
    public required int RemainingUses { get; init; }

    /// <summary>The fear the order shed, if the ally had any. Empty when they were already unafraid.</summary>
    public IReadOnlyList<FearChange> FearChanges { get; init; } = [];

    /// <summary>True when the order brought a publicly Scared ally back below the threshold.</summary>
    public bool NoLongerScared { get; init; }

    public override string OutcomeType => "rally";

    public override string Summary
    {
        get
        {
            var steadied = FearChanges.Any(f => !f.Absorbed)
                ? $" {AllyName} also takes heart and is less afraid than they were" +
                  (NoLongerScared ? $", and no longer looks afraid at all." : ".")
                : "";
            return $"{CommanderName} used {AbilityName} on {AllyName}, steadying them. {AllyName}'s next attack is " +
                   $"{Modifier:+#;-#;0} more likely to land; the effect is used up by that attack whether it lands or " +
                   $"misses, and lapses at the end of {AllyName}'s next turn if unused.{steadied} No dice were rolled. " +
                   $"{CommanderName} has {RemainingUses} use(s) of {AbilityName} left this encounter.";
        }
    }
}

/// <summary>
/// The result of an accepted Defend: the actor is braced, and the next blow that lands on them will be turned
/// aside by its reduction. Public and observable. No randomness.
/// </summary>
public sealed record DefendOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required int Reduction { get; init; }

    public override string OutcomeType => "defend";

    public override string Summary =>
        $"{ActorName} spent the turn braced behind their guard rather than striking. The next blow that lands " +
        $"on {ActorName} will deal {Reduction} less damage; a miss leaves the guard up, and it falls away at " +
        $"the start of {ActorName}'s next turn. No dice were rolled and nothing else changed.";
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

/// <summary>
/// The result of one attempt to frighten an opponent. Public: everyone present heard the threat and saw
/// whether it landed. It carries the whole randomness record - base chance, every modifier and its
/// authoritative source, the effective chance and the raw roll - so the attempt is fully reconstructable.
/// </summary>
/// <remarks>
/// A success raises the target's fear by one, and does nothing else at all. It never forces a surrender,
/// an escape, a hand-over, a disarm or a skipped turn: what the target does about being frightened stays
/// entirely their own choice. The <see cref="Summary"/> deliberately states that the fear moved without
/// naming the number, because the number is the target's own knowledge.
/// </remarks>
public sealed record IntimidateOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }

    /// <summary>The base chance out of 100 before any modifier.</summary>
    public required int BaseChance { get; init; }

    /// <summary>Every modifier applied, with its authoritative source, in deterministic order.</summary>
    public required IReadOnlyList<RngModifier> Modifiers { get; init; }

    /// <summary>The effective chance the raw roll was compared against, after modifiers and clamping.</summary>
    public required int EffectiveChance { get; init; }

    public required int Roll { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The public-channel id of the threat spoken on the same turn. Always present: the action requires one.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    /// <summary>The fear change a successful attempt caused. Empty on a failure, which changes nothing.</summary>
    public IReadOnlyList<FearChange> FearChanges { get; init; } = [];

    public override string OutcomeType => "intimidate_character";

    public override string Summary =>
        Succeeded
            ? $"{ActorName} threatened {TargetName} openly, and it told: {TargetName} is visibly more afraid " +
              $"than they were. NOTHING else changed - {TargetName} keeps their weapon, their belongings and " +
              $"their turn, is not disarmed, has not yielded and has not fled. What they do about it is theirs " +
              $"to decide. Everyone present heard the threat and saw it tell."
            : $"{ActorName} threatened {TargetName} openly, and it did not tell: {TargetName} is no more afraid " +
              $"than before. Nothing changed at all. Everyone present heard the threat and saw it fail.";
}

/// <summary>
/// The result of one character spending their whole turn steadying an ally. Public: everyone present hears
/// the encouragement and sees the ally take heart. No randomness is consulted at all.
/// </summary>
public sealed record SteadyAllyOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string TargetId { get; init; }
    public required string TargetName { get; init; }

    /// <summary>The public-channel id of the words of encouragement. Always present: the action requires one.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    /// <summary>The fear the steadying removed.</summary>
    public IReadOnlyList<FearChange> FearChanges { get; init; } = [];

    /// <summary>True when the ally was publicly Scared and this brought them back below the threshold.</summary>
    public bool NoLongerScared { get; init; }

    public override string OutcomeType => "steady_ally";

    public override string Summary
    {
        get
        {
            var recovered = NoLongerScared
                ? $" {TargetName} has their nerve back and no longer looks afraid."
                : "";
            return $"{ActorName} spent the whole turn steadying {TargetName}, who takes heart and is less " +
                   $"afraid than they were.{recovered} No dice were rolled. Nothing else changed: no damage, " +
                   $"no items, no odds altered, and {TargetName} still chooses their own actions.";
        }
    }
}
