using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>Tidies model prose before it reaches a character or the console.</summary>
public static partial class ModelText
{
    /// <summary>
    /// True when text looks like a tool call the model wrote as prose instead of calling.
    /// </summary>
    /// <remarks>
    /// Some local models emit their tool call as a JSON blob in the message content. That JSON must
    /// never reach a character as if it were narration, so it is detected and handled rather than shown.
    /// </remarks>
    public static bool LooksStructured(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pulls a named string field out of the first JSON object embedded in text, searching nested
    /// objects. Used to recover the DM's "reason" when it emits a reject_action as text rather than a
    /// tool call, so the explanation is preserved instead of leaking raw JSON.
    /// </summary>
    public static string? TryExtractJsonField(string? text, params string[] fieldNames)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            return FindStringField(document.RootElement, fieldNames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindStringField(JsonElement element, string[] fieldNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        fieldNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }

                    var nested = FindStringField(property.Value, fieldNames);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringField(item, fieldNames);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Returns the assistant's prose. Reasoning models that emit literal think blocks in their text
    /// have them stripped: that content is the model's private working, not something a character in
    /// the world perceived. The unedited text is still recorded in the trace.
    /// </summary>
    public static string Clean(ChatResponse response) => Clean(response.Text);

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var cleaned = ThinkBlock().Replace(text, "");

        // An unterminated think block means the whole reply is working-out; keep what follows if any.
        var unterminated = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (unterminated >= 0)
        {
            cleaned = cleaned[..unterminated];
        }

        return cleaned.Trim();
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
