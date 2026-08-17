namespace ModelsAndMonsters.Tracing;

/// <summary>
/// One line of the experiment trace.
/// </summary>
/// <remarks>
/// <see cref="Data"/> is declared as <see cref="object"/> so System.Text.Json serialises the concrete
/// payload record. Payload types live in <c>TracePayloads.cs</c>.
/// </remarks>
public sealed record TraceEvent
{
    public required long Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string RunId { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    /// <summary>The agent or subsystem the event is attributed to, e.g. "Aric", "DungeonMaster", "harness".</summary>
    public required string Actor { get; init; }

    public required TraceEventType EventType { get; init; }

    public object? Data { get; init; }
}
