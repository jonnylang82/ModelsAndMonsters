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
/// <param name="Qualifier">
/// An optional short in-world tag that tells this item apart from others with the same <paramref name="Name"/>
/// — "Rowan's", "the captain's". Authored in the scenario, and deliberately fixed for the life of the item: a
/// purse cut from Rowan's belt is still the one that was Rowan's whoever ends up holding it.
/// </param>
/// <remarks>
/// The qualifier exists because a live run gave a character an inventory that read, in full, "Small Purse of
/// Gold Coins, Small Purse of Gold Coins". Four characters start with identically named purses, and once two
/// land in one pair of hands nothing in the character's own state can tell them apart — the user's note was
/// that characters "lost track of where purses were". Stable ids solve this for the report, but an id can
/// never be shown to a character, so the fiction needs its own way to say which purse is which.
/// </remarks>
public sealed record InventoryItem(
    string Id,
    string Name,
    string Description,
    int? HealingAmount = null,
    string? Qualifier = null)
{
    public bool IsHealingItem => HealingAmount is > 0;

    /// <summary>
    /// The name to show a character: the plain name, plus the qualifier when the scenario gave it one.
    /// Always rendered the same way wherever the item appears, so one purse never reads as two different
    /// things in the room description and in a character's own hands.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Qualifier) ? Name : $"{Name} ({Qualifier.Trim()})";

    /// <summary>
    /// Whether a reference names this item: its id, its qualified display name, or its plain name — and then,
    /// failing all three, the same names with trailing parenthetical annotations peeled off one at a time.
    /// </summary>
    /// <remarks>
    /// The state a model reads is rendered, and a model copies what it reads. A live run had the Dungeon
    /// Master pass <c>"Vial of Goblin Salve (restores 4 health)"</c> — the exact string the state block shows,
    /// annotation and all — and the engine answered <c>ItemNotPossessed</c>, so a goblin was told the vial at
    /// his neck was not there. He then spent a question asking where his own salve was. Every rendered
    /// decoration is a reference a model may hand back, and the qualifier introduced alongside this made a
    /// second one. Peeling is ordered, never fuzzy: exact matches win first, so
    /// <c>"...Coins (Rowan's)"</c> still resolves to Rowan's purse rather than to whichever purse sits first.
    /// </remarks>
    public bool MatchesReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var needle = reference.Trim();
        if (string.Equals(Id, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(DisplayName, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Name, needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        while (TryPeelAnnotation(needle, out needle))
        {
            if (string.Equals(DisplayName, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Name, needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes one trailing "(...)" group, if the text ends in one.</summary>
    private static bool TryPeelAnnotation(string text, out string peeled)
    {
        peeled = text;
        if (!text.EndsWith(')'))
        {
            return false;
        }

        var open = text.LastIndexOf('(');
        if (open <= 0)
        {
            return false;
        }

        peeled = text[..open].TrimEnd();
        return peeled.Length > 0;
    }
}
