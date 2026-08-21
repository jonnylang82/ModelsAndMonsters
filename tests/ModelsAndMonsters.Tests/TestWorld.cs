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

    // ------------------------------------------------------------------------------------------
    // Objects: the two supply cases (v0.4 hidden-information slice).
    // ------------------------------------------------------------------------------------------

    public const string MedicineCaseId = "shrine-medicine-case";
    public const string MillCrateId = "mill-supply-crate";
    public const string MedicineClue = "The faded shrine mark identifies this as a case intended for medicinal supplies.";
    public const string MillClue = "The warped trade mark identifies this as an ordinary mill-supply crate.";

    /// <summary>The shrine medicine case: closed by default, an exterior clue, a Small Healing Potion inside.</summary>
    public static Container MedicineCase(bool open = false, IEnumerable<InventoryItem>? contents = null) => new()
    {
        Id = MedicineCaseId,
        Name = "Faded Shrine Medicine Case",
        Description = "A squat, dusty wooden case, its markings too worn to read at a glance.",
        IsOpen = open,
        ExteriorClue = MedicineClue,
        Contents = contents is null ? [HealingPotion(5)] : [.. contents]
    };

    /// <summary>The mill supply crate: closed by default, an exterior clue, an inert bundle of damp rags inside.</summary>
    public static Container MillCrate(bool open = false, IEnumerable<InventoryItem>? contents = null) => new()
    {
        Id = MillCrateId,
        Name = "Water-Stained Mill Supply Crate",
        Description = "A squat, water-stained wooden crate, its trade mark blurred past reading at a glance.",
        IsOpen = open,
        ExteriorClue = MillClue,
        Contents = contents is null ? [DampRags()] : [.. contents]
    };

    public static InventoryItem DampRags() => new("bundle-of-damp-rags", "Bundle of Damp Rags", "A bound bundle of grey, sodden rags.");

    /// <summary>The 2v2 state with both closed supply cases present. Elara starts wounded, as shipped.</summary>
    public static GameState TwoCasesState(Container? medicine = null, Container? mill = null) =>
        StateWith([medicine ?? MedicineCase(), mill ?? MillCrate()], Rowan(), Elara(health: 6), Vark(), Skrit());

    /// <summary>
    /// The 2v2 scenario mirroring the shipped v0.4 one: the two supply cases in the room and Vark holding
    /// private backstory knowledge of both. Used where the ledger must be seeded from real scenario input.
    /// </summary>
    public static ScenarioDefinition TwoCasesScenario()
    {
        var scenario = TwoVsTwoScenario();
        scenario.Characters.First(c => c.Id == ElaraId).Inventory = [];
        scenario.Characters.First(c => c.Id == ElaraId).Health = 6;
        scenario.Characters.First(c => c.Id == VarkId).BackstoryKnowledge = [MedicineCaseId, MillCrateId];
        scenario.Room.Containers =
        [
            new ContainerDefinition
            {
                Id = MedicineCaseId, Name = "Faded Shrine Medicine Case", Description = "A squat, dusty wooden case.",
                IsOpen = false, ExteriorClue = MedicineClue,
                Contents = [new ItemDefinition { Id = "small-healing-potion", Name = "Small Healing Potion", Description = "A vial.", HealingAmount = 5 }]
            },
            new ContainerDefinition
            {
                Id = MillCrateId, Name = "Water-Stained Mill Supply Crate", Description = "A squat, water-stained crate.",
                IsOpen = false, ExteriorClue = MillClue,
                Contents = [new ItemDefinition { Id = "bundle-of-damp-rags", Name = "Bundle of Damp Rags", Description = "Rags." }]
            }
        ];
        return scenario;
    }

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

    // ------------------------------------------------------------------------------------------
    // The cellar stair door (v0.5 yield-or-escape slice).
    // ------------------------------------------------------------------------------------------

    public const string StairDoorId = "cellar-stair-door";

    /// <summary>The cellar stair door. Closed and unlocked by default — the single way out of the encounter.</summary>
    public static EncounterExit StairDoor(bool open = false) => new()
    {
        Id = StairDoorId,
        Name = "Cellar Stair Door",
        Description = "A heavy wooden door at the foot of the stairs leading out of the flooded cellar.",
        IsOpen = open,
        DestinationDescription = "the dark stairs, up and out of the cellar"
    };

    /// <summary>A room state with the given exit and characters.</summary>
    public static GameState StateWithExit(EncounterExit exit, params Character[] characters) => new()
    {
        Room = new Room("flooded-cellar", "Flooded Cellar", "A cramped, flooded cellar.", ImmutableArray<string>.Empty)
        {
            Exits = [exit]
        },
        Characters = [.. characters],
        Version = 0
    };

    /// <summary>The 2v2 state with the cellar stair door present. Elara starts wounded, as in the shipped scenario.</summary>
    /// <remarks>
    /// The goblins carry purses because terms of surrender must promise a carried item — the weapon in hand
    /// is not enough on its own — and this state is what the negotiation tests bargain over.
    /// </remarks>
    public static GameState TwoVsTwoStateWithExit(bool exitOpen = false) =>
        StateWithExit(StairDoor(exitOpen), Rowan(), Elara(health: 6),
            Vark() with { Inventory = [Purse("vark")] },
            Skrit() with { Inventory = [Purse("skrit")] });

    /// <summary>An engine over a state that includes the cellar stair door; attacks land for full damage.</summary>
    public static GameEngine EngineWithExit(bool exitOpen = false, IRng? rng = null, params Character[] characters) =>
        new(StateWithExit(StairDoor(exitOpen),
                characters.Length == 0 ? [Rowan(), Elara(health: 6), Vark(), Skrit()] : characters),
            rng ?? new SeededRng(1), CombatRules.NoGlancing);

    /// <summary>The 2v2 scenario mirroring the shipped v0.5 one: the two supply cases and the cellar stair door.</summary>
    public static ScenarioDefinition TwoVsTwoScenarioWithExit()
    {
        var scenario = TwoVsTwoScenario();
        scenario.Room.Exits =
        [
            new ExitDefinition
            {
                Id = StairDoorId,
                Name = "Cellar Stair Door",
                Description = "A heavy wooden door at the foot of the stairs.",
                IsOpen = false,
                DestinationDescription = "Outside the encounter"
            }
        ];
        return scenario;
    }

    // ------------------------------------------------------------------------------------------
    // Abilities, purses and negotiated surrender (v0.7 terms-and-tactics slice).
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// One character's purse of gold. Every character in the shipped scenario carries one with its own stable
    /// id, so a purse is the obvious concrete thing to promise in terms of surrender — and identically named
    /// purses are exactly why an offer names item ids rather than item names.
    /// </summary>
    public static InventoryItem Purse(string ownerSuffix) =>
        new($"purse-{ownerSuffix}", "Small Purse of Gold Coins", "A small drawstring purse, heavy with gold coins.");

    /// <summary>Grants a character the named abilities with full charges, plus Defend, which everyone has.</summary>
    public static Character WithAbilities(Character character, params string[] abilityIds)
    {
        var abilities = abilityIds
            .Select(id => AbilityCatalog.Find(id) ?? throw new InvalidOperationException($"No such ability '{id}'."))
            .Select(CharacterAbility.From)
            .ToList();

        if (!abilities.Any(a => a.AbilityId == AbilityCatalog.DefendId))
        {
            abilities.Add(CharacterAbility.From(AbilityCatalog.Defend));
        }

        return character with { Abilities = [.. abilities] };
    }

    /// <summary>Gives a character an exact inventory, replacing whatever they had.</summary>
    public static Character WithItems(Character character, params InventoryItem[] items) =>
        character with { Inventory = [.. items] };

    /// <summary>Rowan with Guard Ally and his purse — the guardian of the v0.7 slice.</summary>
    public static Character RowanV07(int health = 14, int hitChance = 100) =>
        WithItems(WithAbilities(Rowan(health, hitChance), AbilityCatalog.GuardAllyId), Purse("rowan"));

    /// <summary>Elara with Healing Prayer and her purse, wounded as in the shipped scenario.</summary>
    public static Character ElaraV07(int health = 6, int hitChance = 100) =>
        WithItems(WithAbilities(Elara(health, hitChance), AbilityCatalog.HealingPrayerId), Purse("elara"));

    /// <summary>Vark with Rally Grunt, his salve and his purse.</summary>
    public static Character VarkV07(int health = 12, int hitChance = 100) =>
        WithItems(WithAbilities(Vark(health, hitChance), AbilityCatalog.RallyGruntId),
            new InventoryItem("goblin-salve", "Vial of Goblin Salve", "A clay vial of pungent green salve.", 4),
            Purse("vark"));

    /// <summary>Skrit with Dirty Strike and his purse.</summary>
    public static Character SkritV07(int health = 8, int hitChance = 100) =>
        WithItems(WithAbilities(Skrit(health, hitChance), AbilityCatalog.DirtyStrikeId), Purse("skrit"));

    /// <summary>
    /// The v0.7 2v2 state: four characters with one ability each, a purse each, and the cellar stair door.
    /// This is the state the negotiated-surrender and ability tests are written against.
    /// </summary>
    public static GameState V07State(bool exitOpen = false, params Character[] characters) =>
        StateWithExit(StairDoor(exitOpen),
            characters.Length == 0 ? [RowanV07(), ElaraV07(), VarkV07(), SkritV07()] : characters);

    /// <summary>An engine over <see cref="V07State"/>; attacks land for full damage unless a rng/rules pair is given.</summary>
    public static GameEngine V07Engine(IRng? rng = null, CombatRules? rules = null, params Character[] characters) =>
        new(V07State(exitOpen: false, characters), rng ?? new SeededRng(1), rules ?? CombatRules.NoGlancing);

    /// <summary>
    /// The v0.7 scenario definition mirroring the shipped one: one ability per character, a purse each, and
    /// the cellar stair door. Used where the ledger and manifest must come from real scenario input.
    /// </summary>
    public static ScenarioDefinition V07Scenario()
    {
        var scenario = TwoVsTwoScenarioWithExit();

        void Configure(string id, string ability, string purseSuffix)
        {
            var character = scenario.Characters.First(c => c.Id == id);
            character.Abilities = [ability];
            character.Inventory =
            [
                new ItemDefinition
                {
                    Id = $"purse-{purseSuffix}",
                    Name = "Small Purse of Gold Coins",
                    Description = "A small drawstring purse, heavy with gold coins."
                }
            ];
        }

        Configure(RowanId, AbilityCatalog.GuardAllyId, "rowan");
        Configure(ElaraId, AbilityCatalog.HealingPrayerId, "elara");
        Configure(VarkId, AbilityCatalog.RallyGruntId, "vark");
        Configure(SkritId, AbilityCatalog.DirtyStrikeId, "skrit");
        scenario.Characters.First(c => c.Id == ElaraId).Health = 6;

        return scenario;
    }

    // ------------------------------------------------------------------------------------------
    // Environmental cover (v0.9 vertical slice).
    // ------------------------------------------------------------------------------------------

    public const string WorkbenchId = "mill-workbench";

    /// <summary>
    /// The seeded cover object, mirroring the shipped scenario's values unless a test overrides them:
    /// capacity 1, -20 hit chance, 2/2 durability, armour 2, unoccupied.
    /// </summary>
    public static CoverObject Workbench(
        int capacity = 1, int hitChanceModifier = -20, int maxDurability = 2, int? currentDurability = null,
        int armour = 2, string? occupantId = null) => new()
        {
            Id = WorkbenchId,
            Name = "Overturned Mill Workbench",
            Description = "A heavy mill workbench, knocked on its side.",
            Capacity = capacity,
            HitChanceModifier = hitChanceModifier,
            MaximumDurability = maxDurability,
            CurrentDurability = currentDurability ?? maxDurability,
            Armour = armour,
            CurrentOccupantId = occupantId
        };

    /// <summary>The 2v2 state with the seeded workbench present, otherwise unoccupied.</summary>
    public static GameState TwoVsTwoStateWithCover(CoverObject? cover = null) =>
        StateWith([cover ?? Workbench()], Rowan(), Elara(health: 6), Vark(), Skrit());

    /// <summary>An engine over a state that includes the seeded workbench; attacks land for full damage unless a rng/rules pair is given.</summary>
    public static GameEngine EngineWithCover(CoverObject? cover = null, IRng? rng = null, CombatRules? rules = null, params Character[] characters) =>
        new(StateWith([cover ?? Workbench()], characters.Length == 0 ? [Rowan(), Elara(health: 6), Vark(), Skrit()] : characters),
            rng ?? new SeededRng(1), rules ?? CombatRules.NoGlancing);

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
