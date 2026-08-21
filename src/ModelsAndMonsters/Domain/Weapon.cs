namespace ModelsAndMonsters.Domain;

/// <summary>
/// A weapon carried by a character. Weapons have no properties beyond a flat damage value and a stable id.
/// </summary>
/// <remarks>
/// The id exists because v0.7 can move a weapon out of a character's hands: an accepted surrender that
/// promised weapon forfeiture puts it on the room's floor as an inert trophy. That movement has to be
/// traceable to one identity, with no second weapon entity created, so a weapon is identified by
/// <see cref="Id"/> rather than by its name. It defaults to a slug of the name so every existing positional
/// construction keeps its meaning without naming an id.
/// </remarks>
public sealed record Weapon(string Name, int Damage)
{
    private readonly string? _id;

    /// <summary>
    /// Stable identifier. Defaults to a slug derived from <see cref="Name"/>, so a scenario need not supply
    /// one and pre-v0.7 constructions are unchanged.
    /// </summary>
    public string Id
    {
        get => string.IsNullOrWhiteSpace(_id) ? SlugFor(Name) : _id;
        init => _id = value;
    }

    /// <summary>The default id for a weapon of the given name, e.g. "Notched Sabre" becomes "weapon-notched-sabre".</summary>
    public static string SlugFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "weapon";
        }

        var slug = new string([.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return $"weapon-{slug.Trim('-')}";
    }

    /// <summary>
    /// The inert ground item a forfeited weapon becomes. It keeps the weapon's stable id, so the item on the
    /// floor and the weapon that left the hand are provably the same thing. A looted weapon is a trophy, not
    /// an equippable weapon: v0.7 has no equipping.
    /// </summary>
    public InventoryItem AsForfeitedItem() => new(
        Id,
        Name,
        $"A {Name.ToLowerInvariant()}, given up in surrender and no longer wielded by anyone.",
        IsWeaponTrophy: true);
}
