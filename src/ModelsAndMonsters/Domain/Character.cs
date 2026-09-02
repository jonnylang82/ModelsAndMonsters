using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// Authoritative mechanical state for one character.
/// </summary>
/// <remarks>
/// This record deliberately contains no personality, backstory or model configuration. Those live in
/// <see cref="ModelsAndMonsters.Configuration.CharacterDefinition"/> because they are scenario inputs
/// rather than mutable world state, and keeping them out keeps trace state snapshots small and readable.
/// </remarks>
public sealed record Character
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CharacterRole Role { get; init; }

    /// <summary>
    /// The side this character fights on. Two characters are allies when their teams match and enemies
    /// when they differ; teams are what the encounter's terminal condition is evaluated over. When a
    /// scenario does not set one it falls back to a label derived from <see cref="Role"/>, so a v0.1
    /// hero/monster pair still forms two opposing teams without any extra configuration.
    /// </summary>
    public string Team
    {
        get => string.IsNullOrWhiteSpace(_team) ? DefaultTeamForRole(Role) : _team;
        init => _team = value;
    }

    private readonly string? _team;

    public required int MaxHealth { get; init; }

    public required int Health { get; init; }

    public required int Armour { get; init; }

    /// <summary>
    /// Encounter-scoped morale, 0–5. Authoritative engine state: only the engine moves it, always through one
    /// clamped change with a recorded cause, and no model output can touch it. The exact number belongs to
    /// this character, the harness, the trace and the report; opponents see only the public
    /// <see cref="StatusEffectKind.Scared"/> status once it reaches <see cref="FearRules.ScaredThreshold"/>.
    /// </summary>
    public int Fear
    {
        get => _fear;
        init => _fear = FearRules.Clamp(value);
    }

    private readonly int _fear;

    /// <summary>
    /// Visibly scared right now: at or above the threshold AND still in the fight. Derived, never stored.
    /// </summary>
    /// <remarks>
    /// The presence of <see cref="CanAct"/> here is what keeps this in step with the public
    /// <see cref="StatusEffectKind.Scared"/> status, which the engine sweeps away when a character dies,
    /// yields or flees. Without it, somebody who surrendered at fear 4 would report as scared with nothing
    /// on them to show it — the derived flag and its own public shadow disagreeing, which is precisely the
    /// contradiction <see cref="CharacterDisposition"/> exists to prevent. Their <see cref="Fear"/> is
    /// untouched: the report and the final state keep the nerve they left the fight with.
    /// </remarks>
    public bool IsScared => CanAct && FearRules.IsScared(Fear);

    /// <summary>
    /// Whether this character was outnumbered among the ACTIVE combatants the last time the engine looked.
    /// A latch, not a live calculation: fear rises on the transition from false to true, so the engine has to
    /// remember what it last saw. Seeded from the opening state, so a character who starts outnumbered does
    /// not begin the fight already frightened by it.
    /// </summary>
    public bool IsOutnumbered { get; init; }

    /// <summary>
    /// Chance out of 100 that this character's attacks land. Defaults to 100 (never misses) so code and
    /// tests that do not care about the roll keep the old always-hit behaviour; scenarios set it lower.
    /// It is a hidden mechanical stat — never shown to characters or narrated as a number.
    /// </summary>
    public int HitChance { get; init; } = 100;

    public Weapon? Weapon { get; init; }

    public ImmutableArray<InventoryItem> Inventory { get; init; } = [];

    public ImmutableArray<Injury> Injuries { get; init; } = [];

    /// <summary>
    /// The abilities this character holds, with their remaining charges. Authoritative state: a limited
    /// ability's charge is spent here when the engine resolves an accepted use, and nowhere else.
    /// </summary>
    public ImmutableArray<CharacterAbility> Abilities { get; init; } = [];

    /// <summary>
    /// How this character stands in the encounter — the single authoritative type for its standing. When a
    /// scenario or test does not set one, it derives from health: a character at zero health is
    /// <see cref="CharacterDisposition.Dead"/>, otherwise <see cref="CharacterDisposition.Active"/>. That
    /// derivation is what keeps every pre-v0.5 construction (which set only <see cref="Health"/>) meaning
    /// exactly what it did before, while surrender and escape set the disposition explicitly without
    /// touching health.
    /// </summary>
    public CharacterDisposition Disposition
    {
        get => _disposition ?? (Health > 0 ? CharacterDisposition.Active : CharacterDisposition.Dead);
        init => _disposition = value;
    }

    private readonly CharacterDisposition? _disposition;

    /// <summary>
    /// The id of the exit this character left through, when it has <see cref="CharacterDisposition.Escaped"/>.
    /// Null for everyone still in the encounter. Recorded so a withdrawal can be traced and reported to the
    /// exact door used.
    /// </summary>
    public string? EscapedThroughExitId { get; init; }

    /// <summary>Alive unless dead. A surrendered or escaped character is still alive.</summary>
    public bool IsAlive => Disposition != CharacterDisposition.Dead;

    /// <summary>Physically in the room: an active or surrendered character. The escaped and the dead are not present.</summary>
    public bool IsPresent => Disposition is CharacterDisposition.Active or CharacterDisposition.Surrendered
        or CharacterDisposition.Detained;

    /// <summary>Able to take a turn — only an active character acts.</summary>
    public bool CanAct => Disposition == CharacterDisposition.Active;

    /// <summary>A valid target for a combat action. In v0.5 only an active character may be attacked.</summary>
    public bool IsCombatTarget => Disposition == CharacterDisposition.Active;

    /// <summary>The default team label for a role, used when a scenario leaves the team unset.</summary>
    public static string DefaultTeamForRole(CharacterRole role) => role switch
    {
        CharacterRole.Hero => "Heroes",
        CharacterRole.Monster => "Monsters",
        _ => role.ToString()
    };

    /// <summary>True when the other character fights on the same side as this one.</summary>
    public bool IsAllyOf(Character other) =>
        other is not null && string.Equals(Team, other.Team, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves an inventory item reference, reporting ambiguity rather than guessing — the same discipline
    /// the engine applies to characters, objects, exits and container contents. See
    /// <see cref="ItemReference.Resolve"/> for the matching order.
    /// </summary>
    public ItemResolution ResolveItem(string? idOrName) => ItemReference.Resolve(Inventory, idOrName);

    /// <summary>
    /// Finds an inventory item by reference, or null when the reference names none — or names more than one.
    /// </summary>
    /// <remarks>
    /// For callers that only need the item and have no way to refuse. An ambiguous reference yields null
    /// here rather than a first match, so nothing downstream can act on a purse the reference did not
    /// single out; callers that must explain themselves should use <see cref="ResolveItem"/> and refuse the
    /// ambiguity in their own words.
    /// </remarks>
    public InventoryItem? FindItem(string idOrName) => ResolveItem(idOrName).Item;

    /// <summary>True when the character is currently carrying a weapon with the given name.</summary>
    public bool HasWeaponNamed(string weaponName) =>
        Weapon is not null &&
        !string.IsNullOrWhiteSpace(weaponName) &&
        string.Equals(Weapon.Name, weaponName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this character holds no weapon. Only an accepted surrender that promised weapon forfeiture
    /// disarms anyone in v0.7 — there are no disarming attacks and no weapon swapping.
    /// </summary>
    public bool IsDisarmed => Weapon is null;

    /// <summary>
    /// Finds one of this character's abilities by stable id or by name, case-insensitively. Returns null when
    /// the character does not hold it at all, which is distinct from holding it with no charges left.
    /// </summary>
    public CharacterAbility? FindAbility(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Abilities.FirstOrDefault(a => string.Equals(a.AbilityId, needle, StringComparison.OrdinalIgnoreCase))
            ?? Abilities.FirstOrDefault(a => string.Equals(a.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns a new character with one ability's remaining charges replaced. Unlimited abilities are untouched.</summary>
    public Character WithAbilityCharges(string abilityId, int? remainingUses)
    {
        for (var index = 0; index < Abilities.Length; index++)
        {
            if (string.Equals(Abilities[index].AbilityId, abilityId, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Abilities = Abilities.SetItem(index, Abilities[index] with { RemainingUses = remainingUses }) };
            }
        }

        return this;
    }
}
