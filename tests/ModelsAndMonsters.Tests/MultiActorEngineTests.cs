using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The multi-actor engine, scenario seeding and team terminal condition, tested as ordinary
/// deterministic software with no model involvement.
/// </summary>
public sealed class MultiActorEngineTests
{
    // ------------------------------------------------------------------------------------------
    // Scenario seeding (test #1)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_scenario_seeds_exactly_four_characters_with_unique_ids_and_correct_teams()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.TwoVsTwoScenario());

        Assert.Equal(4, state.Characters.Length);

        var ids = state.Characters.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal([TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId], ids);

        Assert.Equal(TestWorld.HeroesTeam, state.RequireById(TestWorld.RowanId).Team);
        Assert.Equal(TestWorld.HeroesTeam, state.RequireById(TestWorld.ElaraId).Team);
        Assert.Equal(TestWorld.GoblinsTeam, state.RequireById(TestWorld.VarkId).Team);
        Assert.Equal(TestWorld.GoblinsTeam, state.RequireById(TestWorld.SkritId).Team);

        // Two teams of two.
        Assert.Equal(2, state.Teams().Count);
        Assert.Equal(2, state.LivingOnTeam(TestWorld.HeroesTeam).Count());
        Assert.Equal(2, state.LivingOnTeam(TestWorld.GoblinsTeam).Count());
    }

    [Fact]
    public void A_character_with_no_team_falls_back_to_a_team_derived_from_its_role()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.Scenario()); // v0.1 scenario: no Team set

        Assert.Equal("Heroes", state.RequireById(TestWorld.HeroId).Team);
        Assert.Equal("Monsters", state.RequireById(TestWorld.MonsterId).Team);
    }

    [Fact]
    public void Duplicate_ids_across_the_four_characters_are_rejected_when_seeding()
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Characters[1].Id = scenario.Characters[0].Id;

        Assert.Throws<InvalidOperationException>(() => ScenarioFactory.CreateInitialState(scenario));
    }

    // ------------------------------------------------------------------------------------------
    // Team terminal condition (tests #4 and #5)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_encounter_continues_while_each_team_has_at_least_one_living_character()
    {
        // Both teams fully alive.
        Assert.False(TerminalCondition.Evaluate(TestWorld.TwoVsTwoState()).IsOver);

        // One goblin down, the other still standing: the goblin team lives, so it is not over.
        var oneGoblinDown = TestWorld.State(TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit(health: 0));
        var result = TerminalCondition.Evaluate(oneGoblinDown);
        Assert.False(result.IsOver);
        Assert.Contains(result.Standings, s => s.Team == TestWorld.GoblinsTeam && s is { Living: 1, Total: 2 });

        // One hero down likewise.
        var oneHeroDown = TestWorld.State(TestWorld.Rowan(health: 0), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit());
        Assert.False(TerminalCondition.Evaluate(oneHeroDown).IsOver);
    }

    [Fact]
    public void The_encounter_ends_immediately_when_one_team_has_no_living_characters()
    {
        var goblinsWiped = TestWorld.State(
            TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(health: 0), TestWorld.Skrit(health: 0));

        var result = TerminalCondition.Evaluate(goblinsWiped);

        Assert.True(result.IsOver);
        Assert.Equal([TestWorld.HeroesTeam], result.WinningTeams);
        Assert.Equal([TestWorld.GoblinsTeam], result.EliminatedTeams);
        Assert.Contains("Heroes win", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_goblins_win_when_both_heroes_are_dead()
    {
        var heroesWiped = TestWorld.State(
            TestWorld.Rowan(health: 0), TestWorld.Elara(health: 0), TestWorld.Vark(), TestWorld.Skrit());

        var result = TerminalCondition.Evaluate(heroesWiped);

        Assert.True(result.IsOver);
        Assert.Equal([TestWorld.GoblinsTeam], result.WinningTeams);
        Assert.Equal([TestWorld.HeroesTeam], result.EliminatedTeams);
    }

    [Fact]
    public void A_voluntary_pass_cannot_satisfy_a_terminal_condition_because_it_changes_no_health()
    {
        // Everyone alive; "passing" is not modelled in state at all, so the evaluation is simply "not over".
        Assert.False(TerminalCondition.Evaluate(TestWorld.TwoVsTwoState()).IsOver);
    }

    // ------------------------------------------------------------------------------------------
    // Targeting (tests #6 and #7, at the engine level)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_attack_resolves_against_the_intended_target_and_leaves_the_bystanders_untouched()
    {
        var engine = new GameEngine(TestWorld.TwoVsTwoState(), new SeededRng(1), CombatRules.NoGlancing);

        // Rowan strikes Skrit specifically, with two goblins present.
        var result = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        Assert.True(result.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(TestWorld.SkritId, outcome.TargetId);
        Assert.Equal("Skrit", outcome.TargetName);

        // Longsword 5 minus Skrit armour 1 = 4.
        Assert.Equal(4, engine.State.RequireById(TestWorld.SkritId).Health);

        // The other goblin and both heroes are untouched.
        Assert.Equal(12, engine.State.RequireById(TestWorld.VarkId).Health);
        Assert.Equal(14, engine.State.RequireById(TestWorld.RowanId).Health);
        Assert.Equal(11, engine.State.RequireById(TestWorld.ElaraId).Health);
    }

    [Fact]
    public void A_dead_target_is_rejected_and_no_other_character_is_silently_substituted()
    {
        var state = TestWorld.State(TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit(health: 0));
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);
        var before = engine.State;

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsDead, result.RejectionReason);

        // Nothing changed, and no living goblin took the blow in Skrit's place.
        Assert.Same(before, engine.State);
        Assert.Equal(12, engine.State.RequireById(TestWorld.VarkId).Health);
    }

    [Fact]
    public void An_unknown_target_reference_is_rejected_rather_than_guessed()
    {
        var engine = new GameEngine(TestWorld.TwoVsTwoState(), new SeededRng(1), CombatRules.NoGlancing);

        // "the goblin" is not a name in the state; the engine refuses rather than picking one.
        var result = engine.Execute(new AttackCharacterAction("Rowan", "the goblin", "Longsword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownTarget, result.RejectionReason);
    }

    [Fact]
    public void The_engine_resolves_a_friendly_fire_strike_that_the_dungeon_master_actually_translated()
    {
        // There is no special friendly-fire prohibition: if the DM translated a strike on an ally, the
        // engine resolves it. Prompts and goals discourage it; the engine does not forbid it.
        var engine = new GameEngine(TestWorld.TwoVsTwoState(), new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));

        Assert.True(result.Accepted);
        Assert.Equal(TestWorld.ElaraId, ((AttackOutcome)result.Outcome!).TargetId);
    }

    // ------------------------------------------------------------------------------------------
    // Reproducibility (test #12)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Replaying_the_same_accepted_actions_with_the_same_seed_produces_identical_outcomes_and_state()
    {
        const long seed = 20260817;

        // Hit chances below 100 and glancing enabled, so both the hit roll and the glancing roll matter
        // and the seed genuinely drives the outcome rather than every attack landing for full damage.
        static GameEngine NewEngine() => new(
            TestWorld.State(
                TestWorld.Rowan(hitChance: 80), TestWorld.Elara(hitChance: 70),
                TestWorld.Vark(hitChance: 75), TestWorld.Skrit(hitChance: 65)),
            new SeededRng(seed),
            CombatRules.Default);

        var script = new[]
        {
            new AttackCharacterAction("Rowan", "Vark", "Longsword"),
            new AttackCharacterAction("Elara", "Skrit", "Iron Mace"),
            new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"),
            new AttackCharacterAction("Skrit", "Elara", "Crude Spear"),
            new AttackCharacterAction("Rowan", "Vark", "Longsword"),
            new AttackCharacterAction("Elara", "Skrit", "Iron Mace")
        };

        var first = NewEngine();
        var second = NewEngine();

        var firstOutcomes = script.Select(a => (AttackOutcome)first.Execute(a).Outcome!).ToList();
        var secondOutcomes = script.Select(a => (AttackOutcome)second.Execute(a).Outcome!).ToList();

        // Every roll, its interpretation, and the damage dealt match draw for draw.
        for (var i = 0; i < script.Length; i++)
        {
            Assert.Equal(firstOutcomes[i].HitRoll, secondOutcomes[i].HitRoll);
            Assert.Equal(firstOutcomes[i].GlancingRoll, secondOutcomes[i].GlancingRoll);
            Assert.Equal(firstOutcomes[i].Hit, secondOutcomes[i].Hit);
            Assert.Equal(firstOutcomes[i].DamageDealt, secondOutcomes[i].DamageDealt);
        }

        // And the final authoritative state is identical for all four characters.
        foreach (var id in new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId })
        {
            Assert.Equal(first.State.RequireById(id).Health, second.State.RequireById(id).Health);
            Assert.Equal(first.State.RequireById(id).Injuries.Length, second.State.RequireById(id).Injuries.Length);
        }

        // A different seed is not guaranteed to differ on any single draw, but must differ somewhere.
        var third = new GameEngine(
            TestWorld.State(
                TestWorld.Rowan(hitChance: 80), TestWorld.Elara(hitChance: 70),
                TestWorld.Vark(hitChance: 75), TestWorld.Skrit(hitChance: 65)),
            new SeededRng(seed + 1),
            CombatRules.Default);
        var thirdRolls = script.Select(a => ((AttackOutcome)third.Execute(a).Outcome!).HitRoll).ToList();
        Assert.NotEqual(firstOutcomes.Select(o => o.HitRoll), thirdRolls);
    }

    // ------------------------------------------------------------------------------------------
    // RNG draw records on the engine result (supports test #11)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_landed_attack_records_a_hit_draw_and_a_glancing_draw_with_full_context()
    {
        var engine = new GameEngine(
            TestWorld.State(TestWorld.Rowan(hitChance: 80), TestWorld.Vark()),
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid),
            CombatRules.Default);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.Equal(2, result.RngDraws.Count);

        var hit = result.RngDraws[0];
        Assert.Equal("attack.hit-check", hit.Purpose);
        Assert.Equal("attack_character", hit.ActionType);
        Assert.Equal(TestWorld.RowanId, hit.ActorId);
        Assert.Equal(TestWorld.VarkId, hit.TargetId);
        Assert.Equal(80, hit.Threshold);
        Assert.Equal(ScriptedRng.Hits, hit.RawRoll);
        Assert.Equal("hit", hit.Result);
        Assert.Equal(1, hit.RangeMin);
        Assert.Equal(100, hit.RangeMax);

        // The two draws advance the generator's sequence position by one apiece, in order.
        var glancing = result.RngDraws[1];
        Assert.Equal("attack.quality-check", glancing.Purpose);
        Assert.Equal(hit.SequenceAfter, glancing.SequenceBefore);
        Assert.Equal(glancing.SequenceBefore + 1, glancing.SequenceAfter);
    }

    [Fact]
    public void A_missed_attack_records_only_the_hit_draw()
    {
        var engine = new GameEngine(
            TestWorld.State(TestWorld.Rowan(hitChance: 50), TestWorld.Vark()),
            new ScriptedRng(ScriptedRng.Misses),
            CombatRules.Default);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        var draw = Assert.Single(result.RngDraws);
        Assert.Equal("attack.hit-check", draw.Purpose);
        Assert.Equal("miss", draw.Result);
    }

    [Fact]
    public void A_rejected_attack_makes_no_draws()
    {
        var engine = new GameEngine(TestWorld.TwoVsTwoState(), new SeededRng(1), CombatRules.Default);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Rowan", "Longsword"));

        Assert.False(result.Accepted);
        Assert.Empty(result.RngDraws);
    }
}
