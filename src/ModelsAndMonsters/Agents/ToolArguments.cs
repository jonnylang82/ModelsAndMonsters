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

    /// <summary>
    /// Reads a boolean argument, tolerating the several shapes providers actually send: a real JSON boolean,
    /// a boxed <see cref="bool"/>, or the words "true"/"false"/"yes"/"no"/"1"/"0" as a string. Returns
    /// <paramref name="fallback"/> when the argument is absent or unreadable, so a missing flag is a defined
    /// value rather than an exception — and the raw arguments are traced either way.
    /// </summary>
    public static bool GetBool(FunctionCallContent call, string parameterName, bool fallback = false)
    {
        var arguments = call.Arguments;
        if (arguments is null || !TryGetCaseInsensitive(arguments, parameterName, out var value))
        {
            return fallback;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Number } number => number.TryGetDouble(out var d) && d != 0,
            _ => ParseWord(Stringify(value)) ?? fallback
        };

        static bool? ParseWord(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => null
        };
    }

    /// <summary>
    /// Reads a list-of-strings argument, tolerating the shapes providers actually send: a JSON array, an
    /// <see cref="IEnumerable{T}"/> of values, or a single comma-separated (or newline-separated) string —
    /// which small models emit for an array parameter often enough to matter. Blank entries are dropped, and
    /// an absent argument yields an empty list rather than throwing.
    /// </summary>
    /// <param name="splitLooseStrings">
    /// Whether a bare string supplied where a list was expected should be split on commas and newlines.
    /// True for id lists, where a model writing "purse-vark, salve" plainly means two entries. FALSE for
    /// prose — a comma inside a spoken line is punctuation, and splitting "Rowan, take the flank!" into two
    /// utterances would put a word in a character's mouth that nobody wrote.
    /// </param>
    public static IReadOnlyList<string> GetStringList(
        FunctionCallContent call, string parameterName, bool splitLooseStrings = true)
    {
        var arguments = call.Arguments;
        if (arguments is null || !TryGetCaseInsensitive(arguments, parameterName, out var value) || value is null)
        {
            return [];
        }

        switch (value)
        {
            case string single:
                return FromLooseString(single, splitLooseStrings);

            case JsonElement { ValueKind: JsonValueKind.Array } array:
                return [.. array.EnumerateArray()
                    .Select(element => Stringify(element))
                    .Where(v => v is not null)
                    .Select(v => v!)];

            case JsonElement { ValueKind: JsonValueKind.String } element:
                return FromLooseString(element.GetString(), splitLooseStrings);

            case JsonElement { ValueKind: JsonValueKind.Null }:
                return [];

            case System.Collections.IEnumerable enumerable:
            {
                var values = new List<string>();
                foreach (var item in enumerable)
                {
                    if (Stringify(item) is { } text)
                    {
                        values.Add(text);
                    }
                }

                return values;
            }

            default:
                return Stringify(value) is { } lone ? FromLooseString(lone, splitLooseStrings) : [];
        }
    }

    /// <summary>
    /// The characters a model may produce where a closing <c>]</c> belongs: the real one, and the full-width
    /// and CJK lenticular lookalikes a quantised model reaches for instead.
    /// </summary>
    private static readonly char[] ArrayClosers = [']', '\uFF3D', '\u3011'];

    /// <summary>
    /// Reads a value that arrived as a bare string where a list was expected. A model that serialises the
    /// array rather than sending one gets parsed properly; anything else is either split or kept whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The serialised-array case is not hypothetical, and it is not rare. Qwen's tool-call wire format is
    /// XML, which has no array type: every parameter value arrives as text, so an array-typed parameter
    /// necessarily comes back as a string that merely LOOKS like an array. Whether it is well-formed is then
    /// down to the model.
    /// </para>
    /// <para>
    /// It often is not, and the failure is loud, because speech is delivered verbatim by design. Three live
    /// examples, each defeating the previous guard:
    /// <list type="bullet">
    /// <item><c>["Take it! You may go!"']</c> — a stray apostrophe; valid brackets, invalid JSON.</item>
    /// <item><c>["Skrit, hold your line ..."】</c> — closed with a CJK lenticular bracket instead of <c>]</c>.</item>
    /// <item><c>["Vark, take this!"</c> — never closed at all.</item>
    /// </list>
    /// The last two got through a guard that required a literal <c>]</c> to be present, and were spoken to
    /// the room with the scaffolding attached. That is not a cosmetic blemish: spoken words are delivered
    /// into every listener's history and folded into their running summaries, so one malformed argument in
    /// round 5 was still sitting in the Dungeon Master's context in round 11.
    /// </para>
    /// <para>
    /// So the signal is how the value OPENS, not whether it closes. This is protocol repair, not language
    /// parsing: it undoes a serialisation the provider could not express, and never inspects the words
    /// between the quotes or infers anything from them.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> FromLooseString(string? text, bool splitLooseStrings)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            var close = trimmed.LastIndexOfAny(ArrayClosers);

            // Only a real bracket can close real JSON; a lookalike means it was never going to parse.
            if (close > 0 && trimmed[close] == ']')
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed[..(close + 1)]);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return [.. document.RootElement.EnumerateArray()
                            .Select(element => Stringify(element))
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v!.Trim())];
                    }
                }
                catch (JsonException)
                {
                    // Fall through to span recovery.
                }
            }

            // Malformed, unclosed, or closed with a lookalike. Recover the quoted spans rather than giving
            // up, because the alternative is speaking the punctuation aloud.
            var quoted = QuotedSpans(trimmed);
            if (quoted.Count > 0)
            {
                return quoted;
            }

            // Nothing quoted to recover: strip the scaffolding and treat what is left as the value. Keeping
            // the brackets would put them in a character's mouth, which is the one outcome to avoid.
            var inner = close > 0 ? trimmed[1..close] : trimmed[1..];
            trimmed = inner.Trim();
            if (trimmed.Length == 0)
            {
                return [];
            }
        }

        return splitLooseStrings ? SplitLoose(trimmed) : [trimmed];
    }

    /// <summary>
    /// Pulls the double-quoted spans out of a malformed JSON array, in order. Returns nothing when there are
    /// none, so a caller can fall back rather than inventing an entry.
    /// </summary>
    private static IReadOnlyList<string> QuotedSpans(string text)
    {
        var spans = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('"', index);
            if (open < 0)
            {
                break;
            }

            var close = text.IndexOf('"', open + 1);
            if (close < 0)
            {
                break;
            }

            var span = text[(open + 1)..close].Trim();
            if (span.Length > 0)
            {
                spans.Add(span);
            }

            index = close + 1;
        }

        return spans;
    }

    /// <summary>Splits a single string that was supplied where a list was asked for. Never invents an entry.</summary>
    private static IReadOnlyList<string> SplitLoose(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

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
