namespace ModelsAndMonsters.Domain;

/// <summary>
/// A weapon carried by a character. v0.1 weapons have no properties beyond a flat damage value.
/// </summary>
public sealed record Weapon(string Name, int Damage);
