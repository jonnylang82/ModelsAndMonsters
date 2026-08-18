using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// A persistent, non-character thing that lives in a room and can be interacted with.
/// </summary>
/// <remarks>
/// <para>
/// This is the minimum reusable object representation the v0.3 slice needs, not a general item engine.
/// The only concrete kind so far is <see cref="Container"/>; the base type exists so a room can hold a
/// heterogeneous <c>ImmutableArray&lt;WorldObject&gt;</c> and so later kinds can be added without changing
/// how the room stores them.
/// </para>
/// <para>
/// The polymorphism attributes make System.Text.Json write a <c>$type</c> discriminator and the derived
/// type's own members when the declared type is the base — which is exactly the case for a room's object
/// array. Without them a container would serialise as only its base fields and its open state and contents
/// would silently vanish from the trace, final-state and report.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Container), "container")]
public abstract record WorldObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>A short, stable kind label, useful in traces and prose without a type check.</summary>
    [JsonIgnore]
    public abstract string Kind { get; }

    /// <summary>True when <paramref name="idOrName"/> matches this object's id or name, case-insensitively.</summary>
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

/// <summary>
/// A container that may be opened, revealing the items inside it.
/// </summary>
/// <remarks>
/// Contents reuse <see cref="InventoryItem"/> rather than a parallel item type, so an item taken from a
/// container is the very same record that then lives in a character's inventory and that the existing
/// <c>use_item</c> action already understands. There is no lock, trap or key state: v0.3 containers open
/// normally, and adding such state is explicitly out of scope.
/// </remarks>
public sealed record Container : WorldObject
{
    /// <summary>Whether the container is open. Closed containers hide their contents from characters.</summary>
    public bool IsOpen { get; init; }

    /// <summary>The items currently inside. Authoritative even while closed; only visibility depends on open state.</summary>
    public ImmutableArray<InventoryItem> Contents { get; init; } = [];

    public override string Kind => "container";

    /// <summary>Finds an item inside this container by id or name, case-insensitively.</summary>
    public InventoryItem? FindItem(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Contents.FirstOrDefault(i =>
            string.Equals(i.Id, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Name, needle, StringComparison.OrdinalIgnoreCase));
    }
}
