using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The end-of-round recap: one artistic-but-truthful line the Dungeon Master speaks once a round's turns are
/// done. These drive the real <see cref="Orchestration.TurnCoordinator"/> against a scripted model, exercising
/// the three guarantees that matter — it is grounded ONLY in that round's public event narrations, it is spoken
/// to the audience but never delivered into any character's knowledge, and a round with nothing to recap makes
/// no model call at all.
/// </summary>
public sealed class RoundSummaryTests
{
    private static OrchestrationHarness Harness(params ChatResponse[] dmResponses) =>
        new(new ScriptedChatClient(dmResponses), new ScriptedChatClient(), new ScriptedChatClient());

    private static string AllText(IReadOnlyList<ChatMessage> request) =>
        string.Join("\n", request.Select(m => m.Text));

    [Fact]
    public async Task The_recap_is_grounded_in_this_rounds_events_spoken_to_the_room_and_never_stored()
    {
        const string line = "The cellar rang with the first true blows as Rowan's blade opened the captain's arm.";
        var harness = Harness(ScriptedChatClient.Text(line));

        harness.Coordinator.BeginRound();
        harness.NarrationLog.Record("action-outcome", "Rowan's longsword opens a gash across Vark's arm.");
        harness.NarrationLog.Record("action-outcome", "Vark's sabre rings off Rowan's mail and draws nothing.");
        harness.NarrationLog.RecordSpeech("goblin-vark", "Vark says: \"You'll bleed for that.\"");
        var entriesBefore = harness.NarrationLog.Entries.Count;

        await harness.Coordinator.SummariseRoundAsync(1, CancellationToken.None);

        // It made exactly one model call, and that call's material carried this round's event narrations —
        // and only those. The spoken line is delivered to the room through the console.
        Assert.Equal(1, harness.DungeonMasterClient.CallCount);
        var material = AllText(harness.DungeonMasterClient.Requests[0]);
        Assert.Contains("opens a gash across Vark's arm", material, StringComparison.Ordinal);
        Assert.Contains("rings off Rowan's mail", material, StringComparison.Ordinal);
        // Speech is not an event: the recap summarises what happened, not what was said.
        Assert.DoesNotContain("You'll bleed for that", material, StringComparison.Ordinal);
        Assert.Contains($"round-summary:1:{line}", harness.Console.Lines);

        // Audience-only: the recap is NOT written to the narration log, so it can never be delivered into a
        // character's knowledge on a later turn — the count is exactly what the round itself recorded.
        Assert.Equal(entriesBefore, harness.NarrationLog.Entries.Count);
    }

    [Fact]
    public async Task A_round_with_no_events_to_recap_makes_no_model_call_and_says_nothing()
    {
        // A client with no scripted responses: if the recap tried to call it, the call would throw. It must not.
        var harness = Harness();

        harness.Coordinator.BeginRound();
        // Only speech this round — nobody acted. There is nothing to recap and nothing to invent.
        harness.NarrationLog.RecordSpeech("hero-rowan", "Rowan says: \"Hold the line.\"");

        await harness.Coordinator.SummariseRoundAsync(1, CancellationToken.None);

        Assert.Equal(0, harness.DungeonMasterClient.CallCount);
        Assert.DoesNotContain(harness.Console.Lines, l => l.StartsWith("round-summary:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_recap_covers_only_its_own_round_not_the_one_before()
    {
        var harness = Harness(
            ScriptedChatClient.Text("Round one line."),
            ScriptedChatClient.Text("Round two line."));

        harness.Coordinator.BeginRound();
        harness.NarrationLog.Record("action-outcome", "First-round blow lands.");
        await harness.Coordinator.SummariseRoundAsync(1, CancellationToken.None);

        harness.Coordinator.BeginRound();
        harness.NarrationLog.Record("action-outcome", "Second-round blow lands.");
        await harness.Coordinator.SummariseRoundAsync(2, CancellationToken.None);

        // BeginRound re-anchors the window, so round two's material carries only round two's event.
        var secondMaterial = AllText(harness.DungeonMasterClient.Requests[1]);
        Assert.Contains("Second-round blow", secondMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain("First-round blow", secondMaterial, StringComparison.Ordinal);
    }
}
