using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

public sealed class TracingTests
{
    private static ScriptedChatClient AcceptedAttackDungeonMaster() => new(
        ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
            ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
        ScriptedChatClient.Text("Your blade opens a gash across the goblin's shoulder."));

    [Fact]
    public async Task Every_model_call_produces_a_paired_request_and_response_event()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var requests = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest).ToList();
        var responses = harness.Sink.Payloads<ModelResponsePayload>(TraceEventType.ModelResponse).ToList();

        Assert.Equal(3, requests.Count); // character decision, DM adjudication, DM outcome narration
        Assert.Equal(requests.Count, responses.Count);
        Assert.Equal(requests.Select(r => r.CallId), responses.Select(r => r.CallId));
        Assert.Equal(["character.decide", "dm.adjudicate", "dm.narrate.outcome"], requests.Select(r => r.Purpose));
    }

    [Fact]
    public async Task Newly_injected_is_correct_even_when_a_fresh_projection_matches_the_previous_message_count()
    {
        // The Dungeon Master's two calls this turn — dm.adjudicate then dm.narrate.outcome — go through the
        // very same TracingChatClient (one per agent) and each runs on its own fresh, bounded projection: a
        // system prompt plus one user message, so both requests carry exactly two messages. A count-only diff
        // would see "2 == 2" on the second call and report nothing newly injected, even though it is an
        // entirely different conversation with a different system prompt and different content.
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var dmRequests = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .Where(r => r.AgentName == DungeonMasterAgent.AgentIdentifier)
            .ToList();
        var adjudicate = dmRequests.Single(r => r.Purpose == "dm.adjudicate");
        var narrateOutcome = dmRequests.Single(r => r.Purpose == "dm.narrate.outcome");

        Assert.Equal(adjudicate.Messages.Count, narrateOutcome.Messages.Count);
        Assert.Equal(narrateOutcome.Messages.Count, narrateOutcome.NewlyInjected.Count);
        Assert.Contains("damage dealt", narrateOutcome.NewlyInjected[^1].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_request_records_the_full_message_collection_options_and_tool_schemas()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var request = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest).First();

        Assert.Equal("Aric", request.AgentName);
        Assert.Equal("Ollama", request.Provider);
        Assert.Equal("scripted-model", request.ModelId);
        Assert.Contains("You are Aric", request.SystemPrompt);

        // System prompt plus the injected turn context.
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);

        // On an agent's first call nothing has been sent before, so everything counts as new.
        Assert.Equal(2, request.NewlyInjected.Count);
        Assert.Contains("It is your turn", request.NewlyInjected[^1].Text!, StringComparison.Ordinal);

        Assert.Equal(0.5f, request.RequestedOptions.Temperature);
        Assert.Equal(20, request.RequestedOptions.TopK);
        Assert.Equal(42, request.RequestedOptions.Seed);

        // The character's four natural tools: ask_dm, take_action, say, end_turn.
        Assert.Equal(4, request.Tools.Count);
        var askDm = request.Tools.First(t => t.Name == CharacterTools.AskDmName);
        Assert.NotNull(askDm.JsonSchema);
        Assert.True(askDm.JsonSchema!.Value.GetProperty("properties").TryGetProperty("question", out _));
    }

    [Fact]
    public async Task A_model_response_records_content_tool_calls_and_timing()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var response = harness.Sink.Payloads<ModelResponsePayload>(TraceEventType.ModelResponse).First();

        Assert.Equal(ChatFinishReason.ToolCalls.Value, response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal(CharacterTools.TakeActionName, call.Name);
        Assert.Equal("h-1", call.CallId);
        Assert.Equal("I strike.", call.Arguments!["intent"]);
        Assert.True(response.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task Tool_execution_records_the_dispatch_decision_and_the_result_given_back()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var dispatch = harness.Sink.Payloads<ToolCallDispatchPayload>(TraceEventType.ToolCallDispatched)
            .First(d => d.ToolName == DungeonMasterTools.AttackCharacterName);
        Assert.Contains("submitted to the engine", dispatch.DispatchDecision, StringComparison.Ordinal);

        var result = harness.Sink.Payloads<ToolCallResultPayload>(TraceEventType.ToolCallResult)
            .First(r => r.ToolName == DungeonMasterTools.AttackCharacterName);
        Assert.Contains("damage dealt", result.Result!.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_engine_action_records_the_action_and_both_state_snapshots()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));

        Assert.True(engineAction.Accepted);
        Assert.Equal("attack_character", engineAction.ActionType);
        Assert.Equal(8, engineAction.StateBefore.RequireById(TestWorld.MonsterId).Health);
        Assert.Equal(5, engineAction.StateAfter.RequireById(TestWorld.MonsterId).Health);
        Assert.Contains("damage dealt", engineAction.OutcomeSummary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Narration_records_the_state_supplied_and_who_received_it()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var narration = Assert.Single(harness.Sink.Payloads<NarrationPayload>(TraceEventType.Narration));
        Assert.Contains("ROOM: Guard Chamber", narration.StateSuppliedToDungeonMaster, StringComparison.Ordinal);
        Assert.Contains("gash", narration.Narration, StringComparison.Ordinal);

        var delivery = Assert.Single(harness.Sink.Payloads<NarrationDeliveredPayload>(TraceEventType.NarrationDelivered));
        Assert.Equal([TestWorld.HeroId], delivery.DeliveredTo);
    }

    [Fact]
    public async Task A_failing_model_call_is_traced_before_the_exception_propagates()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(),
            new ScriptedChatClient(), // no scripted responses: the first call throws
            new ScriptedChatClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunHeroTurn());

        var error = Assert.Single(harness.Sink.Payloads<ModelErrorPayload>(TraceEventType.ModelError));
        Assert.Equal("Aric", error.AgentName);
        Assert.Contains("ran out of responses", error.Message, StringComparison.Ordinal);

        // The request that preceded the failure is still on record.
        Assert.Single(harness.Sink.OfType(TraceEventType.ModelRequest));
    }

    [Fact]
    public async Task Trace_events_carry_run_position_and_a_monotonic_sequence()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn(round: 3, turn: 5);

        var events = harness.Sink.Events;
        Assert.All(events, e => Assert.Equal("test-run", e.RunId));
        Assert.All(events, e => Assert.Equal(3, e.Round));
        Assert.All(events, e => Assert.Equal(5, e.Turn));
        Assert.Equal(events.Select(e => e.Sequence).Order(), events.Select(e => e.Sequence));

        // Model calls are attributed to the agent that made them, not to whoever's turn it is.
        Assert.Contains(events, e => e.EventType == TraceEventType.ModelRequest && e.Actor == "DungeonMaster");
        Assert.Contains(events, e => e.EventType == TraceEventType.ModelRequest && e.Actor == "Aric");
    }

    [Fact]
    public async Task The_whole_trace_serialises_to_one_json_line_per_event()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var path = Path.Combine(Path.GetTempPath(), $"mm-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new JsonlTraceSink(path))
            {
                foreach (var traceEvent in harness.Sink.Events)
                {
                    sink.Write(traceEvent);
                }
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(harness.Sink.Events.Count, lines.Length);

            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.True(document.RootElement.TryGetProperty("Sequence", out _));
                Assert.True(document.RootElement.TryGetProperty("EventType", out var type));
                Assert.Equal(JsonValueKind.String, type.ValueKind);
            }

            // No provider transport detail, credentials or raw representations reach the file.
            var contents = File.ReadAllText(path);
            Assert.DoesNotContain("RawRepresentation", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", contents, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Context_saturation_is_not_flagged_for_a_provider_that_errors_on_overflow()
    {
        // A reply reporting far fewer input tokens than we sent looks like silent truncation on Ollama,
        // but on OpenAI (which rejects an over-long request rather than truncating) it is only tokenizer
        // estimate noise and must not raise a saturation warning.
        var sink = new RecordingTraceSink();
        var trace = new ExperimentTrace("test-run", sink);
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4o-mini"
        };
        var inner = new ScriptedChatClient(
            ScriptedChatClient.WithInputTokens(ScriptedChatClient.Text("The scene is set."), reportedInputTokens: 40));

        using var client = new TracingChatClient(inner, profile, trace);
        using (client.BeginCall("dm.narrate"))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.System, new string('x', 8000))]);
        }

        Assert.Empty(sink.OfType(TraceEventType.ContextWindowSaturated));
    }

    [Fact]
    public void A_payload_that_cannot_be_serialised_is_reported_rather_than_dropped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mm-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new JsonlTraceSink(path))
            {
                sink.Write(new TraceEvent
                {
                    Sequence = 1,
                    Timestamp = DateTimeOffset.UnixEpoch,
                    RunId = "test-run",
                    Round = 1,
                    Turn = 1,
                    Actor = "harness",
                    EventType = TraceEventType.ModelRequest,
                    Data = new SelfReferencing()
                });
            }

            var line = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("serialisationError", line, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class SelfReferencing
    {
        public SelfReferencing Self => this;
    }
}
