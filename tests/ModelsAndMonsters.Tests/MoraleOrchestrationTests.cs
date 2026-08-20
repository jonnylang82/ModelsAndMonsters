using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.8 morale mechanics through the whole orchestration path: a character declaring who it spoke to,
/// the Dungeon Master binding a threat or a steadying, and the information boundary around the fear score.
/// </summary>
public sealed class MoraleOrchestrationTests
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static HarnessOptions Limits() =>
        new() { MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 8 };

    private static MultiActorHarness Harness(
        ScriptedChatClient dungeonMaster,
        (string Name, ScriptedChatClient Client)[] characters,
        GameState? state = null,
        IRng? rng = null) =>
        new(dungeonMaster,
            MultiActorHarness.Clients(characters),
            Limits(),
            state ?? TestWorld.V07State(exitOpen: false),
            rng ?? new SeededRng(1),
            CombatRules.Default,
            TestWorld.V07Scenario());

    // ------------------------------------------------------------------------------------------
    // A threat, end to end
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_threat_spoken_to_one_enemy_binds_to_that_enemy_and_moves_only_their_fear()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                ScriptedChatClient.Text("Vark's sabre comes up level with Rowan's throat, and Rowan's grip shifts.")),
            [("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I level my sabre at Rowan and tell him what is coming."),
                    ("utterances", new[] { "You saw what I did to the last one. You are next." }),
                    ("addressed_to", "Rowan"))))],
            rng: new ScriptedRng(1));

        var turn = await harness.RunTurn("Vark", round: 1, turn: 1);

        // The threat is the whole action; nothing else happens on this turn.
        Assert.Equal(Orchestration.TurnOutcome.ActionResolved, turn.Outcome);
        Assert.Contains("IntimidateCharacter", turn.AcceptedAction!, StringComparison.Ordinal);

        Assert.Equal(1, harness.Engine.State.RequireById(TestWorld.RowanId).Fear);
        Assert.Equal(0, harness.Engine.State.RequireById(TestWorld.ElaraId).Fear);

        var attempt = Assert.Single(harness.Sink.Payloads<IntimidationAttemptedPayload>(TraceEventType.IntimidationAttempted));
        Assert.Equal("Vark", attempt.ActorName);
        Assert.Equal("Rowan", attempt.TargetName);
        Assert.True(attempt.Succeeded);
        Assert.Equal(TestWorld.RowanId, attempt.SpeechAddressedToId);
        Assert.Contains("You are next", attempt.AssociatedSpeech);
        Assert.Equal(0, attempt.TargetFearBefore);
        Assert.Equal(1, attempt.TargetFearAfter);
    }

    [Fact]
    public async Task A_threat_the_speaker_aimed_at_somebody_else_is_refused_and_draws_nothing()
    {
        var rng = new ScriptedRng(1);
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                // The pass that follows the refusal is narrated too.
                ScriptedChatClient.Text("Vark says nothing more, and the moment goes by.")),
            [("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I level my sabre and speak."),
                    ("utterances", new[] { "Elara, stay where you are." }),
                    ("addressed_to", "Elara")),
                ScriptedChatClient.Call("v-2", CharacterTools.EndTurnName, ("reason", "The moment passes."))))],
            rng: rng);

        await harness.RunTurn("Vark", round: 1, turn: 1);

        Assert.Equal(0, harness.Engine.State.RequireById(TestWorld.RowanId).Fear);
        Assert.Equal(0, rng.DrawCount);
        Assert.Empty(harness.Sink.Payloads<IntimidationAttemptedPayload>(TraceEventType.IntimidationAttempted));
    }

    [Fact]
    public async Task A_threat_with_nothing_actually_spoken_is_refused_before_any_draw()
    {
        var rng = new ScriptedRng(1);
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                ScriptedChatClient.Text("Vark says nothing more, and the moment goes by.")),
            [("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I glare at Rowan as menacingly as I know how.")),
                ScriptedChatClient.Call("v-2", CharacterTools.EndTurnName, ("reason", "Nothing comes of it."))))],
            rng: rng);

        await harness.RunTurn("Vark", round: 1, turn: 1);

        Assert.Equal(0, harness.Engine.State.RequireById(TestWorld.RowanId).Fear);
        Assert.Equal(0, rng.DrawCount);
    }

    // ------------------------------------------------------------------------------------------
    // Steadying, end to end
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Steadying_an_ally_is_narrated_publicly_and_recorded_with_no_draw()
    {
        var rng = new ScriptedRng();
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.SteadyAllyName,
                    ("actor", "Rowan"), ("target", "Elara")),
                ScriptedChatClient.Text("Rowan sets a hand on Elara's shoulder, and her guard settles.")),
            [("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I catch Elara's eye and steady her."),
                    ("utterances", new[] { "Hold the line. I am right here." }),
                    ("addressed_to", "Elara")))),],
            state: TestWorld.V07State(exitOpen: false,
                TestWorld.RowanV07(), TestWorld.ElaraV07() with { Fear = 3 },
                TestWorld.VarkV07(), TestWorld.SkritV07()),
            rng: rng);

        var turn = await harness.RunTurn("Rowan", round: 1, turn: 1);

        // It costs the whole turn, like any other action the engine accepts.
        Assert.Equal(Orchestration.TurnOutcome.ActionResolved, turn.Outcome);
        Assert.Contains("SteadyAlly", turn.AcceptedAction!, StringComparison.Ordinal);

        Assert.Equal(2, harness.Engine.State.RequireById(TestWorld.ElaraId).Fear);
        Assert.Equal(0, rng.DrawCount);

        var steadied = Assert.Single(harness.Sink.Payloads<AllySteadiedPayload>(TraceEventType.AllySteadied));
        Assert.Equal("Rowan", steadied.ActorName);
        Assert.Equal("Elara", steadied.TargetName);
        Assert.False(steadied.NoEffect);
        Assert.Equal("RecoveredFromScared", steadied.ScaredTransition);
        Assert.Contains("Hold the line", steadied.AssociatedSpeech);
    }

    [Fact]
    public async Task Speech_reaches_the_room_before_the_action_it_belongs_with_whatever_order_the_reply_used()
    {
        // The intent parser emits the calls a prose reply implies in whatever order it read them. A threat
        // is DEFINED as speech aimed at one person, so a `say` dispatched after its `take_action` would leave
        // the engine seeing an action with nothing spoken — and refuse a perfectly good threat.
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                ScriptedChatClient.Text("Rowan's grip shifts on the longsword.")),
            [("Vark", new ScriptedChatClient(
                // Deliberately the wrong way round: the action first, the words second.
                new Microsoft.Extensions.AI.ChatResponse(
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                    [
                        new Microsoft.Extensions.AI.FunctionCallContent("v-1", CharacterTools.TakeActionName,
                            new Dictionary<string, object?> { ["intent"] = "I level my sabre at Rowan and tell him he is next." }),
                        new Microsoft.Extensions.AI.FunctionCallContent("v-2", CharacterTools.SayName,
                            new Dictionary<string, object?> { ["message"] = "You are next, hero." })
                    ]))
                {
                    FinishReason = Microsoft.Extensions.AI.ChatFinishReason.ToolCalls
                }))],
            rng: new ScriptedRng(1));

        await harness.RunTurn("Vark", round: 1, turn: 1);

        Assert.Equal(1, harness.Engine.State.RequireById(TestWorld.RowanId).Fear);
        Assert.Single(harness.Sink.Payloads<IntimidationAttemptedPayload>(TraceEventType.IntimidationAttempted));
    }

    // ------------------------------------------------------------------------------------------
    // Public events happen only on a visible transition
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Crossing_the_threshold_is_a_public_fact_and_a_change_below_it_is_not()
    {
        // Two threats from Vark: the first takes Rowan to 1 (invisible), the second is from Skrit and takes
        // him to 2 (still invisible). No public morale fact should exist for either.
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                ScriptedChatClient.Text("Rowan's jaw tightens.")),
            [("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I threaten Rowan."),
                    ("utterances", new[] { "You are next." }),
                    ("addressed_to", "Rowan"))))],
            rng: new ScriptedRng(1));

        await harness.RunTurn("Vark", round: 1, turn: 1);

        var fear = Assert.Single(harness.Sink.Payloads<FearChangedPayload>(TraceEventType.FearChanged));
        Assert.Equal("None", fear.ScaredTransition);
        Assert.Empty(fear.PublicRecipients);
        Assert.DoesNotContain(harness.Ledger.Facts, f => f.FactType == Knowledge.FactType.CharacterMorale);
    }

    [Fact]
    public async Task Becoming_scared_is_delivered_to_everyone_present_as_a_public_fact()
    {
        var rowan = TestWorld.RowanV07() with { Fear = 2 };
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.IntimidateCharacterName,
                    ("actor", "Vark"), ("target", "Rowan")),
                ScriptedChatClient.Text("Something goes out of Rowan's stance.")),
            [("Vark", new ScriptedChatClient(
                ScriptedChatClient.Call("v-1", CharacterTools.TakeActionName,
                    ("intent", "I threaten Rowan."),
                    ("utterances", new[] { "You are next." }),
                    ("addressed_to", "Rowan"))))],
            state: TestWorld.V07State(exitOpen: false,
                rowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07()),
            rng: new ScriptedRng(1));

        await harness.RunTurn("Vark", round: 1, turn: 1);

        var fear = Assert.Single(harness.Sink.Payloads<FearChangedPayload>(TraceEventType.FearChanged));
        Assert.Equal("BecameScared", fear.ScaredTransition);
        Assert.NotEmpty(fear.PublicRecipients);

        var fact = Assert.Single(harness.Ledger.Facts, f => f.FactType == Knowledge.FactType.CharacterMorale);
        Assert.Equal("Rowan looks scared and increasingly concerned with survival.", fact.Description);

        // Everyone present holds it, including Rowan themselves.
        foreach (var id in new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId })
        {
            Assert.True(harness.Ledger.Knows(id, fact.Id), $"{id} did not learn the public morale fact.");
        }

        // And the description carries no number, no threshold, and no machinery.
        Assert.DoesNotContain("fear", fact.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("3", fact.Description, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // The information boundary
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_character_is_told_their_own_exact_fear_and_the_non_binding_pull()
    {
        var formatter = new WorldStateFormatter(Prompts);
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07() with { Fear = 4 }, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var block = formatter.FormatCharacterSelfState(state.RequireById(TestWorld.RowanId), state);

        Assert.Contains("Fear 4 out of 5", block, StringComparison.Ordinal);
        Assert.Contains("the choice remains yours", block, StringComparison.OrdinalIgnoreCase);

        // Pressure, not orders: every option named is one the world can actually resolve.
        Assert.Contains("escaping", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("surrendering", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_authoritative_snapshot_never_carries_anyone_exact_fear()
    {
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07() with { Fear = 4 }, TestWorld.ElaraV07() with { Fear = 2 },
            TestWorld.VarkV07(), TestWorld.SkritV07());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.Default);

        var snapshot = WorldStateFormatter.FormatAuthoritativeState(engine.State);

        // Rowan is over the threshold and shows it; Elara is under it and shows nothing at all.
        Assert.Contains("Scared", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Fear 4", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fear 2", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fear 0", snapshot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_opponent_asking_about_a_scared_character_learns_only_what_a_face_shows()
    {
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07() with { Fear = 5 }, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.Default);

        var projector = new Orchestration.AnswerFactsProjector(new Knowledge.KnowledgeLedger(), new Orchestration.NarrationLog());
        var facts = projector.Project(engine.State, TestWorld.VarkId, "Does Rowan look afraid?");

        var morale = Assert.Single(facts.PlainlyVisible, f => f.Contains("scared", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Rowan looks scared and increasingly concerned with survival.", morale);

        // The line a rival is given about somebody else's nerve carries no number at all.
        Assert.DoesNotContain(morale, char.IsDigit);
    }

    [Fact]
    public void A_character_asking_about_themselves_is_given_their_own_figure()
    {
        var state = TestWorld.V07State(exitOpen: false,
            TestWorld.RowanV07() with { Fear = 4 }, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.Default);

        var projector = new Orchestration.AnswerFactsProjector(new Knowledge.KnowledgeLedger(), new Orchestration.NarrationLog());
        var facts = projector.Project(engine.State, TestWorld.RowanId, "How am I holding up?");

        Assert.Contains(facts.AboutYourself, f => f.Contains("fear 4 out of 5", StringComparison.OrdinalIgnoreCase));
    }
}
