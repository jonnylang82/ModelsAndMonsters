using System.Collections.Immutable;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>Small builders so engine tests read as rules rather than as object construction.</summary>
internal static class TestWorld
{
    public const string HeroId = "hero-aric";
    public const string MonsterId = "monster-grik";

    // Characters default to a hit chance of 100, so an engine built with NoGlancing rules resolves
    // every attack as a solid full-damage hit regardless of the rolls. Tests that care about missing or
    // glancing pass an explicit hit chance and a ScriptedRng.
    public static Character Hero(
        int health = 10,
        int maxHealth = 10,
        int armour = 1,
        int hitChance = 100,
        Weapon? weapon = null,
        IEnumerable<InventoryItem>? inventory = null) => new()
        {
            Id = HeroId,
            Name = "Aric",
            Role = CharacterRole.Hero,
            MaxHealth = maxHealth,
            Health = health,
            Armour = armour,
            HitChance = hitChance,
            Weapon = weapon ?? new Weapon("Iron Sword", 4),
            Inventory = inventory is null ? [] : [.. inventory]
        };

    /// <summary>A hero carrying no weapon at all, which is distinct from omitting the argument.</summary>
    public static Character UnarmedHero() => Hero() with { Weapon = null };

    public static Character Monster(
        int health = 8,
        int maxHealth = 8,
        int armour = 1,
        int hitChance = 100,
        Weapon? weapon = null) => new()
        {
            Id = MonsterId,
            Name = "Grik",
            Role = CharacterRole.Monster,
            MaxHealth = maxHealth,
            Health = health,
            Armour = armour,
            HitChance = hitChance,
            Weapon = weapon ?? new Weapon("Rusty Axe", 3)
        };

    public static GameState State(params Character[] characters) => new()
    {
        Room = new Room("guard-chamber", "Guard Chamber", "A cramped stone chamber.", ImmutableArray<string>.Empty),
        Characters = [.. characters],
        Version = 0
    };

    /// <summary>An engine whose attacks always land for full damage — the old deterministic behaviour.</summary>
    public static GameEngine Engine(params Character[] characters) =>
        new(State(characters.Length == 0 ? [Hero(), Monster()] : characters),
            new SeededRng(1), CombatRules.NoGlancing);

    /// <summary>An engine driven by an explicit rng and rules, for testing misses and glancing blows.</summary>
    public static GameEngine Engine(IRng rng, CombatRules rules, params Character[] characters) =>
        new(State(characters.Length == 0 ? [Hero(), Monster()] : characters), rng, rules);

    // ------------------------------------------------------------------------------------------
    // 2v2 multi-actor world: a fighter and cleric (team Heroes) against two goblins (team Goblins).
    // ------------------------------------------------------------------------------------------

    public const string RowanId = "hero-rowan";
    public const string ElaraId = "hero-elara";
    public const string VarkId = "goblin-vark";
    public const string SkritId = "goblin-skrit";

    public const string HeroesTeam = "Heroes";
    public const string GoblinsTeam = "Goblins";

    public static Character Rowan(int health = 14, int hitChance = 100) => new()
    {
        Id = RowanId,
        Name = "Rowan",
        Role = CharacterRole.Hero,
        Team = HeroesTeam,
        MaxHealth = 14,
        Health = health,
        Armour = 2,
        HitChance = hitChance,
        Weapon = new Weapon("Longsword", 5)
    };

    public static Character Elara(int health = 11, int hitChance = 100, IEnumerable<InventoryItem>? inventory = null) => new()
    {
        Id = ElaraId,
        Name = "Elara",
        Role = CharacterRole.Hero,
        Team = HeroesTeam,
        MaxHealth = 11,
        Health = health,
        Armour = 2,
        HitChance = hitChance,
        Weapon = new Weapon("Iron Mace", 3),
        Inventory = inventory is null ? [] : [.. inventory]
    };

    public static Character Vark(int health = 12, int hitChance = 100) => new()
    {
        Id = VarkId,
        Name = "Vark",
        Role = CharacterRole.Monster,
        Team = GoblinsTeam,
        MaxHealth = 12,
        Health = health,
        Armour = 2,
        HitChance = hitChance,
        Weapon = new Weapon("Notched Sabre", 4)
    };

    public static Character Skrit(int health = 8, int hitChance = 100) => new()
    {
        Id = SkritId,
        Name = "Skrit",
        Role = CharacterRole.Monster,
        Team = GoblinsTeam,
        MaxHealth = 8,
        Health = health,
        Armour = 1,
        HitChance = hitChance,
        Weapon = new Weapon("Crude Spear", 3)
    };

    /// <summary>The seeded 2v2 state: Rowan, Elara, Vark, Skrit in a single room.</summary>
    public static GameState TwoVsTwoState() => State(Rowan(), Elara(), Vark(), Skrit());

    /// <summary>
    /// A configured 2v2 scenario mirroring the shipped one but with modest numbers, so a scripted run
    /// resolves in two rounds. Order is the fixed turn order: Rowan, Elara, Vark, Skrit.
    /// </summary>
    public static ScenarioDefinition TwoVsTwoScenario() => new()
    {
        Id = "test-2v2",
        Name = "Test Cellar",
        Room = new RoomDefinition { Id = "cellar", Name = "Cellar", Description = "A cramped cellar." },
        Characters =
        [
            new CharacterDefinition
            {
                Id = RowanId, Name = "Rowan", Role = "Hero", Team = HeroesTeam,
                MaxHealth = 14, Armour = 2, HitChance = 100,
                Weapon = new WeaponDefinition { Name = "Longsword", Damage = 5 },
                Persona = new PersonaDefinition { Personality = "Steady and protective." }
            },
            new CharacterDefinition
            {
                Id = ElaraId, Name = "Elara", Role = "Hero", Team = HeroesTeam,
                MaxHealth = 11, Armour = 2, HitChance = 100,
                Weapon = new WeaponDefinition { Name = "Iron Mace", Damage = 3 },
                Inventory =
                [
                    new ItemDefinition { Id = "small-healing-potion", Name = "Small Healing Potion", Description = "A vial.", HealingAmount = 5 }
                ],
                Persona = new PersonaDefinition { Personality = "Cautious and resolute." }
            },
            new CharacterDefinition
            {
                Id = VarkId, Name = "Vark", Role = "Monster", Team = GoblinsTeam,
                MaxHealth = 12, Armour = 2, HitChance = 100,
                Weapon = new WeaponDefinition { Name = "Notched Sabre", Damage = 4 },
                Persona = new PersonaDefinition { Personality = "Cunning and domineering." }
            },
            new CharacterDefinition
            {
                Id = SkritId, Name = "Skrit", Role = "Monster", Team = GoblinsTeam,
                MaxHealth = 8, Armour = 1, HitChance = 100,
                Weapon = new WeaponDefinition { Name = "Crude Spear", Damage = 3 },
                Persona = new PersonaDefinition { Personality = "Reckless and nervous." }
            }
        ]
    };

    public static InventoryItem HealingPotion(int amount = 4) =>
        new("small-healing-potion", "Small Healing Potion", "A stoppered vial.", amount);

    public static InventoryItem Rope() => new("rope", "Rope", "Twenty feet of hemp rope.");

    // ------------------------------------------------------------------------------------------
    // Objects: the contested chest (v0.3).
    // ------------------------------------------------------------------------------------------

    public const string ChestId = "old-iron-bound-chest";

    /// <summary>The contested chest. Closed by default, holding a single Small Healing Potion.</summary>
    public static Container Chest(bool open = false, IEnumerable<InventoryItem>? contents = null) => new()
    {
        Id = ChestId,
        Name = "Old Iron-Bound Chest",
        Description = "A squat, iron-banded chest of dark wood.",
        IsOpen = open,
        Contents = contents is null ? [HealingPotion(5)] : [.. contents]
    };

    /// <summary>A room state with a given set of objects and characters.</summary>
    public static GameState StateWith(IEnumerable<WorldObject> objects, params Character[] characters) => new()
    {
        Room = new Room("flooded-cellar", "Flooded Cellar", "A cramped, flooded cellar.", ImmutableArray<string>.Empty)
        {
            Objects = [.. objects]
        },
        Characters = [.. characters],
        Version = 0
    };

    /// <summary>The 2v2 state with the chest present. Elara starts wounded, as in the shipped scenario.</summary>
    public static GameState TwoVsTwoStateWithChest(Container? chest = null) =>
        StateWith([chest ?? Chest()], Rowan(), Elara(health: 6), Vark(), Skrit());

    /// <summary>
    /// The 2v2 scenario with the contested chest seeded into the room and the potion moved out of Elara's
    /// inventory and into it, mirroring the shipped v0.3 scenario. Used where a run needs a manifest whose
    /// authoritative scenario section carries the initial container and its contents.
    /// </summary>
    public static ScenarioDefinition TwoVsTwoScenarioWithChest()
    {
        var scenario = TwoVsTwoScenario();
        scenario.Characters.First(c => c.Id == ElaraId).Inventory = [];
        scenario.Characters.First(c => c.Id == ElaraId).Health = 6;
        scenario.Room.Containers =
        [
            new ContainerDefinition
            {
                Id = ChestId,
                Name = "Old Iron-Bound Chest",
                Description = "A squat, iron-banded chest of dark wood.",
                IsOpen = false,
                Contents =
                [
                    new ItemDefinition { Id = "small-healing-potion", Name = "Small Healing Potion", Description = "A vial.", HealingAmount = 5 }
                ]
            }
        ];
        return scenario;
    }

    public static ScenarioDefinition Scenario() => new()
    {
        Id = "test-scenario",
        Name = "Test Scenario",
        Room = new RoomDefinition { Id = "guard-chamber", Name = "Guard Chamber", Description = "A cramped stone chamber." },
        Characters =
        [
            new CharacterDefinition
            {
                Id = HeroId,
                Name = "Aric",
                Role = "Hero",
                MaxHealth = 10,
                Armour = 1,
                Weapon = new WeaponDefinition { Name = "Iron Sword", Damage = 4 },
                Persona = new PersonaDefinition { Personality = "Brave and direct." }
            },
            new CharacterDefinition
            {
                Id = MonsterId,
                Name = "Grik",
                Role = "Monster",
                MaxHealth = 8,
                Armour = 1,
                Weapon = new WeaponDefinition { Name = "Rusty Axe", Damage = 3 },
                Persona = new PersonaDefinition { Personality = "Cowardly and spiteful." }
            }
        ]
    };
}
