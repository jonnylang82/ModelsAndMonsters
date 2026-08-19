namespace ModelsAndMonsters.Domain;

/// <summary>
/// A way out of the encounter. In v0.5 there is exactly one — the cellar stair door — and passing through
/// it removes a character from the fight rather than moving them into a second room.
/// </summary>
/// <remarks>
/// An exit is deliberately not a <see cref="WorldObject"/>: it is not opened or looted like a container, it
/// is a threshold that is either shut or standing open. Forcing it into the container hierarchy would be an
/// unnatural abstraction, so it is its own small record living on <see cref="Room.Exits"/>. There is no
/// lock, no destination room, and no way to close it again once open — those are all out of scope for v0.5.
/// </remarks>
public sealed record EncounterExit
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Whether the exit stands open. It must be open before anyone can pass through it. Starts closed.</summary>
    public bool IsOpen { get; init; }

    /// <summary>Where passing through leads — described, never simulated. "Outside the encounter" in v0.5.</summary>
    public required string DestinationDescription { get; init; }

    /// <summary>True when <paramref name="idOrName"/> matches this exit's id or name, case-insensitively.</summary>
    public bool Matches(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return false;
        }

        var needle = idOrName.Trim();
        return string.Equals(Id, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Name, needle, StringComparison.OrdinalIgnoreCase);
    }
}
