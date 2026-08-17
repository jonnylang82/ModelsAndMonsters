namespace ModelsAndMonsters.Tracing;

/// <summary>
/// The single entry point for writing experiment trace events. Owns the sequence counter and the
/// current position in the simulation (round / turn / actor) so call sites do not have to pass it.
/// </summary>
/// <remarks>
/// Orchestration is strictly sequential, so a simple mutable position is sufficient and keeps the
/// trace call sites short. Writes are still guarded because the tracing chat client may complete on
/// a different thread.
/// </remarks>
public sealed class ExperimentTrace
{
    private readonly ITraceSink _sink;
    private readonly TimeProvider _timeProvider;
    private long _sequence;

    public ExperimentTrace(string runId, ITraceSink sink, TimeProvider? timeProvider = null)
    {
        RunId = runId;
        _sink = sink;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RunId { get; }

    public int Round { get; private set; }

    public int Turn { get; private set; }

    public string Actor { get; private set; } = "harness";

    /// <summary>Total events emitted so far. Useful in tests and in the run summary.</summary>
    public long EventCount => Interlocked.Read(ref _sequence);

    public void SetPosition(int round, int turn, string actor)
    {
        Round = round;
        Turn = turn;
        Actor = actor;
    }

    public void SetActor(string actor) => Actor = actor;

    public void Emit(TraceEventType eventType, object? data = null, string? actor = null)
    {
        var traceEvent = new TraceEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Timestamp = _timeProvider.GetUtcNow(),
            RunId = RunId,
            Round = Round,
            Turn = Turn,
            Actor = actor ?? Actor,
            EventType = eventType,
            Data = data
        };

        _sink.Write(traceEvent);
    }
}
