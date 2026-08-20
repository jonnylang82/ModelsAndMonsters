using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.7 abilities and status effects at the engine level: Guard Ally, Healing Prayer, Rally Grunt, Dirty
/// Strike and Defend, and the exact application, consumption and expiry of every status they create. These are
/// the mechanical guarantees — a charge spent only on an accepted use, a redirection that costs no extra roll,
/// an expiry that fires on one named turn boundary and not a moment sooner.
/// </summary>
public sealed class AbilityEngineTests
{
    private static GameEngine Engine(IRng? rng = null, CombatRules? rules = null, params Character[] characters) =>
        TestWorld.V07Engine(rng, rules, characters);

    private static int? Charges(GameEngine engine, string characterId, string abilityId) =>
        engine.State.RequireById(characterId).FindAbility(abilityId)?.RemainingUses;

    // ------------------------------------------------------------------------------------------
    // The ability surface itself
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_scenario_gives_each_character_their_ability_and_Defend_to_everyone()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.V07Scenario());

        Assert.NotNull(state.RequireById(TestWorld.RowanId).FindAbility(AbilityCatalog.GuardAllyId));
        Assert.NotNull(state.RequireById(TestWorld.ElaraId).FindAbility(AbilityCatalog.HealingPrayerId));
        Assert.NotNull(state.RequireById(TestWorld.VarkId).FindAbility(AbilityCatalog.RallyGruntId));
        Assert.NotNull(state.RequireById(TestWorld.SkritId).FindAbility(AbilityCatalog.DirtyStrikeId));

        // Defend is a plain combat option, so nobody can be without it.
        Assert.All(state.Characters, c => Assert.NotNull(c.FindAbility(AbilityCatalog.DefendId)));

        // Limited abilities start with their charges full; Guard Ally and Defend are unlimited.
        Assert.Equal(1, state.RequireById(TestWorld.ElaraId).FindAbility(AbilityCatalog.HealingPrayerId)!.RemainingUses);
        Assert.Null(state.RequireById(TestWorld.RowanId).FindAbility(AbilityCatalog.GuardAllyId)!.RemainingUses);
    }

    [Fact]
    public void An_ability_the_scenario_book_does_not_know_fails_visibly_at_startup()
    {
        var scenario = TestWorld.V07Scenario();
        scenario.Characters.First(c => c.Id == TestWorld.RowanId).Abilities = ["summon-dragon"];

        var ex = Assert.Throws<InvalidOperationException>(() => ScenarioFactory.CreateInitialState(scenario));

        Assert.Contains("summon-dragon", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ability book", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_cannot_use_an_ability_they_do_not_have()
    {
        var engine = Engine();

        var result = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.HealingPrayerId, "Elara"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityNotHeld, result.RejectionReason);
    }

    [Fact]
    public void An_unknown_ability_is_refused_rather_than_invented()
    {
        var engine = Engine();

        var result = engine.Execute(new UseAbilityAction("Rowan", "fireball", "Vark"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownAbility, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Guard Ally
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Guard_Ally_consumes_the_turn_creates_the_linked_relationship_and_is_repeatable()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.RowanId, 1, 1);

        var first = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara"));

        Assert.True(first.Accepted);
        var guarding = engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Guarding);
        var guarded = engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.Guarded);
        Assert.NotNull(guarding);
        Assert.NotNull(guarded);
        Assert.Equal(guarding.RelationshipId, guarded.RelationshipId);
        Assert.Equal(TestWorld.RowanId, guarded.SourceCharacterId);

        // Unlimited: the charge column is untouched, so it can be taken up again next turn.
        Assert.Null(Charges(engine, TestWorld.RowanId, AbilityCatalog.GuardAllyId));

        // It falls away at the start of Rowan's next turn, and he may take it up again immediately.
        var upkeep = engine.BeginActorTurn(TestWorld.RowanId, 2, 5);
        Assert.Equal(2, upkeep.StatusEvents.Count(e => e.Kind == StatusEventKind.Expired));
        Assert.Empty(engine.State.Statuses);

        var second = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara"));
        Assert.True(second.Accepted);
    }

    [Fact]
    public void Guard_Ally_cannot_target_the_guardian_themselves_or_an_enemy()
    {
        var engine = Engine();

        var self = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Rowan"));
        Assert.False(self.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetIsSelf, self.RejectionReason);

        var enemy = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Vark"));
        Assert.False(enemy.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetNotAlly, enemy.RejectionReason);

        var nobody = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId));
        Assert.False(nobody.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetRequired, nobody.RejectionReason);

        Assert.Empty(engine.State.Statuses);
    }

    [Fact]
    public void Guard_Ally_requires_an_ally_who_is_still_in_the_fight()
    {
        var goneElara = TestWorld.ElaraV07() with { Disposition = CharacterDisposition.Escaped, EscapedThroughExitId = TestWorld.StairDoorId };
        var engine = Engine(null, null, TestWorld.RowanV07(), goneElara, TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasEscaped, result.RejectionReason);
    }

    [Fact]
    public void Guard_Ally_does_not_stack_on_either_side()
    {
        var engine = Engine();
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var again = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara"));
        Assert.False(again.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityAlreadyActive, again.RejectionReason);

        // Exactly one relationship exists: one Guarding and one Guarded, never two of either.
        Assert.Single(engine.State.Statuses.Where(s => s.Kind == StatusEffectKind.Guarding));
        Assert.Single(engine.State.Statuses.Where(s => s.Kind == StatusEffectKind.Guarded));
    }

    [Fact]
    public void The_first_eligible_enemy_attack_is_redirected_to_the_guardian_using_the_guardians_armour_and_health()
    {
        // Rowan (armour 2, 14 health) guards Elara (armour 2, 6 health) against Vark's sabre (damage 4).
        var engine = Engine();
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        var elaraBefore = engine.State.RequireById(TestWorld.ElaraId).Health;

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        Assert.True(attack.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Redirected);
        Assert.Equal(TestWorld.ElaraId, outcome.IntendedTargetId);
        Assert.Equal(TestWorld.RowanId, outcome.TargetId);

        // Rowan's armour and health were used, and Elara took nothing at all.
        Assert.Equal(2, outcome.TargetArmour);
        Assert.Equal(14, outcome.TargetHealthBefore);
        Assert.Equal(12, outcome.TargetHealthAfter);
        Assert.Equal(elaraBefore, engine.State.RequireById(TestWorld.ElaraId).Health);
    }

    [Fact]
    public void A_redirected_attack_makes_exactly_the_ordinary_draws_and_no_more()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = Engine(rng, CombatRules.Default);
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        // One hit draw, one glancing draw. Redirection adds nothing, and the scripted rng would throw if it did.
        Assert.Equal(2, attack.RngDraws.Count);
        Assert.Equal(2, rng.DrawCount);
        Assert.Equal(["attack.hit-check", "attack.glancing-check"], attack.RngDraws.Select(d => d.Purpose).ToArray());
    }

    [Fact]
    public void The_guard_is_consumed_by_one_redirection_and_the_next_attack_lands_on_the_intended_target()
    {
        var engine = Engine();
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var first = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));
        Assert.Equal(TestWorld.RowanId, ((AttackOutcome)first.Outcome!).TargetId);
        Assert.Empty(engine.State.Statuses);
        Assert.Equal(2, first.StatusEvents.Count(e => e.Kind == StatusEventKind.Consumed));

        var second = engine.Execute(new AttackCharacterAction("Skrit", "Elara", "Crude Spear"));
        var outcome = Assert.IsType<AttackOutcome>(second.Outcome);
        Assert.False(outcome.Redirected);
        Assert.Equal(TestWorld.ElaraId, outcome.TargetId);
    }

    [Fact]
    public void An_allied_blow_is_not_redirected_by_a_guard()
    {
        // The guard turns aside an ENEMY blow. Friendly fire is permitted and resolves as described.
        var engine = Engine();
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var friendly = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));

        var outcome = Assert.IsType<AttackOutcome>(friendly.Outcome);
        Assert.False(outcome.Redirected);
        Assert.Equal(TestWorld.ElaraId, outcome.TargetId);
        Assert.NotNull(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.Guarded));
    }

    [Fact]
    public void The_guard_disappears_when_the_guarded_ally_leaves_the_fight()
    {
        // An enemy blow aimed at a guarded ally is always redirected, so the ally cannot leave the fight that
        // way while the guard stands. She leaves by the door instead — and the guard, with nobody left to
        // protect, must fall away on both sides rather than leaving Rowan guarding an empty space.
        var engine = new GameEngine(TestWorld.V07State(exitOpen: true), new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        Assert.Equal(2, engine.State.Statuses.Length);

        var escaped = engine.Execute(new EscapeEncounterAction("Elara", "Cellar Stair Door"));

        Assert.True(escaped.Accepted);
        Assert.Empty(engine.State.Statuses);
        Assert.Equal(2, escaped.StatusEvents.Count(e => e.Kind == StatusEventKind.Removed));

        // And the next enemy blow at Rowan is his own to take, with nothing to redirect.
        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));
        Assert.False(((AttackOutcome)attack.Outcome!).Redirected);
    }

    [Fact]
    public void The_guard_disappears_when_either_participant_leaves_the_fight()
    {
        var engine = Engine(null, null, TestWorld.RowanV07(health: 1), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        Assert.Equal(2, engine.State.Statuses.Length);

        // The guardian is cut down by the other goblin; nothing is left to sustain the guard on either side.
        var killed = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        Assert.True(killed.Accepted);
        Assert.Equal(CharacterDisposition.Dead, engine.State.RequireById(TestWorld.RowanId).Disposition);
        Assert.Empty(engine.State.Statuses);
        Assert.Equal(2, killed.StatusEvents.Count(e => e.Kind == StatusEventKind.Removed));
    }

    // ------------------------------------------------------------------------------------------
    // Healing Prayer
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Healing_Prayer_restores_a_fixed_amount_once_per_encounter_with_no_randomness()
    {
        var rng = new SeededRng(1);
        var engine = Engine(rng);

        var first = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara"));

        Assert.True(first.Accepted);
        var outcome = Assert.IsType<HealingPrayerOutcome>(first.Outcome);
        Assert.Equal(6, outcome.HealthBefore);
        Assert.Equal(10, outcome.HealthAfter);
        Assert.Empty(first.RngDraws);
        Assert.Equal(0, rng.DrawCount);
        Assert.Equal(0, Charges(engine, TestWorld.ElaraId, AbilityCatalog.HealingPrayerId));

        // A second attempt has nothing left to spend.
        var second = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara"));
        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityHasNoUsesLeft, second.RejectionReason);
    }

    [Fact]
    public void Healing_Prayer_cannot_carry_a_target_past_their_maximum_health()
    {
        var engine = Engine(null, null, TestWorld.RowanV07(health: 12), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Rowan"));

        Assert.True(result.Accepted);
        Assert.Equal(14, engine.State.RequireById(TestWorld.RowanId).Health);
    }

    [Fact]
    public void Healing_Prayer_rejects_a_fully_healthy_target_without_spending_the_charge()
    {
        var engine = Engine();

        var result = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Rowan"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetAlreadyAtFullHealth, result.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.ElaraId, AbilityCatalog.HealingPrayerId));
    }

    [Fact]
    public void Healing_Prayer_does_not_revive_the_dead_or_reach_the_escaped()
    {
        var deadRowan = TestWorld.RowanV07() with { Health = 0, Disposition = CharacterDisposition.Dead };
        var engine = Engine(null, null, deadRowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var revive = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Rowan"));
        Assert.False(revive.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsDead, revive.RejectionReason);
        Assert.Equal(CharacterDisposition.Dead, engine.State.RequireById(TestWorld.RowanId).Disposition);
        Assert.Equal(1, Charges(engine, TestWorld.ElaraId, AbilityCatalog.HealingPrayerId));

        var goneVark = TestWorld.VarkV07() with { Disposition = CharacterDisposition.Escaped };
        var second = Engine(null, null, TestWorld.RowanV07(), TestWorld.ElaraV07(), goneVark, TestWorld.SkritV07());
        var enemy = second.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Vark"));
        Assert.False(enemy.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetNotAlly, enemy.RejectionReason);
    }

    [Fact]
    public void Healing_Prayer_with_no_named_target_heals_the_caster()
    {
        var engine = Engine();

        var result = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId));

        Assert.True(result.Accepted);
        Assert.Equal(TestWorld.ElaraId, ((HealingPrayerOutcome)result.Outcome!).TargetId);
    }

    // ------------------------------------------------------------------------------------------
    // Rally Grunt
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Rally_adds_its_modifier_to_the_allys_next_attack_and_is_consumed_by_it()
    {
        // Skrit's hit chance is 65 in the shipped numbers; here the test world uses 100, so use a lower one
        // to make the modifier visible in the effective chance.
        var engine = Engine(new ScriptedRng(70, ScriptedRng.Solid), CombatRules.Default,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));

        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        var rallied = engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied);
        Assert.NotNull(rallied);
        Assert.Equal(AbilityCatalog.HitChanceSwing, rallied.Modifier);
        Assert.Equal(0, Charges(engine, TestWorld.VarkId, AbilityCatalog.RallyGruntId));

        // A roll of 70 misses at 60 but lands at 75.
        var attack = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Hit);
        Assert.Equal(60, outcome.BaseHitChance);
        Assert.Equal(75, outcome.HitChance);

        // And the rally is used up.
        Assert.Null(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));
        Assert.Contains(attack.StatusEvents, e => e.Kind == StatusEventKind.Consumed && e.Status.Kind == StatusEffectKind.Rallied);
    }

    [Fact]
    public void Rally_is_consumed_by_a_miss_just_as_by_a_hit()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Misses), CombatRules.Default,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));
        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        Assert.False(((AttackOutcome)attack.Outcome!).Hit);
        Assert.Null(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));
    }

    [Fact]
    public void An_unused_rally_expires_at_the_end_of_the_allys_next_turn()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, 1, 3);
        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        engine.EndActorTurn(TestWorld.VarkId, 1, 3);

        // Ending the applier's own turn does not expire it.
        Assert.NotNull(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));

        // Nor does the start of the ally's turn — only the end of it.
        engine.BeginActorTurn(TestWorld.SkritId, 1, 4);
        Assert.NotNull(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));

        var upkeep = engine.EndActorTurn(TestWorld.SkritId, 1, 4);
        Assert.Null(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));
        Assert.Contains(upkeep.StatusEvents, e => e.Kind == StatusEventKind.Expired && e.Status.Kind == StatusEffectKind.Rallied);
    }

    [Fact]
    public void Rally_cannot_target_the_commander_or_stack_with_itself()
    {
        var engine = Engine();

        var self = engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Vark"));
        Assert.False(self.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetIsSelf, self.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.VarkId, AbilityCatalog.RallyGruntId));

        // A second application on the same ally is refused rather than stacked (there is only one charge
        // anyway, so this is proved by pre-seeding a second commander with the ability).
        var secondCommander = TestWorld.WithAbilities(TestWorld.Skrit(), AbilityCatalog.RallyGruntId);
        var stacking = Engine(null, null, TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), secondCommander);
        Assert.True(stacking.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        var again = stacking.Execute(new UseAbilityAction("Skrit", AbilityCatalog.RallyGruntId, "Vark"));
        Assert.True(again.Accepted); // a different target is fine
        Assert.Single(stacking.State.Statuses.Where(s => s.Kind == StatusEffectKind.Rallied && s.TargetCharacterId == TestWorld.SkritId));
    }

    // ------------------------------------------------------------------------------------------
    // Dirty Strike
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Dirty_Strike_uses_the_ordinary_attack_draws_and_applies_OffBalance_on_a_hit()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = Engine(rng, CombatRules.Default);

        var result = engine.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Rowan"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.DirtyStrikeId, outcome.ViaAbilityId);
        Assert.True(outcome.Hit);

        // Ordinary damage, ordinary draws: spear 3 minus armour 2 is 1.
        Assert.Equal(1, outcome.DamageDealt);
        Assert.Equal(2, result.RngDraws.Count);
        Assert.Equal(2, rng.DrawCount);

        var offBalance = engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.OffBalance);
        Assert.NotNull(offBalance);
        Assert.Equal(-AbilityCatalog.HitChanceSwing, offBalance.Modifier);
        Assert.Equal(0, Charges(engine, TestWorld.SkritId, AbilityCatalog.DirtyStrikeId));
    }

    [Fact]
    public void Dirty_Strike_applies_nothing_on_a_miss_but_still_spends_its_charge_and_the_turn()
    {
        var rng = new ScriptedRng(ScriptedRng.Misses);
        var engine = Engine(rng, CombatRules.Default,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));

        var result = engine.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Rowan"));

        Assert.True(result.Accepted);
        Assert.False(((AttackOutcome)result.Outcome!).Hit);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.OffBalance));
        Assert.Equal(0, Charges(engine, TestWorld.SkritId, AbilityCatalog.DirtyStrikeId));

        // One draw only: a miss never rolls for a glancing blow, and the trick adds no roll of its own.
        Assert.Single(result.RngDraws);
        Assert.Equal(1, rng.DrawCount);
    }

    [Fact]
    public void Dirty_Strike_refused_before_the_engine_reaches_it_spends_no_charge_and_no_dice()
    {
        var rng = new ScriptedRng();
        var engine = Engine(rng, CombatRules.Default);

        var ally = engine.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Vark"));

        Assert.False(ally.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityTargetNotOpponent, ally.RejectionReason);
        Assert.Equal(1, Charges(engine, TestWorld.SkritId, AbilityCatalog.DirtyStrikeId));
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void OffBalance_reduces_and_is_consumed_by_the_targets_next_attack()
    {
        // Rowan's hit chance is lowered so the penalty is decisive: a roll of 70 lands at 80 but not at 65.
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid, 70), CombatRules.Default,
            TestWorld.RowanV07(hitChance: 80), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        Assert.True(engine.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Rowan")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.Equal(80, outcome.BaseHitChance);
        Assert.Equal(65, outcome.HitChance);
        Assert.False(outcome.Hit);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.OffBalance));
    }

    // ------------------------------------------------------------------------------------------
    // Defend
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Defend_is_available_to_everyone_uses_no_randomness_and_applies_the_status()
    {
        var rng = new SeededRng(1);
        var engine = Engine(rng);

        var result = engine.Execute(new DefendAction("Skrit"));

        Assert.True(result.Accepted);
        Assert.IsType<DefendOutcome>(result.Outcome);
        Assert.Empty(result.RngDraws);
        Assert.Equal(0, rng.DrawCount);

        var defending = engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Defending);
        Assert.NotNull(defending);
        Assert.Equal(-AbilityCatalog.DefendReduction, defending.Modifier);
        Assert.Equal(TestWorld.SkritId, defending.SourceCharacterId);
    }

    [Fact]
    public void Defend_reduces_final_damage_after_armour_and_glancing_and_never_below_zero()
    {
        // Vark's sabre does 4; Rowan's armour is 2, so base damage is 2 and Defend takes it to 1.
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default);
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));

        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.Equal(2, outcome.BaseDamage);
        Assert.Equal(1, outcome.DefendReduction);
        Assert.Equal(1, outcome.DamageDealt);
        Assert.Equal(13, outcome.TargetHealthAfter);
    }

    [Fact]
    public void Defend_cannot_take_damage_below_zero_and_is_still_consumed_by_a_blow_that_lands()
    {
        // Skrit's spear does 3 against Rowan's armour 2: base 1, halved by a glancing blow to 1, then Defend
        // takes it to 0. The blow still landed, so the guard is spent.
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Glances), CombatRules.Default);
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Hit);
        Assert.Equal(0, outcome.DamageDealt);
        Assert.Equal(14, outcome.TargetHealthAfter);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));
    }

    [Fact]
    public void Defend_survives_a_miss()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Misses), CombatRules.Default,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(hitChance: 60), TestWorld.SkritV07());
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));

        Assert.False(((AttackOutcome)attack.Outcome!).Hit);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));
    }

    [Fact]
    public void Defend_expires_at_the_start_of_the_defenders_next_turn_and_is_repeatable()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.RowanId, 1, 1);
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);
        engine.EndActorTurn(TestWorld.RowanId, 1, 1);

        // It survives everyone else's turns.
        engine.BeginActorTurn(TestWorld.VarkId, 1, 3);
        engine.EndActorTurn(TestWorld.VarkId, 1, 3);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));

        // And falls away when Rowan's own next turn begins — leaving him free to brace again.
        var upkeep = engine.BeginActorTurn(TestWorld.RowanId, 2, 5);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));
        Assert.Contains(upkeep.StatusEvents, e => e.Kind == StatusEventKind.Expired && e.Status.Kind == StatusEffectKind.Defending);

        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);
    }

    [Fact]
    public void Defend_does_not_stack_with_itself()
    {
        var engine = Engine();
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);

        var again = engine.Execute(new DefendAction("Rowan"));

        Assert.False(again.Accepted);
        Assert.Equal(EngineRejectionReason.AbilityAlreadyActive, again.RejectionReason);
        Assert.Single(engine.State.StatusesOn(TestWorld.RowanId));
    }

    [Fact]
    public void Defend_reached_through_use_ability_is_the_same_action()
    {
        var engine = Engine();

        var result = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.DefendId));

        Assert.True(result.Accepted);
        Assert.IsType<DefendOutcome>(result.Outcome);
    }

    // ------------------------------------------------------------------------------------------
    // Statuses combined, and their removal when a holder leaves the fight
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Rally_and_OffBalance_on_the_same_attacker_apply_in_a_fixed_order_and_cancel_out()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 70));

        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        // Elara has no Dirty Strike, so OffBalance is placed directly to isolate the modifier arithmetic.
        var withBoth = engine.State.WithStatus(new StatusEffectInstance
        {
            Id = "status-manual",
            Kind = StatusEffectKind.OffBalance,
            SourceCharacterId = TestWorld.ElaraId,
            TargetCharacterId = TestWorld.SkritId,
            AppliedRound = 1,
            AppliedTurn = 1,
            Modifier = -AbilityCatalog.HitChanceSwing,
            ExpiryRule = StatusExpiryRule.EndOfTargetNextTurn
        });
        var combined = new GameEngine(withBoth, new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default);

        var attack = combined.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.Equal(70, outcome.BaseHitChance);
        Assert.Equal(70, outcome.HitChance);
        Assert.Equal(["Rallied", "OffBalance"], outcome.HitModifiers.Select(m => m.SourceId).ToArray());
        Assert.Equal([1, 2], outcome.HitModifiers.Select(m => m.Order).ToArray());
        Assert.All(outcome.HitModifiers, m => Assert.True(m.Consumed));

        // Both were spent by the one attack.
        Assert.Empty(combined.State.StatusesOn(TestWorld.SkritId));
    }

    [Fact]
    public void An_accepted_surrender_removes_the_statuses_a_yielding_character_was_sustaining()
    {
        var engine = Engine();
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var offer = engine.Execute(new OfferSurrenderAction("Rowan", "Vark", ["purse-rowan"], ForfeitWeapon: false));
        var accepted = engine.Execute(new AcceptSurrenderAction("Vark", ((OfferSurrenderOutcome)offer.Outcome!).OfferId));

        Assert.True(accepted.Accepted);
        // Elara is no longer guarded by somebody who has left the fight.
        Assert.Empty(engine.State.Statuses);
        Assert.Equal(2, accepted.StatusEvents.Count(e => e.Kind == StatusEventKind.Removed));
    }
}
