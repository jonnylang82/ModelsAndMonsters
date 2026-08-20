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
    private readonly Dictionary<TraceEventType, int> _counts = [];
    private readonly Lock _countGate = new();
    private long _sequence;
    private int _reasoningOnlyTruncations;

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

    /// <summary>How many events of a type were emitted. Used for end-of-run warnings.</summary>
    public int CountOf(TraceEventType eventType)
    {
        lock (_countGate)
        {
            return _counts.GetValueOrDefault(eventType);
        }
    }

    /// <summary>
    /// Records a truncation where the whole output budget went on invisible reasoning, leaving no tool call
    /// and no visible reply (<c>ModelTruncatedPayload.ReasoningOnly</c>) — the one truncation cause that
    /// "disable Thinking, or raise MaxOutputTokens" actually fixes. Tallied separately from the general
    /// <see cref="TraceEventType.ModelResponseTruncated"/> count so the end-of-run warning does not give that
    /// advice for an ordinary context-exhaustion truncation, which needs the opposite remedy: send less, not
    /// reserve more.
    /// </summary>
    public void NoteReasoningOnlyTruncation()
    {
        lock (_countGate)
        {
            _reasoningOnlyTruncations++;
        }
    }

    /// <summary>How many truncations were the reasoning-only kind <see cref="NoteReasoningOnlyTruncation"/> tracks.</summary>
    public int ReasoningOnlyTruncationCount
    {
        get
        {
            lock (_countGate)
            {
                return _reasoningOnlyTruncations;
            }
        }
    }

    public void Emit(TraceEventType eventType, object? data = null, string? actor = null)
    {
        lock (_countGate)
        {
            _counts[eventType] = _counts.GetValueOrDefault(eventType) + 1;
        }

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
