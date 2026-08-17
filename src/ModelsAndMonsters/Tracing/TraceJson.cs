using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelsAndMonsters.Tracing;

/// <summary>Shared JSON settings for every trace and run artefact.</summary>
public static class TraceJson
{
    /// <summary>Single-line settings used for trace.jsonl.</summary>
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    /// <summary>Indented settings used for run.json and final-state.json.</summary>
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keeps prose readable in the trace files instead of escaping quotes and apostrophes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}
