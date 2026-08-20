using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Randomness under status modifiers (v0.7). A modified roll is only reproducible if the draw record carries
/// its base chance, every modifier that changed it, the order they were applied in and whether each was
/// consumed — otherwise the effective chance is only observable as a final number and two runs cannot be
/// compared. These tests hold that record to its promises, and hold the new mechanics to consulting no
/// randomness they are not entitled to.
/// </summary>
public sealed class StatusRngTests
{
    private static GameEngine Engine(IRng rng, params Character[] characters) =>
        TestWorld.V07Engine(rng, CombatRules.Default, characters);

    [Fact]
    public void A_status_modified_draw_records_its_base_chance_modifiers_and_effective_threshold()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid),
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));
        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        var draw = attack.RngDraws.Single(d => d.Purpose == "attack.hit-check");
        Assert.Equal(60, draw.BaseChance);
        Assert.Equal(75, draw.Threshold);

        var modifier = Assert.Single(draw.ModifierDetails);
        Assert.Equal("Rallied", modifier.SourceId);
        Assert.Equal(TestWorld.VarkId, modifier.SourceCharacterId);
        Assert.Equal(AbilityCatalog.HitChanceSwing, modifier.Value);
        Assert.Equal(1, modifier.Order);
        Assert.True(modifier.Consumed);

        // The readable note carries the same facts, so the trace is legible without parsing the structure.
        Assert.Contains("Rallied", Assert.Single(draw.Modifiers), StringComparison.Ordinal);
        Assert.Contains("consumed", Assert.Single(draw.Modifiers), StringComparison.Ordinal);

        // Every seed/state field a replay needs is still present.
        Assert.Equal(0, draw.Seed);
        Assert.Equal(0, draw.SequenceBefore);
        Assert.Equal(1, draw.SequenceAfter);
        Assert.Equal(100, draw.Sides);
        Assert.Equal(1, draw.RangeMin);
        Assert.Equal(100, draw.RangeMax);
        Assert.False(string.IsNullOrWhiteSpace(draw.Comparison));
        Assert.False(string.IsNullOrWhiteSpace(draw.Result));
    }

    [Fact]
    public void Modifier_order_is_fixed_not_discovered_so_the_effective_chance_is_reproducible()
    {
        // Two states differing only in the order the statuses were applied must reach the same recorded order.
        var rallyFirst = WithStatuses(TestWorld.SkritId, StatusEffectKind.Rallied, StatusEffectKind.OffBalance);
        var offBalanceFirst = WithStatuses(TestWorld.SkritId, StatusEffectKind.OffBalance, StatusEffectKind.Rallied);

        var a = ResolveSkritsAttack(rallyFirst);
        var b = ResolveSkritsAttack(offBalanceFirst);

        Assert.Equal(["Rallied", "OffBalance"], a.Select(m => m.SourceId).ToArray());
        Assert.Equal(["Rallied", "OffBalance"], b.Select(m => m.SourceId).ToArray());
        Assert.Equal(a.Select(m => m.Order).ToArray(), b.Select(m => m.Order).ToArray());
    }

    [Fact]
    public void The_same_seed_and_statuses_reproduce_the_same_effective_chance_and_result()
    {
        AttackOutcome Run()
        {
            var engine = TestWorld.V07Engine(new SeededRng(4242), CombatRules.Default,
                TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));
            engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit"));
            return (AttackOutcome)engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear")).Outcome!;
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first.HitRoll, second.HitRoll);
        Assert.Equal(first.BaseHitChance, second.BaseHitChance);
        Assert.Equal(first.HitChance, second.HitChance);
        Assert.Equal(first.Hit, second.Hit);
        Assert.Equal(first.DamageDealt, second.DamageDealt);
    }

    [Fact]
    public void Guard_redirection_creates_no_extra_draw()
    {
        var guarded = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engineWithGuard = Engine(guarded);
        Assert.True(engineWithGuard.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        var redirected = engineWithGuard.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        var plain = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var enginePlain = Engine(plain);
        var direct = enginePlain.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        // The same number of draws, in the same order, whether or not a guard moved the blow.
        Assert.Equal(direct.RngDraws.Count, redirected.RngDraws.Count);
        Assert.Equal(direct.RngDraws.Select(d => d.Purpose), redirected.RngDraws.Select(d => d.Purpose));
        Assert.Equal(plain.DrawCount, guarded.DrawCount);
    }

    [Fact]
    public void Dirty_Strike_creates_no_additional_status_roll()
    {
        var trick = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engineTrick = Engine(trick);
        var viaAbility = engineTrick.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Rowan"));

        var plainRng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var enginePlain = Engine(plainRng);
        var plainAttack = enginePlain.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));

        Assert.Equal(plainAttack.RngDraws.Count, viaAbility.RngDraws.Count);
        Assert.Equal(plainAttack.RngDraws.Select(d => d.Purpose), viaAbility.RngDraws.Select(d => d.Purpose));
        Assert.Equal(plainRng.DrawCount, trick.DrawCount);

        // The status it applied came from the hit, not from a roll of its own.
        Assert.NotNull(engineTrick.State.StatusOn(TestWorld.RowanId, StatusEffectKind.OffBalance));
    }

    [Fact]
    public void Healing_guarding_rallying_defending_and_surrender_negotiation_consult_no_randomness()
    {
        var rng = new ScriptedRng();   // any draw at all would throw
        var engine = Engine(rng);

        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        Assert.True(engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara")).Accepted);
        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        Assert.True(engine.Execute(new DefendAction("Skrit")).Accepted);

        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        Assert.True(offer.Accepted);
        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", ((OfferSurrenderOutcome)offer.Outcome!).OfferId));
        Assert.True(accepted.Accepted);

        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void Turn_upkeep_consults_no_randomness()
    {
        var rng = new ScriptedRng();
        var engine = Engine(rng);
        engine.BeginActorTurn(TestWorld.RowanId, 1, 1);
        Assert.True(engine.Execute(new DefendAction("Rowan")).Accepted);
        engine.EndActorTurn(TestWorld.RowanId, 1, 1);

        engine.BeginActorTurn(TestWorld.RowanId, 2, 5);
        engine.EndActorTurn(TestWorld.RowanId, 2, 5);

        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void An_unmodified_attack_still_records_a_base_chance_equal_to_its_threshold()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid));

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));

        var draw = attack.RngDraws.Single(d => d.Purpose == "attack.hit-check");
        Assert.Equal(draw.BaseChance, draw.Threshold);
        Assert.Empty(draw.ModifierDetails);
        Assert.Empty(draw.Modifiers);
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Builds a state with the given status kinds on one character, in the order supplied.</summary>
    private static GameState WithStatuses(string targetId, params StatusEffectKind[] kinds)
    {
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07(hitChance: 60));

        var index = 0;
        foreach (var kind in kinds)
        {
            state = state.WithStatus(new StatusEffectInstance
            {
                Id = $"status-fixture-{++index}",
                Kind = kind,
                SourceCharacterId = TestWorld.VarkId,
                TargetCharacterId = targetId,
                AppliedRound = 1,
                AppliedTurn = 1,
                Modifier = kind == StatusEffectKind.Rallied ? AbilityCatalog.HitChanceSwing : -AbilityCatalog.HitChanceSwing,
                ExpiryRule = StatusExpiryRule.EndOfTargetNextTurn
            });
        }

        return state;
    }

    private static IReadOnlyList<RngModifier> ResolveSkritsAttack(GameState state)
    {
        var engine = new GameEngine(state, new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default);
        var attack = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));
        return ((AttackOutcome)attack.Outcome!).HitModifiers;
    }
}
