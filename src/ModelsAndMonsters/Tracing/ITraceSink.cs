namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Destination for experiment trace events.
/// </summary>
/// <remarks>
/// The experiment trace is a data output of the application and is deliberately separate from
/// operational <c>ILogger</c> logging. Nothing written here should reach the console.
/// </remarks>
public interface ITraceSink : IDisposable
{
    void Write(TraceEvent traceEvent);
}
