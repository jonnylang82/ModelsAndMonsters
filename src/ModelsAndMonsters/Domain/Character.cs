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
    /// Chance out of 100 that this character's attacks land. Defaults to 100 (never misses) so code and
    /// tests that do not care about the roll keep the old always-hit behaviour; scenarios set it lower.
    /// It is a hidden mechanical stat — never shown to characters or narrated as a number.
    /// </summary>
    public int HitChance { get; init; } = 100;

    public Weapon? Weapon { get; init; }

    public ImmutableArray<InventoryItem> Inventory { get; init; } = [];

    public ImmutableArray<Injury> Injuries { get; init; } = [];

    public ImmutableArray<string> Abilities { get; init; } = [];

    public bool IsAlive => Health > 0;

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

    /// <summary>Finds an inventory item by id or name, case-insensitively.</summary>
    public InventoryItem? FindItem(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Inventory.FirstOrDefault(i =>
            string.Equals(i.Id, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the character is currently carrying a weapon with the given name.</summary>
    public bool HasWeaponNamed(string weaponName) =>
        Weapon is not null &&
        !string.IsNullOrWhiteSpace(weaponName) &&
        string.Equals(Weapon.Name, weaponName.Trim(), StringComparison.OrdinalIgnoreCase);
}
