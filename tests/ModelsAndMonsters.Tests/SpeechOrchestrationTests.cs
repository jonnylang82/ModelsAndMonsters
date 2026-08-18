using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Public speech through the real <see cref="TurnCoordinator"/> with scripted models: that speaking is a
/// free public event which does not consume the turn, is delivered verbatim to every living listener and
/// nobody else, never mutates authoritative state, and never leaks a speaker's private exchanges.
/// </summary>
public sealed class SpeechOrchestrationTests
{
    private const string PlanMessage = "Elara, get whatever is in that chest. I'll hold off the captain.";

    // Only the four characters we drive need scripted clients; the rest are supplied empty so their call
    // counts can be inspected (an unused client simply never gets a response dequeued).
    private static MultiActorHarness Harness(
        ScriptedChatClient dungeonMaster,
        params (string Name, ScriptedChatClient Client)[] characters) =>
        new(dungeonMaster, MultiActorHarness.Clients(characters));

    // ------------------------------------------------------------------------------------------
    // say is exposed, and speaking does not consume the turn (tests #1, #2)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_say_tool_is_offered_to_a_character_agent()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan does nothing.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))));

        await harness.RunTurn("Rowan");

        var tools = harness.Client("Rowan").RequestOptions[0]!.Tools!.Select(t => t.Name);
        Assert.Contains(CharacterTools.SayName, tools);
    }

    [Fact]
    public async Task Speaking_does_not_end_the_turn_so_the_character_can_still_act()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan's blade crashes into the captain's guard.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I strike the captain.")))));

        var result = await harness.RunTurn("Rowan");

        // The turn resolved on the action, after a speech act that did not end it.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Equal(1, result.SpeechActs);
        Assert.Single(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.Single(harness.Sink.OfType(TraceEventType.EngineAction));
    }

    [Fact]
    public async Task Speaking_then_ending_the_turn_records_the_speech_and_ends_by_choice()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan sets his feet and says no more.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.EndedByCharacter, result.Outcome);
        Assert.Equal(1, result.SpeechActs);
    }

    // ------------------------------------------------------------------------------------------
    // At most once per turn by default (test #3)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_character_may_speak_only_once_per_turn_by_default()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan falls silent.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", "First thing.")),
                ScriptedChatClient.Call("r-2", CharacterTools.SayName, ("message", "Second thing.")),
                ScriptedChatClient.Call("r-3", CharacterTools.EndTurnName, ("reason", "Done.")))));

        var result = await harness.RunTurn("Rowan");

        // Only the first utterance was heard; the second hit the per-turn speech limit.
        Assert.Equal(1, result.SpeechActs);
        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("First thing.", speech.Message);

        var limit = Assert.Single(harness.Sink.Payloads<HarnessLimitPayload>(TraceEventType.HarnessLimitReached));
        Assert.Equal(nameof(HarnessOptions.MaxSpeechActsPerTurn), limit.Limit);
    }

    // ------------------------------------------------------------------------------------------
    // Empty speech is rejected safely (test #4)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Empty_speech_is_rejected_without_being_delivered()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan says nothing at all.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", "   ")),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Nothing to say.")))));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(0, result.SpeechActs);
        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.DoesNotContain(harness.NarrationLog.Entries, e => e.Kind == PublicChannelKind.Speech);
    }

    [Fact]
    public async Task An_unreasonably_large_message_is_rejected_without_being_delivered()
    {
        var wall = new string('x', new HarnessOptions().MaxSpeechCharacters + 50);

        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan thinks better of the speech.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", wall)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Enough.")))));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(0, result.SpeechActs);
        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.DoesNotContain(harness.NarrationLog.Entries, e => e.Kind == PublicChannelKind.Speech);
    }

    // ------------------------------------------------------------------------------------------
    // Verbatim delivery with attribution, to every living character (tests #5, #6)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Speech_is_recorded_verbatim_and_intended_for_every_other_living_character()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan squares up to the captain.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))));

        await harness.RunTurn("Rowan");

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal(PlanMessage, speech.Message); // verbatim, not paraphrased
        Assert.Equal(TestWorld.RowanId, speech.SpeakerId);

        // Intended for every other living character in the room — Elara, Vark and Skrit — and not Rowan.
        Assert.Equal(
            new[] { TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId }.OrderBy(x => x),
            speech.Recipients.OrderBy(x => x));

        // The stored public-channel entry carries the speaker's attribution.
        var entry = Assert.Single(harness.NarrationLog.Entries, e => e.Kind == PublicChannelKind.Speech);
        Assert.Contains("Rowan says:", entry.Text, StringComparison.Ordinal);
        Assert.Contains(PlanMessage, entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listener_hears_the_speech_verbatim_when_their_own_turn_begins()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("Rowan squares up."),
                ScriptedChatClient.Text("Vark bares his teeth and waits.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))),
            ("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.EndTurnName, ("reason", "Bide my time.")))));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Vark", round: 1, turn: 3);

        // Vark receives Rowan's exact words, with attribution, as part of his own turn context.
        var varkContext = harness.Client("Vark").Requests[0].Last().Text!;
        Assert.Contains("Rowan says:", varkContext, StringComparison.Ordinal);
        Assert.Contains(PlanMessage, varkContext, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Dead characters do not receive new speech (test #7)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dead_character_is_not_among_the_intended_recipients_of_new_speech()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan calls across the cellar.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece."))))),
            initialState: TestWorld.State(
                TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit(health: 0)));

        await harness.RunTurn("Rowan");

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.DoesNotContain(TestWorld.SkritId, speech.Recipients); // Skrit is dead
        Assert.Equal(
            new[] { TestWorld.ElaraId, TestWorld.VarkId }.OrderBy(x => x),
            speech.Recipients.OrderBy(x => x));
    }

    // ------------------------------------------------------------------------------------------
    // Speech triggers no immediate recipient model calls (test #8)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Speaking_does_not_trigger_any_recipient_model_call()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan calls out and stands ready.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))),
            ("Elara", new ScriptedChatClient()),
            ("Vark", new ScriptedChatClient()),
            ("Skrit", new ScriptedChatClient()));

        await harness.RunTurn("Rowan");

        // No other character's model was consulted as a result of Rowan speaking.
        Assert.Equal(0, harness.Client("Elara").CallCount);
        Assert.Equal(0, harness.Client("Vark").CallCount);
        Assert.Equal(0, harness.Client("Skrit").CallCount);
    }

    // ------------------------------------------------------------------------------------------
    // Speech changes nothing authoritative, and cannot override state (tests #9, #10)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Speaking_mutates_no_authoritative_state()
    {
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan holds his ground.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))));

        var versionBefore = harness.Engine.State.Version;
        await harness.RunTurn("Rowan");

        Assert.Equal(versionBefore, harness.Engine.State.Version);
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Equal(12, harness.Engine.State.RequireById(TestWorld.VarkId).Health);
    }

    [Fact]
    public async Task A_false_spoken_claim_does_not_change_authoritative_state()
    {
        const string boast = "The captain is nearly dead! Finish him!";

        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan shouts across the water.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", boast)),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Said my piece.")))));

        await harness.RunTurn("Rowan");

        // Vark is in fact untouched: the claim is stored as a spoken claim, not folded into state.
        Assert.Equal(12, harness.Engine.State.RequireById(TestWorld.VarkId).Health);
        var entry = Assert.Single(harness.NarrationLog.Entries, e => e.Kind == PublicChannelKind.Speech);
        Assert.Contains(boast, entry.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Private DM exchanges stay isolated even once public speech exists (test #11)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_private_question_stays_isolated_from_a_teammate_even_alongside_public_speech()
    {
        const string question = "Which goblin looks the more dangerous to you?";
        const string privateAnswer = "The larger one carries itself like the leader.";
        const string publicNarration = "Rowan's longsword opens a gash across the captain's arm.";

        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(privateAnswer),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text(publicNarration),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Elara"), ("target", "Skrit"), ("weapon", "Iron Mace")),
                ScriptedChatClient.Text("Elara's mace glances off the smaller goblin.")),
            ("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.AskDmName, ("question", question)),
                ScriptedChatClient.Call("r-2", CharacterTools.SayName, ("message", PlanMessage)),
                ScriptedChatClient.Call("r-3", CharacterTools.TakeActionName, ("intent", "I strike the captain.")))),
            ("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I swing at Skrit.")))));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Elara", round: 1, turn: 2);

        var elaraHistory = Flatten(harness.Agent("Elara"));

        // Rowan's public words reached his ally...
        Assert.Contains(PlanMessage, elaraHistory, StringComparison.Ordinal);
        // ...but his private question and its answer did not.
        Assert.DoesNotContain(question, elaraHistory, StringComparison.Ordinal);
        Assert.DoesNotContain(privateAnswer, elaraHistory, StringComparison.Ordinal);
    }

    private static string Flatten(ModelAgent agent) =>
        string.Join("\n", agent.Conversation.BuildRequestMessages()
            .SelectMany(m => m.Contents)
            .Select(content => content switch
            {
                Microsoft.Extensions.AI.TextContent text => text.Text,
                Microsoft.Extensions.AI.FunctionCallContent call => string.Join(",", call.Arguments?.Values ?? []),
                Microsoft.Extensions.AI.FunctionResultContent result => result.Result?.ToString() ?? "",
                _ => ""
            }));
}
