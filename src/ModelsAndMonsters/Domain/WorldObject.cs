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
    /// <summary>
    /// The stable id of the room's ground-loot container — the floor everyone can reach, where dropped
    /// items lie in plain sight. Created on demand the first time something is dropped (v0.6). It reuses the
    /// ordinary container machinery, so a dropped item is recovered through the same <c>take_item</c>
    /// interaction as anything else in an open container, rather than through a parallel inventory system.
    /// </summary>
    public const string GroundId = "room-floor";

    /// <summary>Builds the room's ground-loot container holding the given items. Always open and marked as ground.</summary>
    public static Container Ground(ImmutableArray<InventoryItem> contents) => new()
    {
        Id = GroundId,
        Name = "the floor",
        Description = "The floor of the room, where dropped things lie in plain sight, within reach of anyone.",
        IsOpen = true,
        IsGround = true,
        Contents = contents
    };

    /// <summary>Whether the container is open. Closed containers hide their contents from characters.</summary>
    public bool IsOpen { get; init; }

    /// <summary>
    /// True for the room's ground-loot container (the floor). Its contents lie in the open — dropping is a
    /// public act — so, unlike an ordinary opened container, everyone present can see what is on the floor.
    /// </summary>
    public bool IsGround { get; init; }

    /// <summary>
    /// True for a fallen character's body — the lootable "container" the death-looting rule leaves behind. It
    /// is not a chest and must never be narrated as one: a body is not "opened" and is not "reached into". The
    /// flag lets narration and state formatting describe taking belongings from a body in plain in-world terms.
    /// </summary>
    public bool IsCorpse { get; init; }

    /// <summary>The items currently inside. Authoritative even while closed; only visibility depends on open state.</summary>
    public ImmutableArray<InventoryItem> Contents { get; init; } = [];

    /// <summary>
    /// An observer-neutral fact about the container's exterior — a faded maker's or purpose mark — that
    /// cannot be read from the general room description and is legible only to a character who spends a
    /// turn inspecting it closely. Null when the container has no such distinguishing mark (for example a
    /// corpse left by the death-looting rule). It never changes and is never revealed by opening; it is the
    /// discoverable payload of <c>inspect_object</c>.
    /// </summary>
    public string? ExteriorClue { get; init; }

    public override string Kind => "container";

    /// <summary>
    /// Finds an item inside this container by id, qualified display name, or plain name, case-insensitively
    /// — the qualified name first, so two same-named items can still be told apart.
    /// </summary>
    public InventoryItem? FindItem(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        var needle = idOrName.Trim();
        return Contents.FirstOrDefault(i => string.Equals(i.Id, needle, StringComparison.OrdinalIgnoreCase))
            ?? Contents.FirstOrDefault(i => string.Equals(i.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
            ?? Contents.FirstOrDefault(i => string.Equals(i.Name, needle, StringComparison.OrdinalIgnoreCase))
            ?? Contents.FirstOrDefault(i => i.MatchesReference(needle));
    }
}
