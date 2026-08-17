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
}

public sealed class CharacterDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>"Hero" or "Monster".</summary>
    public string Role { get; set; } = "Hero";

    public int MaxHealth { get; set; } = 10;

    /// <summary>Starting health. Defaults to <see cref="MaxHealth"/> when omitted.</summary>
    public int? Health { get; set; }

    public int Armour { get; set; }

    /// <summary>Chance out of 100 that this character's attacks land. Defaults to 75.</summary>
    public int HitChance { get; set; } = 75;

    public WeaponDefinition? Weapon { get; set; }

    public List<ItemDefinition> Inventory { get; set; } = [];

    /// <summary>Seeded descriptive injuries present before the scenario begins.</summary>
    public List<string> Injuries { get; set; } = [];

    public List<string> Abilities { get; set; } = [];

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
}
