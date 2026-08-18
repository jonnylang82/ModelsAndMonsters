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
}
