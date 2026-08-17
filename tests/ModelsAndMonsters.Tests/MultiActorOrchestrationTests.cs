using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The multi-actor turn loop, driven a turn at a time through the real <see cref="TurnCoordinator"/>
/// with scripted models: targeting, isolation, public delivery, dead-actor skips, voluntary passes and
/// complete RNG tracing, all with four distinct characters on two teams.
/// </summary>
public sealed class MultiActorOrchestrationTests
{
    // ------------------------------------------------------------------------------------------
    // Dead actors are skipped without a model call (test #3)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dead_actor_is_skipped_without_a_model_call_and_the_skip_is_traced()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(),
            MultiActorHarness.Clients(("Skrit", new ScriptedChatClient())),
            initialState: TestWorld.State(
                TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit(health: 0)));

        var result = await harness.RunTurn("Skrit", round: 2, turn: 4);

        Assert.Equal(TurnOutcome.Skipped, result.Outcome);
        Assert.Equal(0, harness.Client("Skrit").CallCount);

        var skip = Assert.Single(harness.Sink.Payloads<TurnSkippedPayload>(TraceEventType.TurnSkipped));
        Assert.Equal("Skrit", skip.CharacterName);
        Assert.Equal(TestWorld.GoblinsTeam, skip.Team);

        // No model request was traced for the skipped actor.
        Assert.DoesNotContain(
            harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest),
            r => r.AgentName == "Skrit");
    }

    // ------------------------------------------------------------------------------------------
    // Targeting resolves to the intended character (tests #6 and #7)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_attack_names_a_specific_target_and_the_resolution_is_recorded()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Skrit"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan's longsword bites into the smaller goblin.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I bring my longsword down on Skrit."))))));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        var resolution = Assert.Single(harness.Sink.Payloads<TargetResolutionPayload>(TraceEventType.TargetResolved));
        Assert.Equal(TestWorld.RowanId, resolution.AttackerId);
        Assert.Equal("Skrit", resolution.RequestedTarget);
        Assert.Equal(TestWorld.SkritId, resolution.ResolvedTargetId);
        Assert.True(resolution.Resolved);
        Assert.True(resolution.TargetAlive);
        Assert.False(resolution.TargetIsAlly); // an enemy, correctly

        // The engine resolved the blow against Skrit specifically, and Vark was not touched.
        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.Equal(TestWorld.SkritId, ((AttackOutcome)engineAction.Outcome!).TargetId);
        Assert.Equal(12, harness.Engine.State.RequireById(TestWorld.VarkId).Health);
    }

    [Fact]
    public async Task A_dead_target_is_rejected_and_no_living_character_is_substituted_in_its_place()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Skrit"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Skrit is already dead; there is nothing there to strike."),
                ScriptedChatClient.Text("Rowan lowers the blade a moment.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I cut down Skrit.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "There is nothing there to hit."))))),
            initialState: TestWorld.State(
                TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit(health: 0)));

        await harness.RunTurn("Rowan");

        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.False(engineAction.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsDead.ToString(), engineAction.RejectionReason);

        var resolution = Assert.Single(harness.Sink.Payloads<TargetResolutionPayload>(TraceEventType.TargetResolved));
        Assert.Equal(TestWorld.SkritId, resolution.ResolvedTargetId); // named Skrit, not silently swapped
        Assert.False(resolution.TargetAlive);

        // Vark, the living goblin, was never hit in the dead one's place.
        Assert.Equal(12, harness.Engine.State.RequireById(TestWorld.VarkId).Health);
    }

    // ------------------------------------------------------------------------------------------
    // Private information stays isolated between teammates (test #8)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_private_question_and_answer_never_reach_a_teammates_history()
    {
        const string question = "Which goblin looks the more dangerous to you?";
        const string privateAnswer = "The larger one carries itself like the leader; the smaller keeps glancing at it.";
        const string publicNarration = "Rowan's longsword opens a gash across the captain's arm.";

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(privateAnswer),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text(publicNarration),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Elara"), ("target", "Skrit"), ("weapon", "Iron Mace")),
                ScriptedChatClient.Text("Elara's mace glances off the smaller goblin.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.AskDmName, ("question", question)),
                    ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I strike the captain.")))),
                ("Elara", new ScriptedChatClient(
                    ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I swing my mace at Skrit."))))));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Elara", round: 1, turn: 2);

        var elaraHistory = Flatten(harness.Agent("Elara"));

        // Rowan and Elara are allies, but the private exchange is Rowan's alone.
        Assert.DoesNotContain(question, elaraHistory, StringComparison.Ordinal);
        Assert.DoesNotContain(privateAnswer, elaraHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("I strike the captain.", elaraHistory, StringComparison.Ordinal);

        // But the public outcome of Rowan's blow did reach her.
        Assert.Contains(publicNarration, elaraHistory, StringComparison.Ordinal);

        // And the private answer was delivered only to Rowan, per the trace.
        var answer = Assert.Single(harness.Sink.Payloads<DungeonMasterAnswerPayload>(TraceEventType.DungeonMasterAnswer));
        Assert.Equal("Rowan", answer.CharacterName);
        Assert.Equal(TestWorld.RowanId, answer.CharacterId);
    }

    // ------------------------------------------------------------------------------------------
    // Public narration reaches every living character (test #9)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Public_narration_is_intended_for_and_delivered_to_every_living_character()
    {
        const string publicNarration = "Rowan's longsword crashes into the captain's guard.";

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text(publicNarration),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Vark"), ("target", "Rowan"), ("weapon", "Notched Sabre")),
                ScriptedChatClient.Text("Vark's sabre rings off Rowan's mail.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I strike Vark.")))),
                ("Vark", new ScriptedChatClient(ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I slash at Rowan."))))));

        await harness.RunTurn("Rowan", round: 1, turn: 1);

        // The narration of Rowan's blow is intended for every living character in the room — all four.
        var narration = harness.Sink.Payloads<NarrationPayload>(TraceEventType.Narration)
            .First(n => n.Narration == publicNarration);
        Assert.Equal(
            new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId }.OrderBy(x => x),
            narration.IntendedRecipients.OrderBy(x => x));

        // The acting character received it at once; a later actor receives it when its own turn begins.
        await harness.RunTurn("Vark", round: 1, turn: 3);
        var varkTurnContext = harness.Client("Vark").Requests[0].Last().Text!;
        Assert.Contains(publicNarration, varkTurnContext, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Voluntary end_turn changes no engine state (test #10)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ending_a_turn_completes_it_without_changing_engine_state()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Skrit shrinks back against the crates and does nothing.")),
            MultiActorHarness.Clients(
                ("Skrit", new ScriptedChatClient(ScriptedChatClient.Call("s-1", CharacterTools.EndTurnName,
                    ("reason", "The captain is wounded and I dare not move."))))));

        var versionBefore = harness.Engine.State.Version;
        var result = await harness.RunTurn("Skrit", round: 1, turn: 4);

        Assert.Equal(TurnOutcome.EndedByCharacter, result.Outcome);
        Assert.Equal(versionBefore, harness.Engine.State.Version);
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));

        var passed = Assert.Single(harness.Sink.Payloads<CharacterPassedPayload>(TraceEventType.CharacterPassed));
        Assert.Equal("Skrit", passed.CharacterName);
        Assert.Contains("dare not move", passed.Reason, StringComparison.Ordinal);

        // No health changed for anyone.
        Assert.Equal(14, harness.Engine.State.RequireById(TestWorld.RowanId).Health);
        Assert.Equal(8, harness.Engine.State.RequireById(TestWorld.SkritId).Health);
    }

    // ------------------------------------------------------------------------------------------
    // Every RNG draw is fully traced (test #11)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_rng_draw_from_an_attack_produces_a_complete_trace_record()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan's blade lands solidly on the captain.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I strike Vark."))))),
            initialState: TestWorld.State(
                TestWorld.Rowan(hitChance: 80), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit()),
            rng: new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid),
            rules: CombatRules.Default);

        await harness.RunTurn("Rowan");

        var draws = harness.Sink.Payloads<RngDraw>(TraceEventType.RngDraw).ToList();
        Assert.Equal(2, draws.Count);

        var hit = draws[0];
        Assert.Equal("attack.hit-check", hit.Purpose);
        Assert.Equal("attack_character", hit.ActionType);
        Assert.Equal(TestWorld.RowanId, hit.ActorId);
        Assert.Equal("Rowan", hit.ActorName);
        Assert.Equal(TestWorld.VarkId, hit.TargetId);
        Assert.Equal("Vark", hit.TargetName);
        Assert.Equal("hit or miss", hit.OutcomeSelected);
        Assert.Equal(100, hit.Sides);
        Assert.Equal(ScriptedRng.Hits, hit.RawRoll);
        Assert.Equal(80, hit.Threshold);
        Assert.Equal("hit", hit.Result);
        Assert.Equal(0, hit.Seed); // ScriptedRng reports seed 0
        Assert.Equal(0, hit.SequenceBefore);
        Assert.Equal(1, hit.SequenceAfter);

        var glancing = draws[1];
        Assert.Equal("attack.glancing-check", glancing.Purpose);
        Assert.Equal(1, glancing.SequenceBefore);
        Assert.Equal(2, glancing.SequenceAfter);
        Assert.False(string.IsNullOrWhiteSpace(glancing.Comparison));
    }

    private static string Flatten(ModelAgent agent) =>
        string.Join("\n", agent.Conversation.BuildRequestMessages().SelectMany(Describe));

    private static IEnumerable<string> Describe(ChatMessage message) =>
        message.Contents.Select(content => content switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => $"{call.Name}({string.Join(",", call.Arguments?.Values ?? [])})",
            FunctionResultContent result => result.Result?.ToString() ?? "",
            _ => ""
        });
}
