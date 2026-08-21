using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// A demand — "give me that or else" — carried alongside one mechanical deed (v0.10). Characters can already
/// make demands through structured public speech; this is not a new authoritative action. These tests prove
/// the demand changes nothing by itself: the one deed it rides with resolves exactly as it would alone, no
/// item moves, no surrender offer or agreement appears, and <c>intimidate_character</c> remains the only
/// action that performs a fear check.
/// </summary>
public sealed class DemandOrchestrationTests
{
    [Fact]
    public async Task An_attack_carrying_a_demand_resolves_as_one_attack_and_the_demand_changes_nothing()
    {
        var attackVersion = new RuleCatalog().Find("combat.attack")!.Version;
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan's longsword cuts across Vark's guard as he shouts his demand.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    (CharacterTools.IntentParameter, "I attack Vark."),
                    (CharacterTools.UtterancesParameter, "Drop the salve, Vark, or this ends badly for you!"))))),
            initialState: TestWorld.V07State(),
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                $$"""
                { "supported": true, "candidateActions": ["attack_character"],
                  "citedRules": [ { "ruleId": "combat.attack", "version": "{{attackVersion}}" } ] }
                """)));

        var result = await harness.RunTurn("Rowan", round: 1, turn: 1);

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        // Exactly one engine action resolved: the attack itself, accepted, and nothing else.
        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.Equal(DungeonMasterTools.AttackCharacterName, engineAction.ActionType);
        Assert.True(engineAction.Accepted);
        Assert.IsType<AttackOutcome>(engineAction.Outcome);

        var vark = harness.Engine.State.RequireById(TestWorld.VarkId);
        Assert.Contains(vark.Inventory, i => i.Id == "goblin-salve"); // the demanded item never moved
        Assert.Empty(harness.Engine.State.SurrenderOffers); // the demand created no offer
        Assert.Empty(harness.Engine.State.SurrenderAgreements);

        // The demand was heard as speech, not silently dropped.
        Assert.Single(harness.Sink.OfType(TraceEventType.CharacterSpeech));
    }
}
