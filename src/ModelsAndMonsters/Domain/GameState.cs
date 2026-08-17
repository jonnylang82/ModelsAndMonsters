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
}
