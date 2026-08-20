using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.5 non-lethal outcomes driven through the real <see cref="TurnCoordinator"/>: a character states a
/// natural-language intent, the (scripted) Dungeon Master translates it into surrender / open_exit /
/// escape_encounter, and the coordinator applies it, delivers the public event, records the disposition
/// change, and skips the character on later turns. Covers the surrender and escape flows, the public-fact
/// boundaries, and the guarantee that speech alone changes nobody.
/// </summary>
public sealed class DispositionOrchestrationTests
{
    private static ChatResponse DmOfferSurrender(string offerer, string recipient, bool forfeitWeapon = true) =>
        ScriptedChatClient.Call("dm-offer", DungeonMasterTools.OfferSurrenderName,
            ("offerer", offerer), ("recipient", recipient), ("forfeit_weapon", forfeitWeapon),
            // A carried item is required: terms promising only the weapon are refused.
            ("offered_items", new[] { $"purse-{offerer.ToLowerInvariant()}" }));

    private static ChatResponse DmAcceptSurrender(string recipient, string offerId) =>
        ScriptedChatClient.Call("dm-accept", DungeonMasterTools.AcceptSurrenderName,
            ("recipient", recipient), ("offer", offerId));

    private static ChatResponse DmOpenExit(string actor) =>
        ScriptedChatClient.Call("dm-open", DungeonMasterTools.OpenExitName, ("actor", actor), ("exit", "Cellar Stair Door"));

    private static ChatResponse DmEscape(string actor) =>
        ScriptedChatClient.Call("dm-escape", DungeonMasterTools.EscapeEncounterName, ("actor", actor), ("exit", "Cellar Stair Door"));

    private static ChatResponse Act(string id, string intent) =>
        ScriptedChatClient.Call(id, CharacterTools.TakeActionName, ("intent", intent));

    // ------------------------------------------------------------------------------------------
    // Skipping non-active characters without a model call (tests #9, #13)
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CharacterDisposition.Surrendered)]
    [InlineData(CharacterDisposition.Escaped)]
    public async Task A_surrendered_or_escaped_character_is_skipped_without_a_model_call(CharacterDisposition disposition)
    {
        var skrit = TestWorld.Skrit() with { Disposition = disposition };
        var skritClient = new ScriptedChatClient(); // no scripted responses: a model call would throw
        var harness = new MultiActorHarness(
            new ScriptedChatClient(),
            MultiActorHarness.Clients(("Skrit", skritClient)),
            initialState: TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
                TestWorld.Rowan(), TestWorld.Elara(health: 6), TestWorld.Vark(), skrit));

        var result = await harness.RunTurn("Skrit", round: 2, turn: 6);

        Assert.Equal(TurnOutcome.Skipped, result.Outcome);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, skritClient.CallCount);

        var skip = Assert.Single(harness.Sink.Payloads<TurnSkippedPayload>(TraceEventType.TurnSkipped));
        Assert.Equal(disposition.ToString(), skip.Disposition);
    }

    // ------------------------------------------------------------------------------------------
    // end_turn is only temporary inaction — it never changes disposition (test #15)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ending_a_turn_leaves_the_character_active()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Skrit shifts his grip and waits.")),
            MultiActorHarness.Clients(
                ("Skrit", new ScriptedChatClient(
                    ScriptedChatClient.Call("s-1", CharacterTools.EndTurnName, ("reason", "I hold and watch."))))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        var result = await harness.RunTurn("Skrit");

        Assert.Equal(TurnOutcome.EndedByCharacter, result.Outcome);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.SkritId).Disposition);
    }

    // ------------------------------------------------------------------------------------------
    // The surrender flow (tests #25, #26, #27; scripted surrender flow #1–#6)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rowan_demanding_surrender_is_delivered_as_speech_and_does_not_change_Skrit()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan levels his blade and speaks low.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Calls(
                    ScriptedChatClient.CallContent("r-say", CharacterTools.SayName, ("message", "Skrit — Vark can't save you. Yield now and you'll live.")),
                    ScriptedChatClient.CallContent("r-end", CharacterTools.EndTurnName, ("reason", "Gave him the choice.")))))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        await harness.RunTurn("Rowan");

        // The demand was delivered as public speech, and Skrit — its target — is entirely unchanged by it.
        Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.SkritId).Disposition);
        // No surrender was applied to anyone off the back of speech.
        Assert.Empty(harness.Sink.Payloads<CharacterSurrenderedPayload>(TraceEventType.CharacterSurrendered));

        // Nor did the words become negotiation state. Speech carries the argument; only a structured offer
        // carries terms, so there is nothing on the table for anybody to accept.
        Assert.Empty(harness.Engine.State.SurrenderOffers);
        Assert.Empty(harness.Engine.State.SurrenderAgreements);
        Assert.Empty(harness.Sink.OfType(TraceEventType.SurrenderOfferMade));
    }

    [Fact]
    public async Task Skrit_offers_terms_on_its_own_turn_and_stays_in_the_fight_until_Rowan_accepts()
    {
        // Rowan demands surrender (turn 1). On Skrit's turn it offers concrete terms — which changes nothing
        // by itself. Only when Rowan accepts, on a later turn of his own, is Skrit out of the fight.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("Rowan levels his blade and speaks low."),      // Rowan's end_turn narration
                DmOfferSurrender("Skrit", "Rowan"),                                      // Skrit's intent → offer
                ScriptedChatClient.Text("Skrit holds out empty hands and offers up his spear to be spared."),
                DmAcceptSurrender("Rowan", "offer-1"),                                   // Rowan's intent → accept
                ScriptedChatClient.Text("Rowan takes the spear from Skrit's hands and lets it fall.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Calls(
                        ScriptedChatClient.CallContent("r-say", CharacterTools.SayName, ("message", "Skrit, yield now and you'll live.")),
                        ScriptedChatClient.CallContent("r-end", CharacterTools.EndTurnName, ("reason", "Offered mercy."))),
                    Act("r-2", "I take his spear and tell him he can live."))),
                ("Skrit", new ScriptedChatClient(Act("s-1", "I offer Rowan my spear if he'll let me live.")))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.SkritId).Disposition);

        var skritTurn = await harness.RunTurn("Skrit", round: 1, turn: 4);
        Assert.Equal(TurnOutcome.ActionResolved, skritTurn.Outcome);

        // The offer alone leaves Skrit fighting: still active, still armed, still a target. Nobody has
        // surrendered yet, so no surrender event has been recorded.
        var afterOffer = harness.Engine.State.RequireById(TestWorld.SkritId);
        Assert.Equal(CharacterDisposition.Active, afterOffer.Disposition);
        Assert.NotNull(afterOffer.Weapon);
        Assert.Empty(harness.Sink.Payloads<CharacterSurrenderedPayload>(TraceEventType.CharacterSurrendered));
        var offered = Assert.Single(harness.Sink.Payloads<SurrenderOfferMadePayload>(TraceEventType.SurrenderOfferMade));
        Assert.True(offered.NothingTransferred);
        Assert.True(offered.OffererRemainsTargetable);

        // Rowan accepts on his own turn, and only now is Skrit out of the fight and disarmed.
        var rowanAccepts = await harness.RunTurn("Rowan", round: 2, turn: 5);
        Assert.Equal(TurnOutcome.ActionResolved, rowanAccepts.Outcome);

        var skrit = harness.Engine.State.RequireById(TestWorld.SkritId);
        Assert.Equal(CharacterDisposition.Surrendered, skrit.Disposition);
        Assert.True(skrit.IsAlive);
        Assert.True(skrit.IsDisarmed);

        // A later turn is now skipped without a model call, and Skrit stays alive and present.
        var later = await harness.RunTurn("Skrit", round: 2, turn: 8);
        Assert.Equal(TurnOutcome.Skipped, later.Outcome);
        Assert.Equal(0, later.ModelCalls);
        Assert.True(harness.Engine.State.RequireById(TestWorld.SkritId).IsAlive);

        // The accepted surrender was recorded as a semantic event, a disposition change and a durable agreement.
        Assert.Single(harness.Sink.Payloads<CharacterSurrenderedPayload>(TraceEventType.CharacterSurrendered));
        var change = Assert.Single(harness.Sink.Payloads<DispositionChangedPayload>(TraceEventType.DispositionChanged));
        Assert.Equal("Active", change.PreviousDisposition);
        Assert.Equal("Surrendered", change.NewDisposition);
        Assert.Equal("accepted surrender", change.Cause);

        var agreement = Assert.Single(harness.Sink.Payloads<SurrenderAgreementPayload>(TraceEventType.SurrenderAgreementRecorded));
        Assert.Equal("Skrit", agreement.OffererName);
        Assert.Equal("Rowan", agreement.AcceptedByName);
        Assert.True(agreement.OffererDisarmed);
    }

    // ------------------------------------------------------------------------------------------
    // The escape flow (scripted escape flow #1–#6)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Vark_opens_the_door_publicly_and_Skrit_escapes_through_it_on_a_later_turn()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                DmOpenExit("Vark"),
                ScriptedChatClient.Text("Vark hauls the cellar door open; the dark stairs gape beyond."),
                DmEscape("Skrit"),
                ScriptedChatClient.Text("Skrit bolts through the open door and is gone up the stairs.")),
            MultiActorHarness.Clients(
                ("Vark", new ScriptedChatClient(Act("v-1", "I haul the cellar door open."))),
                ("Skrit", new ScriptedChatClient(Act("s-1", "I bolt through the open doorway and get clear.")))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        await harness.RunTurn("Vark", round: 1, turn: 3);

        // The door is open, and its open state reached every present character as a public fact.
        Assert.True(harness.Engine.State.Exits.Single().IsOpen);
        var openFact = Assert.Single(harness.Ledger.Facts, f => f.FactType == FactType.ExitOpened);
        Assert.True(harness.Ledger.Knows(TestWorld.SkritId, openFact.Id));
        Assert.True(harness.Ledger.Knows(TestWorld.RowanId, openFact.Id));

        var skritTurn = await harness.RunTurn("Skrit", round: 1, turn: 4);
        Assert.Equal(TurnOutcome.ActionResolved, skritTurn.Outcome);

        var skrit = harness.Engine.State.RequireById(TestWorld.SkritId);
        Assert.Equal(CharacterDisposition.Escaped, skrit.Disposition);
        Assert.Equal(TestWorld.StairDoorId, skrit.EscapedThroughExitId);
        Assert.True(skrit.IsAlive);
        Assert.False(skrit.IsCombatTarget);

        var escape = Assert.Single(harness.Sink.Payloads<CharacterEscapedPayload>(TraceEventType.CharacterEscaped));
        Assert.Equal("Cellar Stair Door", escape.ExitName);
    }

    // ------------------------------------------------------------------------------------------
    // Public-knowledge boundaries around who is present (tests #29, #30, #31)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_escaped_character_stops_receiving_public_events()
    {
        // Skrit has already escaped; Vark then offers terms publicly. Skrit must not learn of it.
        var skritGone = TestWorld.Skrit() with { Disposition = CharacterDisposition.Escaped, EscapedThroughExitId = TestWorld.StairDoorId };
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                DmOfferSurrender("Vark", "Rowan"),
                ScriptedChatClient.Text("Vark holds his sabre out hilt-first and offers it up to be spared.")),
            MultiActorHarness.Clients(
                ("Vark", new ScriptedChatClient(Act("v-1", "I offer Rowan my purse and my sabre if he lets me live.")))),
            initialState: TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
                TestWorld.Rowan(), TestWorld.Elara(health: 6),
                // Terms must promise a carried item, so Vark has a purse to put on the table.
                TestWorld.Vark() with { Inventory = [TestWorld.Purse("vark")] }, skritGone));

        await harness.RunTurn("Vark", round: 2, turn: 7);

        var offerFact = Assert.Single(harness.Ledger.Facts, f => f.FactType == FactType.SurrenderOfferMade);
        // A present character learned it; the escaped one did not.
        Assert.True(harness.Ledger.Knows(TestWorld.RowanId, offerFact.Id));
        Assert.False(harness.Ledger.Knows(TestWorld.SkritId, offerFact.Id));

        var delivered = Assert.Single(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered));
        Assert.DoesNotContain(TestWorld.SkritId, delivered.Recipients);
    }

    [Fact]
    public async Task A_surrendered_character_keeps_receiving_public_events_while_the_encounter_continues()
    {
        // Vark has already surrendered; Skrit then opens the door publicly. Vark, still present, learns of it.
        var varkDown = TestWorld.Vark() with { Disposition = CharacterDisposition.Surrendered };
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                DmOpenExit("Skrit"),
                ScriptedChatClient.Text("Skrit drags the cellar door open.")),
            MultiActorHarness.Clients(
                ("Skrit", new ScriptedChatClient(Act("s-1", "I drag the door open.")))),
            initialState: TestWorld.StateWithExit(TestWorld.StairDoor(open: false),
                TestWorld.Rowan(), TestWorld.Elara(health: 6), varkDown, TestWorld.Skrit()));

        await harness.RunTurn("Skrit", round: 2, turn: 8);

        var openFact = Assert.Single(harness.Ledger.Facts, f => f.FactType == FactType.ExitOpened);
        Assert.True(harness.Ledger.Knows(TestWorld.VarkId, openFact.Id));
    }

    // ------------------------------------------------------------------------------------------
    // A weaker DM that invents an exit name is corrected to the room's single exit
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invented_exit_name_is_corrected_to_the_rooms_single_exit()
    {
        // The DM proposes open_exit but hallucinates the exit name instead of using the one in the snapshot.
        // With a single exit the intent is unambiguous, so the harness corrects it rather than letting the
        // engine reject a genuine attempt to leave.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-open", DungeonMasterTools.OpenExitName, ("actor", "Vark"), ("exit", "doorway to the north")),
                ScriptedChatClient.Text("Vark wrenches the cellar door open.")),
            MultiActorHarness.Clients(
                ("Vark", new ScriptedChatClient(Act("v-1", "I haul the heavy wooden door open.")))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        var result = await harness.RunTurn("Vark");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.True(harness.Engine.State.Exits.Single().IsOpen);

        var correction = Assert.Single(
            harness.Sink.Payloads<AdjudicationCorrectionPayload>(TraceEventType.AdjudicationCorrected)
                .Where(c => c.Parameter == DungeonMasterTools.ExitParameter));
        Assert.Equal("doorway to the north", correction.DungeonMasterValue);
        Assert.Equal("Cellar Stair Door", correction.CorrectedValue);
    }

    // ------------------------------------------------------------------------------------------
    // Opening and escaping are separate acts — one action never does both (test #28)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_exit_consumes_the_turn_and_never_also_escapes_the_actor()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                DmOpenExit("Vark"),
                ScriptedChatClient.Text("Vark wrenches the door open.")),
            MultiActorHarness.Clients(
                ("Vark", new ScriptedChatClient(Act("v-1", "I wrench the door open and make to flee.")))),
            initialState: TestWorld.TwoVsTwoStateWithExit());

        var result = await harness.RunTurn("Vark");

        // The open resolved and consumed the turn; the actor is still present, not escaped. Leaving is a
        // separate act on a later turn, so a single action can never both open the door and go through it.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.True(harness.Engine.State.Exits.Single().IsOpen);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Empty(harness.Sink.Payloads<CharacterEscapedPayload>(TraceEventType.CharacterEscaped));
    }
}
