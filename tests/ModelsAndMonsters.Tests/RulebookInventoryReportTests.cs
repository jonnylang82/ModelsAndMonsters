using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.6 report sections — Inventory activity, Rulebook consultations and Context health — rendered from
/// a synthetic run directory, so the report writer is validated deterministically without a live simulation.
/// </summary>
public sealed class RulebookInventoryReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mm-v06report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Render(string traceLines, string finalState)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "trace.jsonl"), traceLines);
        File.WriteAllText(Path.Combine(_directory, "final-state.json"), finalState);
        return File.ReadAllText(RunReportWriter.Write(_directory));
    }

    private static string Line(long seq, int round, int turn, string actor, string eventType, string data) =>
        $"{{\"Sequence\":{seq},\"Timestamp\":\"2026-08-19T00:00:00Z\",\"RunId\":\"r\",\"Round\":{round},\"Turn\":{turn}," +
        $"\"Actor\":\"{actor}\",\"EventType\":\"{eventType}\",\"Data\":{data}}}\n";

    [Fact]
    public void The_inventory_activity_section_counts_transfers_and_shows_ownership_and_provenance()
    {
        var trace =
            Line(1, 1, 1, "Rowan", "InventoryInteraction",
                "{\"ActionType\":\"give_item\",\"ActorName\":\"Rowan\",\"ValidationResult\":\"accepted\",\"TheftSucceeded\":null}") +
            Line(2, 1, 1, "Rowan", "ItemProvenance",
                "{\"ItemName\":\"Flask of Strong Wine\",\"ActionType\":\"give_item\",\"PreviousOwnerOrLocation\":\"hero-rowan\",\"NewOwnerOrLocation\":\"hero-elara\",\"RngInvolved\":false}") +
            Line(3, 2, 5, "Elara", "InventoryInteraction",
                "{\"ActionType\":\"steal_item\",\"ActorName\":\"Elara\",\"ValidationResult\":\"accepted\",\"TheftSucceeded\":true}") +
            Line(4, 2, 5, "Elara", "ItemProvenance",
                "{\"ItemName\":\"Vial of Goblin Salve\",\"ActionType\":\"steal_item\",\"PreviousOwnerOrLocation\":\"goblin-vark\",\"NewOwnerOrLocation\":\"hero-elara\",\"RngInvolved\":true}") +
            Line(5, 3, 9, "Vark", "InventoryInteraction",
                "{\"ActionType\":\"steal_item\",\"ActorName\":\"Vark\",\"ValidationResult\":\"accepted\",\"TheftSucceeded\":false}");

        var finalState =
            "{\"State\":{\"Characters\":[{\"Name\":\"Elara\",\"Inventory\":[{\"Name\":\"Vial of Goblin Salve\"}]}]," +
            "\"Room\":{\"Objects\":[{\"IsGround\":true,\"Name\":\"the floor\",\"Contents\":[{\"Name\":\"Bundle of Damp Rags\"}]}]}}}";

        var report = Render(trace, finalState);

        Assert.Contains("## Inventory activity", report, StringComparison.Ordinal);
        Assert.Contains("| Gives | 1 | 1 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Theft attempts | 2 | 1 | 1 |", report, StringComparison.Ordinal);

        // Final ownership and location.
        Assert.Contains("carried by Elara", report, StringComparison.Ordinal);
        Assert.Contains("on the floor", report, StringComparison.Ordinal);

        // Provenance timeline, in movement order (checked within the timeline section, since item names also
        // appear in the final-ownership section above it).
        var timelineStart = report.IndexOf("Item provenance timeline", StringComparison.Ordinal);
        Assert.True(timelineStart > 0);
        var timeline = report[timelineStart..];
        var flask = timeline.IndexOf("Flask of Strong Wine", StringComparison.Ordinal);
        var salve = timeline.IndexOf("Vial of Goblin Salve", StringComparison.Ordinal);
        Assert.True(flask > 0 && salve > flask, "provenance rows should appear in movement order");
    }

    [Fact]
    public void The_rulebook_consultations_section_summarises_the_stage()
    {
        var trace =
            Line(1, 1, 1, "Rowan", "RulebookConsultation",
                "{\"ConsultationId\":\"c1\",\"Outcome\":\"Supported\",\"CacheHit\":false,\"InputTokens\":1200,\"OutputTokens\":250," +
                "\"CardCount\":2,\"LatencyMs\":9000,\"CitedRules\":[\"combat.attack@v1-abc\"],\"TotalRequestChars\":5600," +
                "\"MaxInputCharsConfigured\":4000,\"Trimmed\":false}") +
            Line(2, 1, 1, "Rowan", "DmAdjudication", "{\"ConsultationId\":\"c1\",\"Category\":\"EngineAccepted\"}") +
            Line(3, 1, 2, "Elara", "RulebookConsultation",
                "{\"ConsultationId\":\"c2\",\"Outcome\":\"Unsupported\",\"CacheHit\":true,\"InputTokens\":0,\"OutputTokens\":0," +
                "\"CardCount\":3,\"LatencyMs\":0,\"CitedRules\":[],\"TotalRequestChars\":2100," +
                "\"MaxInputCharsConfigured\":4000,\"Trimmed\":false}") +
            Line(4, 1, 3, "Vark", "RulebookConsultation",
                "{\"ConsultationId\":\"c3\",\"Outcome\":\"Supported\",\"CacheHit\":false,\"InputTokens\":1100,\"OutputTokens\":200," +
                "\"CardCount\":2,\"LatencyMs\":8000,\"CitedRules\":[\"inventory.steal@v1-xyz\"],\"TotalRequestChars\":5500," +
                "\"MaxInputCharsConfigured\":4000,\"Trimmed\":false}") +
            Line(5, 1, 3, "Vark", "DmAdjudication", "{\"ConsultationId\":\"c3\",\"Category\":\"EngineRejected\"}");

        var report = Render(trace, "{\"State\":{}}");

        Assert.Contains("## Rulebook consultations", report, StringComparison.Ordinal);
        Assert.Contains("Consultations: **3**", report, StringComparison.Ordinal);
        Assert.Contains("1 hit(s), 2 miss(es)", report, StringComparison.Ordinal);
        Assert.Contains("2 supported, 1 unsupported", report, StringComparison.Ordinal);
        // One of the two supported intents was still refused by the engine afterwards.
        Assert.Contains("still refused it 1 of 2 time(s)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_context_health_section_reports_resolver_bounds_and_independence_from_round()
    {
        var trace =
            Line(1, 1, 1, "DungeonMaster", "ModelRequest",
                "{\"AgentName\":\"DungeonMaster\",\"Purpose\":\"dm.adjudicate\",\"Messages\":[{\"Role\":\"system\",\"Text\":\"" + new string('x', 800) + "\"},{\"Role\":\"user\",\"Text\":\"" + new string('y', 400) + "\"}]}") +
            Line(2, 1, 1, "Rowan", "RulebookConsultation",
                "{\"ConsultationId\":\"c1\",\"Outcome\":\"Supported\",\"CacheHit\":false,\"TotalRequestChars\":5600," +
                "\"CardCount\":2,\"MaxInputCharsConfigured\":4000,\"Trimmed\":false,\"CitedRules\":[],\"LatencyMs\":1}") +
            Line(3, 6, 20, "Rowan", "RulebookConsultation",
                "{\"ConsultationId\":\"c2\",\"Outcome\":\"Supported\",\"CacheHit\":false,\"TotalRequestChars\":5600," +
                "\"CardCount\":2,\"MaxInputCharsConfigured\":4000,\"Trimmed\":false,\"CitedRules\":[],\"LatencyMs\":1}");

        var report = Render(trace, "{\"State\":{}}");

        Assert.Contains("## Context health", report, StringComparison.Ordinal);
        Assert.Contains("Maximum DM adjudication request", report, StringComparison.Ordinal);
        Assert.Contains("Maximum Rulebook Resolver request", report, StringComparison.Ordinal);
        // Two rounds, identical resolver request size — the by-round table shows it does not grow.
        Assert.Contains("Resolver request size by round", report, StringComparison.Ordinal);
        Assert.Contains("| 1 | ~5600 |", report, StringComparison.Ordinal);
        Assert.Contains("| 6 | ~5600 |", report, StringComparison.Ordinal);
    }
}
