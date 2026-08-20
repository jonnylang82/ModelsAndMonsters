using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// When a character's history is folded into a running summary, and what the budget is measured against.
/// </summary>
/// <remarks>
/// The budget's whole purpose is "the request I am about to send fits inside the window". That is knowable
/// only once the turn's context has been injected, which is why summarisation happens at turn START. Doing it
/// between turns measures the history alone and then lets the next turn append a fresh context on top of a
/// history that was just declared to fit — which is exactly how a live run put 7,967 tokens of Elara into an
/// 8,192-token window and had her reply truncated at 225 tokens.
/// </remarks>
public sealed class HistoryBudgetTests
{
    private const string Recap = "You traded blows with the goblins and nobody has fallen yet.";

    /// <summary>A budget low enough that any real history exceeds it, so the trim is forced deterministically.</summary>
    private static HarnessOptions Limits() => new()
    {
        MaxQuestionsPerTurn = 2,
        MaxActionAttemptsPerTurn = 3,
        MaxModelCallsPerTurn = 8,
        SummariseHistory = true,
        HistoryTokenBudget = 200,
        RecentTurnsKeptFull = 1,
        UseIntentParser = false
    };

    private static MultiActorHarness Harness(ScriptedChatClient summariser) =>
        new(new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.DefendName, ("actor", "Rowan")),
                ScriptedChatClient.Text("Rowan sets his feet."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.DefendName, ("actor", "Rowan")),
                ScriptedChatClient.Text("Rowan holds his guard.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I set my feet and keep my guard up.")),
                ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I keep my guard up."))))),
            Limits(),
            historySummariserClient: summariser);

    [Fact]
    public async Task The_summary_is_in_place_BEFORE_the_character_is_asked_to_decide()
    {
        // The ordering is the whole fix. If summarisation ran at the end of the previous turn instead, the
        // recap would still be present — but the turn context added afterwards would never have been counted
        // against the budget. Asserting the recap is in the request the character actually answers is what
        // distinguishes the two.
        var harness = Harness(new ScriptedChatClient(ScriptedChatClient.Text(Recap)));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        var summarised = Assert.Single(harness.Sink.Payloads<HistorySummarisedPayload>(TraceEventType.HistorySummarised));
        Assert.Equal("Rowan", summarised.CharacterName);

        // The second turn's decide request already carries the recap.
        var secondTurnRequest = harness.Client("Rowan").Requests[^1];
        var text = string.Join("\n", secondTurnRequest.Select(m => m.Text));
        Assert.Contains(Recap, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_budget_is_measured_against_a_history_that_already_includes_the_turn_context()
    {
        // The specific thing that was being missed. The turn context — self-state, knowledge, pending
        // narration — is over a thousand tokens, and it used to arrive after the budget had been enforced.
        var harness = Harness(new ScriptedChatClient(ScriptedChatClient.Text(Recap)));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        var summarised = Assert.Single(harness.Sink.Payloads<HistorySummarisedPayload>(TraceEventType.HistorySummarised));

        // The turn context is the largest single thing a turn adds; if it were not counted, the "before"
        // figure would be a small fraction of this.
        var turnStarts = harness.Sink.Payloads<TurnStartedPayload>(TraceEventType.TurnStarted).ToList();
        var contextTokens = turnStarts[^1].SelfStateBlock.Length / 5;

        Assert.True(summarised.EstimatedTokensBefore > contextTokens,
            $"The budget saw {summarised.EstimatedTokensBefore} tokens, which does not even cover the " +
            $"~{contextTokens} tokens of turn context that was injected before it ran.");
    }

    [Fact]
    public async Task The_turn_context_itself_is_never_folded_into_the_recap()
    {
        // The trim boundary falls on the most recent user message, which IS the context just injected. If it
        // ever fell earlier, the character would be asked to act on a turn whose situation had been summarised
        // away — the one outcome that would make moving this to turn start a bad trade.
        var harness = Harness(new ScriptedChatClient(ScriptedChatClient.Text(Recap)));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        var text = string.Join("\n", harness.Client("Rowan").Requests[^1].Select(m => m.Text));

        // The second turn's own context survives verbatim alongside the recap.
        Assert.Contains("It is your turn.", text, StringComparison.Ordinal);
        Assert.Contains("Rowan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_recap_leaves_the_history_alone_rather_than_dropping_it()
    {
        // Losing older turns for nothing is worse than carrying them: the summariser gets to fail safe.
        var harness = Harness(new ScriptedChatClient(ScriptedChatClient.Text("   ")));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Empty(harness.Sink.Payloads<HistorySummarisedPayload>(TraceEventType.HistorySummarised));
    }

    [Fact]
    public async Task Nothing_is_summarised_when_there_is_no_older_turn_to_fold()
    {
        // On the very first turn there is exactly one turn to keep and nothing behind it, so the trim is a
        // no-op however small the budget — and it must not consume a summariser call finding that out.
        var summariser = new ScriptedChatClient(ScriptedChatClient.Text(Recap));
        var harness = Harness(summariser);

        await harness.RunTurn("Rowan", round: 1, turn: 1);

        Assert.Empty(harness.Sink.Payloads<HistorySummarisedPayload>(TraceEventType.HistorySummarised));
        Assert.Equal(0, summariser.CallCount);
    }
}
