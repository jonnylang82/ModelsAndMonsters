using System.Text;
using System.Text.Json;

namespace ModelsAndMonsters.Tracing;

/// <summary>
/// Append-only JSON Lines trace file. One event per line, flushed immediately so a crashed run still
/// leaves everything that happened before the failure on disk.
/// </summary>
public sealed class JsonlTraceSink : ITraceSink
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();
    private bool _disposed;

    public JsonlTraceSink(string filePath)
    {
        FilePath = filePath;
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    public string FilePath { get; }

    public void Write(TraceEvent traceEvent)
    {
        ArgumentNullException.ThrowIfNull(traceEvent);

        string line;
        try
        {
            line = JsonSerializer.Serialize(traceEvent, TraceJson.Compact);
        }
        catch (Exception ex)
        {
            // A payload that will not serialise must not take the run down, and must not silently
            // vanish either: record the failure in the stream so the gap is visible.
            line = JsonSerializer.Serialize(
                new TraceEvent
                {
                    Sequence = traceEvent.Sequence,
                    Timestamp = traceEvent.Timestamp,
                    RunId = traceEvent.RunId,
                    Round = traceEvent.Round,
                    Turn = traceEvent.Turn,
                    Actor = traceEvent.Actor,
                    EventType = traceEvent.EventType,
                    Data = new { serialisationError = ex.Message, originalEventType = traceEvent.EventType.ToString() }
                },
                TraceJson.Compact);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
