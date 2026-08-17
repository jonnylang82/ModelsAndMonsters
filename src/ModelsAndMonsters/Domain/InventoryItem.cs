namespace ModelsAndMonsters.Domain;

/// <summary>
/// An item in a character's inventory.
/// </summary>
/// <param name="Id">Stable identifier, unique within the owning inventory.</param>
/// <param name="Name">Name a character would use when talking about the item.</param>
/// <param name="Description">Short in-world description.</param>
/// <param name="HealingAmount">
/// When set, using the item restores this much health and consumes the item.
/// This is the only item effect the v0.1 engine understands.
/// </param>
public sealed record InventoryItem(string Id, string Name, string Description, int? HealingAmount = null)
{
    public bool IsHealingItem => HealingAmount is > 0;
}
