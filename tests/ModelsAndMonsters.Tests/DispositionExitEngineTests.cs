using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.5 non-lethal outcomes at the engine level: opening the single cellar exit, escaping through it,
/// and surrendering — plus the disposition-based team terminal condition and its outcome classification.
/// These are the deterministic guarantees the whole slice rests on, independent of any model.
/// </summary>
public sealed class DispositionExitEngineTests
{
    private static GameEngine EngineWith(bool exitOpen, IRng? rng = null, params Character[] characters) =>
        new(TestWorld.StateWithExit(TestWorld.StairDoor(exitOpen),
                characters.Length == 0
                    // Vark carries a purse in these tests because terms of surrender must promise a carried
                    // item: the weapon alone is not enough (see GameEngine.ResolveOfferSurrender).
                    ? [TestWorld.Rowan(), TestWorld.Elara(health: 6),
                        TestWorld.Vark() with { Inventory = [TestWorld.Purse("vark")] }, TestWorld.Skrit()]
                    : characters),
            rng ?? new SeededRng(1), CombatRules.NoGlancing);

    /// <summary>
    /// Puts a character out of the fight the only way v0.7 allows: they offer concrete terms to a named
    /// opponent, and that opponent accepts. Since v0.7 there is no unilateral surrender at all — a character
    /// cannot make itself untargetable by declaring anything — so every "surrendered" state in these tests is
    /// reached through the negotiation, two accepted actions and two turns.
    /// </summary>
    private static (EngineResult Offer, EngineResult Accepted) NegotiatedSurrender(
        GameEngine engine, string offerer, string recipient, bool forfeitWeapon = true, params string[] items)
    {
        // Terms must promise a carried item, so the offerer's purse stands in when a caller names none.
        var promised = items.Length > 0 ? items : [$"purse-{offerer.ToLowerInvariant()}"];
        var offer = engine.Execute(new OfferSurrenderAction(offerer, recipient, promised, forfeitWeapon));
        var offerId = ((OfferSurrenderOutcome)offer.Outcome!).OfferId;
        var accepted = engine.Execute(new AcceptSurrenderAction(recipient, offerId));
        return (offer, accepted);
    }

    // ------------------------------------------------------------------------------------------
    // Disposition derivations (the compatibility predicates all derive from one enum)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Disposition_defaults_from_health_so_pre_v05_constructions_keep_their_meaning()
    {
        Assert.Equal(CharacterDisposition.Active, TestWorld.Vark(health: 12).Disposition);
        Assert.Equal(CharacterDisposition.Dead, TestWorld.Vark(health: 0).Disposition);
    }

    [Theory]
    [InlineData(CharacterDisposition.Active, true, true, true, true)]
    [InlineData(CharacterDisposition.Surrendered, true, true, false, false)]
    [InlineData(CharacterDisposition.Escaped, true, false, false, false)]
    [InlineData(CharacterDisposition.Dead, false, false, false, false)]
    public void The_four_predicates_follow_from_disposition(
        CharacterDisposition disposition, bool alive, bool present, bool canAct, bool combatTarget)
    {
        var character = TestWorld.Vark() with { Disposition = disposition };
        Assert.Equal(alive, character.IsAlive);
        Assert.Equal(present, character.IsPresent);
        Assert.Equal(canAct, character.CanAct);
        Assert.Equal(combatTarget, character.IsCombatTarget);
    }

    // ------------------------------------------------------------------------------------------
    // The scenario seeds one closed, unlocked exit (test #1)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_scenario_seeds_one_closed_unlocked_exit()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.TwoVsTwoScenarioWithExit());

        var exit = Assert.Single(state.Room.Exits);
        Assert.Equal(TestWorld.StairDoorId, exit.Id);
        Assert.False(exit.IsOpen);
        // There is no lock state to model; a closed exit opens normally, which the open test proves.
    }

    // ------------------------------------------------------------------------------------------
    // Opening the exit (tests #2, #3, #4)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Opening_the_closed_exit_opens_it_and_increments_the_world_version()
    {
        var engine = EngineWith(exitOpen: false);
        var versionBefore = engine.State.Version;

        var result = engine.Execute(new OpenExitAction("Vark", "Cellar Stair Door"));

        Assert.True(result.Accepted);
        Assert.True(engine.State.Exits.Single().IsOpen);
        Assert.Equal(versionBefore + 1, engine.State.Version);
    }

    [Fact]
    public void Opening_an_already_open_exit_is_rejected_without_mutation()
    {
        var engine = EngineWith(exitOpen: true);
        var versionBefore = engine.State.Version;

        var result = engine.Execute(new OpenExitAction("Vark", "Cellar Stair Door"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ExitAlreadyOpen, result.RejectionReason);
        Assert.Equal(versionBefore, engine.State.Version);
        Assert.True(engine.State.Exits.Single().IsOpen);
    }

    [Fact]
    public void Opening_the_exit_surrendering_and_escaping_use_no_rng()
    {
        var rng = new SeededRng(1);
        var engine = EngineWith(exitOpen: false, rng: rng);

        var open = engine.Execute(new OpenExitAction("Vark", "Cellar Stair Door"));
        var escape = engine.Execute(new EscapeEncounterAction("Skrit", "Cellar Stair Door"));
        var (offer, accepted) = NegotiatedSurrender(engine, "Vark", "Rowan");

        Assert.True(open.Accepted && escape.Accepted && offer.Accepted && accepted.Accepted);
        Assert.Empty(open.RngDraws);
        Assert.Empty(escape.RngDraws);
        Assert.Empty(offer.RngDraws);
        Assert.Empty(accepted.RngDraws);
        // The shared combat generator was never advanced, so seeded replay of the fight is unaffected.
        Assert.Equal(0, rng.DrawCount);
    }

    // ------------------------------------------------------------------------------------------
    // Escaping (tests #5, #6, #7, #8)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Escaping_through_a_closed_exit_is_rejected_and_the_actor_stays_active()
    {
        var engine = EngineWith(exitOpen: false);

        var result = engine.Execute(new EscapeEncounterAction("Skrit", "Cellar Stair Door"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ExitClosed, result.RejectionReason);
        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.SkritId).Disposition);
    }

    [Fact]
    public void Escaping_through_an_open_exit_sets_the_actor_to_escaped_and_records_the_exit()
    {
        var engine = EngineWith(exitOpen: true);

        var result = engine.Execute(new EscapeEncounterAction("Skrit", "Cellar Stair Door"));

        Assert.True(result.Accepted);
        var skrit = engine.State.RequireById(TestWorld.SkritId);
        Assert.Equal(CharacterDisposition.Escaped, skrit.Disposition);
        Assert.Equal(TestWorld.StairDoorId, skrit.EscapedThroughExitId);
    }

    [Fact]
    public void An_escaped_character_keeps_its_health_inventory_and_weapon()
    {
        var elara = TestWorld.Elara(health: 6, inventory: [TestWorld.HealingPotion()]);
        var engine = new GameEngine(
            TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
                TestWorld.Rowan(), elara, TestWorld.Vark(), TestWorld.Skrit()),
            new SeededRng(1), CombatRules.NoGlancing);

        engine.Execute(new EscapeEncounterAction("Elara", "Cellar Stair Door"));

        var after = engine.State.RequireById(TestWorld.ElaraId);
        Assert.True(after.IsAlive);
        Assert.Equal(6, after.Health);
        Assert.Contains(after.Inventory, i => i.IsHealingItem);
        Assert.NotNull(after.Weapon);
    }

    [Fact]
    public void An_escaped_character_is_no_longer_present_and_cannot_be_targeted()
    {
        var engine = EngineWith(exitOpen: true);
        engine.Execute(new EscapeEncounterAction("Skrit", "Cellar Stair Door"));

        var skrit = engine.State.RequireById(TestWorld.SkritId);
        Assert.False(skrit.IsPresent);
        Assert.False(skrit.IsCombatTarget);

        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));
        Assert.False(attack.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasEscaped, attack.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // Surrendering, now negotiated (tests #10, #11, #12, #14, carried forward to v0.7)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_offer_sets_the_offerer_to_surrendered()
    {
        var engine = EngineWith(exitOpen: false);
        var versionBefore = engine.State.Version;

        var (offer, accepted) = NegotiatedSurrender(engine, "Vark", "Rowan");

        Assert.True(offer.Accepted);
        Assert.True(accepted.Accepted);
        Assert.Equal(CharacterDisposition.Surrendered, engine.State.RequireById(TestWorld.VarkId).Disposition);

        // Two accepted state-changing actions on two turns — the offer, then the acceptance — and then a
        // third version for the consequence the acceptance had on somebody who was not party to it: Vark
        // leaving the fight puts Skrit alone against two, so Skrit's outnumbering latch flips and his fear
        // rises. That is exactly the v0.8 rule firing, not an extra action.
        Assert.Equal(versionBefore + 3, engine.State.Version);
        Assert.Equal(1, engine.State.RequireById(TestWorld.SkritId).Fear);
    }

    [Fact]
    public void An_offer_alone_leaves_the_offerer_active_and_targetable()
    {
        var engine = EngineWith(exitOpen: false);

        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        Assert.True(offer.Accepted);

        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.True(vark.CanAct);
        Assert.True(vark.IsCombatTarget);

        // Nothing has been given up yet: the sabre is still in its hand.
        Assert.NotNull(vark.Weapon);

        // And it can still be cut down where it stands.
        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        Assert.True(attack.Accepted);
    }

    [Fact]
    public void A_surrendered_character_stays_alive_and_present_but_cannot_act_or_be_targeted()
    {
        var engine = EngineWith(exitOpen: false);
        NegotiatedSurrender(engine, "Vark", "Rowan");

        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.True(vark.IsAlive);
        Assert.True(vark.IsPresent);
        Assert.False(vark.CanAct);
        Assert.False(vark.IsCombatTarget);

        // Unlike v0.5's unilateral surrender, an accepted one disarms: the sabre left its hand.
        Assert.Null(vark.Weapon);
        Assert.True(vark.IsDisarmed);

        var attack = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        Assert.False(attack.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasSurrendered, attack.RejectionReason);
    }

    [Fact]
    public void Surrender_does_not_change_a_teammate_disposition()
    {
        var engine = EngineWith(exitOpen: false);
        NegotiatedSurrender(engine, "Vark", "Rowan");

        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.SkritId).Disposition);
    }

    // ------------------------------------------------------------------------------------------
    // The disposition-based team terminal condition (tests #16, #17, #18)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_individual_surrender_does_not_end_the_encounter_while_a_teammate_is_active()
    {
        var engine = EngineWith(exitOpen: false);
        NegotiatedSurrender(engine, "Vark", "Rowan");

        Assert.False(TerminalCondition.Evaluate(engine.State).IsOver);
    }

    [Fact]
    public void An_individual_escape_does_not_end_the_encounter_while_a_teammate_is_active()
    {
        var engine = EngineWith(exitOpen: true);
        engine.Execute(new EscapeEncounterAction("Vark", "Cellar Stair Door"));

        Assert.False(TerminalCondition.Evaluate(engine.State).IsOver);
    }

    [Fact]
    public void The_encounter_ends_when_one_team_has_no_active_characters_even_though_all_are_alive()
    {
        var engine = EngineWith(exitOpen: true);
        NegotiatedSurrender(engine, "Vark", "Rowan");
        engine.Execute(new EscapeEncounterAction("Skrit", "Cellar Stair Door"));

        var result = TerminalCondition.Evaluate(engine.State);
        Assert.True(result.IsOver);
        Assert.Equal([TestWorld.HeroesTeam], result.WinningTeams);
        Assert.Equal([TestWorld.GoblinsTeam], result.EliminatedTeams);
        // Nobody died — the whole losing team is still alive.
        Assert.All(engine.State.Characters, c => Assert.True(c.IsAlive));
    }

    // ------------------------------------------------------------------------------------------
    // Outcome classification (tests #19, #20, #21, #22, #23)
    // ------------------------------------------------------------------------------------------

    private static Character Dead(Character c) => c with { Health = 0, Disposition = CharacterDisposition.Dead };
    private static Character Surrendered(Character c) => c with { Disposition = CharacterDisposition.Surrendered };
    private static Character Escaped(Character c) => c with { Disposition = CharacterDisposition.Escaped, EscapedThroughExitId = TestWorld.StairDoorId };

    [Fact]
    public void A_fully_dead_defeated_team_is_classified_as_Elimination()
    {
        var state = TestWorld.State(TestWorld.Rowan(), TestWorld.Elara(), Dead(TestWorld.Vark()), Dead(TestWorld.Skrit()));
        Assert.Equal(EncounterOutcome.Elimination, TerminalCondition.Evaluate(state).Outcome);
    }

    [Fact]
    public void A_fully_surrendered_defeated_team_is_classified_as_Surrender()
    {
        var state = TestWorld.State(TestWorld.Rowan(), TestWorld.Elara(), Surrendered(TestWorld.Vark()), Surrendered(TestWorld.Skrit()));
        var result = TerminalCondition.Evaluate(state);
        Assert.Equal(EncounterOutcome.Surrender, result.Outcome);
        Assert.Equal([TestWorld.HeroesTeam], result.WinningTeams);
    }

    [Fact]
    public void A_fully_escaped_defeated_team_is_classified_as_Withdrawal()
    {
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
            TestWorld.Rowan(), TestWorld.Elara(), Escaped(TestWorld.Vark()), Escaped(TestWorld.Skrit()));
        Assert.Equal(EncounterOutcome.Withdrawal, TerminalCondition.Evaluate(state).Outcome);
    }

    [Fact]
    public void A_dead_and_escaped_combination_is_classified_as_Mixed()
    {
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
            TestWorld.Rowan(), TestWorld.Elara(), Dead(TestWorld.Vark()), Escaped(TestWorld.Skrit()));
        var result = TerminalCondition.Evaluate(state);
        Assert.Equal(EncounterOutcome.Mixed, result.Outcome);

        // The per-character resolution names the exit Skrit left by, and calls Vark killed — never "fallen".
        Assert.Contains(result.Resolutions, r => r.CharacterName == "Vark" && r.Summary == "Vark was killed.");
        Assert.Contains(result.Resolutions, r => r.CharacterName == "Skrit" && r.Summary.Contains("Cellar Stair Door", StringComparison.Ordinal));
    }

    [Fact]
    public void No_active_team_remaining_is_classified_as_a_Draw()
    {
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
            Dead(TestWorld.Rowan()), Surrendered(TestWorld.Elara()), Dead(TestWorld.Vark()), Escaped(TestWorld.Skrit()));
        var result = TerminalCondition.Evaluate(state);
        Assert.True(result.IsOver);
        Assert.Empty(result.WinningTeams);
        Assert.Equal(EncounterOutcome.Draw, result.Outcome);
        Assert.Contains("Draw", result.Description, StringComparison.Ordinal);
    }
}
