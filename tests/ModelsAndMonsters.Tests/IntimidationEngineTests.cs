using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// <c>intimidate_character</c> and <c>steady_ally</c> at the engine boundary: the modifiers, the one draw,
/// the one-attempt rule, and the structural speech gate that runs before any of it.
/// </summary>
public sealed class IntimidationEngineTests
{
    /// <summary>A speech event id standing for a line the character really spoke on this turn.</summary>
    private const int Spoken = 42;

    private static GameEngine Engine(IRng rng, params Character[] characters) =>
        new(TestWorld.V07State(exitOpen: false, characters), rng, CombatRules.Default);

    private static IntimidateCharacterAction Threat(
        string actor, string target, int? speech = Spoken, string? addressedTo = null) =>
        new(actor, target, speech, addressedTo);

    private static SteadyAllyAction Steady(
        string actor, string target, int? speech = Spoken, string? addressedTo = null) =>
        new(actor, target, speech, addressedTo);

    // ------------------------------------------------------------------------------------------
    // Modifiers, all of them from authoritative state
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_base_chance_stands_alone_against_an_unafraid_unhurt_target_in_an_even_fight()
    {
        var engine = Engine(new ScriptedRng(50));

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Equal(IntimidationRules.DefaultBaseChance, outcome.BaseChance);
        Assert.Empty(outcome.Modifiers);
        Assert.Equal(IntimidationRules.DefaultBaseChance, outcome.EffectiveChance);
    }

    [Fact]
    public void Each_point_of_the_targets_existing_fear_adds_ten()
    {
        var rowan = TestWorld.RowanV07() with { Fear = 2 };
        var engine = Engine(new ScriptedRng(50), rowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        var modifier = Assert.Single(outcome.Modifiers, m => m.SourceId == "target-fear");
        Assert.Equal(2 * IntimidationRules.PerTargetFearPoint, modifier.Value);
        Assert.Equal(35 + 20, outcome.EffectiveChance);
    }

    [Fact]
    public void An_outnumbered_target_adds_fifteen()
    {
        // Two goblins against one hero: Rowan is outnumbered from the opening state.
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(), TestWorld.VarkV07(), TestWorld.SkritV07()),
            new ScriptedRng(50), CombatRules.Default);

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Single(outcome.Modifiers, m => m.SourceId == "target-outnumbered" && m.Value == IntimidationRules.TargetOutnumbered);
        Assert.Equal(35 + 15, outcome.EffectiveChance);
    }

    [Fact]
    public void A_badly_wounded_target_adds_ten_using_the_projects_existing_health_bands()
    {
        // Rowan at 4 of 14 is 0.29 — the existing "badly wounded" band.
        var rowan = TestWorld.RowanV07(health: 4);
        Assert.Equal(HealthBand.BadlyWounded, HealthBands.Of(rowan));

        var engine = Engine(new ScriptedRng(50), rowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Single(outcome.Modifiers, m => m.SourceId == "target-badly-wounded" && m.Value == IntimidationRules.TargetBadlyWounded);
        Assert.Equal(35 + 10, outcome.EffectiveChance);
    }

    [Fact]
    public void A_target_worse_than_badly_wounded_still_counts()
    {
        // Rowan at 1 of 14 is barely standing — worse than badly wounded, and must not be a HARDER mark.
        var rowan = TestWorld.RowanV07(health: 1);
        Assert.Equal(HealthBand.BarelyStanding, HealthBands.Of(rowan));

        var engine = Engine(new ScriptedRng(50), rowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Single(outcome.Modifiers, m => m.SourceId == "target-badly-wounded");
    }

    [Fact]
    public void A_scared_intimidator_subtracts_ten()
    {
        var vark = TestWorld.VarkV07() with { Fear = 3 };
        var engine = Engine(new ScriptedRng(50), TestWorld.RowanV07(), TestWorld.ElaraV07(), vark, TestWorld.SkritV07());

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Single(outcome.Modifiers, m => m.SourceId == "intimidator-scared" && m.Value == IntimidationRules.IntimidatorScared);
        Assert.Equal(35 - 10, outcome.EffectiveChance);
    }

    [Fact]
    public void The_effective_chance_clamps_to_ninety_at_the_top()
    {
        // Fear 5 (+50), outnumbered (+15), badly wounded (+10) → 110 before the clamp.
        var rowan = TestWorld.RowanV07(health: 3) with { Fear = 5 };
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false, rowan, TestWorld.VarkV07(), TestWorld.SkritV07()),
            new ScriptedRng(50), CombatRules.Default);

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Equal(35 + 50 + 15 + 10, outcome.BaseChance + outcome.Modifiers.Sum(m => m.Value));
        Assert.Equal(IntimidationRules.MaximumChance, outcome.EffectiveChance);
    }

    [Fact]
    public void The_effective_chance_clamps_to_ten_at_the_bottom()
    {
        var rules = CombatRules.Default with { BaseIntimidationChance = 5 };
        var vark = TestWorld.VarkV07() with { Fear = 3 };
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(), TestWorld.ElaraV07(), vark, TestWorld.SkritV07()),
            new ScriptedRng(50), rules);

        var outcome = Assert.IsType<IntimidateOutcome>(engine.Execute(Threat("Vark", "Rowan")).Outcome);

        Assert.Equal(-5, outcome.BaseChance + outcome.Modifiers.Sum(m => m.Value));
        Assert.Equal(IntimidationRules.MinimumChance, outcome.EffectiveChance);
    }

    [Fact]
    public void What_was_said_never_reaches_the_odds()
    {
        // Two threats, identical state, wildly different words — which the engine never sees, because the
        // action carries only a reference. The effective chance and the roll are identical.
        static int ChanceFor(int speechId)
        {
            var engine = new GameEngine(
                TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(), TestWorld.ElaraV07(),
                    TestWorld.VarkV07(), TestWorld.SkritV07()),
                new ScriptedRng(50), CombatRules.Default);
            return ((IntimidateOutcome)engine.Execute(Threat("Vark", "Rowan", speechId)).Outcome!).EffectiveChance;
        }

        Assert.Equal(ChanceFor(1), ChanceFor(9999));
    }

    // ------------------------------------------------------------------------------------------
    // Success and failure
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_roll_at_or_under_the_effective_chance_frightens_the_target_by_one()
    {
        var engine = Engine(new ScriptedRng(35));

        var result = engine.Execute(Threat("Vark", "Rowan"));

        var outcome = Assert.IsType<IntimidateOutcome>(result.Outcome);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, engine.State.RequireById(TestWorld.RowanId).Fear);
        var change = Assert.Single(result.FearChanges);
        Assert.Equal(FearChangeCause.Intimidated, change.Cause);
        Assert.Equal(TestWorld.VarkId, change.SourceCharacterId);
    }

    [Fact]
    public void A_roll_over_the_effective_chance_changes_no_fear_at_all()
    {
        var engine = Engine(new ScriptedRng(36));

        var result = engine.Execute(Threat("Vark", "Rowan"));

        var outcome = Assert.IsType<IntimidateOutcome>(result.Outcome);
        Assert.False(outcome.Succeeded);
        Assert.Empty(result.FearChanges);
        Assert.Equal(0, engine.State.RequireById(TestWorld.RowanId).Fear);
    }

    [Fact]
    public void A_successful_threat_takes_nothing_and_forces_nothing()
    {
        var engine = Engine(new ScriptedRng(1));
        var before = engine.State.RequireById(TestWorld.RowanId);

        engine.Execute(Threat("Vark", "Rowan"));

        var after = engine.State.RequireById(TestWorld.RowanId);
        Assert.Equal(CharacterDisposition.Active, after.Disposition);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.Weapon, after.Weapon);
        Assert.Equal(before.Inventory, after.Inventory);
        Assert.False(after.IsDisarmed);
        Assert.Empty(engine.State.SurrenderOffers);
        Assert.Empty(engine.State.SurrenderAgreements);
    }

    [Fact]
    public void Exactly_one_draw_is_made_and_it_carries_the_whole_derivation()
    {
        var rowan = TestWorld.RowanV07(health: 4) with { Fear = 1 };
        var engine = Engine(new ScriptedRng(55), rowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(Threat("Vark", "Rowan"));

        var draw = Assert.Single(result.RngDraws);
        Assert.Equal("intimidation.check", draw.Purpose);
        Assert.Equal("intimidate_character", draw.ActionType);
        Assert.Equal(TestWorld.VarkId, draw.ActorId);
        Assert.Equal(TestWorld.RowanId, draw.TargetId);
        Assert.Equal(35, draw.BaseChance);
        Assert.Equal(55, draw.EffectiveThresholdOrRoll());
        Assert.Equal(2, draw.ModifierDetails.Count);
        Assert.Contains(draw.ModifierDetails, m => m.SourceId == "target-fear" && m.Value == 10);
        Assert.Contains(draw.ModifierDetails, m => m.SourceId == "target-badly-wounded" && m.Value == 10);
        Assert.Equal(55, draw.Threshold);
        Assert.Equal("intimidated", draw.Result);
        Assert.Equal(0, draw.SequenceBefore);
        Assert.Equal(1, draw.SequenceAfter);
    }

    // ------------------------------------------------------------------------------------------
    // One attempt per pair
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_actor_may_threaten_a_given_enemy_only_once_in_the_encounter()
    {
        var engine = Engine(new ScriptedRng(1, 1));

        Assert.True(engine.Execute(Threat("Vark", "Rowan")).Accepted);

        var second = engine.Execute(Threat("Vark", "Rowan"));
        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.AlreadyAttemptedIntimidation, second.RejectionReason);
    }

    [Fact]
    public void A_failed_attempt_is_spent_just_as_surely_as_a_successful_one()
    {
        var engine = Engine(new ScriptedRng(100));

        Assert.False(((IntimidateOutcome)engine.Execute(Threat("Vark", "Rowan")).Outcome!).Succeeded);
        Assert.Equal(EngineRejectionReason.AlreadyAttemptedIntimidation,
            engine.Execute(Threat("Vark", "Rowan")).RejectionReason);
    }

    [Fact]
    public void The_limit_is_per_pair_not_per_actor()
    {
        var engine = Engine(new ScriptedRng(1, 1, 1));

        Assert.True(engine.Execute(Threat("Vark", "Rowan")).Accepted);
        Assert.True(engine.Execute(Threat("Vark", "Elara")).Accepted);
        Assert.True(engine.Execute(Threat("Skrit", "Rowan")).Accepted);
    }

    [Fact]
    public void Remaining_targets_reflect_what_is_left_to_try()
    {
        var engine = Engine(new ScriptedRng(1));

        Assert.Equal(["Rowan", "Elara"],
            engine.State.RemainingIntimidationTargets(TestWorld.VarkId).Select(c => c.Name));

        engine.Execute(Threat("Vark", "Rowan"));

        Assert.Equal(["Elara"],
            engine.State.RemainingIntimidationTargets(TestWorld.VarkId).Select(c => c.Name));
    }

    // ------------------------------------------------------------------------------------------
    // The structural speech gate, which runs before any draw
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_threat_with_no_words_spoken_is_refused_before_any_draw()
    {
        var rng = new ScriptedRng(1);
        var engine = Engine(rng);

        var result = engine.Execute(Threat("Vark", "Rowan", speech: null));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.IntimidationRequiresSpeech, result.RejectionReason);
        Assert.Empty(result.RngDraws);
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void A_threat_whose_declared_addressee_is_somebody_else_is_refused_before_any_draw()
    {
        var rng = new ScriptedRng(1);
        var engine = Engine(rng);

        var result = engine.Execute(Threat("Vark", "Rowan", addressedTo: TestWorld.ElaraId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.SpeechAddressedToSomebodyElse, result.RejectionReason);
        Assert.Equal(0, rng.DrawCount);
        Assert.Empty(engine.State.IntimidationAttempts);
    }

    [Fact]
    public void A_threat_whose_declared_addressee_matches_the_target_is_accepted()
    {
        var engine = Engine(new ScriptedRng(1));

        Assert.True(engine.Execute(Threat("Vark", "Rowan", addressedTo: TestWorld.RowanId)).Accepted);
    }

    [Fact]
    public void A_threat_with_no_declared_addressee_leaves_the_bound_target_unchallenged()
    {
        var engine = Engine(new ScriptedRng(1));

        Assert.True(engine.Execute(Threat("Vark", "Rowan", addressedTo: null)).Accepted);
    }

    // ------------------------------------------------------------------------------------------
    // Who may be threatened
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_ally_cannot_be_threatened()
    {
        var result = Engine(new ScriptedRng(1)).Execute(Threat("Vark", "Skrit"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsNotAnOpponent, result.RejectionReason);
    }

    [Fact]
    public void The_actor_cannot_threaten_themselves()
    {
        var result = Engine(new ScriptedRng(1)).Execute(Threat("Vark", "Vark"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsSelf, result.RejectionReason);
    }

    [Fact]
    public void Somebody_who_has_already_yielded_cannot_be_threatened()
    {
        var engine = Engine(new ScriptedRng(1));
        engine.Execute(new OfferSurrenderAction("Skrit", "Rowan", ["purse-skrit"], ForfeitWeapon: true));
        engine.Execute(new AcceptSurrenderAction("Rowan", "offer-1"));

        var result = engine.Execute(Threat("Rowan", "Skrit"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasSurrendered, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Steady Ally
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Steadying_an_ally_removes_one_point_of_fear_and_rolls_nothing()
    {
        var elara = TestWorld.ElaraV07() with { Fear = 2 };
        var rng = new ScriptedRng();
        var engine = Engine(rng, TestWorld.RowanV07(), elara, TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(Steady("Rowan", "Elara"));

        Assert.True(result.Accepted);
        Assert.Equal(1, engine.State.RequireById(TestWorld.ElaraId).Fear);
        Assert.Empty(result.RngDraws);
        Assert.Equal(0, rng.DrawCount);
        Assert.Equal(FearChangeCause.SteadiedByAlly, Assert.Single(result.FearChanges).Cause);
    }

    [Fact]
    public void Steadying_can_bring_an_ally_back_from_being_scared()
    {
        var elara = TestWorld.ElaraV07() with { Fear = 3 };
        var engine = Engine(new ScriptedRng(), TestWorld.RowanV07(), elara, TestWorld.VarkV07(), TestWorld.SkritV07());
        Assert.True(engine.State.RequireById(TestWorld.ElaraId).IsScared);

        var outcome = Assert.IsType<SteadyAllyOutcome>(engine.Execute(Steady("Rowan", "Elara")).Outcome);

        Assert.True(outcome.NoLongerScared);
        Assert.False(engine.State.RequireById(TestWorld.ElaraId).IsScared);
        Assert.DoesNotContain(engine.State.StatusesOn(TestWorld.ElaraId), s => s.Kind == StatusEffectKind.Scared);
    }

    [Fact]
    public void Steadying_an_ally_who_is_not_afraid_spends_the_turn_and_changes_nothing()
    {
        var engine = Engine(new ScriptedRng());

        var result = engine.Execute(Steady("Rowan", "Elara"));

        Assert.True(result.Accepted);
        Assert.Equal(0, engine.State.RequireById(TestWorld.ElaraId).Fear);
        Assert.True(Assert.Single(result.FearChanges).Absorbed);
        Assert.True(((SteadyAllyOutcome)result.Outcome!).FearChanges.Single().Absorbed);
    }

    [Fact]
    public void An_actor_cannot_steady_themselves()
    {
        var result = Engine(new ScriptedRng()).Execute(Steady("Rowan", "Rowan"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsSelf, result.RejectionReason);
    }

    [Fact]
    public void An_enemy_cannot_be_steadied()
    {
        var result = Engine(new ScriptedRng()).Execute(Steady("Rowan", "Vark"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsNotAnAlly, result.RejectionReason);
    }

    [Fact]
    public void Steadying_requires_words_actually_spoken()
    {
        var result = Engine(new ScriptedRng()).Execute(Steady("Rowan", "Elara", speech: null));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.IntimidationRequiresSpeech, result.RejectionReason);
    }

    [Fact]
    public void Steadying_aimed_at_somebody_other_than_the_one_spoken_to_is_refused()
    {
        var result = Engine(new ScriptedRng()).Execute(Steady("Rowan", "Elara", addressedTo: TestWorld.VarkId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.SpeechAddressedToSomebodyElse, result.RejectionReason);
    }

    [Fact]
    public void Remaining_steady_targets_are_only_allies_with_fear_to_shed()
    {
        var engine = Engine(new ScriptedRng());
        Assert.Empty(engine.State.RemainingSteadyTargets(TestWorld.RowanId));

        var afraid = new GameEngine(
            TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(),
                TestWorld.ElaraV07() with { Fear = 2 }, TestWorld.VarkV07(), TestWorld.SkritV07()),
            new ScriptedRng(), CombatRules.Default);

        Assert.Equal(["Elara"], afraid.State.RemainingSteadyTargets(TestWorld.RowanId).Select(c => c.Name));
    }
}

/// <summary>Small readability helper for the draw assertions.</summary>
internal static class RngDrawAssertions
{
    /// <summary>The threshold the raw roll was compared against — named for what the test is asserting.</summary>
    public static int EffectiveThresholdOrRoll(this RngDraw draw) => draw.Threshold;
}
