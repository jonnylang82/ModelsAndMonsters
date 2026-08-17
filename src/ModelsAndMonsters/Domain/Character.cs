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
