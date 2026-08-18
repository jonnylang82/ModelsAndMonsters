namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Fans each trace event out to several sinks — the authoritative JSONL file plus, for a live view, a
/// push sink feeding a UI. Sinks are written in order, so the file (registered first) is always written
/// before any best-effort live sink. A live sink is expected to swallow its own failures so a disconnected
/// viewer can never disrupt the file trace or the run.
/// </summary>
public sealed class CompositeTraceSink : ITraceSink
{
    private readonly IReadOnlyList<ITraceSink> _sinks;

    public CompositeTraceSink(params ITraceSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
    }

    public void Write(TraceEvent traceEvent)
    {
        foreach (var sink in _sinks)
        {
            sink.Write(traceEvent);
        }
    }

    public void Dispose()
    {
        foreach (var sink in _sinks)
        {
            sink.Dispose();
        }
    }
}
