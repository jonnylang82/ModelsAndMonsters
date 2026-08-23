namespace ModelsAndMonsters.Configuration;

/// <summary>
/// The seeded scenario, bound from the "Scenario" configuration section (see scenario.json).
/// </summary>
/// <remarks>
/// Character definitions carry more fields than v0.1 uses mechanically. Persona text is fed into the
/// character system prompt; it never enters authoritative game state.
/// </remarks>
public sealed class ScenarioDefinition
{
    public const string SectionName = "Scenario";

    public string Id { get; set; } = "unnamed-scenario";

    public string Name { get; set; } = "Unnamed scenario";

    public string Summary { get; set; } = "";

    public RoomDefinition Room { get; set; } = new();

    public List<CharacterDefinition> Characters { get; set; } = [];
}

public sealed class RoomDefinition
{
    public string Id { get; set; } = "room";

    public string Name { get; set; } = "A room";

    public string Description { get; set; } = "";

    public List<string> Features { get; set; } = [];

    /// <summary>Persistent containers seeded into the room, such as the contested chest.</summary>
    public List<ContainerDefinition> Containers { get; set; } = [];

    /// <summary>Ways out of the encounter seeded into the room, such as the cellar stair door.</summary>
    public List<ExitDefinition> Exits { get; set; } = [];

    /// <summary>Environmental cover seeded into the room, such as the overturned mill workbench (v0.9).</summary>
    public List<CoverDefinition> Cover { get; set; } = [];
}

/// <summary>
/// An environmental cover object seeded into a room (v0.9). Preserve the seeded mechanical values unless
/// testing exposes a clear problem — they are what the v0.9 spec calibrated the vertical slice against.
/// </summary>
public sealed class CoverDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>How many characters can shelter here at once. The v0.9 slice seeds exactly one.</summary>
    public int Capacity { get; set; } = 1;

    /// <summary>Added to an attacker's effective hit chance against a target sheltering here. Negative.</summary>
    public int HitChanceModifier { get; set; } = -20;

    public int MaximumDurability { get; set; } = 2;

    /// <summary>Starting durability. Defaults to <see cref="MaximumDurability"/> — the object starts intact.</summary>
    public int? CurrentDurability { get; set; }

    /// <summary>Subtracted from a weapon's damage when this object is deliberately struck.</summary>
    public int Armour { get; set; } = 2;
}

/// <summary>
/// An exit seeded into a room. Passing through it removes a character from the encounter rather than
/// moving them to another room — there is no second room in v0.5.
/// </summary>
public sealed class ExitDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Whether the exit starts open. Defaults to closed — it must be opened before anyone can leave.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Where passing through leads, described for narration. Defaults to leaving the encounter entirely.</summary>
    public string DestinationDescription { get; set; } = "Outside the encounter";
}

/// <summary>
/// A container seeded into a room. Its <see cref="Contents"/> reuse the same item definition characters
/// carry, so an item taken from it is indistinguishable from one that began in an inventory.
/// </summary>
public sealed class ContainerDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Whether the container starts open. Defaults to closed.</summary>
    public bool IsOpen { get; set; }

    public List<ItemDefinition> Contents { get; set; } = [];

    /// <summary>
    /// An exterior marking discoverable only by closely inspecting the object. It is not part of the
    /// general room description, so a character learns it only by spending a turn examining the container.
    /// Left blank, the container has no distinguishing mark to discover.
    /// </summary>
    public string? ExteriorClue { get; set; }
}

public sealed class CharacterDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>"Hero" or "Monster".</summary>
    public string Role { get; set; } = "Hero";

    /// <summary>
    /// The side this character fights on, e.g. "Heroes" or "Goblins". Characters sharing a team are
    /// allies; the encounter ends when one team has no living members. Left blank, it falls back to a
    /// label derived from <see cref="Role"/> so v0.1 scenarios need no team field.
    /// </summary>
    public string? Team { get; set; }

    public int MaxHealth { get; set; } = 10;

    /// <summary>Starting health. Defaults to <see cref="MaxHealth"/> when omitted.</summary>
    public int? Health { get; set; }

    public int Armour { get; set; }

    /// <summary>Chance out of 100 that this character's attacks land. Defaults to 75.</summary>
    public int HitChance { get; set; } = 75;

    /// <summary>
    /// Starting fear, 0-5. Zero unless a scenario deliberately opens with somebody already shaken — a
    /// grunt who has watched a companion die on the way down the stairs, say. Seeded above the threshold
    /// the character begins visibly Scared, and nothing in the fight caused it, so nothing is traced for it.
    /// </summary>
    public int Fear { get; set; }

    public WeaponDefinition? Weapon { get; set; }

    public List<ItemDefinition> Inventory { get; set; } = [];

    /// <summary>Seeded descriptive injuries present before the scenario begins.</summary>
    public List<string> Injuries { get; set; } = [];

    /// <summary>
    /// The stable ids (or names) of abilities from the built-in ability book this character starts with, each
    /// with its charges full. Defend is added to everyone regardless, so it need not be listed. An id the
    /// ability book does not know is a scenario error and fails at startup rather than silently doing nothing.
    /// </summary>
    public List<string> Abilities { get; set; } = [];

    /// <summary>
    /// Container ids this character begins the encounter already knowing about — both that the container
    /// exists and what it initially holds — as first-hand backstory rather than anything they must
    /// perceive. Used to seed private <c>Backstory</c> knowledge, for example a goblin captain who knows
    /// what is in his own supply cases. Others do not automatically share it.
    /// </summary>
    public List<string> BackstoryKnowledge { get; set; } = [];

    public PersonaDefinition Persona { get; set; } = new();
}

/// <summary>
/// Narrative character configuration. Only the fields that are actually populated reach the prompt.
/// </summary>
public sealed class PersonaDefinition
{
    public string? Backstory { get; set; }

    public string? Personality { get; set; }

    public string? Wants { get; set; }

    public string? Needs { get; set; }

    public string? Fears { get; set; }

    public string? Goal { get; set; }
}

public sealed class WeaponDefinition
{
    /// <summary>
    /// Stable identifier. Left blank it defaults to a slug of <see cref="Name"/>. It exists because an
    /// accepted surrender can move a weapon out of a hand onto the room's floor, and that movement is recorded
    /// against one identity rather than a display name.
    /// </summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int Damage { get; set; }
}

public sealed class ItemDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>When set, the engine treats this as a healing item.</summary>
    public int? HealingAmount { get; set; }

    /// <summary>
    /// When true, this is a focus item: spending a turn using it restores one spent charge of the owner's
    /// own limited ability, and it is not consumed. Gives a once-per-encounter caster something to do after
    /// spending their one big spell.
    /// </summary>
    public bool RestoresAbilityCharge { get; set; }

    /// <summary>
    /// Optional short in-world tag distinguishing this item from others that share its name — "Rowan's".
    /// Set it on any item the scenario deliberately duplicates, or characters holding two cannot tell
    /// them apart.
    /// </summary>
    public string Qualifier { get; set; } = "";
}
