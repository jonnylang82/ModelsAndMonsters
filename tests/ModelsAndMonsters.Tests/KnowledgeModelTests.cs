using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The knowledge model, the inspection engine action and the information-view projection, tested as
/// ordinary deterministic software with no model involvement. Covers the parts of the v0.4 spec that live
/// below orchestration: inspection reveals the right thing privately and uses no RNG (#7, #8, #9); the
/// ledger keeps sources and world versions and does not duplicate or silently update records (#10, #11,
/// #12); backstory seeding is private to its owner (#23, #24); and one character's view never carries
/// another's private knowledge (#18).
/// </summary>
public sealed class KnowledgeModelTests
{
    private static GameEngine EngineWith(GameState state) =>
        // An RNG that throws on the first draw, so any accidental roll during inspection fails loudly.
        new(state, new ScriptedRng(), CombatRules.NoGlancing);

    // ------------------------------------------------------------------------------------------
    // Inspection engine action (#7, #8, #9)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Inspecting_a_closed_container_reveals_its_exterior_clue_but_not_its_contents()
    {
        var engine = EngineWith(TestWorld.TwoCasesState());

        var result = engine.Execute(new InspectObjectAction("Elara", "Faded Shrine Medicine Case"));

        var outcome = Assert.IsType<InspectObjectOutcome>(result.Outcome);
        Assert.True(result.Accepted);
        Assert.Equal(TestWorld.MedicineClue, outcome.ExteriorClue);
        // A closed container yields no contents, only the marking.
        Assert.False(outcome.IsOpen);
        Assert.Empty(outcome.CurrentContents);
        // The public summary names neither the clue nor any contents.
        Assert.DoesNotContain("shrine mark", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Potion", outcome.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspecting_an_open_container_reveals_its_current_contents()
    {
        var engine = EngineWith(TestWorld.TwoCasesState(medicine: TestWorld.MedicineCase(open: true)));

        var result = engine.Execute(new InspectObjectAction("Elara", "Faded Shrine Medicine Case"));

        var outcome = Assert.IsType<InspectObjectOutcome>(result.Outcome);
        Assert.True(outcome.IsOpen);
        Assert.Contains("Small Healing Potion", outcome.CurrentContents);
        Assert.Equal(TestWorld.MedicineClue, outcome.ExteriorClue);
    }

    [Fact]
    public void Inspection_consumes_no_world_version_and_draws_no_randomness()
    {
        var engine = EngineWith(TestWorld.TwoCasesState());
        var before = engine.State;

        var result = engine.Execute(new InspectObjectAction("Elara", "Faded Shrine Medicine Case"));

        Assert.True(result.Accepted);
        // No mutation and no version bump: state before is state after, and no draw was taken.
        Assert.Same(before, engine.State);
        Assert.Equal(before.Version, result.StateAfter.Version);
        Assert.Empty(result.RngDraws);
    }

    [Fact]
    public void Inspecting_an_object_that_is_not_present_is_rejected()
    {
        var engine = EngineWith(TestWorld.TwoCasesState());

        var result = engine.Execute(new InspectObjectAction("Elara", "the altar"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownObject, result.RejectionReason);
    }

    [Fact]
    public void Inspecting_a_featureless_closed_container_finds_nothing_to_discover()
    {
        // A closed container with no exterior clue has nothing a closer look reveals.
        var blank = new Container
        {
            Id = "plain-box", Name = "Plain Box", Description = "An unmarked box.", IsOpen = false, Contents = []
        };
        var engine = EngineWith(TestWorld.StateWith([blank], TestWorld.Rowan(), TestWorld.Vark()));

        var result = engine.Execute(new InspectObjectAction("Rowan", "Plain Box"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.NothingToInspect, result.RejectionReason);
    }

    [Fact]
    public void A_dead_actor_cannot_inspect()
    {
        var engine = EngineWith(TestWorld.StateWith([TestWorld.MedicineCase()], TestWorld.Rowan(health: 0), TestWorld.Vark()));

        var result = engine.Execute(new InspectObjectAction("Rowan", "Faded Shrine Medicine Case"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorIsDead, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // The ledger: sources, world versions, deduplication, and historical stability (#10, #11, #12)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_learned_record_carries_its_source_and_the_world_version_it_was_observed_at()
    {
        var ledger = new KnowledgeLedger();
        var fact = ledger.GetOrAddContentsFact("case", "Case", [TestWorld.HealingPotion()], worldVersion: 4);

        var record = ledger.Learn("elara", fact.Fact.Id, KnowledgeSource.OpenedContainer, round: 2, turn: 6, observedWorldVersion: 4);

        Assert.NotNull(record);
        Assert.Equal(KnowledgeSource.OpenedContainer, record!.Source);
        Assert.Equal(4, record.ObservedWorldVersion);
        Assert.Equal(2, record.LearnedAtRound);
    }

    [Fact]
    public void Learning_the_same_fact_twice_does_not_create_a_duplicate_record()
    {
        var ledger = new KnowledgeLedger();
        var fact = ledger.GetOrAddMarkingFact("case", TestWorld.MedicineClue);

        var first = ledger.Learn("elara", fact.Fact.Id, KnowledgeSource.DirectInspection, 1, 1, 0);
        var second = ledger.Learn("elara", fact.Fact.Id, KnowledgeSource.DirectInspection, 3, 9, 5);

        Assert.NotNull(first);
        // The second learn reports nothing new and adds no record; the original stands untouched.
        Assert.Null(second);
        Assert.Single(ledger.RecordsFor("elara"));
    }

    [Fact]
    public void Identical_contents_reuse_one_fact_but_different_contents_mint_a_new_one()
    {
        var ledger = new KnowledgeLedger();

        var withPotion = ledger.GetOrAddContentsFact("case", "Case", [TestWorld.HealingPotion()], worldVersion: 4);
        var stillPotion = ledger.GetOrAddContentsFact("case", "Case", [TestWorld.HealingPotion()], worldVersion: 5);
        var empty = ledger.GetOrAddContentsFact("case", "Case", [], worldVersion: 6);

        Assert.True(withPotion.WasCreated);
        // The same contents, even at a later version, resolve to the same fact — no duplicate.
        Assert.False(stillPotion.WasCreated);
        Assert.Equal(withPotion.Fact.Id, stillPotion.Fact.Id);
        // Genuinely different contents mint a separate fact.
        Assert.True(empty.WasCreated);
        Assert.NotEqual(withPotion.Fact.Id, empty.Fact.Id);
    }

    [Fact]
    public void A_historical_observation_is_not_rewritten_when_the_world_later_changes()
    {
        var ledger = new KnowledgeLedger();

        // Elara opens the case at version 4 and sees the potion.
        var seen = ledger.GetOrAddContentsFact("case", "Case", [TestWorld.HealingPotion()], worldVersion: 4);
        ledger.Learn("elara", seen.Fact.Id, KnowledgeSource.OpenedContainer, 2, 6, observedWorldVersion: 4);

        // The potion is later taken; the case is now empty at version 5. That is a different fact.
        var nowEmpty = ledger.GetOrAddContentsFact("case", "Case", [], worldVersion: 5);

        // Elara's record still points at what she saw, at the version she saw it — unchanged.
        var elaraRecord = Assert.Single(ledger.RecordsFor("elara"));
        Assert.Equal(seen.Fact.Id, elaraRecord.FactId);
        Assert.Equal(4, elaraRecord.ObservedWorldVersion);
        Assert.NotEqual(seen.Fact.Id, nowEmpty.Fact.Id);
        // Her fact still describes the potion, not the current emptiness.
        Assert.Contains("Small Healing Potion", ledger.FindFact(elaraRecord.FactId)!.Description, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Backstory seeding is private to its owner (#23, #24)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Vark_begins_with_private_backstory_knowledge_of_both_supply_cases()
    {
        var ledger = new KnowledgeLedger();
        var scenario = TestWorld.TwoCasesScenario();
        var state = TestWorld.TwoCasesState();

        KnowledgeSeeder.Seed(ledger, scenario, state);

        var varkRecords = ledger.RecordsFor(TestWorld.VarkId).ToList();
        Assert.All(varkRecords, r => Assert.Equal(KnowledgeSource.Backstory, r.Source));

        // He knows the contents of both cases from the outset.
        var knownContents = varkRecords
            .Select(r => ledger.FindFact(r.FactId)!)
            .Where(f => f.FactType == FactType.ContainerContents)
            .Select(f => f.SubjectId)
            .ToList();
        Assert.Contains(TestWorld.MedicineCaseId, knownContents);
        Assert.Contains(TestWorld.MillCrateId, knownContents);
    }

    [Fact]
    public void Skrit_does_not_automatically_inherit_Varks_backstory_knowledge()
    {
        var ledger = new KnowledgeLedger();

        KnowledgeSeeder.Seed(ledger, TestWorld.TwoCasesScenario(), TestWorld.TwoCasesState());

        Assert.Empty(ledger.RecordsFor(TestWorld.SkritId));
        Assert.Empty(ledger.RecordsFor(TestWorld.RowanId));
        Assert.Empty(ledger.RecordsFor(TestWorld.ElaraId));
    }

    // ------------------------------------------------------------------------------------------
    // The information view is per-character and never leaks another's private knowledge (#18)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_characters_view_carries_its_own_direct_knowledge_but_not_another_characters()
    {
        var ledger = new KnowledgeLedger();
        var state = TestWorld.TwoCasesState();

        // Only Elara has inspected the case.
        var marking = ledger.GetOrAddMarkingFact(TestWorld.MedicineCaseId, TestWorld.MedicineClue);
        ledger.Learn(TestWorld.ElaraId, marking.Fact.Id, KnowledgeSource.DirectInspection, 2, 4, 0);

        var elaraView = CharacterKnowledgeView.RenderForDungeonMaster(
            TestWorld.ElaraId, "Elara", ledger, new NarrationLog(), state);
        var rowanView = CharacterKnowledgeView.RenderForDungeonMaster(
            TestWorld.RowanId, "Rowan", ledger, new NarrationLog(), state);

        // Elara's view carries what she inspected; Rowan's does not, and shows him knowing nothing first-hand.
        Assert.Contains(TestWorld.MedicineClue, elaraView, StringComparison.Ordinal);
        Assert.DoesNotContain(TestWorld.MedicineClue, rowanView, StringComparison.Ordinal);
        Assert.Contains("Nothing beyond what anyone standing in the room can plainly see", rowanView, StringComparison.Ordinal);
    }

    [Fact]
    public void Heard_speech_appears_in_the_view_as_hearsay_never_as_direct_knowledge()
    {
        var ledger = new KnowledgeLedger();
        var state = TestWorld.TwoCasesState();
        var narration = new NarrationLog();

        // Elara says something; it is delivered to Rowan (he has heard it).
        var entry = narration.RecordSpeech(TestWorld.ElaraId, "Elara says:\n\"The shrine case holds a healing potion.\"");
        entry.MarkDeliveredTo(TestWorld.RowanId);

        var rowanView = CharacterKnowledgeView.RenderForDungeonMaster(
            TestWorld.RowanId, "Rowan", ledger, narration, state);

        // It is under hearsay, and Rowan has no first-hand record of the contents.
        Assert.Contains("HAS ONLY HEARD OTHERS SAY", rowanView, StringComparison.Ordinal);
        Assert.Contains("healing potion", rowanView, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ledger.RecordsFor(TestWorld.RowanId));
    }
}
