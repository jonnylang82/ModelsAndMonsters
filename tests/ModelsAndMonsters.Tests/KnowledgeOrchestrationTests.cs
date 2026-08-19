using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The hidden-information flow driven through the real <see cref="TurnCoordinator"/> against scripted
/// models: opening reveals contents only to the opener (#4, #5, #6); a close inspection delivers a private
/// observation and consumes the turn without RNG (#7–#10); a visible take is a public event that updates
/// everyone's knowledge (#19, #20); a stale take is refused without mutation (#21, #22); and the complete
/// inspect → tell → open → take knowledge sequence composes correctly.
/// </summary>
public sealed class KnowledgeOrchestrationTests
{
    private const string MedicineCase = "Faded Shrine Medicine Case";
    private const string Potion = "Small Healing Potion";

    private static bool HasContentsRecord(KnowledgeLedger ledger, string characterId, string subjectId) =>
        ledger.RecordsFor(characterId).Any(r =>
            ledger.FindFact(r.FactId) is { FactType: FactType.ContainerContents } f &&
            string.Equals(f.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase));

    private static bool HasMarkingRecord(KnowledgeLedger ledger, string characterId, string subjectId) =>
        ledger.RecordsFor(characterId).Any(r =>
            ledger.FindFact(r.FactId) is { FactType: FactType.ContainerExteriorMarking } f &&
            string.Equals(f.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase));

    private static bool HasRemovalRecord(KnowledgeLedger ledger, string characterId) =>
        ledger.RecordsFor(characterId).Any(r => ledger.FindFact(r.FactId) is { FactType: FactType.ItemRemoved });

    private static bool HasOpenedRecord(KnowledgeLedger ledger, string characterId, string subjectId) =>
        ledger.RecordsFor(characterId).Any(r =>
            ledger.FindFact(r.FactId) is { FactType: FactType.ContainerOpened } f &&
            string.Equals(f.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase));

    private static MultiActorHarness Harness(ScriptedChatClient dm, params (string Name, ScriptedChatClient Client)[] characters) =>
        new(dm, MultiActorHarness.Clients(characters),
            initialState: TestWorld.TwoCasesState(),
            scenario: TestWorld.TwoCasesScenario());

    // ------------------------------------------------------------------------------------------
    // Opening reveals contents only to the opener (#4, #5, #6, #25)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_a_case_reveals_its_contents_only_to_the_opener()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.OpenContainerName, ("actor", "Elara"), ("container", MedicineCase)),
                ScriptedChatClient.Text("Elara throws back the lid and peers inside.")),
            ("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I open the shrine case and look inside.")))));

        await harness.RunTurn("Elara");

        // The opener directly knows the contents; nobody else does. (#5, #6)
        Assert.True(HasContentsRecord(harness.Ledger, TestWorld.ElaraId, TestWorld.MedicineCaseId));
        Assert.False(HasContentsRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));

        // But the open STATE is public: every living character — Rowan included — learns the case is open. (#4)
        Assert.True(HasOpenedRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));
        Assert.True(HasOpenedRecord(harness.Ledger, TestWorld.ElaraId, TestWorld.MedicineCaseId));

        // The private observation went to Elara alone and named the potion. (#5, #25)
        var observation = Assert.Single(harness.Sink.Payloads<PrivateObservationDeliveredPayload>(TraceEventType.PrivateObservationDelivered));
        Assert.Equal(TestWorld.ElaraId, observation.RecipientId);
        Assert.Contains(Potion, observation.Observation, StringComparison.Ordinal);

        // The public narration of the opening was aimed at everyone alive, and is traced as public. (#25)
        var narration = harness.Sink.Payloads<NarrationPayload>(TraceEventType.Narration).Single(n => n.Purpose == "container-opened");
        Assert.Equal("public", narration.Visibility);
        Assert.Equal(4, narration.IntendedRecipients.Count);

        // Opening is a public event, but only the open state travels — never the contents. (#4)
        var openFact = Assert.Single(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered));
        Assert.Equal("open_container", openFact.SourceEvent);
        Assert.DoesNotContain(Potion, openFact.Fact, StringComparison.Ordinal);
        // No public knowledge delivery from the opening ever carried the contents.
        Assert.All(
            harness.Sink.Payloads<KnowledgeFactLearnedPayload>(TraceEventType.KnowledgeFactLearned)
                .Where(p => p.Visibility == "public"),
            p => Assert.DoesNotContain(Potion, p.Description, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_opener_hears_the_contents_but_a_later_character_does_not()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.OpenContainerName, ("actor", "Elara"), ("container", MedicineCase)),
                ScriptedChatClient.Text("Elara lifts the lid and looks in.")),
            ("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I open the shrine case.")))));

        await harness.RunTurn("Elara");

        // What Rowan will hear when his turn begins is the public narration only — never the private observation.
        var pending = harness.NarrationLog.TakeUndelivered(TestWorld.RowanId);
        var heard = string.Join("\n", pending.Select(p => p.Text));
        Assert.DoesNotContain(Potion, heard, StringComparison.Ordinal);
        Assert.False(HasContentsRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));
    }

    // ------------------------------------------------------------------------------------------
    // A close inspection delivers a private observation, consumes the turn, uses no RNG (#7, #9)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inspecting_a_closed_case_privately_reveals_its_marking_and_consumes_the_turn_without_rng()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.InspectObjectName, ("actor", "Elara"), ("object", MedicineCase)),
                ScriptedChatClient.Text("Elara wipes the grime away and studies the case.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I wipe the grime off the case and study its markings."))))),
            initialState: TestWorld.TwoCasesState(),
            scenario: TestWorld.TwoCasesScenario(),
            // An RNG that throws on any draw proves inspection consults no randomness.
            rng: new ScriptedRng());

        var result = await harness.RunTurn("Elara");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.True(HasMarkingRecord(harness.Ledger, TestWorld.ElaraId, TestWorld.MedicineCaseId));

        var observation = Assert.Single(harness.Sink.Payloads<PrivateObservationDeliveredPayload>(TraceEventType.PrivateObservationDelivered));
        Assert.Equal(TestWorld.ElaraId, observation.RecipientId);
        Assert.Contains("medicinal supplies", observation.Observation, StringComparison.OrdinalIgnoreCase);

        // No randomness was drawn for the inspection.
        Assert.Empty(harness.Sink.OfType(TraceEventType.RngDraw));
    }

    // ------------------------------------------------------------------------------------------
    // Repeat inspection does not create duplicate knowledge (#10)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inspecting_the_same_closed_case_twice_learns_nothing_new_the_second_time()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.InspectObjectName, ("actor", "Elara"), ("object", MedicineCase)),
                ScriptedChatClient.Text("Elara studies the case."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.InspectObjectName, ("actor", "Elara"), ("object", MedicineCase)),
                ScriptedChatClient.Text("Elara studies the case again.")),
            ("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I examine the case closely.")),
                ScriptedChatClient.Call("e-2", CharacterTools.TakeActionName, ("intent", "I examine the case closely once more.")))));

        await harness.RunTurn("Elara", round: 1, turn: 1);
        await harness.RunTurn("Elara", round: 2, turn: 2);

        // Exactly one marking record for Elara — the repeat added nothing.
        Assert.Single(harness.Ledger.RecordsFor(TestWorld.ElaraId),
            r => harness.Ledger.FindFact(r.FactId)!.FactType == FactType.ContainerExteriorMarking);

        // The second inspection reported learning nothing new.
        var observations = harness.Sink.Payloads<PrivateObservationDeliveredPayload>(TraceEventType.PrivateObservationDelivered).ToList();
        Assert.Equal(2, observations.Count);
        Assert.Contains("nothing beyond what you already know", observations[1].Observation, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------
    // Taking a visible item is a public event that updates everyone's knowledge (#19, #20)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Taking_the_potion_is_a_public_event_that_every_living_character_learns()
    {
        var medicineCase = TestWorld.MedicineCase(open: true);
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.TakeItemName, ("actor", "Elara"), ("container", MedicineCase), ("item", Potion)),
                ScriptedChatClient.Text("Elara lifts the vial from the open case and holds it up.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I take the potion from the open case."))))),
            initialState: TestWorld.TwoCasesState(medicine: medicineCase),
            scenario: TestWorld.TwoCasesScenario());

        // Elara has looked inside, so she legitimately knows the potion is there (v0.6 take_item gate).
        harness.SeedContentsKnowledge(TestWorld.ElaraId, medicineCase);

        await harness.RunTurn("Elara");

        // A public fact was delivered to everyone alive, and each living character now holds a removal record. (#19, #20)
        var publicFact = Assert.Single(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered));
        Assert.Contains(Potion, publicFact.Fact, StringComparison.Ordinal);
        foreach (var id in new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId })
        {
            Assert.True(HasRemovalRecord(harness.Ledger, id), $"{id} should have learned of the public removal.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // A stale take is refused without mutation, in-world (#21, #22)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reaching_for_a_potion_that_is_no_longer_there_is_refused_without_mutation()
    {
        // The case is open but already empty — the potion has been taken.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.TakeItemName, ("actor", "Rowan"), ("container", MedicineCase), ("item", Potion)),
                ScriptedChatClient.Text("You reach into the open case, but the potion is no longer there."),
                ScriptedChatClient.Text("Rowan lets his hand fall and steadies himself.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I grab the potion from the open case.")),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "It's gone."))))),
            initialState: TestWorld.TwoCasesState(medicine: TestWorld.MedicineCase(open: true, contents: [])),
            scenario: TestWorld.TwoCasesScenario());

        var versionBefore = harness.Engine.State.Version;
        await harness.RunTurn("Rowan");

        // Nothing moved: the world version is untouched and Rowan's hands are empty. (#21)
        Assert.Equal(versionBefore, harness.Engine.State.Version);
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory);

        // The refusal reached the character in-world, with no engine terminology. (#22)
        var adjudication = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication)
            .Single(a => a.Category == ActionResolutionCategory.EngineRejected.ToString());
        Assert.DoesNotContain("ItemNotInContainer", adjudication.Reason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("engine", adjudication.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Taking_an_item_the_character_has_no_basis_to_identify_is_refused_before_the_engine_even_when_the_case_is_open()
    {
        // The reviewer's case: the medicine case stands OPEN and truly holds the potion, but Elara never
        // opened, inspected or was told of its contents — only Vark (via backstory) knows what is inside. Even
        // though the Dungeon Master (mimicking a leak) names the exact item in a take_item call, the take must
        // be refused before the engine, with NO state change — a DM slip must not become a valid mutation.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.TakeItemName, ("actor", "Elara"), ("container", MedicineCase), ("item", Potion)),
                // The take is refused before the engine, so the next DM call is narrating Elara's end_turn pass.
                ScriptedChatClient.Text("Elara lowers her hand and lets the moment pass.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I take the healing potion from the open case.")),
                ScriptedChatClient.Call("e-2", CharacterTools.EndTurnName, ("reason", "I cannot make it out."))))),
            initialState: TestWorld.TwoCasesState(medicine: TestWorld.MedicineCase(open: true)),
            scenario: TestWorld.TwoCasesScenario());

        var versionBefore = harness.Engine.State.Version;
        await harness.RunTurn("Elara");

        // Authoritative state is untouched: the potion is still in the case, Elara's hands are empty, no draw.
        Assert.Equal(versionBefore, harness.Engine.State.Version);
        var medicine = harness.Engine.State.Room.Objects.OfType<Container>().Single(c => c.Id == TestWorld.MedicineCaseId);
        Assert.Contains(medicine.Contents, i => i.Name == Potion);
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.ElaraId).Inventory);

        // It was refused as unsupported (no legitimate basis), not accepted, and never reached the engine —
        // there is no EngineAction row for the take.
        Assert.DoesNotContain(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction),
            e => e.ActionType == "take_item");
        var adjudication = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication)
            .Single(a => a.CharacterName == "Elara");
        Assert.Equal(ActionResolutionCategory.DmUnsupported.ToString(), adjudication.Category);
    }

    // ------------------------------------------------------------------------------------------
    // The full sequence: inspect → ask → tell → hear → open → take (the spec's scripted proof)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_complete_inspect_tell_open_take_knowledge_sequence_composes_correctly()
    {
        var harness = Harness(
            new ScriptedChatClient(
                // 1. Elara inspects the shrine case.
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.InspectObjectName, ("actor", "Elara"), ("object", MedicineCase)),
                ScriptedChatClient.Text("Elara wipes the grime away and studies the case closely."),
                // 2. Rowan asks about it and is told he cannot know.
                ScriptedChatClient.Text("You saw Elara looking the case over, but you do not know what she made of it."),
                ScriptedChatClient.Text("Rowan holds his ground, watching."),
                // 3. Elara tells the room, then holds back.
                ScriptedChatClient.Text("Elara stays her hand, eyes on the goblins."),
                // 4. Rowan hears it, then holds back.
                ScriptedChatClient.Text("Rowan shifts his grip, taking it in."),
                // 5. Rowan opens the case.
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.OpenContainerName, ("actor", "Rowan"), ("container", MedicineCase)),
                ScriptedChatClient.Text("Rowan throws back the lid and looks inside."),
                // 6. Rowan takes the potion.
                ScriptedChatClient.Call("dm-3", DungeonMasterTools.TakeItemName, ("actor", "Rowan"), ("container", MedicineCase), ("item", Potion)),
                ScriptedChatClient.Text("Rowan lifts the vial free and holds it up for all to see.")),
            ("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I wipe the case and study its markings.")),
                ScriptedChatClient.Call("e-2", CharacterTools.SayName, ("message", "That shrine-marked case holds a healing potion — take it!")),
                ScriptedChatClient.Call("e-3", CharacterTools.EndTurnName, ("reason", "Said my piece.")))),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.AskDmName, ("question", "What did Elara find in that case?")),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Wait and see.")),
                ScriptedChatClient.Call("r-3", CharacterTools.EndTurnName, ("reason", "Take in what she said.")),
                ScriptedChatClient.Call("r-4", CharacterTools.TakeActionName, ("intent", "I open the shrine case.")),
                ScriptedChatClient.Call("r-5", CharacterTools.TakeActionName, ("intent", "I take the potion from the open case.")))));

        // Step 1–2: Elara inspects; only Elara learns the exterior clue.
        await harness.RunTurn("Elara", 1, 1);
        Assert.True(HasMarkingRecord(harness.Ledger, TestWorld.ElaraId, TestWorld.MedicineCaseId));
        Assert.False(HasMarkingRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));

        // Step 3: Rowan asks and remains unaware — he has learned nothing first-hand.
        await harness.RunTurn("Rowan", 1, 2);
        Assert.False(HasMarkingRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));
        Assert.Empty(harness.Ledger.RecordsFor(TestWorld.RowanId));

        // Step 4: Elara tells the room.
        await harness.RunTurn("Elara", 1, 3);

        // Step 5: Rowan hears the claim but has not verified it — hearsay, not direct knowledge.
        await harness.RunTurn("Rowan", 2, 4);
        Assert.NotEmpty(harness.NarrationLog.SpeechHeardBy(TestWorld.RowanId));
        Assert.False(HasContentsRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));

        // Step 6–7: Rowan opens the case and directly learns the contents; Elara still has not seen them.
        await harness.RunTurn("Rowan", 2, 5);
        Assert.True(HasContentsRecord(harness.Ledger, TestWorld.RowanId, TestWorld.MedicineCaseId));
        Assert.False(HasContentsRecord(harness.Ledger, TestWorld.ElaraId, TestWorld.MedicineCaseId));

        // Step 8–9: Rowan removes the potion, and the visible removal becomes public knowledge for all.
        // (The opening a turn earlier was a public event too, so single out the removal.)
        await harness.RunTurn("Rowan", 2, 6);
        Assert.Contains(
            harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered),
            f => f.SourceEvent == "take_item" && f.Fact.Contains(Potion, StringComparison.Ordinal));
        foreach (var id in new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId })
        {
            Assert.True(HasRemovalRecord(harness.Ledger, id), $"{id} should have learned of the public removal.");
        }
    }
}
