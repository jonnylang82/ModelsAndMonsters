using System.Net;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The prose-fallback intent parser driven through the real <see cref="TurnCoordinator"/>: a reply written
/// as prose is parsed into the say/ask/take_action calls it implies and all are dispatched in one pass —
/// speech before the turn-ending action — instead of nudging the character one call at a time.
/// </summary>
public sealed class IntentParserOrchestrationTests
{
    /// <summary>A parser model that is simply unreachable — every call throws, as an exhausted retry does.</summary>
    private sealed class UnreachableChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new HttpRequestException("simulated provider failure", inner: null, HttpStatusCode.InternalServerError);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task A_fused_prose_reply_is_parsed_into_a_say_and_an_action_and_both_are_dispatched()
    {
        var harness = new OrchestrationHarness(
            dungeonMasterClient: new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric's blade bites into Grik's shoulder.")),
            heroClient: new ScriptedChatClient(
                // A fused reply: an action AND a spoken line, written as prose with no tool call.
                ScriptedChatClient.Text("I raise my sword and step in to guard. \"Grik, face me!\"")),
            monsterClient: new ScriptedChatClient(),
            // The parser emits the two calls out of order to prove the coordinator reorders speech first.
            intentParserClient: new ScriptedChatClient(
                ScriptedChatClient.Calls(
                    ScriptedChatClient.CallContent("p1", CharacterTools.TakeActionName,
                        ("intent", "I raise my sword and step in to guard.")),
                    ScriptedChatClient.CallContent("p2", CharacterTools.SayName,
                        ("message", "Grik, face me!")))));

        var result = await harness.RunHeroTurn();

        // The parse was recorded, with speech ordered before the turn-ending action.
        var parsed = Assert.Single(harness.Sink.Payloads<IntentParsedPayload>(TraceEventType.IntentParsed));
        Assert.Equal([CharacterTools.SayName, CharacterTools.TakeActionName], parsed.ExtractedCalls);

        // The say was delivered as public speech...
        Assert.Contains(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech),
            s => s.Message == "Grik, face me!");

        // ...and the action was adjudicated and resolved the turn in the same pass — no nudge, no loop.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    [Fact]
    public async Task A_prose_reply_the_parser_finds_nothing_callable_in_falls_back_to_a_single_action()
    {
        var harness = new OrchestrationHarness(
            dungeonMasterClient: new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric strikes home.")),
            heroClient: new ScriptedChatClient(
                ScriptedChatClient.Text("I bring my sword down on the goblin.")),
            monsterClient: new ScriptedChatClient(),
            // The parser returns nothing callable; the coordinator treats the whole reply as one take_action.
            intentParserClient: new ScriptedChatClient(ScriptedChatClient.Text("")));

        var result = await harness.RunHeroTurn();

        var parsed = Assert.Single(harness.Sink.Payloads<IntentParsedPayload>(TraceEventType.IntentParsed));
        Assert.Equal([CharacterTools.TakeActionName], parsed.ExtractedCalls);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    // ---------------------------------------------------------------------------------------------
    // When the parser itself cannot be reached
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_parser_that_cannot_be_reached_does_not_end_the_run()
    {
        // The failure that killed a live run at round 5. Ollama's own tool-call parser rejected qwen's XML
        // and returned 500 (ollama#14834); the transient retry re-sent twice more and, at the old near-greedy
        // retry temperature, drew the same malformed reply each time. The exception then unwound out of the
        // parse, out of the turn, out of the round loop, and ended the run — because an OPTIONAL component
        // had no failure path at all. The turn must survive: the character simply replied in prose, which is
        // what every character did before the parser existed.
        var parser = new UnreachableChatClient();
        var harness = new OrchestrationHarness(
            dungeonMasterClient: new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric strikes home.")),
            heroClient: new ScriptedChatClient(
                // Prose first, so the parser is reached and fails; then a clean call after the nudge.
                ScriptedChatClient.Text("I bring my sword down on the goblin."),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName,
                    ("intent", "I bring my sword down on the goblin."))),
            monsterClient: new ScriptedChatClient(),
            intentParserClient: parser);

        var result = await harness.RunHeroTurn();

        // The parser was tried and exhausted its retries...
        Assert.Equal(3, parser.CallCount);

        // ...and the turn still resolved, on the nudge path.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Empty(harness.Sink.Payloads<IntentParsedPayload>(TraceEventType.IntentParsed));
    }

    [Fact]
    public async Task The_harness_records_why_it_fell_back_rather_than_leaving_a_gap()
    {
        // The individual model failures are traced by the client either way. What was missing is the harness
        // DECISION that followed them, without which the trace shows three errors and then a nudge with
        // nothing connecting the two.
        var harness = new OrchestrationHarness(
            dungeonMasterClient: new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric strikes home.")),
            heroClient: new ScriptedChatClient(
                ScriptedChatClient.Text("I bring my sword down on the goblin."),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike."))),
            monsterClient: new ScriptedChatClient(),
            intentParserClient: new UnreachableChatClient());

        await harness.RunHeroTurn();

        var error = Assert.Single(
            harness.Sink.Payloads<ToolCallErrorPayload>(TraceEventType.ToolCallError),
            e => e.AgentName == IntentParser.AgentIdentifier);
        Assert.Contains("could not be reached", error.Error, StringComparison.Ordinal);
        Assert.Contains("nudge path", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_run_is_still_cancelled_rather_than_swallowed_as_a_parser_failure()
    {
        // The catch must not turn a user cancellation into "the parser is unavailable, carry on".
        var harness = new OrchestrationHarness(
            dungeonMasterClient: new ScriptedChatClient(),
            heroClient: new ScriptedChatClient(ScriptedChatClient.Text("I bring my sword down on the goblin.")),
            monsterClient: new ScriptedChatClient(),
            intentParserClient: new CancellingChatClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunHeroTurn());
    }

    /// <summary>A parser model that reports the operation was cancelled rather than that it failed.</summary>
    private sealed class CancellingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
