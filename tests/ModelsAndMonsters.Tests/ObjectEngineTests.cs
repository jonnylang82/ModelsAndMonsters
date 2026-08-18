using Microsoft.Extensions.Configuration;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The container and item engine, tested as ordinary deterministic software with no model involvement:
/// seeding the chest, opening it, and transferring its contents — including the rejections that keep two
/// characters from ever acquiring the same item, and the guarantee that none of it touches the combat RNG.
/// </summary>
public sealed class ObjectEngineTests
{
    private static GameEngine EngineWith(GameState state, IRng? rng = null) =>
        // Default to an RNG that throws on the first draw, so any accidental roll during an object action
        // fails the test loudly rather than silently advancing the generator.
        new(state, rng ?? new ScriptedRng(), CombatRules.NoGlancing);

    // ------------------------------------------------------------------------------------------
    // Seeding: exactly one closed chest with the potion inside (tests #12, #13)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_scenario_seeds_exactly_one_closed_chest_with_the_potion_inside_and_in_no_inventory()
    {
        var scenario = LoadShippedScenario();
        var state = ScenarioFactory.CreateInitialState(scenario);

        // Exactly one container, and it is closed.
        var chest = Assert.Single(state.Room.Objects.OfType<Container>());
        Assert.False(chest.IsOpen);

        // The Small Healing Potion begins inside the chest...
        var potion = Assert.Single(chest.Contents);
        Assert.Equal("Small Healing Potion", potion.Name);
        Assert.True(potion.IsHealingItem);

        // ...and in no character's inventory.
        Assert.All(state.Characters, c => Assert.DoesNotContain(c.Inventory, i => i.IsHealingItem));
        Assert.DoesNotContain(state.Characters, c => c.Inventory.Any(i =>
            string.Equals(i.Name, "Small Healing Potion", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Elara_begins_wounded_so_a_healing_potion_would_matter_but_is_still_standing()
    {
        var state = ScenarioFactory.CreateInitialState(LoadShippedScenario());
        var elara = state.RequireById(TestWorld.ElaraId);

        Assert.True(elara.IsAlive);
        Assert.True(elara.Health < elara.MaxHealth, "Elara should start injured.");
    }

    [Fact]
    public void The_scenario_factory_builds_container_objects_from_the_room_definition()
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Room.Containers =
        [
            new ContainerDefinition
            {
                Id = "old-iron-bound-chest",
                Name = "Old Iron-Bound Chest",
                Description = "A squat, iron-banded chest.",
                IsOpen = false,
                Contents = [new ItemDefinition { Id = "small-healing-potion", Name = "Small Healing Potion", Description = "A vial.", HealingAmount = 5 }]
            }
        ];

        var chest = Assert.IsType<Container>(Assert.Single(ScenarioFactory.CreateInitialState(scenario).Room.Objects));
        Assert.Equal("old-iron-bound-chest", chest.Id);
        Assert.Equal(5, Assert.Single(chest.Contents).HealingAmount);
    }

    [Fact]
    public void Duplicate_container_ids_are_rejected_when_seeding()
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Room.Containers =
        [
            new ContainerDefinition { Id = "chest", Name = "Chest A" },
            new ContainerDefinition { Id = "chest", Name = "Chest B" }
        ];

        Assert.Throws<InvalidOperationException>(() => ScenarioFactory.CreateInitialState(scenario));
    }

    // ------------------------------------------------------------------------------------------
    // Opening (tests #15, #16)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Opening_a_closed_chest_changes_only_its_open_state_and_bumps_the_version()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest());
        var before = engine.State;

        var result = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));

        Assert.True(result.Accepted);
        var chest = Assert.Single(engine.State.Room.Objects.OfType<Container>());
        Assert.True(chest.IsOpen);

        // Only the open state changed: the contents are untouched, and so is every character.
        Assert.Equal("Small Healing Potion", Assert.Single(chest.Contents).Name);
        Assert.Equal(before.Version + 1, engine.State.Version);
        foreach (var original in before.Characters)
        {
            Assert.Equal(original.Health, engine.State.RequireById(original.Id).Health);
            Assert.Equal(original.Inventory.Length, engine.State.RequireById(original.Id).Inventory.Length);
        }
    }

    [Fact]
    public void Opening_an_already_open_chest_is_rejected_without_mutation()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));
        var before = engine.State;

        var result = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ContainerAlreadyOpen, result.RejectionReason);
        Assert.Same(before, engine.State);
    }

    [Fact]
    public void A_dead_actor_cannot_open_a_chest()
    {
        var engine = EngineWith(TestWorld.StateWith([TestWorld.Chest()], TestWorld.Rowan(health: 0), TestWorld.Vark()));

        var result = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorIsDead, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Taking (tests #17, #18, #19)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Taking_from_a_closed_chest_is_rejected_without_mutation()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest());
        var before = engine.State;

        var result = engine.Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Small Healing Potion"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ContainerClosed, result.RejectionReason);
        Assert.Same(before, engine.State);
        Assert.Empty(engine.State.RequireById(TestWorld.ElaraId).Inventory);
    }

    [Fact]
    public void Taking_from_an_open_chest_atomically_moves_the_item_into_the_actor_inventory()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));

        var result = engine.Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Small Healing Potion"));

        Assert.True(result.Accepted);

        // The chest is now empty and Elara carries exactly the potion — one atomic transfer.
        var chest = Assert.Single(engine.State.Room.Objects.OfType<Container>());
        Assert.Empty(chest.Contents);
        var potion = Assert.Single(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.Equal("Small Healing Potion", potion.Name);
        Assert.True(potion.IsHealingItem);

        var outcome = Assert.IsType<TakeItemOutcome>(result.Outcome);
        Assert.Equal(TestWorld.ElaraId, outcome.ActorId);
        Assert.Empty(outcome.RemainingContents);
    }

    [Fact]
    public void A_second_attempt_to_take_the_same_item_is_rejected_so_two_characters_never_both_get_it()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));

        var first = engine.Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Small Healing Potion"));
        Assert.True(first.Accepted);

        // Rowan reaches for the same potion Elara already took — it is gone.
        var second = engine.Execute(new TakeItemAction("Rowan", "Old Iron-Bound Chest", "Small Healing Potion"));

        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotInContainer, second.RejectionReason);

        // Only Elara holds it; Rowan's hands stay empty.
        Assert.Single(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
    }

    [Fact]
    public void Taking_an_item_that_is_not_in_the_chest_is_rejected()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));

        var result = engine.Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Golden Crown"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotInContainer, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Invalid and ambiguous references (test #20)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_unknown_container_reference_is_rejected_safely()
    {
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest());
        var before = engine.State;

        var result = engine.Execute(new OpenContainerAction("Rowan", "the barrel"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownContainer, result.RejectionReason);
        Assert.Same(before, engine.State);
    }

    [Fact]
    public void An_ambiguous_container_reference_is_rejected_rather_than_guessed()
    {
        // Two chests share a name; "chest" cannot be resolved to one of them without guessing.
        var twin = TestWorld.Chest() with { Id = "second-chest" };
        var engine = EngineWith(TestWorld.StateWith([TestWorld.Chest(), twin], TestWorld.Rowan()));
        var before = engine.State;

        var result = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ContainerReferenceAmbiguous, result.RejectionReason);
        Assert.Same(before, engine.State);
    }

    [Fact]
    public void An_exact_id_still_resolves_even_when_two_containers_share_a_name()
    {
        var twin = TestWorld.Chest() with { Id = "second-chest" };
        var engine = EngineWith(TestWorld.StateWith([TestWorld.Chest(), twin], TestWorld.Rowan()));

        // The name is ambiguous, but the id is unique.
        var result = engine.Execute(new OpenContainerAction("Rowan", "second-chest"));

        Assert.True(result.Accepted);
        Assert.True(engine.State.Room.Objects.OfType<Container>().Single(c => c.Id == "second-chest").IsOpen);
    }

    // ------------------------------------------------------------------------------------------
    // No RNG (test #22) and the two-step separation behind compound actions (test #21)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Object_actions_consult_no_randomness_and_never_advance_the_combat_generator()
    {
        // A ScriptedRng with no rolls throws on any draw, so reaching the end of both actions without an
        // exception — and with DrawCount still zero — proves neither consulted the generator.
        var rng = new ScriptedRng();
        var engine = new GameEngine(TestWorld.TwoVsTwoStateWithChest(), rng, CombatRules.Default);

        var open = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));
        var take = engine.Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Small Healing Potion"));

        Assert.True(open.Accepted);
        Assert.True(take.Accepted);
        Assert.Empty(open.RngDraws);
        Assert.Empty(take.RngDraws);
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void Combat_after_an_object_action_still_uses_the_first_scripted_roll_untouched()
    {
        // The object actions must not consume rolls meant for combat: the attack that follows sees the
        // very first scripted value, proving the generator was never advanced by opening or taking.
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = new GameEngine(
            TestWorld.StateWith([TestWorld.Chest(open: true)], TestWorld.Rowan(hitChance: 80), TestWorld.Vark()),
            rng, CombatRules.Default);

        engine.Execute(new TakeItemAction("Rowan", "Old Iron-Bound Chest", "Small Healing Potion"));
        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        var hit = attack.RngDraws[0];
        Assert.Equal(ScriptedRng.Hits, hit.RawRoll); // the first roll, not a later one
        Assert.Equal(0, hit.SequenceBefore);
    }

    [Fact]
    public void Opening_never_removes_an_item_and_taking_needs_an_open_chest_so_no_single_action_does_both()
    {
        // The mechanical guarantee behind the compound-action rule: there is no engine action that both
        // opens a chest and takes from it. Opening leaves the contents in place, and taking is refused
        // until the chest is open — so "open and take" can never happen in one step.
        var engine = EngineWith(TestWorld.TwoVsTwoStateWithChest());

        var open = engine.Execute(new OpenContainerAction("Rowan", "Old Iron-Bound Chest"));
        Assert.True(open.Accepted);
        Assert.Single(engine.State.Room.Objects.OfType<Container>().Single().Contents); // still there

        var takeBeforeOpen = new GameEngine(TestWorld.TwoVsTwoStateWithChest(), new ScriptedRng(), CombatRules.NoGlancing)
            .Execute(new TakeItemAction("Elara", "Old Iron-Bound Chest", "Small Healing Potion"));
        Assert.False(takeBeforeOpen.Accepted);
        Assert.Equal(EngineRejectionReason.ContainerClosed, takeBeforeOpen.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Inventory on death: a corpse becomes a lootable container (v0.3.1)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_character_who_dies_carrying_items_leaves_an_open_lootable_corpse_container()
    {
        // Elara (armour 2, health 2) carrying the potion is struck dead by Vark's sabre (4 - 2 = 2).
        var state = TestWorld.StateWith([],
            TestWorld.Vark(),
            TestWorld.Elara(health: 2, inventory: [TestWorld.HealingPotion()]));
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);
        var versionBefore = engine.State.Version;

        var result = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.True(outcome.TargetDied);

        // Elara is dead and no longer carries the potion.
        var elara = engine.State.RequireById(TestWorld.ElaraId);
        Assert.False(elara.IsAlive);
        Assert.Empty(elara.Inventory);

        // A single open corpse container now holds it.
        var corpse = Assert.Single(engine.State.Room.Objects.OfType<Container>());
        Assert.Equal("corpse-" + TestWorld.ElaraId, corpse.Id);
        Assert.True(corpse.IsOpen);
        Assert.Equal("Small Healing Potion", Assert.Single(corpse.Contents).Name);

        // The transfer is part of the one accepted action, and the outcome reports the drop.
        Assert.Equal(versionBefore + 1, engine.State.Version);
        Assert.Equal(corpse.Name, outcome.CorpseContainerName);
        Assert.Contains("Small Healing Potion", outcome.DroppedItems);
        Assert.Contains("can be taken from", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_potion_on_a_corpse_can_then_be_looted_with_the_ordinary_take_item_action()
    {
        var state = TestWorld.StateWith([],
            TestWorld.Vark(),
            TestWorld.Elara(health: 2, inventory: [TestWorld.HealingPotion()]));
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);

        engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre")); // Elara falls, dropping the potion

        // Vark loots the corpse with the same take_item action the chest uses.
        var loot = engine.Execute(new TakeItemAction("Vark", "Elara's body", "Small Healing Potion"));

        Assert.True(loot.Accepted);
        Assert.Contains(engine.State.RequireById(TestWorld.VarkId).Inventory, i => i.IsHealingItem);
        Assert.Empty(engine.State.Room.Objects.OfType<Container>().Single().Contents);
    }

    [Fact]
    public void A_character_who_dies_empty_handed_leaves_no_corpse_container()
    {
        // Skrit (armour 1, health 2) carries nothing; his death should not litter the room.
        var state = TestWorld.StateWith([], TestWorld.Vark(), TestWorld.Skrit(health: 2));
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new AttackCharacterAction("Vark", "Skrit", "Notched Sabre"));

        Assert.True(result.Accepted);
        Assert.True(((AttackOutcome)result.Outcome!).TargetDied);
        Assert.False(engine.State.RequireById(TestWorld.SkritId).IsAlive);
        Assert.Empty(engine.State.Room.Objects.OfType<Container>());
        Assert.Empty(((AttackOutcome)result.Outcome!).DroppedItems);
    }

    // ------------------------------------------------------------------------------------------

    private static ScenarioDefinition LoadShippedScenario()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scenario.json");
        Assert.True(File.Exists(path), $"Expected the shipped scenario at {path}.");

        var scenario = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build()
            .GetSection(ScenarioDefinition.SectionName)
            .Get<ScenarioDefinition>();

        Assert.NotNull(scenario);
        return scenario!;
    }
}
