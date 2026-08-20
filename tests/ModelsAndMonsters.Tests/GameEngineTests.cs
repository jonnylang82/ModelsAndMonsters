using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The engine is ordinary deterministic software and is tested without any model involvement.
/// </summary>
public sealed class GameEngineTests
{
    [Fact]
    public void Attack_deals_weapon_damage_minus_target_armour()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 8, armour: 1));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(3, outcome.DamageDealt);
        Assert.Equal(5, outcome.TargetHealthAfter);
        Assert.Equal(5, engine.State.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public void Armour_reduces_damage()
    {
        var unarmoured = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(armour: 0));
        var armoured = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(armour: 3));

        var withoutArmour = unarmoured.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        var withArmour = armoured.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.Equal(4, ((AttackOutcome)withoutArmour.Outcome!).DamageDealt);
        Assert.Equal(1, ((AttackOutcome)withArmour.Outcome!).DamageDealt);
    }

    [Fact]
    public void Damage_never_becomes_negative_when_armour_exceeds_weapon_damage()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 8, armour: 99));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.True(result.Accepted);
        Assert.Equal(0, ((AttackOutcome)result.Outcome!).DamageDealt);
        Assert.Equal(8, engine.State.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public void Health_is_clamped_at_zero_and_the_character_is_dead()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 2, armour: 0));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var monster = engine.State.RequireById(TestWorld.MonsterId);
        Assert.Equal(0, monster.Health);
        Assert.False(monster.IsAlive);
        Assert.True(((AttackOutcome)result.Outcome!).TargetDied);
    }

    [Fact]
    public void A_lethal_blow_records_a_persistent_injury()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 2, armour: 0));

        engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var injuries = engine.State.RequireById(TestWorld.MonsterId).Injuries;
        Assert.Contains(injuries, i => i.Description.Contains("Iron Sword", StringComparison.Ordinal));
    }

    [Fact]
    public void Crossing_half_health_records_one_injury_and_further_blows_above_it_do_not()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 8, maxHealth: 8, armour: 1));

        // 8 -> 5: still above half, no injury.
        engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        Assert.Empty(engine.State.RequireById(TestWorld.MonsterId).Injuries);

        // 5 -> 2: crosses half, one injury recorded.
        engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        Assert.Single(engine.State.RequireById(TestWorld.MonsterId).Injuries);
    }

    [Theory]
    [InlineData("Nobody", "Grik", "Iron Sword", EngineRejectionReason.UnknownActor)]
    [InlineData("Aric", "Nobody", "Iron Sword", EngineRejectionReason.UnknownTarget)]
    [InlineData("Aric", "Grik", "Warhammer", EngineRejectionReason.WeaponNotPossessed)]
    [InlineData("Aric", "Aric", "Iron Sword", EngineRejectionReason.TargetIsSelf)]
    public void Invalid_attacks_are_rejected_with_a_specific_reason(
        string attacker, string target, string weapon, EngineRejectionReason expected)
    {
        var engine = TestWorld.Engine();

        var result = engine.Execute(new AttackCharacterAction(attacker, target, weapon));

        Assert.False(result.Accepted);
        Assert.Equal(expected, result.RejectionReason);
        Assert.False(string.IsNullOrWhiteSpace(result.RejectionMessage));
    }

    [Fact]
    public void An_unarmed_attacker_is_rejected()
    {
        var engine = TestWorld.Engine(TestWorld.UnarmedHero(), TestWorld.Monster());

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorHasNoWeapon, result.RejectionReason);
    }

    [Fact]
    public void A_dead_character_cannot_act_and_cannot_be_attacked_again()
    {
        var engine = TestWorld.Engine(TestWorld.Hero(), TestWorld.Monster(health: 0));

        var attackingDead = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        var deadAttacking = engine.Execute(new AttackCharacterAction("Grik", "Aric", "Rusty Axe"));

        Assert.Equal(EngineRejectionReason.TargetIsDead, attackingDead.RejectionReason);
        Assert.Equal(EngineRejectionReason.ActorIsDead, deadAttacking.RejectionReason);
    }

    [Theory]
    [InlineData(CharacterDisposition.Surrendered)]
    [InlineData(CharacterDisposition.Escaped)]
    public void A_surrendered_or_escaped_actor_cannot_attack(CharacterDisposition disposition)
    {
        var inactive = TestWorld.Hero() with { Disposition = disposition };
        var engine = TestWorld.Engine(inactive, TestWorld.Monster());

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorNotActive, result.RejectionReason);
    }

    [Theory]
    [InlineData(CharacterDisposition.Surrendered)]
    [InlineData(CharacterDisposition.Escaped)]
    public void A_surrendered_or_escaped_actor_cannot_use_an_item(CharacterDisposition disposition)
    {
        var inactive = TestWorld.Hero(inventory: [TestWorld.HealingPotion(4)]) with { Disposition = disposition };
        var engine = TestWorld.Engine(inactive, TestWorld.Monster());

        var result = engine.Execute(new UseItemAction("Aric", "Small Healing Potion"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorNotActive, result.RejectionReason);
        // Not consumed: a refused attempt from an inactive actor leaves the item in place.
        Assert.Single(engine.State.RequireById(TestWorld.HeroId).Inventory);
    }

    [Fact]
    public void A_rejected_action_does_not_mutate_state()
    {
        var engine = TestWorld.Engine();
        var before = engine.State;

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Warhammer"));

        Assert.False(result.Accepted);
        Assert.Same(before, engine.State);
        Assert.Same(result.StateBefore, result.StateAfter);
        Assert.Equal(before.Version, engine.State.Version);
    }

    [Fact]
    public void An_accepted_action_mutates_state_exactly_once()
    {
        var engine = TestWorld.Engine();
        var before = engine.State;

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.True(result.Accepted);
        Assert.Equal(before.Version + 1, engine.State.Version);
        Assert.Same(result.StateAfter, engine.State);

        // The before snapshot handed to the trace is untouched by the mutation.
        Assert.Equal(8, result.StateBefore.RequireById(TestWorld.MonsterId).Health);
        Assert.Equal(5, result.StateAfter.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public void Characters_can_be_resolved_by_id_as_well_as_by_name()
    {
        var engine = TestWorld.Engine();

        var result = engine.Execute(new AttackCharacterAction(TestWorld.HeroId, TestWorld.MonsterId, "iron sword"));

        Assert.True(result.Accepted);
    }

    [Fact]
    public void Using_a_healing_item_restores_health_and_consumes_the_item()
    {
        var engine = TestWorld.Engine(
            TestWorld.Hero(health: 4, inventory: [TestWorld.HealingPotion(4)]),
            TestWorld.Monster());

        var result = engine.Execute(new UseItemAction("Aric", "Small Healing Potion"));

        Assert.True(result.Accepted);
        var hero = engine.State.RequireById(TestWorld.HeroId);
        Assert.Equal(8, hero.Health);
        Assert.Empty(hero.Inventory);
    }

    [Fact]
    public void Healing_cannot_exceed_maximum_health()
    {
        var engine = TestWorld.Engine(
            TestWorld.Hero(health: 9, maxHealth: 10, inventory: [TestWorld.HealingPotion(4)]),
            TestWorld.Monster());

        engine.Execute(new UseItemAction("Aric", "Small Healing Potion"));

        Assert.Equal(10, engine.State.RequireById(TestWorld.HeroId).Health);
    }

    [Fact]
    public void Using_an_item_the_character_does_not_carry_is_rejected()
    {
        var engine = TestWorld.Engine();

        var result = engine.Execute(new UseItemAction("Aric", "Small Healing Potion"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotPossessed, result.RejectionReason);
    }

    [Fact]
    public void An_item_with_no_supported_effect_is_rejected()
    {
        var engine = TestWorld.Engine(
            TestWorld.Hero(inventory: [TestWorld.Rope()]),
            TestWorld.Monster());

        var result = engine.Execute(new UseItemAction("Aric", "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemHasNoSupportedEffect, result.RejectionReason);
    }

    [Fact]
    public void Using_an_item_on_another_character_is_not_supported()
    {
        var engine = TestWorld.Engine(
            TestWorld.Hero(inventory: [TestWorld.HealingPotion()]),
            TestWorld.Monster());

        var result = engine.Execute(new UseItemAction("Aric", "Small Healing Potion", "Grik"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemTargetNotSupported, result.RejectionReason);
    }

    [Fact]
    public void A_missed_attack_deals_no_damage_and_changes_nothing()
    {
        // Grik has a hit chance of 70; a roll of 100 is above it, so the attack misses.
        var engine = TestWorld.Engine(
            new ScriptedRng(ScriptedRng.Misses),
            CombatRules.Default,
            TestWorld.Hero(), TestWorld.Monster(hitChance: 70));
        var before = engine.State;

        var result = engine.Execute(new AttackCharacterAction("Grik", "Aric", "Rusty Axe"));

        // A miss is an accepted action (the turn is spent) that mutates nothing.
        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.False(outcome.Hit);
        Assert.Equal(0, outcome.DamageDealt);
        Assert.Same(before, engine.State);
        Assert.Equal(before.Version, engine.State.Version);
        Assert.Equal(10, engine.State.RequireById(TestWorld.HeroId).Health);
    }

    [Fact]
    public void A_hit_roll_at_or_under_the_hit_chance_lands()
    {
        var engine = TestWorld.Engine(
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid),
            CombatRules.Default,
            TestWorld.Hero(hitChance: 75), TestWorld.Monster(health: 8, armour: 1));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.True(outcome.Hit);
        Assert.False(outcome.Glancing);
        Assert.Equal(3, outcome.DamageDealt); // full: 4 - 1
        Assert.Equal(5, engine.State.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public void A_glancing_blow_deals_half_damage()
    {
        // Hit lands (roll 1), then the glancing roll (1) is under the 25% glancing chance.
        var engine = TestWorld.Engine(
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Glances),
            CombatRules.Default,
            TestWorld.Hero(), TestWorld.Monster(health: 8, armour: 1));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.True(outcome.Hit);
        Assert.True(outcome.Glancing);
        Assert.Equal(3, outcome.BaseDamage);
        Assert.Equal(2, outcome.DamageDealt); // round(3 / 2) away from zero = 2
        Assert.Equal(6, engine.State.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public void The_hit_roll_is_compared_to_the_attacker_not_the_target()
    {
        // Attacker Aric has hit chance 50; a roll of exactly 50 lands (roll <= chance).
        var engine = TestWorld.Engine(
            new ScriptedRng(50, ScriptedRng.Solid),
            CombatRules.NoGlancing,
            TestWorld.Hero(hitChance: 50), TestWorld.Monster(hitChance: 10));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        Assert.True(((AttackOutcome)result.Outcome!).Hit);
    }

    [Fact]
    public void The_outcome_records_the_rolls_for_replay()
    {
        var engine = TestWorld.Engine(
            new ScriptedRng(42, 90),
            CombatRules.Default,
            TestWorld.Hero(hitChance: 80), TestWorld.Monster(health: 8, armour: 1));

        var outcome = (AttackOutcome)engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword")).Outcome!;

        Assert.Equal(42, outcome.HitRoll);
        Assert.Equal(80, outcome.HitChance);
        Assert.Equal(90, outcome.GlancingRoll);
        Assert.Equal(25, outcome.GlancingChance);
        Assert.False(outcome.Glancing); // 90 is above the 25% glancing chance
    }

    [Fact]
    public void Seeding_a_scenario_produces_the_configured_state()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.Scenario());

        var hero = state.RequireById(TestWorld.HeroId);
        Assert.Equal(10, hero.Health);
        Assert.Equal(CharacterRole.Hero, hero.Role);
        Assert.Equal("Iron Sword", hero.Weapon!.Name);
        Assert.Equal(2, state.Characters.Length);
    }
}
