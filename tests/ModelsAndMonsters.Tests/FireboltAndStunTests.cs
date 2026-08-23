using Microsoft.Extensions.Configuration;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The two v0.11 abilities at the engine level: Firebolt (a magical ranged strike that ignores armour) and
/// Stunning Blow (a weapon strike that, on a hit, costs the target their whole next turn). Both route through
/// the ONE ordinary weapon-strike resolution, so the guarantees under test are the same ones every other
/// ability makes — exactly the ordinary draws, a charge spent only on an accepted use, a status applied only
/// on a landing blow — plus the two things that are genuinely new: fire ignoring armour, and a stun that is
/// consumed by the very turn it steals.
/// </summary>
public sealed class FireboltAndStunTests
{
    private static Character Mage(int health = 10, int armour = 0, int hitChance = 100, Weapon? weapon = null) =>
        TestWorld.WithAbilities(
            TestWorld.Hero(health: health, maxHealth: health, armour: armour, hitChance: hitChance, weapon: weapon ?? new Weapon("Ashwood Staff", 2)),
            AbilityCatalog.FireboltId);

    private static Character Ogre(int health = 26, int armour = 3, Weapon? weapon = null) =>
        TestWorld.WithAbilities(
            TestWorld.Monster(health: health, maxHealth: health, armour: armour, weapon: weapon ?? new Weapon("Iron-Bound Maul", 6)),
            AbilityCatalog.StunId);

    private static readonly InventoryItem Crystal =
        new("focus-crystal", "Cracked Focus-Crystal", "A quartz on a cord.", RestoresAbilityCharge: true);

    private static Character MageWithCrystal(int hitChance = 100) => TestWorld.WithItems(Mage(hitChance: hitChance), Crystal);

    private static int? Charges(GameEngine engine, string characterId, string abilityId) =>
        engine.State.RequireById(characterId).FindAbility(abilityId)?.RemainingUses;

    // ------------------------------------------------------------------------------------------
    // Firebolt
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Firebolt_ignores_armour_and_burns_for_its_full_fire_damage_on_a_solid_hit()
    {
        // A heavily armoured target: an ordinary blow would be nearly all soaked, but fire ignores armour.
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = TestWorld.Engine(rng, CombatRules.Default, Mage(), TestWorld.Monster(health: 20, maxHealth: 20, armour: 4));

        var result = engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.FireboltId, outcome.ViaAbilityId);
        Assert.True(outcome.Hit);
        Assert.True(outcome.IgnoredArmour);

        // The whole point: the full fire damage lands, NOT weapon-minus-armour (which would be 2).
        Assert.Equal(AbilityCatalog.FireboltDamage, outcome.BaseDamage);
        Assert.Equal(AbilityCatalog.FireboltDamage, outcome.DamageDealt);
        Assert.Equal(20 - AbilityCatalog.FireboltDamage, engine.State.RequireById(TestWorld.MonsterId).Health);

        // Exactly the ordinary draws — one to hit, one for quality — and one charge, now spent.
        Assert.Equal(2, result.RngDraws.Count);
        Assert.Equal(2, rng.DrawCount);
        Assert.Equal(0, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));
    }

    [Fact]
    public void Firebolt_quality_scales_the_fire_damage_the_same_way_an_ordinary_blow_scales()
    {
        var critical = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Critical), CombatRules.Default,
            Mage(), TestWorld.Monster(health: 30, maxHealth: 30, armour: 4));
        var critOutcome = Assert.IsType<AttackOutcome>(
            critical.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik")).Outcome);
        Assert.Equal(AttackQuality.Critical, critOutcome.Quality);
        Assert.Equal(CombatRules.DamageFor(AttackQuality.Critical, AbilityCatalog.FireboltDamage), critOutcome.DamageDealt);

        var glancing = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Glances), CombatRules.Default,
            Mage(), TestWorld.Monster(health: 30, maxHealth: 30, armour: 4));
        var glanceOutcome = Assert.IsType<AttackOutcome>(
            glancing.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik")).Outcome);
        Assert.Equal(AttackQuality.Glancing, glanceOutcome.Quality);
        Assert.Equal(CombatRules.DamageFor(AttackQuality.Glancing, AbilityCatalog.FireboltDamage), glanceOutcome.DamageDealt);
    }

    [Fact]
    public void Firebolt_needs_no_weapon_in_hand()
    {
        // A firebolt is loosed from the caster themselves; an empty hand does not stop it, unlike a stun.
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var mage = TestWorld.WithAbilities(TestWorld.UnarmedHero(), AbilityCatalog.FireboltId);
        var engine = TestWorld.Engine(rng, CombatRules.Default, mage, TestWorld.Monster(health: 20, maxHealth: 20, armour: 4));

        var result = engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik"));

        Assert.True(result.Accepted);
        Assert.Equal(AbilityCatalog.FireboltDamage, ((AttackOutcome)result.Outcome!).DamageDealt);
    }

    [Fact]
    public void Firebolt_on_a_miss_deals_nothing_but_still_spends_its_charge_and_makes_one_draw()
    {
        var rng = new ScriptedRng(ScriptedRng.Misses);
        // The caster's own chance must be under 100, or the "miss" roll of 100 still lands.
        var engine = TestWorld.Engine(rng, CombatRules.Default,
            Mage(hitChance: 60), TestWorld.Monster(health: 20, maxHealth: 20, armour: 4));

        var result = engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik"));

        Assert.True(result.Accepted);
        Assert.False(((AttackOutcome)result.Outcome!).Hit);
        Assert.Equal(20, engine.State.RequireById(TestWorld.MonsterId).Health);
        Assert.Equal(0, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));

        // A miss never rolls for quality, and the spell adds no roll of its own.
        Assert.Single(result.RngDraws);
        Assert.Equal(1, rng.DrawCount);
    }

    [Fact]
    public void Firebolt_cannot_be_loosed_at_an_ally_and_spends_nothing_when_refused()
    {
        var rng = new ScriptedRng();
        var engine = TestWorld.Engine(rng, CombatRules.Default,
            Mage(), TestWorld.WithAbilities(TestWorld.Hero(), AbilityCatalog.GuardAllyId) with { Id = "hero-ally", Name = "Bran" });

        var result = engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Bran"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetNotOpponent, result.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void Firebolt_can_be_loosed_only_once_in_an_encounter()
    {
        var engine = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid, ScriptedRng.Hits, ScriptedRng.Solid),
            CombatRules.Default, Mage(), TestWorld.Monster(health: 30, maxHealth: 30, armour: 4));

        Assert.True(engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik")).Accepted);

        var second = engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik"));
        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityHasNoUsesLeft, second.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Stunning Blow
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Stun_deals_ordinary_weapon_damage_and_leaves_the_target_stunned_on_a_hit()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = TestWorld.Engine(rng, CombatRules.Default, Ogre(), TestWorld.Hero(health: 16, maxHealth: 16, armour: 3));

        var result = engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.StunId, outcome.ViaAbilityId);
        Assert.True(outcome.Hit);
        Assert.False(outcome.IgnoredArmour);

        // Ordinary weapon damage: maul 6 minus armour 3 is 3. The stun does not change the damage.
        Assert.Equal(3, outcome.DamageDealt);
        Assert.Equal(nameof(StatusEffectKind.Stunned), outcome.StatusApplied);

        var stunned = engine.State.StatusOn(TestWorld.HeroId, StatusEffectKind.Stunned);
        Assert.NotNull(stunned);
        Assert.Equal(StatusExpiryRule.WhileConditionHolds, stunned.ExpiryRule);
        Assert.Equal(0, Charges(engine, TestWorld.MonsterId, AbilityCatalog.StunId));

        // The stunned character is still alive, present and a valid target — only their turn is taken.
        var target = engine.State.RequireById(TestWorld.HeroId);
        Assert.True(target.IsAlive);
        Assert.True(target.IsCombatTarget);
    }

    [Fact]
    public void A_stunned_character_loses_exactly_their_next_turn_and_the_daze_then_clears()
    {
        var engine = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default,
            Ogre(), TestWorld.Hero(health: 16, maxHealth: 16, armour: 3));

        // The ogre stuns on its own turn (global turn 0).
        engine.BeginActorTurn(TestWorld.MonsterId, round: 0, turn: 0);
        Assert.True(engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric")).Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.HeroId, StatusEffectKind.Stunned));

        // The target's very next turn is consumed by the stun, and the status is consumed with it.
        var lost = engine.BeginActorTurn(TestWorld.HeroId, round: 1, turn: 1);
        Assert.True(lost.ActorIncapacitated);
        Assert.Null(engine.State.StatusOn(TestWorld.HeroId, StatusEffectKind.Stunned));
        Assert.Contains(lost.StatusEvents, e => e.Kind == StatusEventKind.Consumed && e.Status.Kind == StatusEffectKind.Stunned);

        // The turn after that, they act normally again — a stun costs exactly one turn, not two.
        var recovered = engine.BeginActorTurn(TestWorld.HeroId, round: 2, turn: 2);
        Assert.False(recovered.ActorIncapacitated);
    }

    [Fact]
    public void Stun_on_a_miss_applies_no_stun_but_still_spends_its_charge_and_the_turn()
    {
        var rng = new ScriptedRng(ScriptedRng.Misses);
        var engine = TestWorld.Engine(rng, CombatRules.Default,
            Ogre() with { HitChance = 60 }, TestWorld.Hero(health: 16, maxHealth: 16, armour: 3));

        var result = engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric"));

        Assert.True(result.Accepted);
        Assert.False(((AttackOutcome)result.Outcome!).Hit);
        Assert.Null(engine.State.StatusOn(TestWorld.HeroId, StatusEffectKind.Stunned));
        Assert.Equal(0, Charges(engine, TestWorld.MonsterId, AbilityCatalog.StunId));
        Assert.Single(result.RngDraws);
    }

    [Fact]
    public void A_killing_stun_takes_no_turn_because_a_dead_foe_has_none_to_lose()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        // A frail target the maul finishes outright.
        var engine = TestWorld.Engine(rng, CombatRules.Default, Ogre(), TestWorld.Hero(health: 2, maxHealth: 2, armour: 0));

        var result = engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.True(outcome.TargetDied);
        Assert.Null(outcome.StatusApplied);
        Assert.Null(engine.State.StatusOn(TestWorld.HeroId, StatusEffectKind.Stunned));
    }

    [Fact]
    public void Stun_needs_a_weapon_in_hand()
    {
        var rng = new ScriptedRng();
        var unarmedOgre = TestWorld.WithAbilities(TestWorld.Monster() with { Weapon = null }, AbilityCatalog.StunId);
        var engine = TestWorld.Engine(rng, CombatRules.Default, unarmedOgre, TestWorld.Hero());

        var result = engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorHasNoWeapon, result.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.MonsterId, AbilityCatalog.StunId));
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void Stun_can_be_used_only_once_in_an_encounter()
    {
        var engine = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid, ScriptedRng.Hits, ScriptedRng.Solid),
            CombatRules.Default, Ogre(), TestWorld.Hero(health: 40, maxHealth: 40, armour: 0));

        Assert.True(engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric")).Accepted);

        // Even after the first stun has been consumed by a skipped turn, the ability itself is spent.
        engine.BeginActorTurn(TestWorld.HeroId, round: 1, turn: 1);
        var second = engine.Execute(new UseAbilityAction("Grik", AbilityCatalog.StunId, "Aric"));
        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityHasNoUsesLeft, second.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Focus item — recharging a spent ability (v0.11)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Focusing_through_a_focus_item_restores_a_spent_ability_and_does_not_consume_the_item()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid, ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = TestWorld.Engine(rng, CombatRules.Default,
            MageWithCrystal(), TestWorld.Monster(health: 40, maxHealth: 40, armour: 4));

        // Spend the one firebolt.
        Assert.True(engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik")).Accepted);
        Assert.Equal(0, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));

        // Focus through the crystal to rekindle it.
        var result = engine.Execute(new UseItemAction("Aric", "focus-crystal"));
        Assert.True(result.Accepted);
        var outcome = Assert.IsType<FocusOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.FireboltId, outcome.AbilityId);
        Assert.Equal(0, outcome.UsesBefore);
        Assert.Equal(1, outcome.UsesAfter);
        Assert.Equal(1, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));

        // The crystal is NOT consumed — focusing is repeatable — and no dice were rolled.
        Assert.Contains(engine.State.RequireById(TestWorld.HeroId).Inventory, i => i.Id == "focus-crystal");
        Assert.Empty(result.RngDraws);

        // And the rekindled firebolt can be loosed again.
        Assert.True(engine.Execute(new UseAbilityAction("Aric", AbilityCatalog.FireboltId, "Grik")).Accepted);
        Assert.Equal(0, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));
    }

    [Fact]
    public void Focusing_with_no_spent_power_to_restore_is_refused_and_costs_no_turn()
    {
        var engine = TestWorld.Engine(new ScriptedRng(), CombatRules.Default, MageWithCrystal(), TestWorld.Monster());

        // Firebolt is at full — there is nothing to rekindle.
        var result = engine.Execute(new UseItemAction("Aric", "focus-crystal"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.NoDepletedAbilityToRestore, result.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.HeroId, AbilityCatalog.FireboltId));
    }

    [Fact]
    public void A_focus_item_has_nothing_to_restore_for_a_character_with_only_unlimited_abilities()
    {
        // Guard Ally and Defend are unlimited, so there is no charge for the crystal to rekindle.
        var knight = TestWorld.WithItems(TestWorld.WithAbilities(TestWorld.Hero(), AbilityCatalog.GuardAllyId), Crystal);
        var engine = TestWorld.Engine(new ScriptedRng(), CombatRules.Default, knight, TestWorld.Monster());

        var result = engine.Execute(new UseItemAction("Aric", "focus-crystal"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.NoDepletedAbilityToRestore, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // The shipped 3-vs-2 scenario
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_3v2_scenario_builds_with_a_fire_mage_and_a_stun_ogre()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scenario_3_v_2.json");
        Assert.True(File.Exists(path), $"Expected the shipped alternate scenario at {path}.");

        var scenario = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build()
            .GetSection(ScenarioDefinition.SectionName).Get<ScenarioDefinition>();
        Assert.NotNull(scenario);

        var state = ScenarioFactory.CreateInitialState(scenario!);

        // Three heroes against two monsters, and the hero team is named exactly "Heroes" (the observer UI
        // styles only that literal as the hero side).
        var heroes = state.Characters.Where(c => c.Role == CharacterRole.Hero).ToList();
        var monsters = state.Characters.Where(c => c.Role == CharacterRole.Monster).ToList();
        Assert.Equal(3, heroes.Count);
        Assert.Equal(2, monsters.Count);
        Assert.All(heroes, h => Assert.Equal("Heroes", h.Team));
        Assert.All(monsters, m => Assert.NotEqual("Heroes", m.Team));

        // The mage carries a single firebolt; the ogre a single stunning blow.
        var mage = state.RequireById("hero-maelis");
        var firebolt = mage.FindAbility(AbilityCatalog.FireboltId);
        Assert.NotNull(firebolt);
        Assert.Equal(1, firebolt!.RemainingUses);

        var ogre = state.RequireById("monster-brakka");
        var stun = ogre.FindAbility(AbilityCatalog.StunId);
        Assert.NotNull(stun);
        Assert.Equal(1, stun!.RemainingUses);

        // The mage's crystal is a focus item — she can rekindle her firebolt with it.
        var crystal = mage.Inventory.Single(i => i.Id == "focus-crystal");
        Assert.True(crystal.IsFocusItem);

        // Same single exit as the original; different cover and containers present.
        Assert.Single(state.Room.Exits);
        Assert.Single(state.Room.Objects.OfType<CoverObject>());
        Assert.Equal(2, state.Room.Objects.OfType<Container>().Count());
    }
}
