using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Environmental-object destruction (v0.9's <c>damage_environmental_object</c>) driven through the real
/// pipeline — rulebook retrieval, the resolver, the Dungeon Master's tool call, and the engine — rather than
/// only unit-tested at the engine level (<see cref="CoverEngineTests"/>). v0.10 asks specifically for
/// reachability to be proven end to end: a character's intent to batter down cover must actually reach the
/// deterministic engine path, and a container must never be silently accepted in its place.
/// </summary>
public sealed class CoverOrchestrationTests
{
    [Fact]
    public async Task An_intent_to_batter_down_cover_reaches_the_engine_through_the_real_rulebook_pipeline()
    {
        var damageVersion = new RuleCatalog().Find("environment.damage-object")!.Version;
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.DamageEnvironmentalObjectName,
                    ("actor", "Rowan"), ("object", "Overturned Mill Workbench")),
                ScriptedChatClient.Text("Rowan's blade crashes into the workbench, splintering it apart.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    (CharacterTools.IntentParameter, "I strike the Overturned Mill Workbench with my longsword to batter it down."))))),
            initialState: TestWorld.TwoVsTwoStateWithCover(TestWorld.Workbench(maxDurability: 1)),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                $$"""
                { "supported": true, "candidateActions": ["damage_environmental_object"],
                  "citedRules": [ { "ruleId": "environment.damage-object", "version": "{{damageVersion}}" } ] }
                """)));

        var healthBefore = harness.Engine.State.Characters.ToDictionary(c => c.Id, c => c.Health);

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        // The resolver's candidate action reached the Dungeon Master as an actually offered tool...
        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Contains(DungeonMasterTools.DamageEnvironmentalObjectName, consultation.DmCandidateTools);
        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains(DungeonMasterTools.DamageEnvironmentalObjectName, dmTools);

        // ...and the engine actually destroyed the object through its deterministic damage path, not a
        // character (no character damage is invented by striking the object — every character's health is
        // exactly what it was before the blow).
        var cover = harness.Engine.State.Room.Objects.OfType<CoverObject>().Single();
        Assert.Equal(EnvironmentalObjectState.Destroyed, cover.State);
        Assert.All(harness.Engine.State.Characters, c => Assert.Equal(healthBefore[c.Id], c.Health));
    }
}
