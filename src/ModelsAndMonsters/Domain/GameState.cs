using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// A complete, immutable snapshot of the authoritative world.
/// </summary>
/// <remarks>
/// The state is immutable so that "before" and "after" snapshots can be traced without defensive
/// copying, and so an accepted action provably produces exactly one new state.
/// </remarks>
public sealed record GameState
{
    public required Room Room { get; init; }

    public required ImmutableArray<Character> Characters { get; init; }

    /// <summary>Incremented every time the engine accepts and applies an action.</summary>
    public int Version { get; init; }

    public Character? FindById(string id) =>
        Characters.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a character by id or by name, case-insensitively. The Dungeon Master refers to
    /// characters by name, so the engine must accept both and reject anything it cannot resolve.
    /// </summary>
    public Character? Resolve(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Characters.FirstOrDefault(c => string.Equals(c.Id, needle, StringComparison.OrdinalIgnoreCase))
            ?? Characters.FirstOrDefault(c => string.Equals(c.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    public Character RequireById(string id) =>
        FindById(id) ?? throw new InvalidOperationException($"No character with id '{id}' exists in the current state.");

    /// <summary>The distinct teams present in the encounter, in first-appearance order.</summary>
    public IReadOnlyList<string> Teams()
    {
        var seen = new List<string>();
        foreach (var character in Characters)
        {
            if (!seen.Contains(character.Team, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(character.Team);
            }
        }

        return seen;
    }

    /// <summary>Living members of a team.</summary>
    public IEnumerable<Character> LivingOnTeam(string team) =>
        Characters.Where(c => c.IsAlive && string.Equals(c.Team, team, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the character of the same id.</summary>
    public GameState WithCharacter(Character updated)
    {
        for (var index = 0; index < Characters.Length; index++)
        {
            if (string.Equals(Characters[index].Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Characters = Characters.SetItem(index, updated) };
            }
        }

        throw new InvalidOperationException($"No character with id '{updated.Id}' exists in the current state.");
    }

    /// <summary>The world objects present in the room.</summary>
    public ImmutableArray<WorldObject> Objects => Room.Objects;

    /// <summary>
    /// Resolves a world object by id or by name, case-insensitively, reporting ambiguity rather than
    /// guessing. An exact id match always wins and is never ambiguous; failing that, a name is matched,
    /// and a name shared by two or more objects resolves to nothing with <c>Ambiguous</c> set so the
    /// engine can refuse it. This mirrors how characters are resolved: the engine never silently picks
    /// one of several equally valid objects.
    /// </summary>
    public ObjectResolution ResolveObject(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return new ObjectResolution(null, Ambiguous: false);
        }

        var needle = idOrName.Trim();

        var byId = Room.Objects.FirstOrDefault(o => string.Equals(o.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return new ObjectResolution(byId, Ambiguous: false);
        }

        var byName = Room.Objects
            .Where(o => string.Equals(o.Name, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            0 => new ObjectResolution(null, Ambiguous: false),
            1 => new ObjectResolution(byName[0], Ambiguous: false),
            _ => new ObjectResolution(null, Ambiguous: true)
        };
    }

    /// <summary>Returns a new state with <paramref name="updated"/> replacing the container of the same id in the room.</summary>
    public GameState WithContainer(Container updated)
    {
        var objects = Room.Objects;
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index] is Container existing &&
                string.Equals(existing.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
            {
                return this with { Room = Room with { Objects = objects.SetItem(index, updated) } };
            }
        }

        throw new InvalidOperationException($"No container with id '{updated.Id}' exists in the current room.");
    }
}

/// <summary>
/// The outcome of resolving a world-object reference: the object it named (if any), and whether the
/// reference was ambiguous. Ambiguity is distinct from "not found" because it must be refused with a
/// different explanation — asking the character to say which one they mean rather than saying there is
/// no such object.
/// </summary>
public readonly record struct ObjectResolution(WorldObject? Object, bool Ambiguous)
{
    public bool Found => Object is not null;
}
