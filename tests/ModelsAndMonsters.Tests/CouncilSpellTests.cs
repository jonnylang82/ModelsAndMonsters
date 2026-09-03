using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Rulebook.Selection;

namespace ModelsAndMonsters.Tests;

public sealed class CouncilSpellTests
{
    private static Character Caster(params AbilityDefinition[] spells) => TestWorld.Rowan() with
    {
        SpellcastingModifier = 3, SpellSaveDC = 13,
        Abilities = [.. spells.Select(CharacterAbility.From)]
    };

    private static GameEngine Engine(Character caster, params int[] rolls) =>
        TestWorld.Engine(new ScriptedRng(rolls), CombatRules.NoGlancing, caster,
            TestWorld.Elara(health: 3), TestWorld.Vark(), TestWorld.Skrit());

    [Fact]
    public void Sleep_rolls_five_dice_spends_charge_blocks_actions_but_does_not_surrender()
    {
        var engine = Engine(Caster(AbilityCatalog.Sleep), 4, 4, 4, 4, 4);
        engine.BeginActorTurn(TestWorld.RowanId, 1, 1);
        var result = engine.Execute(new UseAbilityAction("Rowan", "sleep", "Vark"));
        Assert.True(result.Accepted);
        Assert.Equal(5, result.RngDraws.Count);
        Assert.NotNull(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.Sleeping));
        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Equal(0, engine.State.RequireById(TestWorld.RowanId).FindAbility("sleep")!.RemainingUses);
        Assert.True(engine.BeginActorTurn(TestWorld.VarkId, 1, 3).ActorIncapacitated);
        Assert.False(engine.Execute(new DefendAction("Vark")).Accepted);
        Assert.True(engine.BeginActorTurn(TestWorld.VarkId, 10, 50).ActorIncapacitated);
        Assert.False(engine.BeginActorTurn(TestWorld.VarkId, 11, 55).ActorIncapacitated);
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, 30)]
    public void Sleep_resistance_or_insufficient_power_spends_spell_without_changing_target(bool immune, int health)
    {
        var target = TestWorld.Vark() with { ImmuneToSleep = immune, Health = health, MaxHealth = health };
        var engine = TestWorld.Engine(new ScriptedRng(1, 1, 1, 1, 1), CombatRules.NoGlancing, Caster(AbilityCatalog.Sleep), target);
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", "sleep", "Vark")).Accepted);
        Assert.Empty(engine.State.Statuses);
        Assert.Equal(health, engine.State.RequireById(target.Id).Health);
        Assert.Equal(0, engine.State.RequireById(TestWorld.RowanId).FindAbility("sleep")!.RemainingUses);
    }

    [Fact]
    public void A_companion_can_wake_a_sleeper_but_the_sleeper_cannot_wake_themselves()
    {
        var engine = Engine(Caster(AbilityCatalog.Sleep), 4, 4, 4, 4, 4);
        engine.Execute(new UseAbilityAction("Rowan", "sleep", "Vark"));
        Assert.False(engine.Execute(new UseAbilityAction("Vark", "wake", "Vark")).Accepted);
        Assert.True(engine.Execute(new UseAbilityAction("Skrit", "wake", "Vark")).Accepted);
        Assert.Null(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.Sleeping));
        Assert.Equal(12, engine.State.RequireById(TestWorld.VarkId).Health);
    }

    [Fact]
    public void Damage_wakes_sleep_but_death_of_its_caster_does_not()
    {
        var engine = Engine(Caster(AbilityCatalog.Sleep) with { Health = 1, Armour = 0 }, 4, 4, 4, 4, 4, 1, 50, 1, 50);
        engine.Execute(new UseAbilityAction("Rowan", "sleep", "Vark"));
        engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));
        Assert.False(engine.State.RequireById(TestWorld.RowanId).CanAct);
        Assert.NotNull(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.Sleeping));
        engine.Execute(new AttackCharacterAction("Elara", "Vark", "Iron Mace"));
        Assert.Null(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.Sleeping));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(13, false)]
    public void Faerie_fire_uses_dexterity_save_and_never_deals_fire_damage(int roll, bool applies)
    {
        var engine = Engine(Caster(AbilityCatalog.FaerieFire), roll);
        var result = engine.Execute(new UseAbilityAction("Rowan", "faerie-fire", "Vark"));
        Assert.True(result.Accepted);
        Assert.Equal(applies, engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.FaerieFire) is not null);
        Assert.Equal(12, engine.State.RequireById(TestWorld.VarkId).Health);
        Assert.Single(result.RngDraws);
    }

    [Fact]
    public void Concentration_spell_replaces_previous_concentration_and_damage_can_break_it()
    {
        var engine = Engine(Caster(AbilityCatalog.FaerieFire, AbilityCatalog.DivineFavor), 1, 1, 50, 1);
        engine.Execute(new UseAbilityAction("Rowan", "faerie-fire", "Vark"));
        engine.Execute(new UseAbilityAction("Rowan", "divine-favor"));
        Assert.Null(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.FaerieFire));
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.DivineFavor));
        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));
        Assert.True(attack.Accepted);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.DivineFavor));
        Assert.Contains(attack.RngDraws, r => r.Purpose == "spell.concentration-save" && r.Threshold == 10);
    }

    [Fact]
    public void Divine_favor_adds_traced_radiant_damage_to_a_weapon_hit_not_a_firebolt()
    {
        var engine = Engine(Caster(AbilityCatalog.DivineFavor, AbilityCatalog.Firebolt), 1, 50, 3, 1, 50);
        engine.Execute(new UseAbilityAction("Rowan", "divine-favor"));
        var hit = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        var outcome = Assert.IsType<AttackOutcome>(hit.Outcome);
        Assert.Equal(3, outcome.RadiantDamage);
        Assert.Equal(6, outcome.DamageDealt);
        Assert.Contains("radiant", outcome.Summary);
        var bolt = engine.Execute(new UseAbilityAction("Rowan", "firebolt", "Skrit"));
        Assert.Equal(0, Assert.IsType<AttackOutcome>(bolt.Outcome).RadiantDamage);
    }

    [Fact]
    public void Healing_word_uses_casting_modifier_caps_health_and_cannot_repeat_without_charge()
    {
        var engine = Engine(Caster(AbilityCatalog.HealingWord), 4);
        var result = engine.Execute(new UseAbilityAction("Rowan", "healing-word", "Elara"));
        Assert.True(result.Accepted);
        Assert.Equal(10, engine.State.RequireById(TestWorld.ElaraId).Health);
        Assert.Single(result.RngDraws);
        Assert.False(engine.Execute(new UseAbilityAction("Rowan", "healing-word", "Elara")).Accepted);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 11)]
    public void Healing_word_invalid_targets_do_not_consume_dice_or_charge(bool construct, int health)
    {
        var engine = TestWorld.Engine(new ScriptedRng(), CombatRules.NoGlancing, Caster(AbilityCatalog.HealingWord),
            TestWorld.Elara(health) with { IsConstructOrUndead = construct });
        var result = engine.Execute(new UseAbilityAction("Rowan", "healing-word", "Elara"));
        Assert.False(result.Accepted);
        Assert.Equal(1, engine.State.RequireById(TestWorld.RowanId).FindAbility("healing-word")!.RemainingUses);
    }

    [Fact]
    public void Ally_healing_consumes_actors_item_only_and_refuses_waste_on_full_health()
    {
        var engine = Engine(Caster() with { Inventory = [TestWorld.HealingPotion(20)] });
        var result = engine.Execute(new UseItemAction("Rowan", "Small Healing Potion", "Elara"));
        Assert.True(result.Accepted);
        Assert.Equal(11, engine.State.RequireById(TestWorld.ElaraId).Health);
        Assert.Equal(14, engine.State.RequireById(TestWorld.RowanId).Health);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Equal("Elara", Assert.IsType<HealOutcome>(result.Outcome).TargetName);
        var full = Engine(Caster() with { Inventory = [TestWorld.HealingPotion()] });
        Assert.False(full.Execute(new UseItemAction("Rowan", "Small Healing Potion")).Accepted);
        Assert.Single(full.State.RequireById(TestWorld.RowanId).Inventory);
    }

    [Fact]
    public void Defend_routing_also_supplies_guard_ally_without_expanding_every_spell()
    {
        var selection = RuleSelectionSupport.Build(new RuleCatalog(), RuleSelectionMode.ActionRouting, ["combat.defend"], []);
        Assert.Contains(selection.Cards, c => c.RuleId == "ability.guard-ally");
        Assert.DoesNotContain(selection.Cards, c => c.RuleId == "ability.sleep");
    }

    [Fact]
    public void Faerie_fire_changes_the_actual_hit_threshold()
    {
        var engine = Engine(Caster(AbilityCatalog.FaerieFire) with { HitChance = 50 }, 1, 60, 50);
        engine.Execute(new UseAbilityAction("Rowan", "faerie-fire", "Vark"));
        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Hit);
        Assert.Equal(65, outcome.HitChance);
    }

    [Fact]
    public void Healing_word_cannot_overheal_or_reach_a_dead_ally()
    {
        var caster = Caster(AbilityCatalog.HealingWord) with { SpellcastingModifier = 10 };
        var engine = Engine(caster, 4);
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", "healing-word", "Elara")).Accepted);
        Assert.Equal(11, engine.State.RequireById(TestWorld.ElaraId).Health);
        var dead = TestWorld.Engine(new ScriptedRng(), CombatRules.NoGlancing, caster,
            TestWorld.Elara(health: 0) with { Disposition = CharacterDisposition.Dead });
        Assert.False(dead.Execute(new UseAbilityAction("Rowan", "healing-word", "Elara")).Accepted);
        Assert.Equal(1, dead.State.RequireById(TestWorld.RowanId).FindAbility("healing-word")!.RemainingUses);
    }
}
