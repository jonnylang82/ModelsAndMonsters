using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Reads arguments out of a model's tool call.
/// </summary>
/// <remarks>
/// Arguments arrive as loosely typed values that differ by provider (raw strings, boxed primitives, or
/// <see cref="JsonElement"/>). Reading is deliberately forgiving about casing and about a single
/// unnamed argument, because small local models get those details wrong often enough to matter — but
/// every reading is visible in the trace, so leniency never hides what actually happened.
/// </remarks>
public static class ToolArguments
{
    public static string? GetString(FunctionCallContent call, string parameterName)
    {
        var arguments = call.Arguments;
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        if (TryGetCaseInsensitive(arguments, parameterName, out var value))
        {
            return Stringify(value);
        }

        // Some models emit the right value under the wrong key. With exactly one argument there is no
        // ambiguity about what they meant.
        if (arguments.Count == 1)
        {
            return Stringify(arguments.Values.First());
        }

        return null;
    }

    public static string GetRequiredString(FunctionCallContent call, string parameterName) =>
        GetString(call, parameterName)
            ?? throw new ArgumentException($"Tool call '{call.Name}' is missing required argument '{parameterName}'.");

    private static bool TryGetCaseInsensitive(IDictionary<string, object?> arguments, string key, out object? value)
    {
        if (arguments.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var (candidate, candidateValue) in arguments)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string text => Normalise(text),
        JsonElement { ValueKind: JsonValueKind.String } element => Normalise(element.GetString()),
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement element => Normalise(element.ToString()),
        _ => Normalise(value.ToString())
    };

    private static string? Normalise(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
