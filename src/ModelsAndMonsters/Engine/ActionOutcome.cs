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
    /// The environmental cover the target was sheltering behind when this attack was resolved, when there was
    /// one (v0.9). Null for an attack against an uncovered target — every field below it is meaningless then.
    /// </summary>
    public string? CoverId { get; init; }

    public string? CoverName { get; init; }

    /// <summary>
    /// The attacker's effective hit chance before cover — every existing modifier applied, none of them
    /// cover's. Equal to <see cref="HitChance"/> when <see cref="CoverId"/> is null.
    /// </summary>
    public int? PreCoverHitChance { get; init; }

    /// <summary>The cover's own hit-chance modifier folded in to reach <see cref="HitChance"/>. Negative.</summary>
    public int? CoverHitChanceModifier { get; init; }

    /// <summary>
    /// True when the raw roll would have hit without cover but did not beat the covered chance: the blow was
    /// turned aside by the cover rather than missing outright. <see cref="Hit"/> is false in this case, and no
    /// quality draw was made.
    /// </summary>
    public bool InterceptedByCover { get; init; }

    /// <summary>The cover's durability either side of this attack. Unchanged unless <see cref="InterceptedByCover"/>.</summary>
    public int? CoverDurabilityBefore { get; init; }

    public int? CoverDurabilityAfter { get; init; }

    /// <summary>True when this interception reduced the cover's durability to zero, exposing its occupant.</summary>
    public bool CoverDestroyed { get; init; }

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

            if (InterceptedByCover)
            {
                var destroyedNote = CoverDestroyed
                    ? $" The blow finally broke it — {CoverName} is now DESTROYED, and {TargetName} is exposed."
                    : $" {CoverName} is now damaged ({CoverDurabilityAfter} durability left).";
                return $"{AttackerName} struck at {TargetName}{via} with {WeaponName} — the blow would have landed " +
                       $"(rolled {HitRoll} against a pre-cover chance of {PreCoverHitChance}), but {CoverName} turned it " +
                       $"aside (covered chance {HitChance}). {TargetName} takes NO damage and remains behind {CoverName}." +
                       $"{destroyedNote}{redirect}";
            }

            if (!Hit)
            {
                var coverNote = CoverId is not null
                    ? $" {TargetName} was sheltering behind {CoverName}, but this was an ordinary miss — the roll " +
                      $"never reached even the pre-cover chance of {PreCoverHitChance}, so the cover changed nothing."
                    : "";
                return $"{AttackerName} attacked {TargetName}{via} with {WeaponName} but MISSED " +
                       $"(rolled {HitRoll} against {chance}). No damage. " +
                       $"{TargetName} is unharmed with {TargetHealthAfter}/{TargetMaxHealth} health.{coverNote}{redirect}";
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
            var coverBypassed = CoverId is not null
                ? $" The blow found {TargetName} despite {CoverName} (covered chance {HitChance})."
                : "";
            return $"{AttackerName} hit {TargetName}{via} with {WeaponName} — {quality} (rolled {HitRoll} against {chance}). " +
                   $"Weapon damage {WeaponDamage} minus armour {TargetArmour} = {BaseDamage}, " +
                   $"{DamageDealt} damage dealt. " +
                   $"{TargetName} health {TargetHealthBefore} -> {TargetHealthAfter}. {status}{defended}{applied}{injury}{morale}{loot}{coverBypassed}{redirect}";
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

    /// <summary>
    /// The name of the weapon the offerer was holding at the moment of acceptance, or null when they already
    /// held none. This is about MANDATORY DISARMAMENT — yielding always empties a raised hand, whatever the
    /// terms said — and is populated whether or not the weapon was part of the NEGOTIATED tribute; whether it
    /// was actually promised is the separate question <see cref="Domain.SurrenderAgreement.ForfeitedWeaponId"/>
    /// answers.
    /// </summary>
    public string? ForfeitedWeaponName { get; init; }

    /// <summary>Where the weapon now lies, whenever <see cref="ForfeitedWeaponName"/> is not null.</summary>
    public string? GroundContainerName { get; init; }

    /// <summary>
    /// True when the weapon was actually one of the negotiated terms (<c>offer.ForfeitWeapon</c>), false when
    /// it left the offerer's hand only because yielding disarms regardless of what was promised. Keeps the
    /// narrated <see cref="Summary"/> from implying every disarmament was part of the bargain — v0.10.
    /// </summary>
    public bool WeaponWasPromised { get; init; }

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
                ? $" {OffererName} had no weapon to give up."
                : WeaponWasPromised
                    ? $" {OffererName} gave up their {ForfeitedWeaponName} as promised, which now lies on " +
                      $"{GroundContainerName} and can be picked up by anyone; {OffererName} is now DISARMED and holds no weapon."
                    : $" {OffererName}'s {ForfeitedWeaponName} was not part of the bargain, but yielding empties a " +
                      $"raised hand regardless: it now lies on {GroundContainerName} and can be picked up by " +
                      $"anyone; {OffererName} is now DISARMED and holds no weapon.";
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

/// <summary>
/// The result of an accepted <c>take_cover</c> (v0.9). Public: everyone present sees the character take
/// shelter. No randomness. Occupancy is now authoritative and exclusive, and the public <c>InCover</c> status
/// is applied.
/// </summary>
public sealed record TakeCoverOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string CoverId { get; init; }
    public required string CoverName { get; init; }

    public override string OutcomeType => "take_cover";

    public override string Summary =>
        $"{ActorName} moved behind {CoverName} and took shelter there. It offers real protection while " +
        $"{ActorName} stays behind it and attempts nothing that would expose them.";
}

/// <summary>
/// The result of an accepted <c>leave_cover</c> (v0.9). Public: everyone present sees the character step out.
/// No randomness. The occupancy relationship and the <c>InCover</c> status both end.
/// </summary>
public sealed record LeaveCoverOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string CoverId { get; init; }
    public required string CoverName { get; init; }

    public override string OutcomeType => "leave_cover";

    public override string Summary =>
        $"{ActorName} deliberately stepped out from behind {CoverName}, giving up its protection. " +
        $"{ActorName} is now fully exposed.";
}

/// <summary>
/// The result of an accepted <c>damage_environmental_object</c> (v0.9). Public: everyone present sees the
/// blow land on the object. No hit or quality roll — a stationary object does not dodge — so the damage is
/// the deterministic <c>max(1, weapon damage - object armour)</c>. When this reduces durability to zero the
/// object is destroyed and, if anyone was sheltering there, they are exposed as part of the same event.
/// </summary>
public sealed record DamageEnvironmentalObjectOutcome : ActionOutcome
{
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required string WeaponName { get; init; }
    public required int WeaponDamage { get; init; }
    public required string ObjectId { get; init; }
    public required string ObjectName { get; init; }
    public required int ObjectArmour { get; init; }
    public required int DamageApplied { get; init; }
    public required int DurabilityBefore { get; init; }
    public required int DurabilityAfter { get; init; }
    public required bool Destroyed { get; init; }

    /// <summary>True when this same action also broke the actor's own cover, before the blow was struck.</summary>
    public required bool SelfCoverBroken { get; init; }

    /// <summary>The name of whoever was sheltering here when it was destroyed, when it was destroyed occupied.</summary>
    public string? ExposedOccupantName { get; init; }

    public override string OutcomeType => "damage_environmental_object";

    public override string Summary
    {
        get
        {
            var selfExposed = SelfCoverBroken ? $" {ActorName} stepped out from their own cover to swing." : "";
            var exposed = Destroyed && ExposedOccupantName is not null
                ? $" {ExposedOccupantName}, who was sheltering there, is now exposed."
                : "";
            var state = Destroyed ? "DESTROYED — reduced to wreckage" : $"damaged ({DurabilityAfter} durability left)";
            return $"{ActorName} struck {ObjectName} with {WeaponName} (damage {WeaponDamage} minus object armour " +
                   $"{ObjectArmour} = {DamageApplied}, no roll — a stationary object does not dodge). " +
                   $"{ObjectName} is now {state}.{exposed}{selfExposed}";
        }
    }
}
