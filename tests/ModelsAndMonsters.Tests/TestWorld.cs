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

    public static InventoryItem HealingPotion(int amount = 4) =>
        new("small-healing-potion", "Small Healing Potion", "A stoppered vial.", amount);

    public static InventoryItem Rope() => new("rope", "Rope", "Twenty feet of hemp rope.");

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
