using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

public sealed class CouncilConversationTests
{
    [Fact]
    public async Task Declared_addressee_overrides_a_misclassified_person_mentioned_in_speech()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits."), ScriptedChatClient.Text("Vark waits.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("say", CharacterTools.SayName, ("message", "Let Skrit go."), ("addressed_to", "Vark")),
                    ScriptedChatClient.Call("end", CharacterTools.EndTurnName, ("reason", "Wait.")))),
                ("Vark", new ScriptedChatClient(
                    ScriptedChatClient.Call("answer", CharacterTools.RespondName, ("request_id", "request-1"), ("decision", "decline"), ("message", "No.")),
                    ScriptedChatClient.Call("end2", CharacterTools.EndTurnName, ("reason", "Wait."))))),
            conversationParserClient: new ScriptedChatClient(
                ScriptedChatClient.Call("route", "classify_conversation", ("kind", "request"), ("recipient", "Skrit"))));
        await harness.RunTurn("Rowan");
        await harness.RunTurn("Vark", turn: 2);
        Assert.Contains(harness.Client("Vark").Requests[0], m => m.Text?.Contains("request-1, from Rowan: Let Skrit go.") == true);
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
    }

    [Fact]
    public async Task Unavailable_conversation_router_preserves_speech_without_inventing_a_request()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("say", CharacterTools.SayName, ("message", "Vark, give me the seal.")),
                ScriptedChatClient.Call("end", CharacterTools.EndTurnName, ("reason", "Wait."))))),
            conversationParserClient: new ScriptedChatClient()); // exhausted client throws
        var turn = await harness.RunTurn("Rowan");
        Assert.Equal(TurnOutcome.EndedByCharacter, turn.Outcome);
        Assert.Single(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Single(harness.Sink.OfType(TraceEventType.ConversationRequest)); // diagnostic only
    }

    [Fact]
    public async Task Ordinary_speech_requests_and_refusals_are_tracked_without_native_request_tools()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits."), ScriptedChatClient.Text("Vark waits.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("say1", CharacterTools.SayName, ("message", "Vark, give me the seal.")),
                    ScriptedChatClient.Call("end1", CharacterTools.EndTurnName, ("reason", "Wait.")))),
                ("Vark", new ScriptedChatClient(
                    ScriptedChatClient.Call("say2", CharacterTools.SayName, ("message", "No. The seal stays with me.")),
                    ScriptedChatClient.Call("end2", CharacterTools.EndTurnName, ("reason", "Hold my ground."))))),
            conversationParserClient: new ScriptedChatClient(
                ScriptedChatClient.Call("route1", "classify_conversation", ("kind", "request"), ("recipient", "Vark")),
                ScriptedChatClient.Call("route2", "classify_conversation", ("kind", "response"), ("request_id", "request-1"), ("decision", "decline"))));
        await harness.RunTurn("Rowan");
        await harness.RunTurn("Vark", turn: 2);
        Assert.Equal(2, harness.Sink.OfType(TraceEventType.ConversationRequest).Count());
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Equal(2, harness.Client("Vark").CallCount);
    }

    [Fact]
    public async Task A_request_in_action_text_bypasses_physical_adjudication()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("act", CharacterTools.TakeActionName, ("intent", "I ask Vark to give me the seal.")),
                ScriptedChatClient.Call("end", CharacterTools.EndTurnName, ("reason", "Wait."))))),
            conversationParserClient: new ScriptedChatClient(
                ScriptedChatClient.Call("route", "classify_conversation", ("kind", "request"), ("recipient", "Vark"))));
        var turn = await harness.RunTurn("Rowan");
        Assert.Equal(0, turn.ActionAttempts);
        Assert.Single(harness.Sink.OfType(TraceEventType.ConversationRequest));
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Equal(1, harness.DungeonMasterClient.CallCount); // only the end-turn narration
    }

    [Fact]
    public void Factual_recap_preserves_item_chronology_without_inventing_a_story()
    {
        var brief = new EncounterStoryBrief
        {
            ScenarioPremise = "Archive", Roster = "Heroes and wardens",
            ChronologicalEvents = ["Round 3: The Curator stole the seal from Smut.", "Round 6: Smut escaped.", "Round 7: Mirabel stole the seal from the living Curator."],
            EventsTotal = 3, Kills = [], FinalCharacterStates = [], TerminalCondition = "Stopped",
            Outcome = EncounterOutcome.HarnessLimit, RoundsPlayed = 7
        };
        var recap = brief.RenderFactualRecap();
        Assert.Contains("1. Round 3:", recap);
        Assert.Contains("2. Round 6:", recap);
        Assert.Contains("3. Round 7:", recap);
        Assert.DoesNotContain("corpse", recap);
        Assert.DoesNotContain("Smut escaped with the seal", recap);
    }

    [Fact]
    public async Task Explicit_voluntary_surrender_is_still_allowed()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("offer", DungeonMasterTools.OfferSurrenderName, ("offerer", "Rowan"), ("recipient", "Vark"), ("offered_items", Array.Empty<string>()), ("forfeit_weapon", true)),
                ScriptedChatClient.Text("Rowan offers his surrender, not yet accepted.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("act", CharacterTools.TakeActionName, ("intent", "I surrender my sword in return for my life."), ("surrender_self", true))))),
            new HarnessOptions { RequireSurrenderConfirmation = true });
        await harness.RunTurn("Rowan");
        Assert.Single(harness.Engine.State.SurrenderOffers);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.RowanId).Disposition);
        Assert.NotNull(harness.Engine.State.RequireById(TestWorld.RowanId).Weapon);
    }

    [Fact]
    public void Council_blocks_allied_attacks_and_theft_before_rng_or_mutation()
    {
        var owner = TestWorld.Skrit() with { Inventory = [new InventoryItem("seal", "Seal", "Badge")] };
        var rng = new ScriptedRng();
        var state = TestWorld.State(TestWorld.Vark(), owner);
        var engine = new GameEngine(state, rng, CombatRules.NoGlancing with { PreventFriendlyHostility = true });
        Assert.False(engine.Execute(new AttackCharacterAction("Vark", "Skrit", "Notched Sabre")).Accepted);
        Assert.False(engine.Execute(new StealItemAction("Vark", "Skrit", "Seal")).Accepted);
        Assert.Same(state, engine.State);
        Assert.Equal(0, rng.DrawCount);
        Assert.Single(engine.State.RequireById(owner.Id).Inventory);
    }

    [Theory]
    [InlineData("accept", "Yes, but wait until I can hand it over.")]
    [InlineData("decline", "No. I need it.")]
    [InlineData("counter", "Only if you let me leave.")]
    [InlineData("ignore", "")]
    public async Task Requests_reach_recipient_and_each_response_preserves_ownership(string decision, string reply)
    {
        var owner = TestWorld.Vark() with { Inventory = [new InventoryItem("seal", "Seal", "Badge")] };
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits."), ScriptedChatClient.Text("Vark waits.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("ask", CharacterTools.RequestName, ("recipient", "Vark"), ("message", "Give me the seal.")),
                    ScriptedChatClient.Call("pass", CharacterTools.EndTurnName, ("reason", "Wait for his decision.")))),
                ("Vark", new ScriptedChatClient(
                    ScriptedChatClient.Call("answer", CharacterTools.RespondName, ("request_id", "request-1"), ("decision", decision), ("message", reply)),
                    ScriptedChatClient.Call("pass2", CharacterTools.EndTurnName, ("reason", "Stay here."))))),
            initialState: TestWorld.State(TestWorld.Rowan(), owner));
        await harness.RunTurn("Rowan");
        await harness.RunTurn("Vark", turn: 2);
        Assert.Contains(harness.Client("Vark").Requests[0], m => m.Text?.Contains("request-1, from Rowan: Give me the seal.") == true);
        Assert.Single(harness.Engine.State.RequireById(owner.Id).Inventory);
        Assert.Empty(harness.Engine.State.SurrenderOffers);
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.True(harness.Sink.OfType(TraceEventType.ConversationRequest).Count() >= 2);
    }

    [Fact]
    public async Task Agreement_requires_the_owners_own_action_to_transfer()
    {
        var owner = TestWorld.Vark() with { Inventory = [new InventoryItem("seal", "Seal", "Badge")] };
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan waits."),
                ScriptedChatClient.Call("give", DungeonMasterTools.GiveItemName, ("actor", "Vark"), ("recipient", "Rowan"), ("item", "Seal")),
                ScriptedChatClient.Text("Vark hands the seal to Rowan.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("ask", CharacterTools.RequestName, ("recipient", "Vark"), ("message", "Give me the seal.")),
                    ScriptedChatClient.Call("pass", CharacterTools.EndTurnName, ("reason", "Wait.")))),
                ("Vark", new ScriptedChatClient(
                    ScriptedChatClient.Call("answer", CharacterTools.RespondName, ("request_id", "request-1"), ("decision", "accept"), ("message", "Yes.")),
                    ScriptedChatClient.Call("act", CharacterTools.TakeActionName, ("intent", "I hand my seal to Rowan."))))),
            initialState: TestWorld.State(TestWorld.Rowan(), owner));
        await harness.RunTurn("Rowan");
        Assert.Single(harness.Engine.State.RequireById(owner.Id).Inventory);
        await harness.RunTurn("Vark", turn: 2);
        Assert.Empty(harness.Engine.State.RequireById(owner.Id).Inventory);
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Id == "seal");
        Assert.Single(harness.Sink.OfType(TraceEventType.EngineAction));
    }

    [Fact]
    public async Task Wrongly_interpreted_bargain_cannot_create_unconfirmed_surrender()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("wrong", DungeonMasterTools.OfferSurrenderName, ("offerer", "Rowan"), ("recipient", "Vark"), ("offered_items", Array.Empty<string>()), ("forfeit_weapon", true)),
                ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("act", CharacterTools.TakeActionName, ("intent", "Take my sword and leave us alone.")),
                ScriptedChatClient.Call("pass", CharacterTools.EndTurnName, ("reason", "No, I am not surrendering."))))),
            new HarnessOptions { RequireSurrenderConfirmation = true });
        await harness.RunTurn("Rowan");
        Assert.Empty(harness.Engine.State.SurrenderOffers);
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.NotNull(harness.Engine.State.RequireById(TestWorld.RowanId).Weapon);
    }

    [Fact]
    public async Task Dm_routes_request_submitted_as_action_without_forcing_a_result()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("route", CharacterTools.RequestName, ("recipient", "Vark"), ("message", "Let us pass.")),
                ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("act", CharacterTools.TakeActionName, ("intent", "I ask Vark to let us pass.")),
                ScriptedChatClient.Call("pass", CharacterTools.EndTurnName, ("reason", "Await his answer."))))));
        var turn = await harness.RunTurn("Rowan");
        Assert.Equal(TurnOutcome.EndedByCharacter, turn.Outcome);
        Assert.Single(harness.Sink.OfType(TraceEventType.ConversationRequest));
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
    }

    [Fact]
    public void Requests_are_recipient_bound_replaced_per_pair_and_removed_when_someone_leaves()
    {
        var state = TestWorld.TwoVsTwoState();
        var requests = new ConversationRequests();
        var first = requests.Add(TestWorld.RowanId, TestWorld.VarkId, "Give it here.");
        var revised = requests.Add(TestWorld.RowanId, TestWorld.VarkId, "Show it instead.");
        Assert.Null(requests.Resolve(TestWorld.VarkId, first.Id, state));
        Assert.Null(requests.Resolve(TestWorld.SkritId, revised.Id, state));
        Assert.Single(requests.For(TestWorld.VarkId, state));
        var gone = state with { Characters = [.. state.Characters.Select(c => c.Id == TestWorld.RowanId ? c with { Disposition = CharacterDisposition.Escaped } : c)] };
        Assert.Empty(requests.For(TestWorld.VarkId, gone));
    }
}
