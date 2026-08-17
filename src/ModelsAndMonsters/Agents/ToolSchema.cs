using System.Text.Json;

namespace ModelsAndMonsters.Agents;

/// <summary>Helper for hand-authored tool JSON schemas.</summary>
internal static class ToolSchema
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
