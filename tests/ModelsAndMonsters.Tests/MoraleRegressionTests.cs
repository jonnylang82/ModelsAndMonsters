using System.Text.Json;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// What v0.8 must NOT have changed: the surrender protocol, the existing abilities, the knowledge boundary,
/// and the ability to render a run recorded before morale existed.
/// </summary>
public sealed class MoraleRegressionTests
{
    private static GameEngine Engine(IRng? rng = null, params Character[] characters) =>
        new(TestWorld.V07State(exitOpen: false, characters), rng ?? new SeededRng(1), CombatRules.Default);

    // ------------------------------------------------------------------------------------------
    // Fear never surrenders or escapes anybody
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_character_at_maximum_fear_stays_active_and_takes_their_own_turns()
    {
        var engine = Engine(null,
            TestWorld.RowanV07() with { Fear = 5 }, TestWorld.ElaraV07(),
            TestWorld.VarkV07(), TestWorld.SkritV07());

        var rowan = engine.State.RequireById(TestWorld.RowanId);
        Assert.Equal(CharacterDisposition.Active, rowan.Disposition);
        Assert.True(rowan.CanAct);
        Assert.True(rowan.IsCombatTarget);
        Assert.Empty(engine.State.SurrenderOffers);
        Assert.Empty(engine.State.SurrenderAgreements);
        Assert.Null(rowan.EscapedThroughExitId);
    }

    [Fact]
    public void Surrender_still_needs_a_concrete_offer_that_somebody_accepts()
    {
        var engine = Engine(null,
            TestWorld.RowanV07(), TestWorld.ElaraV07(),
            TestWorld.VarkV07() with { Fear = 5 }, TestWorld.SkritV07());

        // A terrified character offering nothing is refused exactly as before.
        var empty = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: false));
        Assert.False(empty.Accepted);
        Assert.Equal(EngineRejectionReason.OfferHasNoConcession, empty.RejectionReason);

        // A real offer only creates a proposal: still active, still armed, still a target.
        var offered = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        Assert.True(offered.Accepted);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.False(vark.IsDisarmed);

        // And only the named recipient ends it.
        var wrongPerson = engine.Execute(new AcceptSurrenderAction("Elara", "offer-1"));
        Assert.False(wrongPerson.Accepted);
        Assert.Equal(EngineRejectionReason.OfferNotAddressedToActor, wrongPerson.RejectionReason);

        Assert.True(engine.Execute(new AcceptSurrenderAction("Rowan", "offer-1")).Accepted);
        Assert.Equal(CharacterDisposition.Surrendered, engine.State.RequireById(TestWorld.VarkId).Disposition);
    }

    [Fact]
    public void Escaping_still_needs_the_door_opened_first_however_frightened_the_character_is()
    {
        var engine = Engine(null,
            TestWorld.RowanV07() with { Fear = 5 }, TestWorld.ElaraV07(),
            TestWorld.VarkV07(), TestWorld.SkritV07());

        var throughShut = engine.Execute(new EscapeEncounterAction("Rowan", "cellar-stair-door"));
        Assert.False(throughShut.Accepted);
        Assert.Equal(EngineRejectionReason.ExitClosed, throughShut.RejectionReason);

        Assert.True(engine.Execute(new OpenExitAction("Rowan", "cellar-stair-door")).Accepted);
        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.RowanId).Disposition);

        Assert.True(engine.Execute(new EscapeEncounterAction("Rowan", "cellar-stair-door")).Accepted);
        Assert.Equal(CharacterDisposition.Escaped, engine.State.RequireById(TestWorld.RowanId).Disposition);
    }

    [Fact]
    public void A_successful_threat_manufactures_neither_an_offer_nor_an_acceptance()
    {
        var engine = Engine(new ScriptedRng(1, 1, 1, 1, 1),
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        for (var i = 0; i < 2; i++)
        {
            engine.Execute(new IntimidateCharacterAction(i == 0 ? "Vark" : "Skrit", "Rowan", 1));
        }

        Assert.Equal(2, engine.State.RequireById(TestWorld.RowanId).Fear);
        Assert.Empty(engine.State.SurrenderOffers);
        Assert.Empty(engine.State.SurrenderAgreements);
        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.RowanId).Disposition);
    }

    // ------------------------------------------------------------------------------------------
    // The existing abilities
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Guard_Ally_still_redirects_one_blow_and_adds_no_draw()
    {
        var rng = new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid);
        var engine = new GameEngine(TestWorld.V07State(exitOpen: false), rng, CombatRules.Default);

        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        var result = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.True(outcome.Redirected);
        Assert.Equal(TestWorld.RowanId, outcome.TargetId);
        Assert.Equal(2, result.RngDraws.Count);
    }

    [Fact]
    public void Defend_still_turns_aside_one_point_of_damage_from_the_next_blow_that_lands()
    {
        var engine = new GameEngine(TestWorld.V07State(exitOpen: false),
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default);

        Assert.True(engine.Execute(new DefendAction("Elara")).Accepted);
        var outcome = Assert.IsType<AttackOutcome>(engine.Execute(
            new AttackCharacterAction("Vark", "Elara", "Notched Sabre")).Outcome);

        Assert.Equal(AbilityCatalog.DefendReduction, outcome.DefendReduction);
    }

    [Fact]
    public void Healing_Prayer_still_restores_its_fixed_amount_with_no_draw_and_no_fear_change()
    {
        var rng = new ScriptedRng();
        var engine = new GameEngine(TestWorld.V07State(exitOpen: false), rng, CombatRules.Default);

        var result = engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara"));

        var outcome = Assert.IsType<HealingPrayerOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.HealingPrayerAmount, outcome.HealthAfter - outcome.HealthBefore);
        Assert.Equal(0, rng.DrawCount);
        Assert.Empty(result.FearChanges);
    }

    [Fact]
    public void Dirty_Strike_still_applies_OffBalance_on_a_hit_and_uses_only_the_ordinary_draws()
    {
        var engine = new GameEngine(TestWorld.V07State(exitOpen: false),
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.Default);

        var result = engine.Execute(new UseAbilityAction("Skrit", AbilityCatalog.DirtyStrikeId, "Rowan"));

        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(nameof(StatusEffectKind.OffBalance), outcome.StatusApplied);
        Assert.Equal(2, result.RngDraws.Count);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.OffBalance));
    }

    [Fact]
    public void Rally_Grunt_keeps_its_hit_chance_effect_and_now_also_steadies_the_nerve()
    {
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false,
                TestWorld.RowanV07(), TestWorld.ElaraV07(),
                TestWorld.VarkV07(), TestWorld.SkritV07() with { Fear = 2 }),
            new ScriptedRng(), CombatRules.Default);

        var result = engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit"));

        // The v0.7 behaviour, unchanged.
        var outcome = Assert.IsType<RallyOutcome>(result.Outcome);
        Assert.Equal(AbilityCatalog.HitChanceSwing, outcome.Modifier);
        var status = engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied);
        Assert.NotNull(status);
        Assert.Equal(AbilityCatalog.HitChanceSwing, status.Modifier);
        Assert.Equal(0, outcome.RemainingUses);

        // And the v0.8 addition, alongside it.
        Assert.Equal(1, engine.State.RequireById(TestWorld.SkritId).Fear);
        Assert.Equal(FearChangeCause.RallyGrunt, Assert.Single(result.FearChanges).Cause);
    }

    [Fact]
    public void Rally_Grunt_on_an_unafraid_ally_still_works_and_simply_sheds_nothing()
    {
        var engine = new GameEngine(TestWorld.V07State(exitOpen: false), new ScriptedRng(), CombatRules.Default);

        var result = engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit"));

        Assert.True(result.Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.SkritId, StatusEffectKind.Rallied));
        Assert.True(Assert.Single(result.FearChanges).Absorbed);
    }

    // ------------------------------------------------------------------------------------------
    // Knowledge stays private
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Object_knowledge_is_still_private_until_it_is_publicly_revealed()
    {
        var ledger = new Knowledge.KnowledgeLedger();
        var chest = TestWorld.Chest(open: true);
        var fact = ledger.GetOrAddContentsFact(chest.Id, chest.Name, chest.Contents, 0);
        ledger.Learn(TestWorld.RowanId, fact.Fact.Id, Knowledge.KnowledgeSource.OpenedContainer, 1, 1, 0);

        Assert.True(ledger.Knows(TestWorld.RowanId, fact.Fact.Id));
        Assert.False(ledger.Knows(TestWorld.VarkId, fact.Fact.Id));
    }

    // ------------------------------------------------------------------------------------------
    // Older runs still render
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_recorded_before_morale_existed_still_renders_a_report()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mm-v07-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // A minimal v0.7-shaped trace: an attack whose outcome has the old Glancing flag and no
            // Quality, no FearChanges anywhere, and a final state whose characters carry no Fear field.
            File.WriteAllLines(Path.Combine(directory, "trace.jsonl"),
            [
                """
                {"Sequence":1,"LineNumber":1,"Timestamp":"2026-01-01T00:00:00Z","EventType":"EngineAction","Actor":"Rowan","Round":1,"Turn":1,
                 "Data":{"ActionType":"attack_character","Accepted":true,
                 "Outcome":{"OutcomeType":"attack","AttackerName":"Rowan","TargetName":"Vark","Hit":true,"Glancing":true,
                 "DamageDealt":2,"TargetDied":false}}}
                """.Replace("\r\n", "").Replace("\n", ""),
                """
                {"Sequence":2,"LineNumber":2,"Timestamp":"2026-01-01T00:00:01Z","EventType":"TurnEnded","Actor":"Rowan","Round":1,"Turn":1,
                 "Data":{"CharacterName":"Rowan","Result":"ActionResolved","AcceptedAction":"AttackCharacter(...)"}}
                """.Replace("\r\n", "").Replace("\n", "")
            ]);

            File.WriteAllText(Path.Combine(directory, "final-state.json"),
                """{"Version":3,"Characters":[{"Name":"Rowan","Health":14,"MaxHealth":14},{"Name":"Vark","Health":10,"MaxHealth":12}]}""");

            var path = RunReportWriter.Write(directory);
            var report = File.ReadAllText(path);

            // It renders, it reads the old glancing flag correctly, and the morale sections simply do not appear.
            Assert.Contains("## Attack quality", report, StringComparison.Ordinal);
            Assert.Contains("| Glancing | 1 |", report, StringComparison.Ordinal);
            Assert.DoesNotContain("## Morale summary", report, StringComparison.Ordinal);
            Assert.DoesNotContain("## Intimidation and reassurance", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_final_state_written_before_morale_existed_reads_as_fear_zero()
    {
        using var document = JsonDocument.Parse("""{"Name":"Rowan","Health":14}""");

        // The report's final-fear reader treats an absent field as zero rather than throwing, which is what
        // lets a v0.7 final-state.json render at all.
        Assert.False(document.RootElement.TryGetProperty("Fear", out _));
    }
}
