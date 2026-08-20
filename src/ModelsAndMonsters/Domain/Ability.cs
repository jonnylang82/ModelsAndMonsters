using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// What kind of thing an ability is, in the fiction. Categories carry no mechanics of their own — they exist
/// so the Rulebook Resolver can recognise "a prayer", "a trick", "an order" from a character's own words.
/// </summary>
public enum AbilityCategory
{
    /// <summary>A learned martial technique.</summary>
    Technique,

    /// <summary>A prayer or spell.</summary>
    Spell,

    /// <summary>An order shouted at an ally.</summary>
    Command,

    /// <summary>A dirty, opportunistic trick.</summary>
    Trick,

    /// <summary>A plain combat option everybody has.</summary>
    BasicAction
}

/// <summary>
/// The concrete engine effects an ability may have. Deliberately a small closed set with one engine handler
/// each: there is no scripting language and no model-defined effect.
/// </summary>
public enum AbilityEffectKind
{
    /// <summary>Creates the linked Guarding/Guarded relationship, redirecting one attack aimed at the ally.</summary>
    GuardAlly,

    /// <summary>Restores a fixed amount of health, capped at maximum. No randomness.</summary>
    Heal,

    /// <summary>Applies <see cref="StatusEffectKind.Rallied"/> to an ally.</summary>
    Rally,

    /// <summary>Performs one ordinary weapon attack and, on a hit, applies <see cref="StatusEffectKind.OffBalance"/>.</summary>
    StrikeAndOffBalance,

    /// <summary>Applies <see cref="StatusEffectKind.Defending"/> to the actor.</summary>
    Defend
}

/// <summary>Who an ability may be used on. Validated authoritatively by the engine.</summary>
public enum AbilityTargetRule
{
    /// <summary>No target: the ability applies to the actor alone (Defend).</summary>
    SelfOnly,

    /// <summary>The actor, or one active, living, present ally.</summary>
    SelfOrAlly,

    /// <summary>One active, living, present ally — never the actor.</summary>
    OtherAlly,

    /// <summary>One active, living, present opponent.</summary>
    Opponent
}

/// <summary>
/// The definition of one ability: what it is, who it may be aimed at, how often it may be used and which
/// concrete engine handler resolves it. Definitions are static scenario data, not mutable world state — a
/// character's remaining charges live on <see cref="CharacterAbility"/>.
/// </summary>
public sealed record AbilityDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AbilityCategory Category { get; init; }

    /// <summary>A natural-language account, used in prompts and in the ability's rule card.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// How to refer to the ability in a line a character actually reads — "standing over a companion" rather
    /// than "Guard Ally".
    /// </summary>
    /// <remarks>
    /// A refusal that named the mechanical label produced exactly the leak this project keeps closing: a live
    /// run had the Dungeon Master tell a cleric "only Rowan possesses the Guard Ally ability needed to stand
    /// between you and harm". Nobody in the world can perceive an ability list, so engine refusals describe
    /// the deed instead, and the mechanical name stays inside the machinery.
    /// </remarks>
    public required string InWorldName { get; init; }

    public required AbilityTargetRule TargetRule { get; init; }

    /// <summary>Uses allowed per encounter, or null when the ability is unlimited.</summary>
    public int? MaxUsesPerEncounter { get; init; }

    public required AbilityEffectKind EffectKind { get; init; }

    /// <summary>The effect's magnitude: health restored, hit-chance modifier, damage reduction. 0 when it has none.</summary>
    public int EffectValue { get; init; }

    /// <summary>True when resolving the ability makes the ordinary attack draws. Never an extra parallel roll.</summary>
    public bool UsesAttackRng { get; init; }

    public StatusVisibility Visibility { get; init; } = StatusVisibility.Public;

    /// <summary>The rule card that governs this ability, so guidance and ability stay in step.</summary>
    public required string RuleId { get; init; }

    public bool IsUnlimited => MaxUsesPerEncounter is null;
}

/// <summary>
/// One character's hold on an ability: which ability, and how many uses are left this encounter. This is
/// authoritative mutable state (charges are spent), unlike the shared <see cref="AbilityDefinition"/>.
/// </summary>
/// <remarks>
/// The name and category are denormalised onto the record so a state snapshot, a report or the observer UI
/// reads without a catalog lookup, which keeps <c>final-state.json</c> self-describing.
/// </remarks>
public sealed record CharacterAbility
{
    public required string AbilityId { get; init; }

    public required string Name { get; init; }

    public required AbilityCategory Category { get; init; }

    /// <summary>Uses allowed this encounter, or null when unlimited.</summary>
    public int? MaxUses { get; init; }

    /// <summary>Uses left this encounter, or null when unlimited.</summary>
    public int? RemainingUses { get; init; }

    /// <summary>True when the ability may still be used: unlimited, or with a charge left.</summary>
    public bool HasChargeLeft => RemainingUses is null || RemainingUses > 0;

    /// <summary>Uses left rendered for a prompt or a card: "unlimited", "1 use left", "none left".</summary>
    public string DescribeUses() => RemainingUses switch
    {
        null => "unlimited",
        0 => "none left",
        1 => "1 use left",
        var n => $"{n} uses left"
    };

    /// <summary>Builds a character's starting hold on a definition, with its charges full.</summary>
    public static CharacterAbility From(AbilityDefinition definition) => new()
    {
        AbilityId = definition.Id,
        Name = definition.Name,
        Category = definition.Category,
        MaxUses = definition.MaxUsesPerEncounter,
        RemainingUses = definition.MaxUsesPerEncounter
    };
}

/// <summary>
/// The built-in ability book: one definition per supported ability, with a concrete engine handler behind
/// each. There is no way to define a new ability at runtime and no scripting: adding one means adding an
/// <see cref="AbilityEffectKind"/> and its handler in the engine, deliberately.
/// </summary>
public static class AbilityCatalog
{
    public const string GuardAllyId = "guard-ally";
    public const string HealingPrayerId = "healing-prayer";
    public const string RallyGruntId = "rally-grunt";
    public const string DirtyStrikeId = "dirty-strike";
    public const string DefendId = "defend";

    /// <summary>The hit-chance swing Rally Grunt grants and Dirty Strike inflicts.</summary>
    public const int HitChanceSwing = 15;

    /// <summary>The health Healing Prayer restores.</summary>
    public const int HealingPrayerAmount = 4;

    /// <summary>The damage a Defending character turns aside from the next blow that lands.</summary>
    public const int DefendReduction = 1;

    public static readonly AbilityDefinition GuardAlly = new()
    {
        Id = GuardAllyId,
        Name = "Guard Ally",
        InWorldName = "standing over a companion to take the blow meant for them",
        Category = AbilityCategory.Technique,
        Description =
            "Spend the whole turn standing over one companion, so the next blow an enemy aims at them lands on " +
            "the guardian instead. It protects one ally at a time, is used up by the one attack it turns aside, " +
            "and falls away at the start of the guardian's next turn. It can be done again and again — its cost " +
            "is the guardian's entire turn and the wound they take in the ally's place.",
        TargetRule = AbilityTargetRule.OtherAlly,
        MaxUsesPerEncounter = null,
        EffectKind = AbilityEffectKind.GuardAlly,
        EffectValue = 0,
        UsesAttackRng = false,
        RuleId = "ability.guard-ally"
    };

    public static readonly AbilityDefinition HealingPrayer = new()
    {
        Id = HealingPrayerId,
        Name = "Healing Prayer",
        InWorldName = "praying a wound closed",
        Category = AbilityCategory.Spell,
        // Deliberately number-free: this text goes into a character's own prompt, and a description that
        // named the exact figure had the character say it out loud mid-fight ("four health restore to me").
        // The rule card carries the mechanics; this carries what the character would actually feel and know.
        Description =
            "A spoken prayer that knits a wound closed — enough to take a bad gash back to a scratch, on the " +
            "caster or one companion. It can be worked once in an encounter. It cannot raise the dead, cannot " +
            "reach someone who has fled, cannot make anyone sounder than whole, and is wasted on someone unhurt.",
        TargetRule = AbilityTargetRule.SelfOrAlly,
        MaxUsesPerEncounter = 1,
        EffectKind = AbilityEffectKind.Heal,
        EffectValue = HealingPrayerAmount,
        UsesAttackRng = false,
        RuleId = "ability.healing-prayer"
    };

    public static readonly AbilityDefinition RallyGrunt = new()
    {
        Id = RallyGruntId,
        Name = "Rally Grunt",
        InWorldName = "barking an order that steadies a companion",
        Category = AbilityCategory.Command,
        Description =
            "A barked order that steadies one companion, making their next attack markedly more likely to land. " +
            "It can be given once in an encounter, and it is used up by that companion's next attack whether it " +
            "lands or misses.",
        TargetRule = AbilityTargetRule.OtherAlly,
        MaxUsesPerEncounter = 1,
        EffectKind = AbilityEffectKind.Rally,
        EffectValue = HitChanceSwing,
        UsesAttackRng = false,
        RuleId = "ability.rally-grunt"
    };

    public static readonly AbilityDefinition DirtyStrike = new()
    {
        Id = DirtyStrikeId,
        Name = "Dirty Strike",
        InWorldName = "a foul blow that leaves a foe off balance",
        Category = AbilityCategory.Trick,
        Description =
            "One underhanded blow with the weapon in hand — a kick to the knee behind the strike — that, when it " +
            "connects, leaves the enemy off balance so their next attack is markedly less likely to land. It can " +
            "be tried once in an encounter, and the chance is spent whether the blow lands or misses.",
        TargetRule = AbilityTargetRule.Opponent,
        MaxUsesPerEncounter = 1,
        EffectKind = AbilityEffectKind.StrikeAndOffBalance,
        EffectValue = HitChanceSwing,
        UsesAttackRng = true,
        RuleId = "ability.dirty-strike"
    };

    public static readonly AbilityDefinition Defend = new()
    {
        Id = DefendId,
        Name = "Defend",
        InWorldName = "bracing behind your guard",
        Category = AbilityCategory.BasicAction,
        Description =
            "Spend the whole turn braced behind guard rather than striking, so the next blow that does land " +
            "glances off for one less wound. Anyone can do it, as often as they like; each time costs the whole " +
            "turn, it is used up by the next blow that lands, it survives a miss, and it falls away at the start " +
            "of the defender's next turn.",
        TargetRule = AbilityTargetRule.SelfOnly,
        MaxUsesPerEncounter = null,
        EffectKind = AbilityEffectKind.Defend,
        EffectValue = DefendReduction,
        UsesAttackRng = false,
        RuleId = "combat.defend"
    };

    /// <summary>Every ability the engine can resolve, in a stable order.</summary>
    public static readonly ImmutableArray<AbilityDefinition> All =
        [GuardAlly, HealingPrayer, RallyGrunt, DirtyStrike, Defend];

    private static readonly Dictionary<string, AbilityDefinition> ById =
        All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a definition by its stable id, or null when no such ability exists.</summary>
    public static AbilityDefinition? Find(string? abilityId) =>
        !string.IsNullOrWhiteSpace(abilityId) && ById.TryGetValue(abilityId.Trim(), out var definition)
            ? definition
            : null;

    /// <summary>
    /// Resolves an ability reference by stable id or by name, case-insensitively — the same latitude the
    /// engine gives character, item and exit references, because the Dungeon Master speaks in names.
    /// </summary>
    public static AbilityDefinition? Resolve(string? idOrName)
    {
        if (Find(idOrName) is { } byId)
        {
            return byId;
        }

        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return All.FirstOrDefault(a => string.Equals(a.Name, needle, StringComparison.OrdinalIgnoreCase));
    }
}
