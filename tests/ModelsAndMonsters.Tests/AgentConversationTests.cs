using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Tests;

public sealed class AgentConversationTests
{
    [Fact]
    public void CompactTurn_drops_prose_fails_and_nudges_but_keeps_calls_and_results()
    {
        var convo = new AgentConversation("Rowan", "system prompt");

        // A prior, already-resolved turn: its clean call + result must survive untouched.
        var priorCall = new FunctionCallContent("p1", "take_action", new Dictionary<string, object?> { ["intent"] = "I guard" });
        convo.Append(new ChatMessage(ChatRole.Assistant, [priorCall]));
        convo.AppendToolResult("p1", "You raise your guard.");

        // This turn begins: inject context, then mark.
        convo.AppendUser("It is your turn. Here is what you see...");
        var mark = convo.Count;

        // The turn's messy middle: two prose replies that produced no call, each followed by a nudge.
        convo.Append(new ChatMessage(ChatRole.Assistant, "I wade forward and shout to Elara to stay close."));
        convo.AppendUser("You tried to speak; call say(message).");
        convo.Append(new ChatMessage(ChatRole.Assistant, "\"Elara, stay close!\" I call, raising my blade."));
        convo.AppendUser("You tried to speak; call say(message).");

        // Then the clean resolution: a say and an action, each with a result.
        var say = new FunctionCallContent("c1", "say", new Dictionary<string, object?> { ["message"] = "Elara, stay close!" });
        convo.Append(new ChatMessage(ChatRole.Assistant, [say]));
        convo.AppendToolResult("c1", "Your words carry across the room.");
        var act = new FunctionCallContent("c2", "take_action", new Dictionary<string, object?> { ["intent"] = "I raise my blade to guard Elara." });
        convo.Append(new ChatMessage(ChatRole.Assistant, [act]));
        convo.AppendToolResult("c2", "You step in front of Elara.");

        var before = convo.Count;
        convo.CompactTurn(mark);

        // Four messages of scaffolding (2 prose fails + 2 nudges) are gone; nothing else is.
        Assert.Equal(before - 4, convo.Count);

        // Prior turn survives in full.
        Assert.True(convo.Messages[0].Contents.OfType<FunctionCallContent>().Any());
        Assert.Equal(ChatRole.Tool, convo.Messages[1].Role);

        // The injected turn context is kept, and it is the only surviving user message (no nudges remain).
        Assert.Equal(1, convo.Messages.Count(m => m.Role == ChatRole.User));

        // Every retained assistant tool call still has a matching tool result — the history is valid to send.
        var callIds = convo.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId).ToHashSet();
        var resultIds = convo.Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet();
        Assert.Equal(callIds, resultIds);
        Assert.Contains("c1", callIds);
        Assert.Contains("c2", callIds);

        // No prose-only assistant message survives.
        Assert.DoesNotContain(convo.Messages, m => m.Role == ChatRole.Assistant && !m.Contents.OfType<FunctionCallContent>().Any());
    }

    [Fact]
    public void CompactTurn_is_a_no_op_when_the_turn_added_only_clean_calls()
    {
        var convo = new AgentConversation("Elara", "system");
        convo.AppendUser("Your turn.");
        var mark = convo.Count;
        var call = new FunctionCallContent("c1", "take_action", new Dictionary<string, object?> { ["intent"] = "I strike." });
        convo.Append(new ChatMessage(ChatRole.Assistant, [call]));
        convo.AppendToolResult("c1", "You strike.");

        convo.CompactTurn(mark);

        Assert.Equal(3, convo.Count);
    }

    // A resolved turn in post-prune shape: one injected user message, one assistant tool call, one result.
    private static void AppendResolvedTurn(AgentConversation convo, int n)
    {
        convo.AppendUser($"Turn {n}: here is what you see.");
        convo.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent($"c{n}", "take_action", new Dictionary<string, object?> { ["intent"] = $"I act {n}." })]));
        convo.AppendToolResult($"c{n}", $"You did {n}.");
    }

    [Fact]
    public void TryPlanSummaryTrim_keeps_the_last_N_turns_and_folds_the_rest_preserving_pairing()
    {
        var convo = new AgentConversation("Rowan", "system");
        for (var t = 1; t <= 3; t++) AppendResolvedTurn(convo, t);

        Assert.True(convo.TryPlanSummaryTrim(keepRecentTurns: 1, out var boundary, out var older));
        // The older history to summarise covers turns 1–2; turn 3 is kept full.
        Assert.Contains("Turn 1", older);
        Assert.Contains("Turn 2", older);
        Assert.DoesNotContain("Turn 3", older);

        convo.ApplySummary(boundary, "Earlier: you fought through two exchanges.");

        Assert.True(convo.HasSummary);
        // [summary] + turn 3 (context + call + result) = 4 messages.
        Assert.Equal(4, convo.Count);
        Assert.Contains(convo.Messages, m => m.Role == ChatRole.User && m.Text.Contains("Earlier:", StringComparison.Ordinal));
        Assert.Contains(convo.Messages, m => m.Text.Contains("Turn 3", StringComparison.Ordinal));

        // The one kept tool call still has its matching result — the history stays valid to send.
        var callIds = convo.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId).ToHashSet();
        var resultIds = convo.Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Select(r => r.CallId).ToHashSet();
        Assert.Equal(callIds, resultIds);
        Assert.Contains("c3", callIds);
    }

    [Fact]
    public void TryPlanSummaryTrim_is_false_when_there_are_no_more_turns_than_are_kept()
    {
        var convo = new AgentConversation("Elara", "system");
        AppendResolvedTurn(convo, 1);

        Assert.False(convo.TryPlanSummaryTrim(keepRecentTurns: 2, out _, out _));
    }

    [Fact]
    public void A_second_summary_folds_the_first_one_in_rather_than_summarising_a_summary()
    {
        var convo = new AgentConversation("Vark", "system");
        for (var t = 1; t <= 3; t++) AppendResolvedTurn(convo, t);
        Assert.True(convo.TryPlanSummaryTrim(1, out var firstBoundary, out _));
        convo.ApplySummary(firstBoundary, "Recap one.");

        for (var t = 4; t <= 5; t++) AppendResolvedTurn(convo, t);

        Assert.True(convo.TryPlanSummaryTrim(1, out _, out var older));
        // The existing running summary is part of what the next summary must fold in.
        Assert.Contains("Recap one.", older, StringComparison.Ordinal);
        // ...and the summary is not counted as a turn, so only turn 5 would be kept.
        Assert.Contains("Turn 4", older, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn 5", older, StringComparison.Ordinal);
    }
}
