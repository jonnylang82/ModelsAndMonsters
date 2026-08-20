namespace ModelsAndMonsters.Domain;

/// <summary>
/// The outcome of resolving an item reference against a collection of items: the item it named (if any),
/// and whether the reference was ambiguous. As with <see cref="ObjectResolution"/> and
/// <see cref="ExitResolution"/>, ambiguity is distinct from "not found" so it can be refused with a
/// different explanation — asking which one is meant rather than saying there is no such item.
/// </summary>
public readonly record struct ItemResolution(InventoryItem? Item, bool Ambiguous)
{
    public bool Found => Item is not null;

    public static ItemResolution None => new(null, Ambiguous: false);
}

/// <summary>
/// The one rule for turning a written item reference into an item, shared by character inventories and
/// container contents.
/// </summary>
/// <remarks>
/// It is shared because it was not, and the two halves drifted. Containers reported ambiguity and got a
/// refusal; inventories ran a chain of <c>FirstOrDefault</c> calls and silently returned whichever match sat
/// first. A live run walked straight into the gap: Vark carried two items both displaying as "Small Purse of
/// Gold Coins" — qualified "(the runt's)" and "(the captain's)" — and Rowan tried to steal "Vark's purse".
/// The Dungeon Master could see the problem and spent its whole output budget deliberating about it in prose
/// ("But which purse? The intent doesn't specify") instead of calling a tool, because the engine offered it
/// no way to ask. Had the same reference named two purses in a chest, it would simply have been refused.
/// </remarks>
public static class ItemReference
{
    /// <summary>
    /// Resolves a reference by id, then qualified display name, then plain name, then rendered-annotation
    /// peeling, reporting ambiguity rather than guessing.
    /// </summary>
    /// <remarks>
    /// The order is what makes duplicate names workable rather than merely refused. An exact id match always
    /// wins and is never ambiguous. The qualified name is tried next, so "Small Purse of Gold Coins (the
    /// captain's)" picks exactly that purse even though two purses share the plain name — without this step
    /// two identically named items would be permanently unreferenceable. Only the unqualified plain name,
    /// which genuinely does name both, comes back ambiguous.
    /// </remarks>
    public static ItemResolution Resolve(IEnumerable<InventoryItem> items, string? reference)
    {
        if (items is null || string.IsNullOrWhiteSpace(reference))
        {
            return ItemResolution.None;
        }

        var needle = reference.Trim();
        var candidates = items as IReadOnlyList<InventoryItem> ?? items.ToList();

        var byId = candidates.FirstOrDefault(i => string.Equals(i.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return new ItemResolution(byId, Ambiguous: false);
        }

        var byDisplayName = candidates
            .Where(i => string.Equals(i.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byDisplayName.Count == 1)
        {
            return new ItemResolution(byDisplayName[0], Ambiguous: false);
        }

        var byName = candidates
            .Where(i => string.Equals(i.Name, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 0)
        {
            // Last resort: the reference may carry a rendered annotation the model copied out of the state.
            byName = [.. candidates.Where(i => i.MatchesReference(needle))];
        }

        return byName.Count switch
        {
            0 => ItemResolution.None,
            1 => new ItemResolution(byName[0], Ambiguous: false),
            _ => new ItemResolution(null, Ambiguous: true)
        };
    }
}
